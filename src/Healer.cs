using System;
using UnityEngine;

namespace Weathering
{
    /// <summary>
    /// Pure healing math on plain arrays: paint fade and offset-field erosion. It never touches the
    /// scene, ZDOs or TerrainComp, so it is safe on a dedicated server; callers own the masks.
    /// </summary>
    internal static class Healer
    {
        // Erosion tuning.
        public const float TalusSlope = 0.35f;      // m per m; gentler slopes are left alone so shapes settle
        public const float ResetEpsilon = 0.02f;    // offsets smaller than this snap back to vanilla
        public const int IterationsPerDay = 24;
        public const int MaxIterationsPerPass = 240;

        // Rate the runner passes to Erode. 0.25 is only the stability limit of linear diffusion; with
        // the talus threshold a vertex facing a neighbour with more steep pairs can invert (overshoot)
        // above 1 / (2 * 4 neighbours), leaving a raised centre in a pit. 0.125 settles monotonically.
        public const float DiffusionRate = 0.125f;
        const float MaxRate = DiffusionRate;

        // Paved vertices (b above this) never fade.
        const float PavedThreshold = 0.5f;

        // Snapshot of the offsets at the start of an iteration, reused between calls.
        static float[] s_scratch = new float[0];

        public static bool IsPaintPending(Color c) => c.b <= PavedThreshold && (c.r > 0f || c.g > 0f);

        /// <summary>
        /// Moves dirt (r) and cultivated (g) paint toward 0 by each vertex's own entry in
        /// <paramref name="amounts"/> on unfrozen, non-Deep-North vertices, so every spot fades only for
        /// the time since its own delay ran out. Returns true while any such vertex still holds paint.
        /// </summary>
        public static bool FadePaint(Color[] paint, bool[] modifiedPaint, bool[] frozen, bool[] deepNorth, float[] amounts)
        {
            bool pending = false;
            for (int i = 0; i < paint.Length; i++)
            {
                if (!modifiedPaint[i] || frozen[i] || deepNorth[i]) continue;
                float amount = amounts[i];
                if (!(amount > 0f)) amount = 0f; // also catches NaN
                Color c = paint[i];
                if (float.IsNaN(c.r) || float.IsNaN(c.g) || float.IsNaN(c.b)) continue;
                if (c.b > PavedThreshold) continue;

                c.r = Mathf.Max(0f, c.r - amount);
                c.g = Mathf.Max(0f, c.g - amount);
                paint[i] = c;

                if (c.r <= 0f && c.g <= 0f && c.b <= 0f) modifiedPaint[i] = false;
                else if (IsPaintPending(c)) pending = true;
            }
            return pending;
        }

        /// <summary>
        /// Diffuses the offset field in place. A neighbour pair exchanges material only when its slope
        /// exceeds TalusSlope, in proportion to the excess. Frozen (and NaN) vertices stay fixed and act
        /// as boundaries; neighbours outside the grid are ignored. Returns true if any pair with an
        /// unfrozen vertex can still move.
        /// </summary>
        public static bool Erode(float[] offset, bool[] frozen, int pitch, int iterations, float rate)
        {
            int count = offset.Length;
            if (pitch <= 0 || count % pitch != 0 || frozen.Length != count) return false;
            int height = count / pitch;
            rate = rate > 0f ? Mathf.Min(rate, MaxRate) : 0f; // also catches NaN

            if (s_scratch.Length < count) s_scratch = new float[count];
            float[] snap = s_scratch;

            if (rate > 0f)
                for (int it = 0; it < iterations; it++)
            {
                Array.Copy(offset, snap, count);
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < pitch; x++)
                    {
                        int a = y * pitch + x;
                        if (x + 1 < pitch) Exchange(offset, snap, frozen, a, a + 1, rate);
                        if (y + 1 < height) Exchange(offset, snap, frozen, a, a + pitch, rate);
                    }
                }
            }

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < pitch; x++)
                {
                    int a = y * pitch + x;
                    if (x + 1 < pitch && CanMove(offset, frozen, a, a + 1)) return true;
                    if (y + 1 < height && CanMove(offset, frozen, a, a + pitch)) return true;
                }
            }
            return false;
        }

        /// <summary>Snaps unfrozen offsets with |d| below ResetEpsilon to 0 and clears their height flag.</summary>
        public static void ZeroTiny(float[] offset, bool[] modifiedHeight, bool[] frozen)
        {
            for (int i = 0; i < offset.Length; i++)
            {
                if (frozen[i]) continue;
                if (Mathf.Abs(offset[i]) >= ResetEpsilon) continue; // NaN compares false and falls through
                offset[i] = 0f;
                modifiedHeight[i] = false;
            }
        }

        // Both sides read the pre-iteration snapshot, so a pair moves equal and opposite amounts.
        static void Exchange(float[] offset, float[] snap, bool[] frozen, int a, int b, float rate)
        {
            float diff = snap[b] - snap[a];
            if (float.IsNaN(diff)) return;
            float abs = Mathf.Abs(diff);
            if (abs <= TalusSlope) return;

            float flow = rate * (abs - TalusSlope) * Mathf.Sign(diff);
            if (!frozen[a]) offset[a] += flow;
            if (!frozen[b]) offset[b] -= flow;
        }

        static bool CanMove(float[] offset, bool[] frozen, int a, int b)
        {
            if (frozen[a] && frozen[b]) return false;
            float diff = offset[b] - offset[a];
            return !float.IsNaN(diff) && Mathf.Abs(diff) > TalusSlope;
        }
    }
}
