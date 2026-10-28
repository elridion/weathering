using System;
using UnityEngine;

namespace Weathering
{
    /// <summary>
    /// A zone's saved terrain edits (the TerrainComp's TCData), decoded from and encoded to its ZDO
    /// without a TerrainComp, so the server can heal zones nobody has loaded. The byte layout is
    /// exactly what vanilla TerrainComp.Save writes and Load reads. Paint is only meaningful where
    /// ModifiedPaint is set; elsewhere the game uses the generated paint, which is not known here.
    /// </summary>
    internal class TerrainData
    {
        // Vanilla's terrainCompVersion; Load reads and ignores it.
        private const int SaveVersion = 1;

        public int Version;
        public int Operations;
        public Vector3 LastOpPoint;
        public float LastOpRadius;
        public bool[] ModifiedHeight;
        public float[] LevelDelta;
        public float[] SmoothDelta;
        public bool[] ModifiedPaint;
        public Color[] Paint;
        public int Pitch;

        /// <summary>
        /// Reads the ZDO's TCData for a grid of <paramref name="pitch"/> x <paramref name="pitch"/>
        /// vertices. False when there is none or it does not fit the grid (vanilla refuses it too).
        /// </summary>
        public static bool TryRead(ZDO zdo, int pitch, out TerrainData data)
        {
            data = null;
            if (zdo == null) return false;
            return TryRead(zdo.GetByteArray(ZDOVars.s_TCData), pitch, out data);
        }

        /// <summary>The same, on compressed TCData bytes (as received for an overnight heal).</summary>
        public static bool TryRead(byte[] compressed, int pitch, out TerrainData data)
        {
            data = null;
            if (compressed == null || pitch < 2) return false;

            int width = pitch - 1;
            int count = pitch * pitch;
            var result = new TerrainData
            {
                Pitch = pitch,
                ModifiedHeight = new bool[count],
                LevelDelta = new float[count],
                SmoothDelta = new float[count],
                ModifiedPaint = new bool[count],
                Paint = new Color[count],
            };

            try
            {
                var pkg = new ZPackage(Utils.Decompress(compressed));
                result.Version = pkg.ReadInt();
                result.Operations = pkg.ReadInt();
                result.LastOpPoint = pkg.ReadVector3();
                result.LastOpRadius = pkg.ReadSingle();

                if (pkg.ReadInt() != count) return false;
                for (int i = 0; i < count; i++)
                {
                    result.ModifiedHeight[i] = pkg.ReadBool();
                    if (!result.ModifiedHeight[i]) continue;
                    result.LevelDelta[i] = pkg.ReadSingle();
                    result.SmoothDelta[i] = pkg.ReadSingle();
                }

                // Vanilla reads any paint count up to the array size; more would overflow there too.
                int paintCount = pkg.ReadInt();
                if (paintCount < 0 || paintCount > count) return false;
                for (int j = 0; j < paintCount; j++)
                {
                    result.ModifiedPaint[j] = pkg.ReadBool();
                    if (!result.ModifiedPaint[j]) continue;
                    result.Paint[j] = new Color(pkg.ReadSingle(), pkg.ReadSingle(), pkg.ReadSingle(), pkg.ReadSingle());
                }

                if (paintCount == width * width) RemapLegacyPaint(result, width);
            }
            catch (Exception)
            {
                // Truncated or undecompressable data: vanilla would throw on it as well.
                return false;
            }

            data = result;
            return true;
        }

        /// <summary>Writes the data back the way vanilla TerrainComp.Save does.</summary>
        public void Write(ZDO zdo) => zdo.Set(ZDOVars.s_TCData, Encode());

        /// <summary>The compressed TCData bytes vanilla TerrainComp.Save would write.</summary>
        public byte[] Encode()
        {
            var pkg = new ZPackage();
            pkg.Write(SaveVersion);
            pkg.Write(Operations);
            pkg.Write(LastOpPoint);
            pkg.Write(LastOpRadius);
            pkg.Write(ModifiedHeight.Length);
            for (int i = 0; i < ModifiedHeight.Length; i++)
            {
                pkg.Write(ModifiedHeight[i]);
                if (!ModifiedHeight[i]) continue;
                pkg.Write(LevelDelta[i]);
                pkg.Write(SmoothDelta[i]);
            }
            pkg.Write(ModifiedPaint.Length);
            for (int j = 0; j < ModifiedPaint.Length; j++)
            {
                pkg.Write(ModifiedPaint[j]);
                if (!ModifiedPaint[j]) continue;
                pkg.Write(Paint[j].r);
                pkg.Write(Paint[j].g);
                pkg.Write(Paint[j].b);
                pkg.Write(Paint[j].a);
            }
            return Utils.Compress(pkg.GetArray());
        }

        // Old saves stored paint on a width x width grid. This is vanilla Load's remap, line for line,
        // so the result is exactly what the game would show after loading the zone.
        private static void RemapLegacyPaint(TerrainData data, int width)
        {
            Color[] paint = (Color[])data.Paint.Clone();
            bool[] modified = (bool[])data.ModifiedPaint.Clone();
            int pitch = width + 1;
            for (int k = 0; k < data.Paint.Length; k++)
            {
                int row = k / pitch;
                int nextRow = (k + 1) / pitch;
                int source = k - row;
                if (row == width) source -= width;
                if (k > 0 && (k - row) % width == 0 && (k + 1 - nextRow) % width == 0) source--;
                data.Paint[k] = paint[source];
                data.ModifiedPaint[k] = modified[source];
            }
        }
    }
}
