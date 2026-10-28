using System;
using HarmonyLib;
using UnityEngine;

namespace Weathering
{
    /// <summary>
    /// Stamps every vertex a terrain edit changed with the time of the edit. InternalDoOperation runs
    /// once per edit, only on the zone owner, so the arrays are snapshotted before it and diffed after
    /// it; diffing catches whatever the operation did internally. Paint spilled into a neighbouring
    /// zone at a border bypasses that zone's InternalDoOperation and is not stamped here.
    /// </summary>
    [HarmonyPatch(typeof(TerrainComp), "InternalDoOperation")]
    internal static class TerrainPatches
    {
        private static readonly AccessTools.FieldRef<TerrainComp, float[]> s_levelDelta =
            AccessTools.FieldRefAccess<TerrainComp, float[]>("m_levelDelta");
        private static readonly AccessTools.FieldRef<TerrainComp, float[]> s_smoothDelta =
            AccessTools.FieldRefAccess<TerrainComp, float[]>("m_smoothDelta");

        // Snapshot buffers, reused across edits and resized only when the vertex count changes.
        private static float[] s_levelBefore = new float[0];
        private static float[] s_smoothBefore = new float[0];
        private static Color[] s_paintBefore = new Color[0];

        private static void Prefix(TerrainComp __instance, out bool __state)
        {
            __state = false;
            if (!__instance.IsOwner()) return;

            float[] level = s_levelDelta(__instance);
            float[] smooth = s_smoothDelta(__instance);
            Color[] paint = StampStore.s_paintMask(__instance);
            if (level == null || smooth == null || paint == null) return;

            Copy(level, ref s_levelBefore);
            Copy(smooth, ref s_smoothBefore);
            Copy(paint, ref s_paintBefore);
            __state = true;
        }

        private static void Postfix(TerrainComp __instance, bool __state)
        {
            if (!__state) return;

            float[] level = s_levelDelta(__instance);
            float[] smooth = s_smoothDelta(__instance);
            Color[] paint = StampStore.s_paintMask(__instance);
            int pitch = StampStore.s_pitch(__instance);
            int count = pitch * pitch;
            if (level.Length != count || smooth.Length != count || paint.Length != count) return;
            if (s_levelBefore.Length != count || s_paintBefore.Length != count) return;

            int[] stamps = null;
            // A stamp of 0 means "none", so an edit in a world's first minute is stored as minute 1.
            int now = Math.Max(StampStore.Now(), 1);
            for (int i = 0; i < count; i++)
            {
                if (level[i] == s_levelBefore[i] && smooth[i] == s_smoothBefore[i] && SamePaint(paint[i], s_paintBefore[i]))
                    continue;
                if (stamps == null) stamps = StampStore.Load(__instance);
                stamps[i] = now;
            }
            if (stamps == null) return;

            // Older edits in this zone that predate the mod start their delay from here as well.
            StampStore.EnsureStamped(__instance, stamps, now);
            StampStore.Save(__instance, stamps);
            StampStore.SetPending(__instance, true);
        }

        private static void Copy<T>(T[] source, ref T[] buffer)
        {
            if (buffer.Length != source.Length) buffer = new T[source.Length];
            Array.Copy(source, buffer, source.Length);
        }

        // Exact comparison: Color's == tolerates tiny differences, and a faint stroke still counts.
        private static bool SamePaint(Color a, Color b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
    }
}
