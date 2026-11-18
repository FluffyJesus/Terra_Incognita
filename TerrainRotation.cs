using UnityEngine;

namespace TerraIncognita
{
    /// <summary>
    /// Terrain renders and collides axis-aligned from its transform position, ignoring rotation.
    /// To turn an island by quarter*90° (root yaw, Unity's clockwise-from-above) we rotate a per-island copy of
    /// the TerrainData by the same angle and re-anchor the terrain so its centre stays where the rotated
    /// root expects it.
    ///
    /// Yaw +90°: local (x, z) → world (z, -x). In grid terms (row j = z, column i = x, N = resolution):
    ///   new[j', i'] = old[i', N-1-j']   — applied <c>quarter</c> times.
    /// </summary>
    internal static class TerrainRotation
    {
        public static void Apply(Transform root, int quarter)
        {
            quarter &= 3;
            foreach (var t in root.GetComponentsInChildren<Terrain>(true))
            {
                var src = t.terrainData;
                if (!src) continue;

                // Authored centre in root-local axes. InverseTransformPoint undoes the root's yaw, so the corner
                // comes back as authored; terrain size is in world units → divide by the root scale.
                var size = src.size;
                var rs = root.lossyScale;
                Vector3 cornerLocal = root.InverseTransformPoint(t.transform.position);
                Vector3 centreLocal = cornerLocal + new Vector3(size.x / 2f / rs.x, 0f, size.z / 2f / rs.z);

                // always a per-island copy: layers get swapped and detail grass painted on it afterwards
                {
                    var td = Object.Instantiate(src);           // never touch the shared bundle asset
                    td.name = src.name + " rot" + quarter * 90;

                    int hr = td.heightmapResolution;
                    var h = td.GetHeights(0, 0, hr, hr);
                    for (int k = 0; k < quarter; k++) h = Rot(h);
                    td.SetHeights(0, 0, h);

                    int ar = td.alphamapResolution;
                    if (td.alphamapLayers > 0)
                    {
                        var a = td.GetAlphamaps(0, 0, ar, ar);
                        for (int k = 0; k < quarter; k++) a = Rot(a);
                        td.SetAlphamaps(0, 0, a);
                    }
                    if ((quarter & 1) == 1) td.size = new Vector3(size.z, size.y, size.x);

                    t.terrainData = td;
                    var col = t.GetComponent<TerrainCollider>();
                    if (col) col.terrainData = td;
                    size = td.size;
                }

                // re-anchor: world centre = root applied to the authored centre; terrain origin = centre - half size
                Vector3 centreWorld = root.TransformPoint(centreLocal);
                t.transform.position = new Vector3(centreWorld.x - size.x / 2f, t.transform.position.y, centreWorld.z - size.z / 2f);
            }
        }

        private static float[,] Rot(float[,] a)
        {
            int rows = a.GetLength(0), cols = a.GetLength(1);
            var r = new float[cols, rows];
            for (int j = 0; j < cols; j++)
                for (int i = 0; i < rows; i++)
                    r[j, i] = a[i, cols - 1 - j];
            return r;
        }

        private static float[,,] Rot(float[,,] a)
        {
            int rows = a.GetLength(0), cols = a.GetLength(1), layers = a.GetLength(2);
            var r = new float[cols, rows, layers];
            for (int j = 0; j < cols; j++)
                for (int i = 0; i < rows; i++)
                    for (int l = 0; l < layers; l++)
                        r[j, i, l] = a[i, cols - 1 - j, l];
            return r;
        }
    }
}
