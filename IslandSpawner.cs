using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;

namespace TerraIncognita
{
    internal struct SpawnedIsland
    {
        public Transform Root;
        public Climate Climate;
        public int Seed;          // per-island seed for prop variety
        public float Latitude;    // °N, for the palm line
        public IslandType Type;
        public bool OnRoute;      // beside a trade route (open sea) rather than on an archipelago's outskirts
    }

    internal static class IslandSpawner
    {
        private const string BundleFile = "terra_incognita";
        private static AssetBundle bundle;

        public static List<SpawnedIsland> Spawn(List<Placement> placements, FloatingOriginManager origin)
        {
            var spawned = new List<SpawnedIsland>();
            var used = new Dictionary<GameObject, int>();
            var prefabs = LoadPrefabs();
            var world = Refs.shiftingWorld ? Refs.shiftingWorld : origin.transform;
            var report = new System.Text.StringBuilder();
            report.AppendLine("# Terra Incognita placements (seed " + Plugin.Seed + ")");
            report.AppendLine("# NANDCommand: paste the moveBoat line into the console — the boat lands 170 units south of the island centre.");
            report.AppendLine("# name | origin | region | ponds | caves | real XZ | nearest vanilla land | command");

            foreach (var p in placements)
            {
                GameObject go;
                if (prefabs.TryGetValue(p.Type, out var variants) && variants.Count > 0)
                {
                    // least-used variant first: a second island of a type gets a different prefab while unseen ones remain
                    int least = variants.Min(v => used.TryGetValue(v, out int k) ? k : 0);
                    var pool = variants.Where(v => (used.TryGetValue(v, out int k) ? k : 0) == least).ToList();
                    var prefab = pool[p.Variant % pool.Count];
                    used[prefab] = least + 1;
                    go = Object.Instantiate(prefab, world);
                    FixShaders(go.transform);
                }
                else
                {
                    go = Placeholder(p.Type, world); // lets us test placement before any art exists
                }

                go.name = $"terra incognita {p.Type} ({p.Origin}) #{spawned.Count + 1}";   // unique: two route islands of a type had one name
                // Children of _shifting world live in "shifted" space: local = real + current offset.
                var shifted = origin.RealPosToShiftingPos(new Vector3(p.RealPos.x, 0f, p.RealPos.y));
                go.transform.localPosition = new Vector3(shifted.x, go.transform.localPosition.y, shifted.z);

                // Unity Terrain ignores rotation, its children (props) don't. Islands with a terrain therefore
                // turn in 90° steps and get a heightmap rotated by the same angle; others turn freely.
                if (go.GetComponentInChildren<Terrain>(true))
                {
                    int quarter = Mathf.FloorToInt(p.Yaw / 90f) & 3;
                    go.transform.localRotation = Quaternion.Euler(0f, quarter * 90f, 0f);
                    TerrainRotation.Apply(go.transform, quarter);
                }
                else go.transform.localRotation = Quaternion.Euler(0f, p.Yaw, 0f);

                var region = Regions.Nearest(p.RealPos, origin);
                SetupGround(go, p.Variant, p.Type, region);
                SetupDepthCache(go);
                SetupHorizon(go);
                spawned.Add(new SpawnedIsland { Root = go.transform, Climate = region, Seed = p.Variant * 7919 + spawned.Count, Latitude = p.RealPos.y / 9000f + 36f, Type = p.Type, OnRoute = p.Origin == "route" });

                // NANDCommand moveBoat: world = (long, 0, lat) * 9000 + (0, 0, -36*9000)  →  lat = z/9000 + 36, long = x/9000.
                // Current culture on purpose: the console parses floats with it (ru → comma).
                float lat = (p.RealPos.y - 170f) / 9000f + 36f, lon = p.RealPos.x / 9000f;
                string cmd = $"moveBoat {lat.ToString("F4")} {lon.ToString("F4")}";
                string near = NearestVanilla(p.RealPos, origin);
                int ponds = go.GetComponentsInChildren<Transform>(true).Count(t => t.name == "pond");
                report.AppendLine($"{go.name} | {p.Origin} | {region} | {(ponds > 0 ? "pond" : "-")} | {Caves(go.transform)} | ({p.RealPos.x:F0}, {p.RealPos.y:F0}) | {near} | {cmd}");
                if (Plugin.DebugLog.Value)
                    Plugin.Debug($"  {go.name} [{region}]: real ({p.RealPos.x:F0}, {p.RealPos.y:F0}), near {near}  →  {cmd}");
            }

            try
            {
                // moveBoat commands to every island: a developer's list (spoils the exploring) — DevTools only;
                // MarkPonds / MarkWrecks only add to an existing file
                string dir = Path.Combine(Path.GetDirectoryName(Plugin.Instance.Info.Location), "dumps");
                if (Plugin.DevToolsEnabled.Value)
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "placements.txt"), report.ToString());
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("placements.txt: " + e.Message); }
            return spawned;
        }

        /// <summary>For placements.txt: "2 grotto + 1 sea" — sculpted grottos, and sea caves in mesa walls (one
        /// "cave spot chamber" each) — or "-".</summary>
        private static string Caves(Transform root)
        {
            var all = root.GetComponentsInChildren<Transform>(true);
            int grottos = all.Count(t => t.name.StartsWith("rock sculpted grotto"));
            int sea = all.Count(t => t.name == "cave spot chamber" && t.parent && t.parent.name.StartsWith("rock sculpted mesawall"));   // (side passages: "cave spot branch")
            var parts = new List<string>();
            if (grottos > 0) parts.Add($"{grottos} grotto");
            if (sea > 0) parts.Add($"{sea} sea");
            return parts.Count > 0 ? string.Join(" + ", parts) : "-";
        }

        /// <summary>Append the wreck column to placements.txt: "wreck: boat [+ N sails]" or "-".</summary>
        internal static void MarkWrecks(IDictionary<string, string> wrecks)
        {
            try
            {
                string file = Path.Combine(Path.Combine(Path.GetDirectoryName(Plugin.Instance.Info.Location), "dumps"), "placements.txt");
                if (!File.Exists(file)) return;
                var lines = File.ReadAllLines(file).Select(l =>
                {
                    if (l.StartsWith("# name")) return l + " | wreck";
                    if (l.StartsWith("#")) return l;
                    var parts = l.Split('|');
                    if (parts.Length < 5) return l;
                    return l + " | " + (wrecks.TryGetValue(parts[0].Trim(), out var w) ? "wreck: " + w : "-");
                }).ToArray();
                File.WriteAllLines(file, lines);
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("placements.txt: " + e.Message); }
        }

        /// <summary>Mark islands that got a runtime pond in placements.txt ("ponds" column).</summary>
        internal static void MarkPonds(ICollection<string> islandNames)
        {
            if (islandNames.Count == 0) return;
            try
            {
                string file = Path.Combine(Path.Combine(Path.GetDirectoryName(Plugin.Instance.Info.Location), "dumps"), "placements.txt");
                if (!File.Exists(file)) return;
                var lines = File.ReadAllLines(file).Select(l =>
                {
                    var parts = l.Split('|');
                    return parts.Length > 4 && islandNames.Contains(parts[0].Trim()) ? (parts[3] = " pond ", string.Join("|", parts)).Item2 : l;
                }).ToArray();
                File.WriteAllLines(file, lines);
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("placements.txt: " + e.Message); }
        }

        private static string NearestVanilla(Vector2 real, FloatingOriginManager origin)
        {
            string best = "-";
            float bestD = float.MaxValue;
            foreach (var h in IslandDistanceTracker.instance ? IslandDistanceTracker.instance.islands : new List<IslandHorizon>())
            {
                if (!h || h.name.StartsWith("terra incognita")) continue;
                var r = origin.ShiftingPosToRealPos(h.GetPosition());
                float d = (new Vector2(r.x, r.z) - real).magnitude;
                if (d < bestD) { bestD = d; best = h.name; }
            }
            return $"{best} {bestD:F0}u";
        }

        /// <summary>
        /// Reuse the game's own island behaviour: curvature drop below the horizon + "distance to land"
        /// (islandIndex 0 => no scene streaming, nothing written into Refs.islands).
        /// </summary>
        private static void SetupHorizon(GameObject go)
        {
            if (!go.GetComponent<IslandHorizon>())
            {
                var h = go.AddComponent<IslandHorizon>();
                h.islandIndex = 0;
            }
            if (!go.GetComponent<DetailStreamer>())
                go.AddComponent<DetailStreamer>();
        }

        // ---- vanilla island recipe (from F8 dumps of Lion Fang / Fey Valley / Turtle Island) ----
        //  * Terrain: layer TerrainDepth + TerrainLayerFix (switches to Default after 2 frames, i.e. after
        //    the Crest depth cache has rendered it), tag "Terrain", TerrainCollider physMat "Wood",
        //    materialType BuiltInStandard. Terrain ignores transform scale.
        //  * Solid rock meshes: layer TerrainDepth (permanently), tag "Stone", MeshCollider.
        //  * child "depth": Crest.OceanDepthCache (Realtime, OnStart, layers {"TerrainDepth"}, res 512),
        //    scale ≈ 3x island footprint, + DepthCacheFixer (spawns the "depth fix" ring prefab).
        private const string DepthLayerName = "TerrainDepth";

        private static void SetupGround(GameObject go, int variantSeed, IslandType type, Climate region)
        {
            int depthLayer = LayerMask.NameToLayer(DepthLayerName);
            var wood = Resources.FindObjectsOfTypeAll<PhysicMaterial>().FirstOrDefault(m => m.name == "Wood");
            var gameLayers = Resources.FindObjectsOfTypeAll<TerrainLayer>()
                .GroupBy(l => l.name).ToDictionary(g => g.Key, g => g.First());

            foreach (var t in go.GetComponentsInChildren<Terrain>(true))
            {
                t.gameObject.layer = depthLayer;
                t.gameObject.tag = "Terrain";
                if (!t.GetComponent<TerrainLayerFix>()) t.gameObject.AddComponent<TerrainLayerFix>();
                // Vanilla uses 5, fine for its gentle slopes; our steep walls visibly pop between LODs as the camera moves
                t.heightmapPixelError = Plugin.TerrainPixelError.Value;
                var col = t.GetComponent<TerrainCollider>();
                if (col && wood) col.sharedMaterial = wood;

                // Bundle ships TerrainLayers named like the game's ("Sand Cracked", "Grass Leaves", "Rock 2"...);
                // swap them for the game's own instances so no game textures need to be redistributed.
                var td = t.terrainData;
                if (td && td.terrainLayers != null)
                {
                    var before = td.terrainLayers.Where(l => l).Select(l => l.name).ToArray();
                    td.terrainLayers = td.terrainLayers
                        .Select(l => l && gameLayers.TryGetValue(l.name, out var g) ? g : l).ToArray();
                    var missing = before.Where(n => !gameLayers.ContainsKey(n)).ToArray();
                    if (missing.Length > 0)
                        Plugin.Log.LogWarning($"{go.name}: no game TerrainLayer named {string.Join(", ", missing)} — placeholder stays");
                }

                if (region == Climate.Asian || region == Climate.Oceanic) DetailGrass.Lush(t, go.transform);
                DetailGrass.Apply(t, variantSeed, type, region);
            }

            // rocks / cliffs that should make shallow water: everything with a MeshCollider outside "detail*"
            foreach (var mc in go.GetComponentsInChildren<MeshCollider>(true))
                if (!IsDetail(mc.transform, go.transform))
                {
                    mc.gameObject.layer = depthLayer;
                    mc.gameObject.tag = "Stone";
                }
            // placeholder primitives
            foreach (var c in go.GetComponentsInChildren<CapsuleCollider>(true)) c.gameObject.layer = depthLayer;
        }

        private static void SetupDepthCache(GameObject go)
        {
            if (go.transform.Find("depth")) return; // prefab already has one

            var bounds = new Bounds(go.transform.position, Vector3.zero);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) bounds.Encapsulate(r.bounds);
            foreach (var t in go.GetComponentsInChildren<Terrain>(true))
                bounds.Encapsulate(new Bounds(t.transform.position + t.terrainData.size / 2, t.terrainData.size));
            float footprint = Mathf.Max(bounds.size.x, bounds.size.z, 100f) * 3f;

            var depth = new GameObject("depth");
            depth.transform.SetParent(go.transform, false);
            depth.transform.position = new Vector3(bounds.center.x, go.transform.position.y, bounds.center.z);
            var ls = go.transform.lossyScale;
            depth.transform.localScale = new Vector3(footprint / ls.x, 1f, footprint / ls.z);

            var cache = depth.AddComponent<Crest.OceanDepthCache>();   // Start runs next frame → fields set in time
            cache._layerNames = new[] { DepthLayerName };
            cache._resolution = 512;
            depth.AddComponent<DepthCacheFixer>();                     // Awake: spawns "depth fix" next to it
        }

        private static bool IsDetail(Transform t, Transform root)
        {
            for (; t && t != root; t = t.parent) if (t.name.StartsWith("detail")) return true;
            return false;
        }

        private static Dictionary<IslandType, List<GameObject>> prefabCache;
        private static Dictionary<IslandType, float> heightCache;

        /// <summary>
        /// Highest point above the sea of each island type: the tallest of its prefab variants (terrain heightmap max,
        /// the prefab root sitting at sea level). Placeholder height when the bundle lacks the type.
        /// </summary>
        internal static Dictionary<IslandType, float> TypeHeights()
        {
            if (heightCache != null) return heightCache;
            heightCache = new Dictionary<IslandType, float>();
            foreach (var kv in LoadPrefabs())
            {
                float top = 0f;
                foreach (var go in kv.Value)
                {
                    var t = go.GetComponentInChildren<Terrain>(true);
                    if (!t || !t.terrainData) continue;
                    var td = t.terrainData; int r = td.heightmapResolution;
                    var h = td.GetHeights(0, 0, r, r);
                    float mx = 0f; foreach (var v in h) if (v > mx) mx = v;
                    float y = go.transform.InverseTransformPoint(t.transform.position).y + mx * td.size.y;
                    top = Mathf.Max(top, y);
                }
                heightCache[kv.Key] = top > 0f ? top : IslandTypes.Info[kv.Key].PlaceholderHeight;
            }
            foreach (IslandType t in System.Enum.GetValues(typeof(IslandType)))
                if (!heightCache.ContainsKey(t)) heightCache[t] = IslandTypes.Info[t].PlaceholderHeight;
            Plugin.Log.LogInfo("Island heights: " + string.Join(", ", heightCache.Select(kv => $"{kv.Key} {kv.Value:0} m").ToArray()));
            return heightCache;
        }

        /// <summary>Island prefabs in the bundle grouped by type (cached). Keys = types the placer may choose.</summary>
        internal static Dictionary<IslandType, List<GameObject>> LoadPrefabs()
        {
            if (prefabCache != null) return prefabCache;
            var map = prefabCache = new Dictionary<IslandType, List<GameObject>>();
            string path = Path.Combine(Path.GetDirectoryName(Plugin.Instance.Info.Location), BundleFile);
            if (bundle == null && File.Exists(path))
                bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null)
            {
                Plugin.Log.LogWarning($"Bundle not found at {path} — spawning placeholders");
                return map;
            }

            // Naming convention in the Unity project: <prefix>_<seed>, prefixes in IslandTypes.Info
            foreach (var go in bundle.LoadAllAssets<GameObject>())
            {
                var t = IslandTypes.FromPrefabName(go.name);
                if (t == null) continue;
                if (!map.TryGetValue(t.Value, out var list)) map[t.Value] = list = new List<GameObject>();
                list.Add(go);
            }
            foreach (var l in map.Values) l.Sort((a, b) => string.CompareOrdinal(a.name, b.name)); // determinism
            Plugin.Log.LogInfo("Prefabs: " + string.Join(", ", map.Select(kv => $"{kv.Key}={kv.Value.Count}")));
            return map;
        }

        /// <summary>Survivors' camp props in the bundle ("ti camp &lt;name&gt;", Tools › Terra Incognita › 5).</summary>
        internal static List<GameObject> CampPrefabs()
        {
            LoadPrefabs();
            if (bundle == null) return new List<GameObject>();
            return bundle.LoadAllAssets<GameObject>().Where(g => g.name.StartsWith("ti camp ")).OrderBy(g => g.name).ToList();
        }

        /// <summary>Bundled materials carry editor-compiled shader copies; swap to the game's own shaders by name.</summary>
        internal static void FixShaders(Transform root)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m && m.shader && Shader.Find(m.shader.name) is Shader s) m.shader = s;
            foreach (var t in root.GetComponentsInChildren<Terrain>(true))
                if (t.materialTemplate && Shader.Find(t.materialTemplate.shader.name) is Shader s) t.materialTemplate.shader = s;
        }

        private static GameObject Placeholder(IslandType type, Transform parent)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.transform.SetParent(parent, false);
            var info = IslandTypes.Info[type];
            go.transform.localScale = new Vector3(info.PlaceholderSize, info.PlaceholderHeight / 2f, info.PlaceholderSize);
            go.transform.localPosition = new Vector3(0, -5, 0);
            return go;
        }
    }

    /// <summary>
    /// Same idea as the game's IslandPerformanceSwitcher: children tagged by name "detail*" (vegetation,
    /// props, wreck debris) are only active when the player is close. Terrain/rock silhouettes stay visible.
    /// </summary>
    internal class DetailStreamer : MonoBehaviour
    {
        public float detailDistance = 1800f;
        private IslandHorizon horizon;
        private List<GameObject> details;
        private bool shown = true;

        private void Start()
        {
            horizon = GetComponent<IslandHorizon>();
            details = GetComponentsInChildren<Transform>(true)
                .Where(t => t != transform && t.name.StartsWith("detail")).Select(t => t.gameObject).ToList();
        }

        private void Update()
        {
            bool want = horizon.currentDistance < detailDistance;
            if (want == shown) return;
            shown = want;
            foreach (var d in details) if (d) d.SetActive(want);
        }
    }
}
