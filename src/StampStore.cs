using System;
using HarmonyLib;
using UnityEngine;

namespace Weathering
{
    /// <summary>
    /// Keeps the per-vertex edit times and the "needs healing" flag on a TerrainComp's own ZDO.
    /// Stamps are whole in-game minutes since world time 0, with 0 meaning "no stamp", so a stamp
    /// taken in the first minute of a world is written as 1 instead of being lost.
    /// </summary>
    internal static class StampStore
    {
        // Persisted keys: never rename once shipped.
        private static readonly int s_stampsKey = "weathering_stamps".GetStableHashCode();
        private static readonly int s_pendingKey = "weathering_pending".GetStableHashCode();

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

        public static int[] Load(TerrainComp tc)
        {
            int count = s_pitch(tc) * s_pitch(tc);
            var stamps = new int[count];
            ZDO zdo = GetZDO(tc);
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
            ZDO zdo = GetZDO(tc);
            if (zdo == null) return;

            int length = stamps.Length * sizeof(int);
            if (s_saveBuffer.Length != length) s_saveBuffer = new byte[length];
            Buffer.BlockCopy(stamps, 0, s_saveBuffer, 0, length);
            zdo.Set(s_stampsKey, Utils.Compress(s_saveBuffer));
        }

        public static void EnsureStamped(TerrainComp tc, int[] stamps, int now)
        {
            bool[] modifiedHeight = s_modifiedHeight(tc);
            bool[] modifiedPaint = s_modifiedPaint(tc);
            Color[] paintMask = s_paintMask(tc);
            if (modifiedHeight == null || modifiedPaint == null || paintMask == null) return;

            int stamp = Math.Max(now, 1);
            int count = Math.Min(stamps.Length, modifiedHeight.Length);
            for (int i = 0; i < count; i++)
            {
                if (stamps[i] != 0) continue;
                // Edits made before the mod was installed have no stamp; they start their delay now.
                if (modifiedHeight[i] || (modifiedPaint[i] && (paintMask[i].r > 0f || paintMask[i].g > 0f)))
                    stamps[i] = stamp;
            }
            SetPending(tc, true);
        }

        public static bool IsPending(TerrainComp tc)
        {
            ZDO zdo = GetZDO(tc);
            return zdo != null && zdo.GetBool(s_pendingKey);
        }

        public static void SetPending(TerrainComp tc, bool value)
        {
            if (!tc.IsOwner()) return;
            ZDO zdo = GetZDO(tc);
            if (zdo == null) return;
            zdo.Set(s_pendingKey, value);
        }

        public static int Now() => (int)(ZNet.instance.GetTimeSeconds() / 60);

        private static ZDO GetZDO(TerrainComp tc)
        {
            ZNetView nview = s_nview(tc);
            if (!nview || !nview.IsValid()) return null;
            return nview.GetZDO();
        }
    }
}
