using System;
using System.Collections.Generic;
using UnityEngine;

namespace Weathering
{
    /// <summary>
    /// Plants the sapling of a felled tree at its regrowth site on the server, once the site is
    /// <c>DelayDays</c> old and nobody is near, or while everyone sleeps (sleep mode) also near
    /// players. The sapling is created as a bare ZDO the way <c>ZNetView.Awake</c> would, without an
    /// instance; it appears when its zone is next loaded, or at once for players nearby, as any new
    /// ZDO does. Outside sleep mode a site is only touched while its zone passes the heal runner's
    /// idle test. Creating a ZDO and destroying a claimed one is safe near players: vanilla's server
    /// does the same.
    /// </summary>
    internal class RegrowthRunner : MonoBehaviour
    {
        // Scheduling.
        private const float SweepInterval = 10f;     // real seconds between the starts of two sweeps
        private const int InspectPerFrame = 128;     // sites looked at per frame while searching for due ones
        // Sites acted on per frame: each one may collect the protected circles around its zone.
        private const int ActPerFrame = 4;

        // A stump this close to the site is the felled tree's own (vanilla spawns it at the tree's position).
        private const float StumpRadius = 1f;

        // Ground under a site.
        private const float MaxTerrainDelta = 8f;    // vanilla clamps an edited vertex to its base height +-8 m
        private const float SunkTolerance = 1f;      // ground this much lower than the tree and under water drops the site

        // Sweep state, the same shape as the heal runner's: a sweep fills s_scan a few hundred sectors
        // per frame and replaces the queue when it completes.
        private static ZDOMan s_zdoMan;
        private static readonly List<ZDO> s_scan = new List<ZDO>();
        private static readonly List<ZDO> s_queue = new List<ZDO>();
        private static bool s_sweeping;
        private static int s_scanIndex;
        private static float s_sweepStart = float.NegativeInfinity;
        private static int s_cursor;
        private static bool s_wasSleeping;

        // World time in seconds before which a site blocked by its stump is not checked again.
        private static readonly Dictionary<ZDOID, long> s_blockedUntil = new Dictionary<ZDOID, long>();
        private static readonly HashSet<ZDOID> s_seen = new HashSet<ZDOID>();
        private static readonly List<ZDOID> s_stale = new List<ZDOID>();

        // Protected circles of the last zone collected, reused for other sites of that zone this frame.
        private static readonly List<ProtectedCircle> s_circles = new List<ProtectedCircle>();
        private static Vector2s s_circlesZone;
        private static int s_circlesFrame = -1;
        private static readonly List<ZDO> s_found = new List<ZDO>();
        private static readonly List<ZDO> s_compilers = new List<ZDO>();
        private static float s_gridScale;

        private void Update()
        {
            ZNet net = ZNet.instance;
            if (!net || !net.IsServer() || !BaseIndex.IsReady) return;
            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null || !ZNetScene.instance) return;

            if (zdoMan != s_zdoMan) Reset(zdoMan);
            // The skip lasts about 12 s, longer than a sweep: start over at the front of the queue
            // the moment it begins instead of waiting for the next sweep.
            bool sleeping = HealRunner.IsSleepMode();
            if (sleeping && !s_wasSleeping) s_cursor = 0;
            s_wasSleeping = sleeping;
            Sweep(zdoMan);
            ProcessNext(sleeping);
        }

