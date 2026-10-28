using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Weathering
{
    /// <summary>
    /// Heals flagged zones on the server, working on their saved terrain data while no player is near
    /// them; nothing is instantiated. Only the server holds every ZDO, so only it sees every base and
    /// Warden stone around a zone, and a loaded TerrainComp re-saves its whole array over anything
    /// written underneath it, so a zone is only written while nobody can have it loaded. Players see
    /// the result when they come back and the zone loads.
    /// While everyone sleeps (sleep mode) zones near players heal too, as of the morning the skip
    /// ends at. The server then writes only zones it owns itself; a zone a connected peer owns is
    /// sent to that peer, which applies it if nothing changed since (see HealApply).
    /// </summary>
    internal class HealRunner : MonoBehaviour
    {
        // Scheduling.
        private const float SweepInterval = 10f;     // real seconds between the starts of two sweeps
        private const int InspectPerFrame = 256;     // ZDOs looked at per frame while searching for a due zone
        // Real seconds from the end of one heal pass to the start of the next: a pass decodes up to 5
        // zones and runs up to 240 erosion iterations, too much to repeat back to back when many
        // zones are due.
        private const float HealInterval = 0.5f;
        // Erosion iterations run per frame while a pass is in flight. A whole 240-iteration catch-up
        // in one frame took tens of milliseconds, a visible stutter for the host.
        private const int ErosionIterationsPerFrame = 24;

        // Used when the simulation distance cannot be read: the largest the game allows (Near 5, Far 2).
        private const int FallbackNear = 5;
        private const int FallbackFar = 2;

        private const float ZoneSize = 64f;
        private const float MaxOffset = 8f;          // vanilla clamps height edits to +-8 m
        private const long DefaultDayLength = 1200L;
        private const float PavedThreshold = 0.5f;

        // The server's own synced simulation distance; peers' are public on ZNetPeer.
        private static readonly AccessTools.FieldRef<ZNet, SimulationDistance> s_simulationDistance =
            AccessTools.FieldRefAccess<ZNet, SimulationDistance>("m_simulationDistance");
        // The world time a sleep skip ends at (the next morning); private on EnvMan.
        private static readonly AccessTools.FieldRef<EnvMan, double> s_skipToTime =
            AccessTools.FieldRefAccess<EnvMan, double>("m_skipToTime");

        // Edge neighbours by side: 0 is +x, 1 is -x, 2 is +z, 3 is -z (grid y runs along world z).
        private static readonly int[] s_sideDx = { 1, -1, 0, 0 };
        private static readonly int[] s_sideDy = { 0, 0, 1, -1 };
        private const int CornerSide = 4;

        // Grid, read once from the zone prefab.
        private static bool s_gridRead;
        private static bool s_gridMissing;
        private static int s_width;
        private static int s_pitch;
        private static float s_scale;
        private static bool s_sharedBorders;
        private static string s_compilerName;
        private static int s_compilerHash;

        // Sweep state. A sweep fills s_scan a few hundred sectors per frame; when it completes, its
        // zones replace the queue that is being worked through.
        private static ZDOMan s_zdoMan;
        private static readonly List<ZDO> s_scan = new List<ZDO>();
        private static readonly List<ZDO> s_queue = new List<ZDO>();
        private static readonly Dictionary<Vector2s, ZDO> s_byZone = new Dictionary<Vector2s, ZDO>();
        private static bool s_sweeping;
        private static int s_scanIndex;
        private static float s_sweepStart = float.NegativeInfinity;
        private static int s_cursor;
        private static float s_nextHealTime;

        private static int s_idleDistanceFrame = -1;
        private static int s_idleDistance;

        // Per-pass buffers, resized only when the vertex count changes.
        private static bool[] s_frozen = new bool[0];
        private static bool[] s_deepNorth = new bool[0];
        private static float[] s_offset = new float[0];
        private static int[] s_stampSnapshot = new int[0];
        private static float[] s_fadeAmount = new float[0];
        private static readonly List<ProtectedCircle> s_circles = new List<ProtectedCircle>();
        private static readonly Neighbour[] s_neighbours = { new Neighbour(), new Neighbour(), new Neighbour(), new Neighbour() };
        private static readonly List<ZDO> s_found = new List<ZDO>();

        // The heal pass in flight, spread over frames: prepared in one frame, eroded a slice per frame,
        // written in a last frame. Only one pass runs at a time, and only on the main thread, so the
        // per-pass buffers above and Healer's scratch buffer are never shared between passes. Nothing
        // is written to any ZDO before the finish frame, so an aborted pass leaves no trace.
        private static bool s_passActive;
        private static ZDO s_passZdo;
        private static Vector2s s_passZone;
        private static uint s_passRevision;
        private static long s_passNow;
        private static TerrainData s_passData;
        private static int[] s_passStamps;
        private static int s_passNowMinutes;
        private static float s_passDelayMinutes;
        private static bool s_passPaintPending;
        private static bool s_passWaiting;
        private static bool s_passCanErode;
        private static int s_passIterationsLeft;
        private static bool s_passEroded;
        private static bool s_passErosionPending;
        // Session ID of whoever writes the pass's results: the server's own, or the connected peer
        // that owns the zone (sleep mode only). Neighbours are linked only if the same one writes them.
        private static long s_passWriter;

        // Results bound for a peer, sent as one message when the pass writes.
        private static readonly List<HealApply.Entry> s_outgoing = new List<HealApply.Entry>();

        private class Neighbour
        {
            public bool Linked;
            public ZDO Zdo;
            public uint Revision;
            public TerrainData Data;
            public int[] Stamps;
        }

        internal static int Pitch => s_pitch;

        private void Update()
        {
            // Every peer must be able to apply an overnight heal of a zone it owns.
            HealApply.EnsureRegistered();
            ZNet net = ZNet.instance;
            if (!net || !net.IsServer() || !BaseIndex.IsReady) return;
            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null || !ZNetScene.instance || !EnsureGrid()) return;

            if (zdoMan != s_zdoMan) Reset(zdoMan);
            Sweep(zdoMan);
            HealNext();
        }

        /// <summary>
        /// True when no peer and not the server's own player is close enough to have the zone loaded,
        /// even as a ghost zone: its Chebyshev zone distance from every reference position is above
        /// Near + Far + 1 of the largest simulation distance in use. Server only. Anything that writes
        /// saved data of unloaded zones (healing, tree regrowth) must check this first, except in sleep
        /// mode, where the heal runner writes only zones the server owns (WriterOf).
        /// </summary>
        internal static bool IsZoneIdle(Vector2s zone) => NearestViewerDistance(zone) > IdleDistance();

        /// <summary>IsZoneIdle, and the server has no instance of this ZDO loaded either.</summary>
        internal static bool IsZoneIdle(Vector2s zone, ZDO zdo)
        {
            if (!IsZoneIdle(zone)) return false;
            ZNetScene scene = ZNetScene.instance;
            return !scene || !scene.FindInstance(zdo);
        }

        /// <summary>Zone distance a viewer must be beyond for a zone to count as idle. Cached per frame.</summary>
        internal static int IdleDistance()
        {
            if (s_idleDistanceFrame == Time.frameCount) return s_idleDistance;
            s_idleDistanceFrame = Time.frameCount;
            s_idleDistance = ComputeIdleDistance();
            return s_idleDistance;
        }

        private static int ComputeIdleDistance()
        {
            int near = 0;
            int far = 0;
            ZNet net = ZNet.instance;
            if (net)
            {
                SimulationDistance own = s_simulationDistance(net);
                near = own.NearSimulationDistance;
                far = own.FarSimulationDistance;
                foreach (ZNetPeer peer in net.GetPeers())
                {
                    near = Math.Max(near, peer.m_simulationDistance.NearSimulationDistance);
                    far = Math.Max(far, peer.m_simulationDistance.FarSimulationDistance);
                }
            }
            if (near < 1) near = FallbackNear;
            if (far < 1) far = FallbackFar;
            return near + far + 1;
        }

        /// <summary>Chebyshev zone distance to the nearest peer, or to the host's own player.</summary>
        internal static int NearestViewerDistance(Vector2s zone)
        {
            ZNet net = ZNet.instance;
            if (!net) return int.MaxValue;
            int best = int.MaxValue;
            // A dedicated server has no player and its reference position stays at the world origin,
            // which would keep the spawn area from ever healing. Whatever the server itself has
            // loaded is caught by the FindInstance check instead.
            if (Player.m_localPlayer) best = ZoneDistance(zone, ZoneSystem.GetZone(net.GetReferencePosition()));
            foreach (ZNetPeer peer in net.GetPeers())
                best = Math.Min(best, ZoneDistance(zone, ZoneSystem.GetZone(peer.GetRefPos())));
            return best;
        }

        /// <summary>Reads the terrain grid and compiler prefab from the zone prefab; false until it can.</summary>
        internal static bool EnsureGrid()
        {
            if (s_gridRead) return true;
            if (s_gridMissing) return false;
            ZoneSystem zoneSystem = ZoneSystem.instance;
            if (!zoneSystem || !zoneSystem.m_zonePrefab) return false;

            Heightmap heightmap = zoneSystem.m_zonePrefab.GetComponentInChildren<Heightmap>(true);
            if (!heightmap || !heightmap.m_terrainCompilerPrefab || heightmap.m_width < 2)
            {
                s_gridMissing = true;
                Jotunn.Logger.LogWarning("Could not find the terrain grid on the zone prefab; terrain will not heal.");
                return false;
            }

            s_width = heightmap.m_width;
            s_pitch = s_width + 1;
            s_scale = heightmap.m_scale;
            // Border vertices are shared with the neighbour only when the grid spans the zone exactly.
            s_sharedBorders = Mathf.Abs(s_width * s_scale - ZoneSize) < 0.001f;
            s_compilerName = heightmap.m_terrainCompilerPrefab.name;
            s_compilerHash = s_compilerName.GetStableHashCode();
            s_gridRead = true;
            return true;
        }

        /// <summary>Terrain compiler ZDOs within <paramref name="radius"/> zones (Chebyshev) of a zone.</summary>
        internal static void FindCompilers(Vector2s zone, int radius, List<ZDO> result)
        {
            result.Clear();
            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null || !EnsureGrid()) return;
            s_found.Clear();
            // Classic with no far ring visits exactly the square of sectors around the zone.
            zdoMan.FindSectorObjects(zone, new SimulationDistance(radius, 0, classic: true), s_found);
            foreach (ZDO zdo in s_found)
                if (IsCompiler(zdo)) result.Add(zdo);
            s_found.Clear();
        }

        /// <summary>Pending zones among those found by the last completed sweep.</summary>
        internal static int CountPending()
        {
            int count = 0;
            foreach (ZDO zdo in s_queue)
                if (IsCompiler(zdo) && StampStore.IsPending(zdo)) count++;
            return count;
        }

        internal static long DayLength()
        {
            EnvMan env = EnvMan.instance;
            return env ? Math.Max(1L, env.m_dayLengthSec) : DefaultDayLength;
        }

        /// <summary>Takes the ZDO for the server before writing, the way vanilla's server code does.</summary>
        internal static void Claim(ZDO zdo) => zdo.SetOwner(ZDOMan.GetSessionID());

        /// <summary>
        /// Sleep mode: on the server while everyone sleeps and the game fast-forwards to morning. Zones
        /// and regrowth sites then need not be idle, and "now" is the morning the skip ends at.
        /// </summary>
        internal static bool IsSleepMode()
        {
            ZNet net = ZNet.instance;
            EnvMan env = EnvMan.instance;
            return net && env && net.IsServer() && env.IsTimeSkipping();
        }

        /// <summary>
        /// World time in whole seconds a heal pass or regrowth check counts as now: the skip target in
        /// sleep mode, so the night counts, else the current time.
        /// </summary>
        internal static long PassTime()
        {
            long now = (long)ZNet.instance.GetTimeSeconds();
            if (!IsSleepMode()) return now;
            return Math.Max(now, (long)s_skipToTime(EnvMan.instance));
        }

        // Who may write a zone: the server itself (its session ID) when the zone is idle, or when the
        // server, nobody or a peer that is gone owns it; else the connected peer that owns it. Outside
        // sleep mode only idle zones are healed, so it is always the server there.
        private static long WriterOf(Vector2s zone, ZDO zdo)
        {
            long server = ZDOMan.GetSessionID();
            if (IsZoneIdle(zone, zdo)) return server;
            long owner = zdo.GetOwner();
            if (owner == 0L || owner == server) return server;
            ZNet net = ZNet.instance;
            return net && net.GetPeer(owner) != null ? owner : server;
        }

        // Writes a zone's result if the server is the writer, else queues it for the owning peer, who
        // applies it only while the zone still has the data revision the pass read. Null data or
        // stamps, and a last of 0, leave those as they are.
        private static void Commit(long writer, ZDO zdo, uint revision, TerrainData data, int[] stamps, long last, bool pending)
        {
            if (writer == ZDOMan.GetSessionID())
            {
                Claim(zdo);
                if (data != null) data.Write(zdo);
                if (stamps != null) StampStore.Save(zdo, stamps);
                if (last != 0L) StampStore.SetLast(zdo, last);
                StampStore.SetPending(zdo, pending);
                return;
            }
            s_outgoing.Add(new HealApply.Entry
            {
                Id = zdo.m_uid,
                Revision = revision,
                TerrainData = data?.Encode(),
                Stamps = stamps != null ? StampStore.Encode(stamps) : null,
                Last = last,
                Pending = pending,
            });
        }

        // Sends what Commit queued for a peer, as one message so the owner applies all or none of it.
        private static void Flush(long writer)
        {
            if (s_outgoing.Count == 0) return;
            try
            {
                HealApply.Send(writer, s_outgoing);
            }
            finally
            {
                s_outgoing.Clear();
            }
        }

        private static void Reset(ZDOMan zdoMan)
        {
            s_zdoMan = zdoMan;
            s_scan.Clear();
            s_queue.Clear();
            s_byZone.Clear();
            s_sweeping = false;
            s_scanIndex = 0;
            s_sweepStart = float.NegativeInfinity;
            s_cursor = 0;
            s_nextHealTime = 0f;
            ClearPass();
        }

        private static void Sweep(ZDOMan zdoMan)
        {
            if (!s_sweeping)
            {
                if (Time.time - s_sweepStart < SweepInterval) return;
                s_sweeping = true;
                s_sweepStart = Time.time;
                s_scanIndex = 0;
                s_scan.Clear();
            }
            if (!zdoMan.GetAllZDOsWithPrefabIterative(s_compilerName, s_scan, ref s_scanIndex)) return;

            s_sweeping = false;
            s_queue.Clear();
            s_byZone.Clear();
            // Vanilla lists sector 0 twice, and a zone can briefly hold a duplicate compiler; keep one per zone.
            foreach (ZDO zdo in s_scan)
            {
                if (!IsCompiler(zdo)) continue;
                Vector2s zone = ZoneSystem.GetZone(zdo.GetPosition());
                if (s_byZone.ContainsKey(zone)) continue;
                s_byZone[zone] = zdo;
                s_queue.Add(zdo);
            }
            s_scan.Clear();
            s_cursor = 0;
        }

        /// <summary>The heal pass in flight, for weathering_status; null when there is none.</summary>
        internal static string DescribePass()
        {
            if (!s_passActive) return null;
            string phase = s_passEroded ? "writing it back" : $"{s_passIterationsLeft} erosion iterations left";
            return $"Healing zone {s_passZone.x},{s_passZone.y} now: {phase}.";
        }

        private static void HealNext()
        {
            if (s_passActive)
            {
                ContinuePass();
                return;
            }
            if (s_cursor >= s_queue.Count) return;
            // Screens are dark while everyone sleeps: no wait between passes.
            bool sleeping = IsSleepMode();
            if (!sleeping && Time.realtimeSinceStartup < s_nextHealTime) return;
            long now = PassTime();
            long day = DayLength();

            for (int n = 0; n < InspectPerFrame && s_cursor < s_queue.Count; n++)
            {
                ZDO zdo = s_queue[s_cursor++];
                if (!IsCompiler(zdo) || !StampStore.IsPending(zdo)) continue;
                long last = StampStore.GetLast(zdo);
                // A world time behind the last pass (a restored save) counts as due.
                if (last != 0L && now >= last && now - last < day) continue;
                Vector2s zone = ZoneSystem.GetZone(zdo.GetPosition());
                if (!sleeping && !IsZoneIdle(zone, zdo)) continue;
                long writer = WriterOf(zone, zdo);

                try
                {
                    Prepare(zdo, zone, now, day, writer, sleeping);
                }
                catch (Exception e)
                {
                    Fail(zdo, zone, writer, e);
                }
                // A zone that was only stamped or could not be read is done in this frame.
                if (!s_passActive) EndPass();
                // In sleep mode the whole pass runs in this frame.
                else if (sleeping) ContinuePass();
                return;
            }
        }

        // One frame of the pass in flight: a slice of erosion, or the final checks and writes. In
        // sleep mode all of the erosion and the writes, in one frame.
        private static void ContinuePass()
        {
            ZDO zdo = s_passZdo;
            Vector2s zone = s_passZone;
            try
            {
                // Someone came near (or the server loaded it): what is in memory would be saved over
                // or would overwrite their edits. Drop it; the zone stays pending and is retried.
                // In sleep mode Finish checks who may write instead.
                bool sleeping = IsSleepMode();
                if (!IsCompiler(zdo) || (!sleeping && !IsZoneIdle(zone, zdo)))
                {
                    EndPass();
                    return;
                }
                if (!s_passEroded)
                {
                    ErodeSlice(sleeping ? s_passIterationsLeft : ErosionIterationsPerFrame);
                    if (!s_passEroded || !sleeping) return;
                }
                Finish();
            }
            catch (Exception e)
            {
                Fail(zdo, zone, s_passWriter, e);
            }
            EndPass();
        }

        private static void Fail(ZDO zdo, Vector2s zone, long writer, Exception e)
        {
            Jotunn.Logger.LogWarning($"Could not heal zone {zone.x},{zone.y}, it will not be retried until it is edited again: {e.Message}");
            try
            {
                s_outgoing.Clear();
                Commit(writer, zdo, zdo.DataRevision, null, null, 0L, false);
                Flush(writer);
            }
            catch (Exception)
            {
                // Nothing more can be done for a ZDO that cannot even take a flag.
            }
        }

        // Ends the pass in flight, if any, without writing, and starts the wait before the next one.
        private static void EndPass()
        {
            ClearPass();
            s_nextHealTime = Time.realtimeSinceStartup + HealInterval;
        }

        private static void ClearPass()
        {
            s_passActive = false;
            s_passZdo = null;
            s_passData = null;
            s_passStamps = null;
            s_passEroded = false;
            s_passIterationsLeft = 0;
            s_passWriter = 0L;
            s_outgoing.Clear();
            for (int s = 0; s < s_neighbours.Length; s++)
                ClearNeighbour(s_neighbours[s]);
        }

        // Decodes the zone and its linked neighbours, builds the masks, fades the paint and loads the
        // offsets, then leaves the pass in flight for ErodeSlice. Writes only for a zone seen for the
        // first time or one whose data cannot be read; those end here. <paramref name="writer"/> is
        // who writes the results (WriterOf).
        private static void Prepare(ZDO zdo, Vector2s zone, long now, long day, long writer, bool sleeping)
        {
            int width = s_width;
            int pitch = s_pitch;
            int count = pitch * pitch;

            uint revision = zdo.DataRevision;
            if (!TerrainData.TryRead(zdo, pitch, out TerrainData data))
            {
                if (zdo.GetByteArray(ZDOVars.s_TCData) != null)
                    Jotunn.Logger.LogWarning($"Could not read the terrain data of zone {zone.x},{zone.y}; it will not heal until it is edited again.");
                Commit(writer, zdo, revision, null, null, 0L, false);
                Flush(writer);
                return;
            }

            int nowMinutes = (int)(now / 60);
            int[] stamps = StampStore.Load(zdo, count);
            StampStore.EnsureStamped(data, stamps, nowMinutes);
            long last = StampStore.GetLast(zdo);

            if (last == 0L)
            {
                // First sight: edits without a stamp start their delay now; the first heal is a day later.
                // The zone was pending to get here, and stays so.
                Commit(writer, zdo, revision, null, stamps, Math.Max(now, 1L), true);
                Flush(writer);
                return;
            }

            float elapsedDays = Mathf.Max(0f, (now - last) / (float)day);
            float delayMinutes = WeatheringPlugin.DelayDays.Value * day / 60f;
            float dayMinutes = day / 60f;
            float fadeDays = WeatheringPlugin.FadeDays.Value;

            EnsureBuffers(count);
            bool[] frozen = s_frozen;
            bool[] deepNorth = s_deepNorth;
            float[] offset = s_offset;
            float[] fadeAmount = s_fadeAmount;
            // The most days any due height edit has been past its delay since the last pass; a zone
            // whose edits only just came due gets that much erosion, not the whole time since the last pass.
            float erosionDays = 0f;

            BaseIndex.Collect(zone, s_circles);
            LoadNeighbours(zone, pitch, nowMinutes, writer, sleeping);

            WorldGenerator worldGen = WorldGenerator.instance;
            Vector3 centre = ZoneSystem.GetZonePos(zone);
            float half = width / 2f;
            // Vertices held back only by their delay still need healing later and keep the zone flagged.
            bool waiting = false;

            for (int y = 0; y <= width; y++)
            {
                for (int x = 0; x <= width; x++)
                {
                    int i = y * pitch + x;
                    bool modifiedHeight = data.ModifiedHeight[i];
                    bool modifiedPaint = data.ModifiedPaint[i];
                    Color paint = data.Paint[i];
                    float d = modifiedHeight ? data.LevelDelta[i] + data.SmoothDelta[i] : 0f;
                    offset[i] = d;

                    float wx = centre.x + (x - half) * s_scale;
                    float wz = centre.z + (y - half) * s_scale;
                    // Only painted vertices need the biome, and it is the costliest lookup here.
                    deepNorth[i] = modifiedPaint && worldGen != null &&
                        worldGen.GetBiome(wx, wz) == Heightmap.Biome.DeepNorth;

                    int spotStamp = stamps[i];
                    bool fresh = IsFresh(spotStamp, nowMinutes, delayMinutes);
                    bool held = float.IsNaN(d) || fresh || (modifiedPaint && paint.b > PavedThreshold);

                    int side = BorderSide(x, y, width);
                    if (side == CornerSide)
                    {
                        // A corner is shared by four zones; it never moves, so no copy can drift.
                        held = true;
                    }
                    else if (side >= 0)
                    {
                        Neighbour neighbour = s_neighbours[side];
                        if (!neighbour.Linked)
                        {
                            held = true;
                        }
                        else
                        {
                            // The shared vertex moves only if both zones agree it may.
                            int j = MirrorIndex(side, x, y, width, pitch);
                            int otherStamp = neighbour.Stamps[j];
                            if (IsFresh(otherStamp, nowMinutes, delayMinutes)) fresh = held = true;
                            // The shared vertex heals only for as long as both copies have been due.
                            if (otherStamp > spotStamp) spotStamp = otherStamp;
                            if (neighbour.Data.ModifiedPaint[j] && neighbour.Data.Paint[j].b > PavedThreshold) held = true;
                        }
                    }

                    if (!held)
                    {
                        var point = new Vector2(wx, wz);
                        for (int c = 0; c < s_circles.Count; c++)
                        {
                            if (!s_circles[c].Contains(point)) continue;
                            held = true;
                            break;
                        }
                    }

                    frozen[i] = held;
                    if (fresh && StampStore.NeedsStamp(modifiedHeight, modifiedPaint, paint)) waiting = true;

                    float spotDays = DueDays(spotStamp, elapsedDays, nowMinutes, delayMinutes, dayMinutes);
                    fadeAmount[i] = fadeDays > 0f ? spotDays / fadeDays : 0f;
                    // Frozen but due height edits count too (a paved edge, a base rim, a zone border):
                    // they stay put but their steep edge still erodes the unfrozen ground next to them.
                    if (!fresh && modifiedHeight && spotDays > erosionDays) erosionDays = spotDays;
                }
            }

            bool paintPending = Healer.FadePaint(data.Paint, data.ModifiedPaint, frozen, deepNorth, fadeAmount);

            float erosionRate = WeatheringPlugin.ErosionRate.Value;
            int iterations = Mathf.Min(Mathf.RoundToInt(erosionDays * Healer.IterationsPerDay * erosionRate),
                Healer.MaxIterationsPerPass);
            iterations = Mathf.Max(iterations, 0);

            s_passActive = true;
            s_passZdo = zdo;
            s_passZone = zone;
            s_passRevision = revision;
            s_passNow = now;
            s_passData = data;
            s_passStamps = stamps;
            s_passNowMinutes = nowMinutes;
            s_passDelayMinutes = delayMinutes;
            s_passPaintPending = paintPending;
            s_passWaiting = waiting;
            // With erosion off (or too weak for a single iteration in a day) steep pairs would otherwise
            // keep the zone flagged and rewritten every day forever. Judged by a whole day, not this
            // pass, so edits that only just came due still keep the zone flagged for the next pass.
            s_passCanErode = Mathf.RoundToInt(Healer.IterationsPerDay * erosionRate) > 0;
            s_passIterationsLeft = iterations;
            s_passEroded = false;
            s_passErosionPending = false;
            s_passWriter = writer;
        }

        // Runs up to maxIterations iterations. Erosion is done when the pass's iterations are used up
        // or nothing can move any more (further iterations would change nothing); the last slice's
        // answer is the pass's. A pass with no iterations still runs one empty slice for it.
        private static void ErodeSlice(int maxIterations)
        {
            int slice = Math.Min(s_passIterationsLeft, maxIterations);
            bool moving = Healer.Erode(s_offset, s_frozen, s_pitch, slice, Healer.DiffusionRate);
            s_passIterationsLeft -= slice;
            s_passErosionPending = moving && s_passCanErode;
            if (!moving || s_passIterationsLeft <= 0) s_passEroded = true;
        }

        // Writes the pass back, but only if nothing touched the zone or its linked neighbours since
        // Prepare read them: a changed data revision means the decoded copy is stale, and writing it
        // would undo that change. Then the pass is dropped unwritten and the zone, still pending with
        // its old last-pass time, is retried later. The same goes when whoever may write the zone or a
        // linked neighbour is no longer the pass's writer (sleep mode: its owner changed).
        private static void Finish()
        {
            ZDO zdo = s_passZdo;
            if (zdo.DataRevision != s_passRevision) return;
            // Outside sleep mode ContinuePass has checked that the zone is idle, so this is the server.
            if (WriterOf(s_passZone, zdo) != s_passWriter) return;
            bool sleeping = IsSleepMode();
            for (int s = 0; s < s_neighbours.Length; s++)
            {
                Neighbour neighbour = s_neighbours[s];
                if (!neighbour.Linked) continue;
                if (!IsCompiler(neighbour.Zdo) || neighbour.Zdo.DataRevision != neighbour.Revision) return;
                // MirrorBorders may write it, so it must still be safe to write too.
                Vector2s other = ZoneSystem.GetZone(neighbour.Zdo.GetPosition());
                if (!sleeping && !IsZoneIdle(other, neighbour.Zdo)) return;
                if (WriterOf(other, neighbour.Zdo) != s_passWriter) return;
            }

            TerrainData data = s_passData;
            int[] stamps = s_passStamps;
            Healer.ZeroTiny(s_offset, data.ModifiedHeight, s_frozen);

            WriteBackHeights(data, stamps, s_passNowMinutes, s_passDelayMinutes);
            ClearStaleStamps(data, stamps);

            // This zone first: if a neighbour write fails, the zone itself is already consistent.
            Commit(s_passWriter, zdo, s_passRevision, data, stamps, s_passNow,
                s_passPaintPending || s_passErosionPending || s_passWaiting);

            MirrorBorders(data, stamps);
            // A peer gets the zone and its mirrored neighbours in one message, applied all or nothing.
            Flush(s_passWriter);
        }

        // Height results go back as level = clamped offset, smooth = 0, like vanilla's Level and Raise.
        // Frozen vertices keep their saved values untouched.
        private static void WriteBackHeights(TerrainData data, int[] stamps, int nowMinutes, float delayMinutes)
        {
            int width = s_width;
            int pitch = s_pitch;
            Array.Copy(stamps, s_stampSnapshot, stamps.Length);

            for (int y = 0; y <= width; y++)
            {
                for (int x = 0; x <= width; x++)
                {
                    int i = y * pitch + x;
                    if (s_frozen[i]) continue;
                    float d = s_offset[i];
                    if (!data.ModifiedHeight[i])
                    {
                        // ZeroTiny cleared it, or it never held an edit and received nothing.
                        if (d == 0f)
                        {
                            data.LevelDelta[i] = 0f;
                            data.SmoothDelta[i] = 0f;
                            continue;
                        }
                        // Earth spread here: the vertex is modified now and inherits an old stamp.
                        data.ModifiedHeight[i] = true;
                        stamps[i] = InheritedStamp(i, x, y, nowMinutes, delayMinutes);
                    }
                    data.LevelDelta[i] = Mathf.Clamp(d, -MaxOffset, MaxOffset);
                    data.SmoothDelta[i] = 0f;
                }
            }
        }

        // The oldest non-fresh stamp of the vertex and its 4 neighbours, so spread earth never restarts
        // the delay. Minute 1 (long past) when there is none.
        private static int InheritedStamp(int i, int x, int y, int nowMinutes, float delayMinutes)
        {
            int[] snap = s_stampSnapshot;
            int best = 0;
            best = Older(best, snap[i], nowMinutes, delayMinutes);
            if (x > 0) best = Older(best, snap[i - 1], nowMinutes, delayMinutes);
            if (x < s_width) best = Older(best, snap[i + 1], nowMinutes, delayMinutes);
            if (y > 0) best = Older(best, snap[i - s_pitch], nowMinutes, delayMinutes);
            if (y < s_width) best = Older(best, snap[i + s_pitch], nowMinutes, delayMinutes);
            return best != 0 ? best : 1;
        }

        private static int Older(int best, int stamp, int nowMinutes, float delayMinutes)
        {
            if (stamp == 0 || IsFresh(stamp, nowMinutes, delayMinutes)) return best;
            return best == 0 ? stamp : Math.Min(best, stamp);
        }

        // A stamp on a vertex with nothing left to heal would only make a later edit look older or
        // newer than it is; drop it.
        private static void ClearStaleStamps(TerrainData data, int[] stamps)
        {
            for (int i = 0; i < stamps.Length; i++)
            {
                if (stamps[i] == 0) continue;
                if (!StampStore.NeedsStamp(data.ModifiedHeight[i], data.ModifiedPaint[i], data.Paint[i])) stamps[i] = 0;
            }
        }

        // Writes this zone's new border heights into each linked neighbour's copy of the same vertices,
        // so both zones end with identical values. Paint is per zone and is not copied.
        private static void MirrorBorders(TerrainData data, int[] stamps)
        {
            int width = s_width;
            int pitch = s_pitch;
            for (int side = 0; side < s_neighbours.Length; side++)
            {
                Neighbour neighbour = s_neighbours[side];
                if (!neighbour.Linked) continue;
                TerrainData other = neighbour.Data;
                bool changed = false;

                for (int k = 1; k < width; k++)
                {
                    BorderVertex(side, k, width, out int x, out int y);
                    int i = y * pitch + x;
                    if (s_frozen[i]) continue;
                    int j = MirrorIndex(side, x, y, width, pitch);

                    bool modified = data.ModifiedHeight[i];
                    float level = modified ? data.LevelDelta[i] : 0f;
                    float smooth = modified ? data.SmoothDelta[i] : 0f;
                    if (other.ModifiedHeight[j] != modified || other.LevelDelta[j] != level || other.SmoothDelta[j] != smooth)
                    {
                        other.ModifiedHeight[j] = modified;
                        other.LevelDelta[j] = level;
                        other.SmoothDelta[j] = smooth;
                        changed = true;
                    }

                    // Keep the neighbour's stamp in step, so its next pass neither freezes the vertex as
                    // fresh nor stamps it as a new edit.
                    int stamp = StampStore.NeedsStamp(modified, other.ModifiedPaint[j], other.Paint[j])
                        ? (modified ? stamps[i] : neighbour.Stamps[j])
                        : 0;
                    if (neighbour.Stamps[j] != stamp)
                    {
                        neighbour.Stamps[j] = stamp;
                        changed = true;
                    }
                }

                if (!changed) continue;
                Commit(s_passWriter, neighbour.Zdo, neighbour.Revision, other, neighbour.Stamps, 0L, true);
            }
        }

        // Links each edge neighbour with saved data that the pass may write along with this zone:
        // an idle one outside sleep mode, one with the same writer in sleep mode. Other borders stay
        // frozen for this pass.
        private static void LoadNeighbours(Vector2s zone, int pitch, int nowMinutes, long writer, bool sleeping)
        {
            for (int side = 0; side < s_neighbours.Length; side++)
            {
                Neighbour neighbour = s_neighbours[side];
                ClearNeighbour(neighbour);
                if (!s_sharedBorders) continue;

                var other = new Vector2s(zone.x + s_sideDx[side], zone.y + s_sideDy[side]);
                if (!s_byZone.TryGetValue(other, out ZDO zdo) || !IsCompiler(zdo)) continue;
                if (!ZoneSystem.GetZone(zdo.GetPosition()).Equals(other)) continue;
                if (sleeping ? WriterOf(other, zdo) != writer : !IsZoneIdle(other, zdo)) continue;
                uint revision = zdo.DataRevision;
                if (!TerrainData.TryRead(zdo, pitch, out TerrainData data)) continue;

                int[] stamps = StampStore.Load(zdo, pitch * pitch);
                // Unstamped edits there would be stamped "now" on its first sight; judge them the same way.
                StampStore.EnsureStamped(data, stamps, nowMinutes);
                neighbour.Zdo = zdo;
                neighbour.Revision = revision;
                neighbour.Data = data;
                neighbour.Stamps = stamps;
                neighbour.Linked = true;
            }
        }

        private static void ClearNeighbour(Neighbour neighbour)
        {
            neighbour.Linked = false;
            neighbour.Zdo = null;
            neighbour.Revision = 0;
            neighbour.Data = null;
            neighbour.Stamps = null;
        }

        private static void EnsureBuffers(int count)
        {
            if (s_frozen.Length == count) return;
            s_frozen = new bool[count];
            s_deepNorth = new bool[count];
            s_offset = new float[count];
            s_stampSnapshot = new int[count];
            s_fadeAmount = new float[count];
        }

        private static bool IsFresh(int stamp, int nowMinutes, float delayMinutes) =>
            stamp != 0 && nowMinutes - stamp < delayMinutes;

        // Days a spot has been past its own delay, but no more than the time since the zone's last pass
        // (anything earlier was healed then). A vertex without a stamp holds nothing to heal and simply
        // takes the whole elapsed time.
        private static float DueDays(int stamp, float elapsedDays, int nowMinutes, float delayMinutes, float dayMinutes)
        {
            if (stamp == 0) return elapsedDays;
            float due = (nowMinutes - stamp - delayMinutes) / dayMinutes;
            return Mathf.Max(0f, Mathf.Min(elapsedDays, due));
        }

        private static bool IsCompiler(ZDO zdo) => zdo != null && zdo.IsValid() && zdo.GetPrefab() == s_compilerHash;

        private static int ZoneDistance(Vector2s a, Vector2s b) => Math.Max(Math.Abs(a.x - b.x), Math.Abs(a.y - b.y));

        // Which side a vertex lies on: 0 is x = width (+x), 1 is x = 0, 2 is y = width (+z), 3 is y = 0,
        // CornerSide for the four corners, -1 for the interior.
        private static int BorderSide(int x, int y, int width)
        {
            bool edgeX = x == 0 || x == width;
            bool edgeY = y == 0 || y == width;
            if (edgeX && edgeY) return CornerSide;
            if (edgeX) return x == width ? 0 : 1;
            if (edgeY) return y == width ? 2 : 3;
            return -1;
        }

        // The k-th vertex along a side, corners excluded (k runs 1 to width - 1).
        private static void BorderVertex(int side, int k, int width, out int x, out int y)
        {
            switch (side)
            {
                case 0: x = width; y = k; break;
                case 1: x = 0; y = k; break;
                case 2: x = k; y = width; break;
                default: x = k; y = 0; break;
            }
        }

        // The same vertex's index in the neighbour across the side: the coordinate along the border is
        // kept, the one across it flips between width and 0 (zone centres are width * scale apart).
        private static int MirrorIndex(int side, int x, int y, int width, int pitch)
        {
            switch (side)
            {
                case 0: x = 0; break;
                case 1: x = width; break;
                case 2: y = 0; break;
                default: y = width; break;
            }
            return y * pitch + x;
        }
    }
}
