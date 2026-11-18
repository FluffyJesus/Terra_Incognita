using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace TerraIncognita
{
    /// <summary>
    /// Grass, ferns and flowers on vanilla islands are terrain *detail* objects. Every island terrain lives in the
    /// main scene (always loaded), so all of the game's detail prototypes are available at runtime. Each island type
    /// gets a palette of them (config "Vegetation"), planted wherever our splat map says "Grass Leaves".
    /// No game textures go into the bundle.
    ///
    /// Prototype names (from another modder's survey): LGW_d25 tall blade grass, LGW_d46 feathery ferns (commonest),
    /// LGW_d43 sprawling grass, LGW_d47 big fern rosette, LGW_d34/d35/d41 stalks with buds/seed heads,
    /// LGW_d36 small leafy bush, LGW_d37-39 leafy sprigs, LGW_d40/d45 plumes, LGW_d42 white puff,
    /// LGW_d44 yellow spider flower, LGW_d29 blue bell, LGW_d33 pink spider-lily, LGW_d31 orange flame tuft.
    /// </summary>
    internal static class DetailGrass
    {
        private const string GrassLayer = "Grass Leaves";
        private const string Pebble = "rock_01_LOD (TERRAIN)";
        private static readonly HashSet<string> DrySand = new HashSet<string> { "Sand Cracked", "Sand Rocky" };
        private const float DetailCellMetres = 1.2f;     // grass cell size, independent of island size (was a fixed 256 cells)

        private static Dictionary<string, DetailPrototype> catalog;
        private static Terrain vanillaRef;

        // ------------------------------------------------------------------ live tuning (GrassWindow)
        // Per-prototype multiplier from config "GrassOverrides" ("LGW_d42:0, LGW_d46:0.5"; 0 = off). "solo" shows a
        // single prototype on every island so it can be recognised on screen.
        internal static readonly Dictionary<string, float> Overrides = new Dictionary<string, float>();
        internal static string Solo;

        // grass types that are really seaweed: planted under water by the shore instead (config [Vegetation] Seaweed)
        internal static readonly HashSet<string> Seaweed = new HashSet<string>();

        internal static void LoadOverrides()
        {
            Overrides.Clear();
            foreach (var (name, w) in ParseSpec(Plugin.GrassOverrides.Value)) Overrides[name] = Mathf.Max(0f, w);
            Seaweed.Clear();
            foreach (var s in (Plugin.Seaweed.Value ?? "").Split(',')) if (s.Trim().Length > 0) Seaweed.Add(s.Trim());
        }

        internal static string SaveSeaweed() => string.Join(", ", Seaweed.OrderBy(s => s).ToArray());

        internal static string SaveOverrides() => string.Join(", ", Overrides.Where(kv => Mathf.Abs(kv.Value - 1f) > 0.001f)
            .OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value.ToString("0.##", CultureInfo.InvariantCulture)}").ToArray());

        internal static float Multiplier(string key)
        {
            if (Solo != null) return key == Solo ? 1f : 0f;
            return key != null && Overrides.TryGetValue(key, out var m) ? m : 1f;
        }

        /// <summary>Prototype names per region that islands in this session use (for the window).</summary>
        internal static IEnumerable<(Climate region, string key, float weight)> PaletteEntries() =>
            palettes.SelectMany(kv => kv.Value.Select(p => (kv.Key, Key(p.proto), p.weight)));

        /// <summary>Plant again on every Terra Incognita terrain with the current overrides.</summary>
        internal static int ReapplyAll()
        {
            int n = 0;
            foreach (var s in Object.FindObjectsOfType<GrassState>())
            {
                var t = s.GetComponent<Terrain>();
                if (!t) continue;
                Apply(t, s.seed, s.type, s.region);
                n++;
            }
            return n;
        }

        public static void Apply(Terrain t, int seed, IslandType type, Climate region)
        {
            var rng = new System.Random(seed);
            var state = t.GetComponent<GrassState>();
            if (!state) state = t.gameObject.AddComponent<GrassState>();
            state.seed = seed; state.type = type; state.region = region;
            var td = t.terrainData;
            if (!td || td.terrainLayers == null) return;
            int grass = System.Array.FindIndex(td.terrainLayers, l => l && l.name == GrassLayer);
            if (grass < 0) return;

            var palette = Palette(region);
            if (palette.Count == 0) return;
            // weights are shares inside the region's palette; the island type sets how thick the cover is
            float density = IslandTypes.Info[type].GrassDensity / Mathf.Max(0.01f, palette.Sum(p => p.weight));
            if (region == Climate.Asian || region == Climate.Oceanic) density *= Plugin.LushGrassDensity.Value;   // jungle

            // prototypes = region palette (+ the reed for pond shores, if this island has ponds and the palette lacks it)
            var protos = palette.Select(p => p.proto).ToList();
            shores.TryGetValue(td, out var pondShores);
            int reed = -1;
            if (pondShores != null && pondShores.Count > 0 && catalog.TryGetValue(Plugin.PondReeds.Value.Trim(), out var reedProto))
            {
                reed = protos.FindIndex(pr => Key(pr) == Key(reedProto));
                if (reed < 0) { protos.Add(reedProto); reed = protos.Count - 1; }
            }
            // the oases' lush palette (the Asian one; already lush islands keep their own), its prototypes added
            oases.TryGetValue(td, out var oasisList);
            var lush = new List<(int idx, float weight)>();
            if (oasisList != null && oasisList.Count > 0)
            {
                var lp = region == Climate.Asian || region == Climate.Oceanic ? palette : Palette(Climate.Asian);
                foreach (var (proto, w) in lp)
                {
                    if (Key(proto) == Pebble || Seaweed.Contains(Key(proto))) continue;
                    int i = protos.FindIndex(pr => Key(pr) == Key(proto));
                    if (i < 0) { protos.Add(proto); i = protos.Count - 1; }
                    lush.Add((i, w));
                }
            }
            float lushDensity = IslandTypes.Info[type].GrassDensity * 1.8f / Mathf.Max(0.01f, lush.Sum(l => l.weight));
            td.detailPrototypes = protos.ToArray();
            if (vanillaRef)
            {
                var src = vanillaRef.terrainData;
                td.wavingGrassAmount = src.wavingGrassAmount;
                td.wavingGrassSpeed = src.wavingGrassSpeed;
                td.wavingGrassStrength = src.wavingGrassStrength;
                td.wavingGrassTint = src.wavingGrassTint;
            }
            // ~1.2 m cells whatever the square size (256 cells on a 1 km square gave 4 m cells = sparse grass)
            int DetailRes = Mathf.Clamp(Mathf.RoundToInt(td.size.x / DetailCellMetres / 16f) * 16, 128, 1024);
            td.SetDetailResolution(DetailRes, 16);

            int ar = td.alphamapResolution;
            var alpha = td.GetAlphamaps(0, 0, ar, ar);
            var layers = protos.Select(_ => new int[DetailRes, DetailRes]).ToArray();
            var mult = palette.Select(p => Multiplier(Key(p.proto))).ToArray();
            // pebbles (desert detail mesh) also lie loose on dry sand — never on rock, wet sand or the seabed
            int peb = palette.FindIndex(p => Key(p.proto) == Pebble);
            var sandIdx = td.terrainLayers.Select((l, i) => (l, i)).Where(li => li.l && DrySand.Contains(li.l.name)).Select(li => li.i).ToArray();
            float pebOnSand = peb >= 0 ? Plugin.PebblesOnSand.Value * mult[peb] : 0f;

            float cellLush = td.size.x / DetailRes;
            // how much of the oasis palette here: 1 within 4 shore radii, 0 past 5
            float Oasis(int x, int z)
            {
                if (lush.Count == 0) return 0f;
                float best = 0f;
                foreach (var (c, rad, maxR) in oasisList)
                {
                    float dx = x * cellLush - c.x, dz = z * cellLush - c.y;
                    if (dx * dx + dz * dz > maxR * maxR * 36f) continue;
                    float d = Mathf.Sqrt(dx * dx + dz * dz) / Mathf.Max(0.1f, rad(Mathf.Atan2(dz, dx)));
                    best = Mathf.Max(best, Mathf.Clamp01(5f - d));
                }
                return best;
            }
            for (int z = 0; z < DetailRes; z++)
                for (int x = 0; x < DetailRes; x++)
                {
                    int az = z * (ar - 1) / (DetailRes - 1), ax = x * (ar - 1) / (DetailRes - 1);
                    float g = alpha[az, ax, grass];
                    float oz = Oasis(x, z);
                    if (oz > 0f && rng.NextDouble() < oz)
                    {
                        // an oasis cell: the lush palette, denser (on the grass the oasis ring painted)
                        if (g < 0.35f) continue;
                        float kl = (g - 0.35f) / 0.65f;
                        foreach (var (idx, w) in lush)
                        {
                            float expected = w * lushDensity * kl;
                            int n = (int)expected;
                            if (rng.NextDouble() < expected - n) n++;
                            layers[idx][z, x] = n;
                        }
                        continue;
                    }
                    if (g < 0.35f)
                    {
                        if (pebOnSand <= 0f) continue;
                        float sand = 0f;
                        foreach (int si in sandIdx) sand += alpha[az, ax, si];
                        if (sand < 0.6f) continue;
                        float e = pebOnSand * sand;
                        int pn = (int)e;
                        if (rng.NextDouble() < e - pn) pn++;
                        layers[peb][z, x] = pn;
                        continue;
                    }
                    float k = (g - 0.35f) / 0.65f;                  // 0 at patch edge → 1 in its middle
                    for (int p = 0; p < palette.Count; p++)
                    {
                        // expected count per cell = share of the type's density; rare ones stay rare.
                        // The multiplier only thins/thickens this prototype — the others keep their density.
                        float expected = palette[p].weight * density * k * mult[p];
                        int n = (int)expected;
                        if (rng.NextDouble() < expected - n) n++;
                        layers[p][z, x] = n;
                    }
                }
            // reeds: a dense band around each pond shore, half in the water, thinning out up the bank
            if (reed >= 0)
            {
                float cellM = td.size.x / DetailRes;
                foreach (var sh in pondShores)
                {
                    int cx = Mathf.RoundToInt(sh.centre.x / cellM), cz = Mathf.RoundToInt(sh.centre.y / cellM);
                    int rc = Mathf.CeilToInt(sh.maxR * 1.5f / cellM) + 1;
                    for (int dz = -rc; dz <= rc; dz++)
                        for (int dx = -rc; dx <= rc; dx++)
                        {
                            int x = cx + dx, z = cz + dz;
                            if (x < 0 || z < 0 || x >= DetailRes || z >= DetailRes) continue;
                            float d = Mathf.Sqrt(dx * dx + dz * dz) * cellM / sh.radius(Mathf.Atan2(dz, dx));   // 1 = shore
                            if (d < 0.85f || d > 1.45f) continue;
                            // patchy: dense clumps along the shore, gaps between them
                            float clump = Mathf.PerlinNoise(x * 0.35f + sh.centre.x, z * 0.35f + sh.centre.y);
                            float e = Plugin.PondReedDensity.Value * (1f - Mathf.Abs(d - 1.05f) / 0.4f) * Mathf.Clamp01(clump * 2f - 0.5f);
                            if (e <= 0f) continue;
                            int n = (int)e;
                            if (rng.NextDouble() < e - n) n++;
                            layers[reed][z, x] = Mathf.Max(layers[reed][z, x], n);
                        }
                }
            }
            // seaweed (prototypes marked in the grass window): never on land; patches on the seabed by the shore,
            // 0.4–3.5 m deep, in clumps (noise), thicker in the middle of the depth band
            var weed = Enumerable.Range(0, palette.Count).Where(p => Seaweed.Contains(Key(palette[p].proto))).ToList();
            if (weed.Count > 0)
            {
                foreach (int p in weed) System.Array.Clear(layers[p], 0, layers[p].Length);
                int hr = td.heightmapResolution;
                var hts = td.GetHeights(0, 0, hr, hr);
                float sea = (t.transform.parent ? t.transform.parent.position.y : 0f) - t.GetPosition().y;   // sea level in terrain metres
                float ox = (float)rng.NextDouble() * 100f;
                for (int z = 0; z < DetailRes; z++)
                    for (int x = 0; x < DetailRes; x++)
                    {
                        float depth = sea - hts[z * (hr - 1) / (DetailRes - 1), x * (hr - 1) / (DetailRes - 1)] * td.size.y;
                        // never shallower than SeaweedMinDepth (3 m: tide mods lower the sea, the weed stood dry)
                        float d0 = Plugin.SeaweedMinDepth.Value, d1 = d0 + 4f;
                        if (depth < d0 || depth > d1) continue;
                        float band = 1f - Mathf.Abs(depth - (d0 + d1) * 0.5f) / ((d1 - d0) * 0.5f);
                        float clump = Mathf.Clamp01(Mathf.PerlinNoise(x * 0.12f + ox, z * 0.12f) * 2.4f - 1.2f);
                        if (clump <= 0f) continue;
                        foreach (int p in weed)
                        {
                            float e = Plugin.SeaweedDensity.Value * band * clump * mult[p] / weed.Count;
                            int n = (int)e;
                            if (rng.NextDouble() < e - n) n++;
                            layers[p][z, x] = n;
                        }
                    }
            }
            // clearings (a camp's tent and fire): nothing grows there
            if (clearings.TryGetValue(td, out var clear))
            {
                float cellM = td.size.x / DetailRes;
                foreach (var (c, r) in clear)
                {
                    int cx = Mathf.RoundToInt(c.x / cellM), cz = Mathf.RoundToInt(c.y / cellM), rc = Mathf.CeilToInt(r / cellM) + 1;
                    for (int dz = -rc; dz <= rc; dz++)
                        for (int dx = -rc; dx <= rc; dx++)
                        {
                            int x = cx + dx, z = cz + dz;
                            if (x < 0 || z < 0 || x >= DetailRes || z >= DetailRes || (dx * dx + dz * dz) * cellM * cellM > r * r) continue;
                            foreach (var l in layers) l[z, x] = 0;
                        }
                }
            }
            // masks (ground covered by a sculpted rock): tested cell by cell within their area
            if (masks.TryGetValue(td, out var ms))
            {
                float cellM = td.size.x / DetailRes;
                foreach (var (area, covered) in ms)
                {
                    int x0 = Mathf.Max(0, Mathf.FloorToInt(area.xMin / cellM)), x1 = Mathf.Min(DetailRes - 1, Mathf.CeilToInt(area.xMax / cellM));
                    int z0 = Mathf.Max(0, Mathf.FloorToInt(area.yMin / cellM)), z1 = Mathf.Min(DetailRes - 1, Mathf.CeilToInt(area.yMax / cellM));
                    for (int z = z0; z <= z1; z++)
                        for (int x = x0; x <= x1; x++)
                        {
                            bool any = false;
                            foreach (var l in layers) if (l[z, x] != 0) { any = true; break; }
                            if (any && covered(new Vector2((x + 0.5f) * cellM, (z + 0.5f) * cellM)))
                                foreach (var l in layers) l[z, x] = 0;
                        }
                }
            }
            for (int p = 0; p < protos.Count; p++) td.SetDetailLayer(0, 0, p, layers[p]);

            t.detailObjectDistance = vanillaRef ? vanillaRef.detailObjectDistance : 80f;
            t.detailObjectDensity = vanillaRef ? vanillaRef.detailObjectDensity : 1f;
        }

        // ------------------------------------------------------------------ palette

        private static readonly Dictionary<Climate, List<(DetailPrototype proto, float weight)>> palettes =
            new Dictionary<Climate, List<(DetailPrototype, float)>>();

        private static List<(DetailPrototype proto, float weight)> Palette(Climate type)
        {
            if (palettes.TryGetValue(type, out var cached)) return cached;
            BuildCatalog();

            var list = new List<(DetailPrototype, float)>();
            var missing = new List<string>();
            foreach (var (name, weight) in ParseSpec(Plugin.VegetationFor(type)))
            {
                if (catalog.TryGetValue(name, out var proto)) list.Add((proto, weight));
                else missing.Add(name);
            }
            if (missing.Count > 0)
                Plugin.Log.LogWarning($"Vegetation {type}: not found in game terrains: {string.Join(", ", missing.ToArray())}");

            // the desert's pebbles also go into every other region's grass (a little) and onto sand everywhere
            if (catalog.TryGetValue(Pebble, out var pebble) && !list.Any(p => Key(p.Item1) == Pebble))
                list.Add((pebble, Plugin.PebblesInGrass.Value));

            if (list.Count == 0 && vanillaRef)                  // fallback: Lion Fang's own set
                list = vanillaRef.terrainData.detailPrototypes.Select((p, i) => (p, i == 0 ? 1f : 0.3f)).ToList();

            Plugin.Log.LogInfo($"Vegetation {type}: {string.Join(", ", list.Select(p => $"{Key(p.Item1)}×{p.Item2:0.##}").ToArray())}");
            palettes[type] = list;
            return list;
        }

        private static void BuildCatalog()
        {
            if (catalog != null) return;
            catalog = new Dictionary<string, DetailPrototype>();
            // All TerrainData in memory, including islands switched off by IslandPerformanceSwitcher (far away):
            // FindObjectsOfType<Terrain> only saw active ones (22 prototypes from 8 terrains). Our own copies are
            // named "<prefab>_terrain…" — skip them.
            var datas = Resources.FindObjectsOfTypeAll<TerrainData>()
                .Where(d => d && !d.name.Contains("_terrain")).ToList();
            var terrains = Object.FindObjectsOfType<Terrain>()
                .Where(t => t.terrainData && !IsOurs(t.transform)).ToList();
            foreach (var d in datas)
                foreach (var p in d.detailPrototypes)
                {
                    string k = Key(p);
                    if (k != null && !catalog.ContainsKey(k)) catalog[k] = p;
                }
            vanillaRef = terrains.FirstOrDefault(t => t.transform.parent && t.transform.parent.name.Contains("lion fang")
                                                      && t.terrainData.detailPrototypes.Length > 0)
                         ?? terrains.FirstOrDefault(t => t.terrainData.detailPrototypes.Length > 0);
            Plugin.Log.LogInfo($"Vegetation catalog: {catalog.Count} detail prototypes from {datas.Count} terrain datas " +
                               $"({string.Join(", ", catalog.Keys.OrderBy(k => k).ToArray())})");
            if (Plugin.DevToolsEnabled.Value)
            try { WriteVanillaSurvey(datas); }
            catch (System.Exception e) { Plugin.Log.LogWarning("vegetation survey: " + e.Message); }
        }

        /// <summary>
        /// dumps\vegetation_by_island.txt: for every vanilla island terrain — owner island, real position, region guess,
        /// and each detail prototype's share of all planted details. Source data for regional palettes.
        /// </summary>
        private static void WriteVanillaSurvey(List<TerrainData> datas)
        {
            var owners = Resources.FindObjectsOfTypeAll<Terrain>()
                .Where(t => t && t.terrainData && t.gameObject.scene.IsValid())
                .GroupBy(t => t.terrainData).ToDictionary(g => g.Key, g => g.First());
            var fom = FloatingOriginManager.instance;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# island | terrainData | real XZ | prototype share (by planted count)");
            foreach (var d in datas.OrderBy(d => d.name))
            {
                if (d.detailPrototypes.Length == 0) continue;
                owners.TryGetValue(d, out var t);
                string island = "?";
                for (var tr = t ? t.transform : null; tr; tr = tr.parent)
                    if (tr.GetComponent<IslandHorizon>()) { island = tr.name; break; }
                string pos = "-";
                if (t && fom) { var r = fom.ShiftingPosToRealPos(t.transform.position); pos = $"({r.x:F0}, {r.z:F0})"; }

                var counts = new long[d.detailPrototypes.Length];
                int res = d.detailResolution;
                for (int p = 0; p < counts.Length; p++)
                {
                    var layer = d.GetDetailLayer(0, 0, res, res, p);
                    long s = 0;
                    for (int z = 0; z < res; z += 2) for (int x = 0; x < res; x += 2) s += layer[z, x];
                    counts[p] = s;
                }
                long total = System.Math.Max(1, counts.Sum());
                var parts = d.detailPrototypes.Select((pr, i) => (name: Key(pr) ?? "?", share: counts[i] * 100.0 / total))
                    .OrderByDescending(x => x.share).Select(x => $"{x.name} {x.share:F0}%");
                sb.AppendLine($"{island} | {d.name} | {pos} | {string.Join(", ", parts.ToArray())}");
            }
            string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Plugin.Instance.Info.Location), "dumps");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "vegetation_by_island.txt"), sb.ToString());
        }

        // pond shores per terrain (Ponds.cs): centre in metres from the terrain corner, shore radius by world angle
        private static readonly Dictionary<TerrainData, List<(Vector2 centre, System.Func<float, float> radius, float maxR)>> shores =
            new Dictionary<TerrainData, List<(Vector2, System.Func<float, float>, float)>>();

        private static readonly Dictionary<TerrainData, List<(Vector2 c, float r)>> clearings = new Dictionary<TerrainData, List<(Vector2, float)>>();

        /// <summary>A circle (terrain-local metres) kept free of grass from now on; takes effect on the next Apply.</summary>
        internal static void AddClearing(TerrainData td, Vector2 centre, float radius)
        {
            if (!clearings.TryGetValue(td, out var l)) clearings[td] = l = new List<(Vector2, float)>();
            l.Add((centre, radius));
        }

        private static readonly Dictionary<TerrainData, List<(Rect area, System.Func<Vector2, bool> covered)>> masks =
            new Dictionary<TerrainData, List<(Rect, System.Func<Vector2, bool>)>>();

        /// <summary>No grass where `covered` (terrain-local metres) says so, within `area`; takes effect on the next Apply.</summary>
        internal static void AddMask(TerrainData td, Rect area, System.Func<Vector2, bool> covered)
        {
            if (!masks.TryGetValue(td, out var l)) masks[td] = l = new List<(Rect, System.Func<Vector2, bool>)>();
            l.Add((area, covered));
        }

        // oases: round their water the region's palette gives way to a lush one (the jungle's grass and ferns, denser,
        // no pebbles) out to ~4 shore radii, blending back over the next one — a desert oasis grew dry tufts and stones
        private static readonly Dictionary<TerrainData, List<(Vector2 centre, System.Func<float, float> radius, float maxR)>> oases =
            new Dictionary<TerrainData, List<(Vector2, System.Func<float, float>, float)>>();
        internal static void AddOasis(TerrainData td, Vector2 centre, System.Func<float, float> radius, float maxR)
        {
            if (!oases.TryGetValue(td, out var l)) oases[td] = l = new List<(Vector2, System.Func<float, float>, float)>();
            l.Add((centre, radius, maxR));
        }

        internal static void AddShore(TerrainData td, Vector2 centre, System.Func<float, float> radius, float maxR)
        {
            if (!shores.TryGetValue(td, out var l)) shores[td] = l = new List<(Vector2, System.Func<float, float>, float)>();
            l.Add((centre, radius, maxR));
        }

        /// <summary>
        /// Emerald / Lagoon are jungle: the green climbs higher and steeper. On the island's own splat copy, dry sand
        /// above the beach (≥ 3 m) turns mostly to grass and rock on slopes up to ~50° gets overgrown; the beach and
        /// the sheer faces stay. Patchy (noise), so some bare ground remains.
        /// </summary>
        internal static void Lush(Terrain t, Transform islandRoot)
        {
            var td = t.terrainData;
            var layers = td.terrainLayers;
            if (layers == null) return;
            int grass = System.Array.FindIndex(layers, l => l && l.name == GrassLayer);
            if (grass < 0) return;
            float k = Plugin.LushGreen.Value;
            if (k <= 0f) return;
            float baseY = islandRoot.InverseTransformPoint(t.GetPosition()).y;
            int ar = td.alphamapResolution;
            var a = td.GetAlphamaps(0, 0, ar, ar);
            int n = a.GetLength(2);
            // the ground the editor painted bare stays bare: under our sculpted rocks (a ray from above meets one no
            // lower than 0.5 m under the ground) and a smoking / dead crater's floor (its marker: "volcano crater
            // smoke|none r<radius>"). The jungle green grew grass in a crater and under rocks.
            int rockL = System.Array.FindIndex(layers, l => l && l.name.StartsWith("Rock"));
            Physics.SyncTransforms();
            var rocks = islandRoot.GetComponentsInChildren<MeshCollider>(true).Where(c => c && c.sharedMesh && c.name.StartsWith("rock sculpted ")).ToList();
            var craters = new List<(Vector3 c, float r)>();
            foreach (var m in islandRoot.GetComponentsInChildren<Transform>(true).Where(tt => tt.name.StartsWith("volcano crater")))
            {
                var parts = m.name.Split(' ');
                if (parts.Contains("lake")) continue;
                var rp = parts.FirstOrDefault(p => p.Length > 1 && p[0] == 'r');
                if (rp != null && float.TryParse(rp.Substring(1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rr))
                    craters.Add((m.position, rr * 0.95f));
            }
            var tp = t.GetPosition();
            bool InCrater(float u, float v)
            {
                float wx = tp.x + u * td.size.x, wz = tp.z + v * td.size.z;
                foreach (var (c, r) in craters) if (new Vector2(wx - c.x, wz - c.z).sqrMagnitude < r * r) return true;
                return false;
            }
            bool UnderRock(float u, float v)
            {
                var w = new Vector3(tp.x + u * td.size.x, 0f, tp.z + v * td.size.z);
                float gy = td.GetInterpolatedHeight(u, v) + tp.y;
                foreach (var rc in rocks)
                {
                    var b = rc.bounds;
                    if (w.x < b.min.x || w.x > b.max.x || w.z < b.min.z || w.z > b.max.z) continue;
                    if (rc.Raycast(new Ray(new Vector3(w.x, b.max.y + 5f, w.z), Vector3.down), out var hit, b.size.y + 10f) && hit.point.y > gy - 0.5f) return true;
                }
                return false;
            }
            for (int z = 0; z < ar; z++)
                for (int x = 0; x < ar; x++)
                {
                    float u = x / (ar - 1f), v = z / (ar - 1f);
                    float h = td.GetInterpolatedHeight(u, v) + baseY;
                    if (h < 3f) continue;                                                  // the beach stays sand
                    float slope = td.GetSteepness(u, v);
                    float g = k * Mathf.Clamp01((h - 3f) / 2f) * Mathf.Clamp01((52f - slope) / 12f)
                              * (0.65f + 0.35f * Mathf.PerlinNoise(u * 40f, v * 40f));
                    if (g <= 0f || a[z, x, grass] >= g) continue;
                    if (InCrater(u, v)) continue;
                    if (rockL >= 0 && a[z, x, rockL] > 0.3f && UnderRock(u, v)) continue; // (only rock-painted ground is ray-tested)
                    for (int l = 0; l < n; l++) a[z, x, l] *= 1f - g;
                    a[z, x, grass] += g;
                }
            td.SetAlphamaps(0, 0, a);
        }

        /// <summary>What an island's terrain was planted with, so the grass window can plant it again.</summary>
        internal class GrassState : MonoBehaviour
        {
            public int seed;
            public IslandType type;
            public Climate region;
        }

        // a prototype is either a texture (grass billboard) or a mesh prefab
        private static string Key(DetailPrototype p) =>
            p.prototypeTexture ? p.prototypeTexture.name : p.prototype ? p.prototype.name : null;

        private static bool IsOurs(Transform t)
        {
            for (; t; t = t.parent) if (t.name.StartsWith("terra incognita")) return true;
            return false;
        }

        // "LGW_d25:1, LGW_d43:0.5"
        private static IEnumerable<(string, float)> ParseSpec(string spec)
        {
            foreach (var part in (spec ?? "").Split(','))
            {
                var kv = part.Split(':');
                string name = kv[0].Trim();
                if (name.Length == 0) continue;
                float w = 1f;
                if (kv.Length > 1) float.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out w);
                yield return (name, w);
            }
        }
    }
}
