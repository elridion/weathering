using System;
using UnityEngine;

namespace Weathering
{
    /// <summary>
    /// Pure healing math on plain arrays: grass spread and offset-field erosion. It never touches the
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

        // Grass spread tuning.
        public const float SoftEdge = 1f;           // m; band ahead of the grass front that is only partly reclaimed
        public const float FallbackDays = 10f;      // in-game days past its delay before dirt grass has not reached fades anyway
        public const float FallbackFadeDays = 2f;   // in-game days that fallback fade takes (the old FadeDays default)
        // Dirt or cultivated weight up to this counts as grass: vanilla's brush edges leave faint tints.
        const float SourceEpsilon = 0.01f;
        // Forward and backward chamfer sweeps; more than one pair only matters for a front that has to
        // wind around paved or protected ground, and a sweep that changes nothing ends early.
        const int MaxDistanceSweeps = 4;
        const float Sqrt2 = 1.41421356f;

        // Vertices the grass front can pass through (sources and reclaimable dirt), reused between calls.
        static bool[] s_passable = new bool[0];

        public static bool IsPaintPending(Color c) => c.b <= PavedThreshold && (c.r > 0f || c.g > 0f);

        /// <summary>
        /// True for a vertex grass can spread from: unpainted, or painted with no dirt or cultivated
        /// weight (the cultivator's grass) and not paved. In the Deep North g holds snow depth, so only
        /// dirt counts there.
        /// </summary>
        public static bool IsGrassSource(Color c, bool modifiedPaint, bool deepNorth)
        {
            if (!modifiedPaint) return true;
            if (float.IsNaN(c.r) || float.IsNaN(c.g) || float.IsNaN(c.b)) return false;
            if (c.b > PavedThreshold) return false;
            return c.r <= SourceEpsilon && (deepNorth || c.g <= SourceEpsilon);
        }

        /// <summary>
        /// Grows grass back into dirt (r) and cultivated (g) paint from its edges. Grass sources
        /// (IsGrassSource) count whether frozen or not, so protected grass seeds the front. Only
        /// reclaimable vertices (unfrozen, outside the Deep North, not paved, holding r or g) carry it
        /// further; frozen dirt, Deep North dirt and paved ground block it, so grass never jumps through
        /// fresh or protected dirt. <paramref name="distance"/> holds on entry each vertex's distance in
        /// meters to grass beyond the grid (infinity for none) and on return its distance to the nearest
        /// grass. A reclaimable vertex within its own <paramref name="reach"/> becomes grass; up to
        /// SoftEdge further it loses that fraction of its weight, so the front is ragged. Each vertex also
        /// fades by its <paramref name="fallback"/> amount, for dirt grass cannot reach. Alpha (the
        /// vegetation mask) is never touched. Returns true while any reclaimable vertex still holds paint.
        /// </summary>
        public static bool SpreadGrass(Color[] paint, bool[] modifiedPaint, bool[] frozen, bool[] deepNorth,
            float[] reach, float[] fallback, float[] distance, int pitch, float spacing)
        {
            int count = paint.Length;
            if (pitch <= 0 || count % pitch != 0 || !(spacing > 0f)) return false;
            if (modifiedPaint.Length != count || frozen.Length != count || deepNorth.Length != count ||
                reach.Length != count || fallback.Length != count || distance.Length != count) return false;
            int height = count / pitch;

            if (s_passable.Length < count) s_passable = new bool[count];
            bool[] passable = s_passable;
            for (int i = 0; i < count; i++)
            {
                if (IsGrassSource(paint[i], modifiedPaint[i], deepNorth[i]))
                {
                    passable[i] = true;
                    distance[i] = 0f;
                    continue;
                }
                passable[i] = IsReclaimable(paint, modifiedPaint, frozen, deepNorth, i);
                if (!passable[i] || float.IsNaN(distance[i])) distance[i] = float.PositiveInfinity;
            }

            float straight = spacing;
            float diagonal = spacing * Sqrt2;
            for (int sweep = 0; sweep < MaxDistanceSweeps; sweep++)
            {
                bool changed = false;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < pitch; x++)
                    {
                        int i = y * pitch + x;
                        if (!passable[i]) continue;
                        float d = distance[i];
                        if (x > 0) d = Mathf.Min(d, distance[i - 1] + straight);
                        if (y > 0)
                        {
                            d = Mathf.Min(d, distance[i - pitch] + straight);
                            if (x > 0) d = Mathf.Min(d, distance[i - pitch - 1] + diagonal);
                            if (x + 1 < pitch) d = Mathf.Min(d, distance[i - pitch + 1] + diagonal);
                        }
                        if (d < distance[i])
                        {
                            distance[i] = d;
                            changed = true;
                        }
                    }
                }
                for (int y = height - 1; y >= 0; y--)
                {
                    for (int x = pitch - 1; x >= 0; x--)
                    {
                        int i = y * pitch + x;
                        if (!passable[i]) continue;
                        float d = distance[i];
                        if (x + 1 < pitch) d = Mathf.Min(d, distance[i + 1] + straight);
                        if (y + 1 < height)
                        {
                            d = Mathf.Min(d, distance[i + pitch] + straight);
                            if (x + 1 < pitch) d = Mathf.Min(d, distance[i + pitch + 1] + diagonal);
                            if (x > 0) d = Mathf.Min(d, distance[i + pitch - 1] + diagonal);
                        }
                        if (d < distance[i])
                        {
                            distance[i] = d;
                            changed = true;
                        }
                    }
                }
                if (!changed) break;
            }

            bool pending = false;
            for (int i = 0; i < count; i++)
            {
                if (!IsReclaimable(paint, modifiedPaint, frozen, deepNorth, i)) continue;
                // Full within reach, then falling off over SoftEdge. The front advances the whole reach
                // each pass; a band subtracted from the reach would slow it down by SoftEdge a day.
                // Below one vertex spacing a day the weight goes in steps, so slow spread still arrives.
                float amount = 0f;
                if (reach[i] > 0f) amount = (reach[i] + SoftEdge - distance[i]) / SoftEdge;
                if (fallback[i] > amount) amount = fallback[i];

                Color c = paint[i];
                if (amount > 0f)
                {
                    c.r = Mathf.Max(0f, c.r - amount);
                    c.g = Mathf.Max(0f, c.g - amount);
                    paint[i] = c;
                }

                // Only paint that went here goes back to the generated mask. A vertex with no dirt or
                // cultivated weight to begin with (the cultivator's grass paints 0,0,0) is not
                // reclaimable; clearing it would swap the generated paint and vegetation alpha back in.
                if (c.r <= 0f && c.g <= 0f && c.b <= 0f) modifiedPaint[i] = false;
                else if (IsPaintPending(c)) pending = true;
            }
            return pending;
        }

        // A vertex the grass may take back: painted dirt or cultivated, unfrozen, not paved, and outside
        // the Deep North (g is snow depth there).
        static bool IsReclaimable(Color[] paint, bool[] modifiedPaint, bool[] frozen, bool[] deepNorth, int i)
        {
            if (!modifiedPaint[i] || frozen[i] || deepNorth[i]) return false;
            Color c = paint[i];
            if (float.IsNaN(c.r) || float.IsNaN(c.g) || float.IsNaN(c.b)) return false;
            return IsPaintPending(c);
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
