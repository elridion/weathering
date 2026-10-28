using System;
using HarmonyLib;
using UnityEngine;

namespace Weathering
{
    /// <summary>
    /// Keeps the per-vertex edit times, the "needs healing" flag and the time of the last heal pass on
    /// a terrain compiler's own ZDO. Stamps are whole in-game minutes since world time 0, with 0
    /// meaning "no stamp", so a stamp taken in the first minute of a world is written as 1 instead of
    /// being lost. The TerrainComp overloads write only on the owner; the ZDO overloads never check
    /// ownership, because the heal runner claims the ZDO before it writes.
    /// </summary>
    internal static class StampStore
    {
        // Persisted keys: never rename once shipped.
        private static readonly int s_stampsKey = "weathering_stamps".GetStableHashCode();
        private static readonly int s_pendingKey = "weathering_pending".GetStableHashCode();
        private static readonly int s_lastKey = "weathering_last".GetStableHashCode();

        internal static readonly AccessTools.FieldRef<TerrainComp, ZNetView> s_nview =
            AccessTools.FieldRefAccess<TerrainComp, ZNetView>("m_nview");
        internal static readonly AccessTools.FieldRef<TerrainComp, int> s_pitch =
            AccessTools.FieldRefAccess<TerrainComp, int>("m_pitch");
        internal static readonly AccessTools.FieldRef<TerrainComp, bool[]> s_modifiedHeight =
            AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedHeight");
        internal static readonly AccessTools.FieldRef<TerrainComp, bool[]> s_modifiedPaint =
            AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedPaint");
        internal static readonly AccessTools.FieldRef<TerrainComp, Color[]> s_paintMask =
            AccessTools.FieldRefAccess<TerrainComp, Color[]>("m_paintMask");

        // Reused for the int-to-byte copy before compressing; Utils.Compress takes the whole array,
        // so it is resized whenever the vertex count changes.
        private static byte[] s_saveBuffer = new byte[0];

        public static int[] Load(TerrainComp tc) => Load(GetZDO(tc), s_pitch(tc) * s_pitch(tc));

        public static int[] Load(ZDO zdo, int count) => Load(zdo, new int[count]);

        /// <summary>Loads the stamps into <paramref name="stamps"/>, all 0 when there are none; returns it.</summary>
        public static int[] Load(ZDO zdo, int[] stamps)
        {
            int count = stamps.Length;
            Array.Clear(stamps, 0, count);
            if (zdo == null) return stamps;

            byte[] compressed = zdo.GetByteArray(s_stampsKey);
            if (compressed == null) return stamps;

            byte[] bytes;
            try
            {
                bytes = Utils.Decompress(compressed);
            }
            catch (Exception)
            {
                // Corrupt data must not break terrain loading; starting over only delays healing.
                return stamps;
            }

            if (bytes == null || bytes.Length != count * sizeof(int)) return stamps;
            Buffer.BlockCopy(bytes, 0, stamps, 0, bytes.Length);
            return stamps;
        }

        public static void Save(TerrainComp tc, int[] stamps)
        {
            if (!tc.IsOwner()) return;
            Save(GetZDO(tc), stamps);
        }

        public static void Save(ZDO zdo, int[] stamps)
        {
            if (zdo == null) return;
            SaveEncoded(zdo, Encode(stamps));
        }

        /// <summary>The stamps in their saved form, for sending to the zone owner.</summary>
        public static byte[] Encode(int[] stamps)
        {
            int length = stamps.Length * sizeof(int);
            if (s_saveBuffer.Length != length) s_saveBuffer = new byte[length];
            Buffer.BlockCopy(stamps, 0, s_saveBuffer, 0, length);
            return Utils.Compress(s_saveBuffer);
        }

        /// <summary>Saves stamps already in their saved form (from <see cref="Encode"/>).</summary>
        public static void SaveEncoded(ZDO zdo, byte[] encoded)
        {
            if (zdo == null) return;
            zdo.Set(s_stampsKey, encoded);
        }

        public static void EnsureStamped(TerrainComp tc, int[] stamps, int now)
        {
            bool[] modifiedHeight = s_modifiedHeight(tc);
            bool[] modifiedPaint = s_modifiedPaint(tc);
            Color[] paintMask = s_paintMask(tc);
            if (modifiedHeight == null || modifiedPaint == null || paintMask == null) return;

            EnsureStamped(modifiedHeight, modifiedPaint, paintMask, stamps, now);
            SetPending(tc, true);
        }

        /// <summary>Same as the TerrainComp overload, on decoded data; the caller sets the pending flag.</summary>
        public static void EnsureStamped(TerrainData data, int[] stamps, int now) =>
            EnsureStamped(data.ModifiedHeight, data.ModifiedPaint, data.Paint, stamps, now);

        /// <summary>True when a vertex holds an edit that can still heal and so needs a stamp.</summary>
        public static bool NeedsStamp(bool modifiedHeight, bool modifiedPaint, Color paint) =>
            modifiedHeight || (modifiedPaint && (paint.r > 0f || paint.g > 0f));

        public static bool IsPending(TerrainComp tc) => IsPending(GetZDO(tc));

        public static bool IsPending(ZDO zdo) => zdo != null && zdo.GetBool(s_pendingKey);

        public static void SetPending(TerrainComp tc, bool value)
        {
            if (!tc.IsOwner()) return;
            SetPending(GetZDO(tc), value);
        }

        public static void SetPending(ZDO zdo, bool value)
        {
            if (zdo == null) return;
            zdo.Set(s_pendingKey, value);
        }

        /// <summary>World time in whole seconds of the zone's last heal pass; 0 when never seen.</summary>
        public static long GetLast(ZDO zdo) => zdo == null ? 0L : zdo.GetLong(s_lastKey);

        public static void SetLast(ZDO zdo, long seconds)
        {
            if (zdo == null) return;
            zdo.Set(s_lastKey, seconds);
        }

        public static int Now() => (int)(ZNet.instance.GetTimeSeconds() / 60);

        private static void EnsureStamped(bool[] modifiedHeight, bool[] modifiedPaint, Color[] paint, int[] stamps, int now)
        {
            int stamp = Math.Max(now, 1);
            int count = Math.Min(stamps.Length, Math.Min(modifiedHeight.Length, Math.Min(modifiedPaint.Length, paint.Length)));
            for (int i = 0; i < count; i++)
            {
                if (stamps[i] != 0) continue;
                // Edits made before the mod was installed have no stamp; they start their delay now.
                if (NeedsStamp(modifiedHeight[i], modifiedPaint[i], paint[i])) stamps[i] = stamp;
            }
        }

        private static ZDO GetZDO(TerrainComp tc)
        {
            ZNetView nview = s_nview(tc);
            if (!nview || !nview.IsValid()) return null;
            return nview.GetZDO();
        }
    }
}