        /// <summary>Regrowth sites within <paramref name="radius"/> zones (Chebyshev) of a zone.</summary>
        internal static void FindSites(Vector2s zone, int radius, List<ZDO> result)
        {
            result.Clear();
            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null) return;
            s_found.Clear();
            zdoMan.FindSectorObjects(zone, new SimulationDistance(radius, 0, classic: true), s_found);
            foreach (ZDO zdo in s_found)
                if (IsSite(zdo)) result.Add(zdo);
            s_found.Clear();
        }

        /// <summary>Sites found by the last completed sweep, and how many of them are old enough to regrow.</summary>
        internal static void Count(out int sites, out int due)
        {
            sites = 0;
            due = 0;
            if (!ZNet.instance) return;
            int now = StampStore.Now();
            float delay = DelayMinutes();
            foreach (ZDO zdo in s_queue)
            {
                if (!IsSite(zdo)) continue;
                sites++;
                if (IsDue(zdo, now, delay)) due++;
            }
        }

        /// <summary>Lets aged sites skip a pending stump recheck.</summary>
        internal static void ForgetBlocked(ZDO zdo) => s_blockedUntil.Remove(zdo.m_uid);

        internal static float DelayMinutes() => WeatheringPlugin.DelayDays.Value * HealRunner.DayLength() / 60f;

        private static void Reset(ZDOMan zdoMan)
        {
            s_zdoMan = zdoMan;
            s_scan.Clear();
            s_queue.Clear();
            s_blockedUntil.Clear();
            s_sweeping = false;
            s_scanIndex = 0;
            s_sweepStart = float.NegativeInfinity;
            s_cursor = 0;
            s_circlesFrame = -1;
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
            if (!zdoMan.GetAllZDOsWithPrefabIterative(RegrowthSites.MarkerName, s_scan, ref s_scanIndex)) return;

            s_sweeping = false;
            s_queue.Clear();
            s_seen.Clear();
            // Vanilla lists sector 0 twice; keep each site once.
            foreach (ZDO zdo in s_scan)
                if (IsSite(zdo) && s_seen.Add(zdo.m_uid)) s_queue.Add(zdo);
            s_scan.Clear();
            s_cursor = 0;

            // Forget stump rechecks of sites that are gone.
            s_stale.Clear();
            foreach (ZDOID id in s_blockedUntil.Keys)
                if (!s_seen.Contains(id)) s_stale.Add(id);
            foreach (ZDOID id in s_stale)
                s_blockedUntil.Remove(id);
            s_stale.Clear();
            s_seen.Clear();
        }

        private static void ProcessNext(bool sleeping)
        {
            // While everyone sleeps, sites near players regrow too, as of the morning the skip ends
            // at, and the queue is gone round again so sites keep being picked up until it ends.
            if (sleeping && s_cursor >= s_queue.Count) s_cursor = 0;
            if (s_cursor >= s_queue.Count) return;
            long nowSeconds = HealRunner.PassTime();
            int now = (int)(nowSeconds / 60);
            float delay = DelayMinutes();
            int acted = 0;

            for (int n = 0; n < InspectPerFrame && acted < ActPerFrame && s_cursor < s_queue.Count; n++)
            {
                ZDO zdo = s_queue[s_cursor++];
                if (!IsSite(zdo) || !IsDue(zdo, now, delay)) continue;
                if (s_blockedUntil.TryGetValue(zdo.m_uid, out long until) && nowSeconds < until) continue;
                Vector2s zone = ZoneSystem.GetZone(zdo.GetPosition());
                if (!sleeping && !HealRunner.IsZoneIdle(zone, zdo)) continue;

                acted++;
                Process(zdo, zone, nowSeconds);
            }
        }

        private static void Process(ZDO site, Vector2s zone, long nowSeconds)
        {
            try
            {
                Regrow(site, zone, nowSeconds);
            }
            catch (Exception e)
            {
                Vector3 pos = site.GetPosition();
                Jotunn.Logger.LogWarning($"Could not regrow the tree at {pos.x:0},{pos.z:0}, dropping its regrowth site: {e.Message}");
                try
                {
                    DestroySite(site);
                }
                catch (Exception)
                {
                    // Nothing more can be done for a site that cannot even be destroyed.
                }
            }
        }

        private static void Regrow(ZDO site, Vector2s zone, long nowSeconds)
        {
            // The tree no sapling grows into any more (a mod was removed): this site can never regrow.
            if (!RegrowthSites.TryGetSapling(site.GetInt(RegrowthSites.TreeKey), out int saplingHash))
            {
                DestroySite(site);
                return;
            }
            GameObject prefab = ZNetScene.instance.GetPrefab(saplingHash);
            ZNetView view = prefab ? prefab.GetComponent<ZNetView>() : null;
            if (!view)
            {
                DestroySite(site);
                return;
            }

            Vector3 pos = site.GetPosition();
            // Trees felled inside a base or next to a building leave a site too; that spot never regrows.
            var point = new Vector2(pos.x, pos.z);
            List<ProtectedCircle> circles = CirclesFor(zone);
            for (int c = 0; c < circles.Count; c++)
            {
                if (!circles[c].Contains(point)) continue;
                DestroySite(site);
                return;
            }

            int stumpHash = site.GetInt(RegrowthSites.StumpKey);
            if (stumpHash != 0 && HasStump(pos, stumpHash))
            {
                // A standing stump blocks regrowth; check again a day later in case it was removed.
                s_blockedUntil[site.m_uid] = nowSeconds + HealRunner.DayLength();
                return;
            }

            // The ground may have been dug, raised or eroded since the tree fell, and Plant.Grow spawns
            // the tree where the sapling stands without snapping it down: plant on the ground as it is now.
            if (!TryGetPlantHeight(pos, zone, out float height))
            {
                DestroySite(site);
                return;
            }
            pos.y = height;

            PlantSapling(view, saplingHash, pos, site.GetRotation(), prefab.transform.localScale, nowSeconds);
            DestroySite(site);
        }

        // False when the ground has been dug down under water since the felling: nothing regrows there.
        private static bool TryGetPlantHeight(Vector3 pos, Vector2s zone, out float height)
        {
            height = pos.y;
            if (!TryGetGroundHeight(pos, zone, out float ground)) return true;
            height = ground;
            ZoneSystem zoneSystem = ZoneSystem.instance;
            float water = zoneSystem ? zoneSystem.m_waterLevel : ZoneSystem.c_WaterLevel;
            // Some trees stand in shallow water to begin with: only ground that sank there counts.
            return ground >= water || ground >= pos.y - SunkTolerance;
        }

        // False when the site's saved height is the best guess: the ground there is as generated.
        private static bool TryGetGroundHeight(Vector3 pos, Vector2s zone, out float height)
        {
            // A zone loaded here (a listen host in sleep mode) knows its ground exactly, location
            // levelling included. The check skips distant LOD terrain, which has no collider.
            ZoneSystem zoneSystem = ZoneSystem.instance;
            if (zoneSystem && Heightmap.FindHeightmap(pos) && zoneSystem.GetGroundHeight(pos, out height)) return true;
            return TryGetSavedGroundHeight(pos, zone, out height);
        }

        // The ground from the zone's saved terrain edits: the generated height as HeightmapBuilder
        // blends it across the zone's corner biomes, plus each vertex's edit clamped as
        // TerrainComp.ApplyToHeightmap does, bilinear between the four vertices around the site.
        // Location levelling (TerrainModifier) is not known without the location loaded, so a spot
        // no player edit touches keeps the felled tree's own height, which already includes it.
        private static bool TryGetSavedGroundHeight(Vector3 pos, Vector2s zone, out float height)
        {
            height = 0f;
            WorldGenerator worldGen = WorldGenerator.instance;
            float scale = GridScale();
            if (worldGen == null || scale <= 0f || !HealRunner.EnsureGrid()) return false;
            int pitch = HealRunner.Pitch;
            ZDO compiler = FindCompiler(zone);
            if (compiler == null || !TerrainData.TryRead(compiler, pitch, out TerrainData data)) return false;

            int width = pitch - 1;
            float span = width * scale;
            Vector3 center = ZoneSystem.GetZonePos(zone);
            float x0 = center.x - span * 0.5f;
            float z0 = center.z - span * 0.5f;
            float gx = Mathf.Clamp((pos.x - x0) / scale, 0f, width);
            float gz = Mathf.Clamp((pos.z - z0) / scale, 0f, width);
            int ix = Mathf.Min((int)gx, width - 1);
            int iz = Mathf.Min((int)gz, width - 1);

            bool edited = false;
            for (int k = iz; k <= iz + 1 && !edited; k++)
            {
                for (int l = ix; l <= ix + 1; l++)
                {
                    int i = k * pitch + l;
                    if (data.LevelDelta[i] == 0f && data.SmoothDelta[i] == 0f) continue;
                    edited = true;
                    break;
                }
            }
            if (!edited) return false;

            var corners = new GridCorners
            {
                Low = worldGen.GetBiome(x0, z0),
                HighX = worldGen.GetBiome(x0 + span, z0),
                HighZ = worldGen.GetBiome(x0, z0 + span),
                High = worldGen.GetBiome(x0 + span, z0 + span),
            };
            float h00 = VertexHeight(worldGen, data, corners, x0, z0, scale, width, ix, iz);
            float h10 = VertexHeight(worldGen, data, corners, x0, z0, scale, width, ix + 1, iz);
            float h01 = VertexHeight(worldGen, data, corners, x0, z0, scale, width, ix, iz + 1);
            float h11 = VertexHeight(worldGen, data, corners, x0, z0, scale, width, ix + 1, iz + 1);
            float fx = gx - ix;
            float fz = gz - iz;
            height = Mathf.Lerp(Mathf.Lerp(h00, h10, fx), Mathf.Lerp(h01, h11, fx), fz);
            return true;
        }

        private struct GridCorners
        {
            public Heightmap.Biome Low;
            public Heightmap.Biome HighX;
            public Heightmap.Biome HighZ;
            public Heightmap.Biome High;
        }

        // HeightmapBuilder.Build for one vertex (not distant LOD), then TerrainComp.ApplyToHeightmap.
        private static float VertexHeight(WorldGenerator worldGen, TerrainData data, GridCorners corners, float x0, float z0,
            float scale, int width, int l, int k)
        {
            float wx = x0 + l * scale;
            float wz = z0 + k * scale;
            float baseHeight;
            if (corners.HighZ == corners.Low && corners.HighX == corners.Low && corners.High == corners.Low)
            {
                baseHeight = worldGen.GetBiomeHeight(corners.Low, wx, wz, out _);
            }
            else
            {
                float tx = Mathf.SmoothStep(0f, 1f, (float)l / width);
                float tz = Mathf.SmoothStep(0f, 1f, (float)k / width);
                float low = Mathf.LerpUnclamped(worldGen.GetBiomeHeight(corners.Low, wx, wz, out _),
                    worldGen.GetBiomeHeight(corners.HighX, wx, wz, out _), tx);
                float high = Mathf.LerpUnclamped(worldGen.GetBiomeHeight(corners.HighZ, wx, wz, out _),
                    worldGen.GetBiomeHeight(corners.High, wx, wz, out _), tx);
                baseHeight = Mathf.LerpUnclamped(low, high, tz);
            }

            int i = k * (width + 1) + l;
            float delta = data.LevelDelta[i] + data.SmoothDelta[i];
            if (delta == 0f) return baseHeight;
            return Mathf.Clamp(baseHeight + delta, baseHeight - MaxTerrainDelta, baseHeight + MaxTerrainDelta);
        }

        // Vanilla's TerrainComp.FindTerrainCompiler takes the first compiler inside the zone too.
        private static ZDO FindCompiler(Vector2s zone)
        {
            HealRunner.FindCompilers(zone, 0, s_compilers);
            ZDO result = null;
            foreach (ZDO zdo in s_compilers)
            {
                if (!ZoneSystem.GetZone(zdo.GetPosition()).Equals(zone)) continue;
                result = zdo;
                break;
            }
            s_compilers.Clear();
            return result;
        }

        private static float GridScale()
        {
            if (s_gridScale > 0f) return s_gridScale;
            ZoneSystem zoneSystem = ZoneSystem.instance;
            Heightmap heightmap = zoneSystem && zoneSystem.m_zonePrefab
                ? zoneSystem.m_zonePrefab.GetComponentInChildren<Heightmap>(true)
                : null;
            if (heightmap) s_gridScale = heightmap.m_scale;
            return s_gridScale;
        }

        // What ZNetView.Awake does for a new object, minus the instance: the server owns the new ZDO,
        // and whoever has the zone loaded (no one outside sleep mode) instantiates it like any new
        // ZDO; vanilla hands its ownership to a nearby peer. Planted at the pass's now, so in sleep
        // mode it starts growing at the morning the skip ends at.
        private static void PlantSapling(ZNetView view, int saplingHash, Vector3 pos, Quaternion rotation, Vector3 scale,
            long plantedSeconds)
        {
            ZDO zdo = ZDOMan.instance.CreateNewZDO(pos, saplingHash);
            zdo.Persistent = view.m_persistent;
            zdo.Type = view.m_type;
            zdo.Distant = view.m_distant;
            zdo.SetPrefab(saplingHash);
            zdo.SetRotation(rotation);
            if (view.m_syncInitialScale && scale != Vector3.one) zdo.Set(ZDOVars.s_scaleHash, scale);
            // Plant counts growth from this time; left at 0 it would start on first load instead.
            // Ticks of world time, as ZNet.GetTime gives them.
            zdo.Set(ZDOVars.s_plantTime, plantedSeconds * TimeSpan.TicksPerSecond);
        }

        private static void DestroySite(ZDO site)
        {
            s_blockedUntil.Remove(site.m_uid);
            HealRunner.Claim(site);
            ZDOMan.instance.DestroyZDO(site);
        }

        // Searches only the sectors the 1 m circle touches: usually just the site's own.
        private static bool HasStump(Vector3 pos, int stumpHash)
        {
            Vector2s lowZone = ZoneSystem.GetZone(new Vector3(pos.x - StumpRadius, 0f, pos.z - StumpRadius));
            Vector2s highZone = ZoneSystem.GetZone(new Vector3(pos.x + StumpRadius, 0f, pos.z + StumpRadius));
            ZDOMan zdoMan = ZDOMan.instance;
            var single = new SimulationDistance(0, 0, classic: true);
            try
            {
                for (int y = lowZone.y; y <= highZone.y; y++)
                {
                    for (int x = lowZone.x; x <= highZone.x; x++)
                    {
                        s_found.Clear();
                        zdoMan.FindSectorObjects(new Vector2s(x, y), single, s_found);
                        foreach (ZDO zdo in s_found)
                        {
                            if (zdo == null || !zdo.IsValid() || zdo.GetPrefab() != stumpHash) continue;
                            Vector3 other = zdo.GetPosition();
                            float dx = other.x - pos.x;
                            float dz = other.z - pos.z;
                            if (dx * dx + dz * dz <= StumpRadius * StumpRadius) return true;
                        }
                    }
                }
                return false;
            }
            finally
            {
                s_found.Clear();
            }
        }

        private static List<ProtectedCircle> CirclesFor(Vector2s zone)
        {
            if (s_circlesFrame == Time.frameCount && s_circlesZone.Equals(zone)) return s_circles;
            BaseIndex.Collect(zone, s_circles);
            s_circlesZone = zone;
            s_circlesFrame = Time.frameCount;
            return s_circles;
        }

        // A world time behind the felling (a restored save) counts as due, like the heal runner's last pass.
        private static bool IsDue(ZDO site, int now, float delayMinutes)
        {
            int felled = site.GetInt(RegrowthSites.FelledKey);
            return now < felled || now - felled >= delayMinutes;
        }

        private static bool IsSite(ZDO zdo) => zdo != null && zdo.IsValid() && zdo.GetPrefab() == RegrowthSites.MarkerHash;
    }
}
