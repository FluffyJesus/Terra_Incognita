using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TerraIncognita
{
    /// <summary>
    /// Borrows props (trees, bushes, rocks) from the game's own scenery scenes at runtime, so the bundle never
    /// contains game assets. Island prefabs only hold *markers*; each marker stands for a role ("tree", "bush",
    /// "rock", "cliff") and is swapped for a random prop of that role from the island's region
    /// (config [Props] Desert/Asian/Oceanic/North).
    ///
    /// Markers from the first bundles carry Lion Fang names: palm_tree_1..4 = tree, palm_tree_small_2 = bush,
    /// rock2 = rock — so already generated islands get regional props without regenerating.
    ///
    /// Scenery props have their MeshRenderers disabled (a mesh combiner draws them) — clones get them re-enabled.
    /// </summary>
    internal static class PropLibrary
    {
        public static readonly string[] Roles = { "tree", "bush", "rock", "cliff", "stone" };   // stone = basin ring (Ponds)

        // region → role → templates
        private static readonly Dictionary<Climate, Dictionary<string, List<GameObject>>> sets =
            new Dictionary<Climate, Dictionary<string, List<GameObject>>>();
        private static Transform root;
        public static bool Ready { get; private set; }

        /// <summary>
        /// Loading scenery scenes is the riskiest thing the mod does (other mods react to every scene load), so only
        /// regions that actually have islands are harvested. Desert always: it is the fallback set.
        /// </summary>
        public static IEnumerator Harvest(ICollection<Climate> needed, bool needPond)
        {
            root = new GameObject("TerraIncognita PropLibrary").transform;
            root.gameObject.SetActive(false);                       // templates never render or collide
            Object.DontDestroyOnLoad(root.gameObject);

            foreach (Climate region in System.Enum.GetValues(typeof(Climate)))
                if (region == Climate.Desert || needed.Contains(region))
                    yield return HarvestRegion(region, Plugin.PropsFor(region));
            yield return HarvestRocks(Plugin.RockSources.Value);
            // (ponds no longer borrow the onsen's Water4: without its reflection scripts it was white or see-through)
            // Fey Valley's lake (island 35, scene 35): a plain cube wearing "Water4Advanced pond" — that material works
            // with no scripts, so ponds wear a copy. Found while scene 35 is open for northern props, else opened for it.
            if (needPond && !FeyWater && Plugin.PondStyle.Value == "fey")
                yield return VisitScene(35, GrabFeyWater);
            Plugin.Log.LogInfo(FeyWater ? $"Pond water: Fey Valley's '{FeyWater.name}' [{FeyWater.shader.name}]" : "Pond water: own (Standard)");

            Ready = true;
        }

        /// <summary>A copy of Fey Valley's lake material (kept alive after its scene unloads), or null.</summary>
        internal static Material FeyWater;

        private static void GrabFeyWater(Scene scene)
        {
            if (FeyWater) return;
            foreach (var r in scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Renderer>(true)))
                foreach (var m in r.sharedMaterials)
                    if (m && m.name.StartsWith("Water4Advanced pond"))
                    {
                        FeyWater = new Material(m) { name = "terra incognita pond (Fey Valley)" };
                        TuneFeyWater(FeyWater);
                        return;
                    }
        }

        // A still pond, not a valley stream: the waves drift slower ([Ponds] FlowSpeed scales Water4's _WaveSpeed and
        // Gerstner _GSpeed), and the water is darker so it reads in bright sun ([Ponds] Shade scales _BaseColor and
        // _ReflectionColor). The lake's own values are logged.
        private static void TuneFeyWater(Material m)
        {
            // Water4 (Unity Standard Assets) scrolls its two normal maps by _Time · _BumpDirection (xy, zw) and the
            // Gerstner waves by _GSpeed; there is no "_WaveSpeed" (the first version scaled nothing).
            var flowProps = new[] { "_BumpDirection", "_AnimationDirection", "_GSpeed" };
            var colorProps = new[] { "_BaseColor", "_ReflectionColor", "_SpecularColor" };
            string Dump() => string.Join("; ", flowProps.Concat(colorProps).Concat(new[] { "_BumpTiling", "_DistortParams", "_InvFadeParemeter", "_FresnelScale" })
                .Where(m.HasProperty).Select(n => $"{n}={(n == "_FresnelScale" ? m.GetFloat(n).ToString("0.###") : m.GetVector(n).ToString("0.###"))}").ToArray());
            string before = Dump();
            float flow = Plugin.PondFlowSpeed.Value, shade = Plugin.PondShade.Value;
            foreach (var n in flowProps)
                if (m.HasProperty(n)) m.SetVector(n, m.GetVector(n) * flow);
            foreach (var n in colorProps.Take(2))
                if (m.HasProperty(n)) { var c = m.GetColor(n); m.SetColor(n, new Color(c.r * shade, c.g * shade, c.b * shade, c.a)); }
            // Water4's opacity is the depth under the surface × _InvFadeParemeter.x (0.22 for the deep lake: a 1 m pond
            // was 80 % see-through) — [Ponds] Opacity scales it; the base colour (what the water shows instead of the
            // bottom) gets a little more weight with it
            float opacity = Plugin.PondOpacity.Value;
            if (m.HasProperty("_InvFadeParemeter")) { var v = m.GetVector("_InvFadeParemeter"); v.x *= opacity; m.SetVector("_InvFadeParemeter", v); }
            if (m.HasProperty("_BaseColor") && opacity > 1f) { var c = m.GetColor("_BaseColor"); c.a = Mathf.Lerp(c.a, 1f, Mathf.Clamp01((opacity - 1f) / 8f)); m.SetColor("_BaseColor", c); }
            Plugin.Log.LogInfo($"Fey Valley water (flow ×{flow}, shade ×{shade}): {before}  →  {Dump()}");
        }

        private static IEnumerator HarvestRegion(Climate region, string spec)
        {
            var (scenes, roles) = ParseSpec(spec);
            var wanted = new HashSet<string>(roles.Values.SelectMany(v => v));
            var found = new Dictionary<string, GameObject>();

            foreach (int sceneIndex in scenes)
            {
                if (found.Count == wanted.Count) break;                   // everything found, skip remaining scenes
                // logged BEFORE loading: if the game dies, the last line names the scene
                Plugin.Log.LogInfo($"Props {region}: loading scenery scene {sceneIndex} for " +
                                   string.Join(", ", wanted.Where(w => !found.ContainsKey(w)).ToArray()));
                yield return VisitScene(sceneIndex, scene =>
                {
                    if (sceneIndex == 35) GrabFeyWater(scene);
                    foreach (var t in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)))
                    {
                        string key = Strip(t.name);
                        if (!wanted.Contains(key) || found.ContainsKey(key)) continue;
                        if (!t.GetComponentInChildren<MeshFilter>(true)) continue;   // empty groups with a prop's name
                        found[key] = MakeTemplate(t, $"{region}/{key}");
                    }
                });
            }

            var byRole = sets[region] = new Dictionary<string, List<GameObject>>();
            foreach (var kv in roles)
            {
                var list = new List<GameObject>();
                foreach (var name in kv.Value)
                {
                    if (!found.TryGetValue(name, out var tpl)) continue;
                    list.Add(tpl);
                }
                byRole[kv.Key] = list;
            }
            var missing = wanted.Where(w => !found.ContainsKey(w)).ToArray();
            if (missing.Length > 0)
                Plugin.Log.LogWarning($"Props {region}: not found in scenes {string.Join(",", scenes.Select(s => s.ToString()).ToArray())}: {string.Join(", ", missing)}");
            Plugin.Log.LogInfo($"Props {region}: " + string.Join("; ", byRole.Select(kv =>
                $"{kv.Key} = {string.Join(", ", kv.Value.Select(t => $"{t.name.Split('/')[1]} {Fmt(Size(t))}").ToArray())}").ToArray()));
        }

        /// <summary>
        /// Make sure a scenery scene is loaded, call <paramref name="visit"/>, then unload it again if we were the
        /// ones who loaded it (and the island hasn't claimed it meanwhile).
        /// </summary>
        public static IEnumerator VisitScene(int buildIndex, System.Action<Scene> visit)
        {
            if (buildIndex <= 0 || buildIndex >= SceneManager.sceneCountInBuildSettings) yield break;
            var scene = SceneManager.GetSceneByBuildIndex(buildIndex);
            var owner = IslandDistanceTracker.instance
                ? IslandDistanceTracker.instance.islands.FirstOrDefault(h => h && h.islandIndex == buildIndex) : null;

            bool weLoaded = false;
            if (!scene.isLoaded)
            {
                if (owner && owner.SceneLoaded())
                {
                    // the island itself is loading it right now (player nearby) — just wait
                    while (!SceneManager.GetSceneByBuildIndex(buildIndex).isLoaded) yield return null;
                }
                else
                {
                    var op = SceneManager.LoadSceneAsync(buildIndex, LoadSceneMode.Additive);
                    while (op != null && !op.isDone) yield return null;
                    weLoaded = true;
                }
                scene = SceneManager.GetSceneByBuildIndex(buildIndex);
            }
            yield return null; // let the scene's Awake/Start run

            if (scene.isLoaded)
            {
                try { visit(scene); }
                catch (System.Exception e) { Plugin.Log.LogError($"scene {buildIndex}: {e}"); }
            }

            // Unload only what we loaded, and only if the island hasn't claimed it meanwhile.
            if (weLoaded && !(owner && owner.SceneLoaded()))
            {
                var op = SceneManager.UnloadSceneAsync(buildIndex);
                while (op != null && !op.isDone) yield return null;
            }
        }

        // ------------------------------------------------------------------ rocks
        // One pool for every region (config RockSources). Emerald's rocks are left out: there they stand on edge,
        // stretched, with plants attached. Of every name the most upright, least stretched instance is cloned, snapped
        // to whole 90° tilts (Z-up exports), made uniform, stripped of plants and sized like Lion Fang's rock2 — the
        // generator's footprints assume that size. The island's region then recolours it (warm / Medi blue).
        private static readonly List<GameObject> rockPool = new List<GameObject>();
        private static Material rockMatWarm, rockMatBlue;
        private const float RockRefSize = 8.4f;   // Lion Fang rock2, largest horizontal side

        private static IEnumerator HarvestRocks(string spec)
        {
            foreach (var part in (spec ?? "").Split(';'))
            {
                var kv = part.Split('=');
                if (kv.Length != 2) continue;
                var scenes = kv[0].Split(',').Select(s => int.TryParse(s.Trim(), out int i) ? i : -1).Where(i => i > 0).ToList();
                var wanted = new HashSet<string>(kv[1].Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));
                var best = new Dictionary<string, (GameObject tpl, float dev)>();
                foreach (int sceneIndex in scenes)
                {
                    if (best.Count == wanted.Count) break;
                    Plugin.Log.LogInfo($"Rocks: loading scenery scene {sceneIndex} for " +
                                       string.Join(", ", wanted.Where(w => !best.ContainsKey(w)).ToArray()));
                    yield return VisitScene(sceneIndex, scene =>
                    {
                        foreach (var t in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)))
                        {
                            string key = Strip(t.name);
                            if (!wanted.Contains(key) || !t.GetComponentInChildren<MeshFilter>(true)) continue;
                            float dev = TiltDeviation(t);
                            if (best.TryGetValue(key, out var b) && b.dev <= dev) continue;
                            if (b.tpl) Object.Destroy(b.tpl);
                            best[key] = (MakeRockTemplate(t, $"Rock/{sceneIndex}/{key}"), dev);
                        }
                    });
                }
                foreach (var b in best.Values) rockPool.Add(b.tpl);
            }
            foreach (var tpl in rockPool)
            {
                var m = tpl.GetComponentInChildren<Renderer>(true)?.sharedMaterial;
                if (!m) continue;
                if (m.name.Contains("blue")) { if (!rockMatBlue) rockMatBlue = m; }
                else if (!rockMatWarm) rockMatWarm = m;
            }
            Plugin.Log.LogInfo($"Rocks: {string.Join(", ", rockPool.Select(t => $"{t.name} {Fmt(Size(t))}").ToArray())}; " +
                               $"warm {(rockMatWarm ? rockMatWarm.name : "-")}, blue {(rockMatBlue ? rockMatBlue.name : "-")}");
        }

        // degrees away from a whole-90° tilt, plus a penalty for stretched (non-uniform) scale
        private static float TiltDeviation(Transform t)
        {
            var e = t.rotation.eulerAngles;
            float dx = Mathf.Abs(Mathf.DeltaAngle(e.x, Mathf.Round(e.x / 90f) * 90f));
            float dz = Mathf.Abs(Mathf.DeltaAngle(e.z, Mathf.Round(e.z / 90f) * 90f));
            var s = t.lossyScale;
            float lo = Mathf.Min(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)), hi = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            return dx + dz + (hi / Mathf.Max(lo, 1e-4f) - 1f) * 30f;
        }

        private static GameObject MakeRockTemplate(Transform src, string name)
        {
            var go = MakeTemplate(src, name);
            var e = src.rotation.eulerAngles;
            go.transform.localRotation = Quaternion.Euler(Mathf.Round(e.x / 90f) * 90f, 0f, Mathf.Round(e.z / 90f) * 90f);
            var s = go.transform.localScale;
            float u = Mathf.Pow(Mathf.Abs(s.x * s.y * s.z), 1f / 3f);
            go.transform.localScale = Vector3.one * u;
            // plants growing on the rock in its home scene
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var sh = r.sharedMaterial && r.sharedMaterial.shader ? r.sharedMaterial.shader.name : "";
                if (sh.Contains("Tree") || sh.Contains("Cutout") || sh.Contains("Grass") || sh.Contains("Leaves"))
                    Object.DestroyImmediate(r.gameObject);
            }
            var size = Size(go);
            go.transform.localScale *= RockRefSize / Mathf.Max(size.x, size.z, 0.1f);
            return go;
        }

        // ---- rock selector (grass window): ids "6/rock2", config DisabledRocks
        internal static string RockId(GameObject tpl) => tpl.name.StartsWith("Rock/") ? tpl.name.Substring(5) : tpl.name;
        internal static IEnumerable<(string id, string size)> RockList() => rockPool.Select(t => (RockId(t), Fmt(Size(t))));

        private static HashSet<string> DisabledRockSet() => new HashSet<string>((Plugin.DisabledRocks.Value ?? "")
            .Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));

        internal static bool RockEnabled(string id) => !DisabledRockSet().Contains(id);

        /// <summary>Switch a rock on/off: saved to config, and placed copies on our islands are shown/hidden now.</summary>
        internal static void SetRockEnabled(string id, bool on)
        {
            var set = DisabledRockSet();
            if (on) set.Remove(id); else set.Add(id);
            Plugin.DisabledRocks.Value = string.Join(", ", set.OrderBy(s => s).ToArray());
            Plugin.Instance.Config.Save();
            foreach (var h in Object.FindObjectsOfType<IslandHorizon>().Where(h => h.name.StartsWith("terra incognita")))
                foreach (var t in h.GetComponentsInChildren<Transform>(true))
                    if (t.name == "rock " + id || t.name == "cliff " + id) t.gameObject.SetActive(on);
        }

        /// <summary>Recolour a placed rock for the island's region: Medi/Aestrin blue-grey, everywhere else warm.</summary>
        private static void RegionalRockMaterial(GameObject go, Climate region)
        {
            // our painted shader where the bundle brought it (the vanilla rocks then match the sculpted ones); else the
            // game's own rock material, warm or blue-grey
            var mat = RockSculptor.VanillaRockMaterial(region) ?? (region == Climate.North && rockMatBlue ? rockMatBlue : rockMatWarm);
            if (!mat) return;
            foreach (var rend in go.GetComponentsInChildren<Renderer>(true))
                rend.sharedMaterials = rend.sharedMaterials.Select(_ => mat).ToArray();
        }

        // Pond water: a vanilla Water4 surface (Emerald onsen, scene 39). The tile alone renders flat white: Water4's
        // reflection/specular scripts sit on a parent ("Water4Advanced"-style object). So the nearest ancestor that
        // carries scripts is cloned with them, and every other tile under it is removed — only our one plane is kept.
        private static GameObject pondTemplate;

        private static IEnumerator HarvestPond(string spec)
        {
            // "39=water"
            var kv = (spec ?? "").Split('=');
            if (kv.Length != 2 || !int.TryParse(kv[0].Trim(), out int sceneIndex)) yield break;
            string name = kv[1].Trim();
            Plugin.Log.LogInfo($"Pond: loading scenery scene {sceneIndex} for '{name}'");
            string info = "";
            yield return VisitScene(sceneIndex, scene =>
            {
                var src = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true))
                    .FirstOrDefault(t => Strip(t.name) == name && t.GetComponent<MeshFilter>());
                if (!src) return;
                var top = src;
                for (var t = src.parent; t && !t.GetComponent<IslandSceneryScene>(); t = t.parent)
                    if (t.GetComponents<MonoBehaviour>().Any(m => m && !(m is IslandSceneryScene))) top = t;
                string path = RelPath(top, src);
                var tpl = MakeTemplate(top, "Pond/" + name, keepScripts: true);
                var keep = path.Length == 0 ? tpl.transform : tpl.transform.Find(path);
                // drop the other tiles / pools (renderers that aren't our plane or above it)
                foreach (var r in tpl.GetComponentsInChildren<Renderer>(true).ToList())
                    if (keep && r.transform != keep && !keep.IsChildOf(r.transform)) Object.DestroyImmediate(r.gameObject);
                pondTemplate = tpl;
                info = $"cloned '{top.name}' (scripts: {string.Join(", ", top.GetComponents<MonoBehaviour>().Where(m => m).Select(m => m.GetType().Name).ToArray())}), plane '{path}'";
            });
            if (pondTemplate)
            {
                Plugin.Log.LogInfo($"Pond: {info}, {Fmt(Size(pondTemplate))}");
                TunePondMaterial(pondTemplate);
            }
            else Plugin.Log.LogWarning($"Pond: '{name}' not found in scene {sceneIndex} — ponds stay dry");
        }

        // Water4 without its PlanarReflection script reflects an unset texture: at shallow view angles (Fresnel)
        // the pond turned flat white/opaque, looking down it was clear. A fixed reflection colour (alpha 1 = used
        // instead of the texture) and a steeper Fresnel make it read as water from everywhere. Config PondMaterial:
        // "_Name=x,y,z,w; _Other=…", '*' keeps a component. Original values are logged.
        private static readonly string[] Water4Props =
            { "_DistortParams", "_InvFadeParemeter", "_ReflectionColor", "_BaseColor", "_SpecularColor", "_FresnelScale", "_Shininess" };

        private static void TunePondMaterial(GameObject tpl)
        {
            var r = tpl.GetComponentInChildren<Renderer>(true);
            if (!r || !r.sharedMaterial) return;
            var m = new Material(r.sharedMaterial) { name = r.sharedMaterial.name + " (pond)" };
            var before = new List<string>();
            foreach (var n in Water4Props)
            {
                if (!m.HasProperty(n)) continue;
                before.Add(n == "_FresnelScale" || n == "_Shininess" ? $"{n}={m.GetFloat(n):0.###}" : $"{n}={m.GetVector(n)}");
            }
            Plugin.Log.LogInfo("Pond material: " + string.Join("; ", before.ToArray()));
            foreach (var part in (Plugin.PondMaterial.Value ?? "").Split(';'))
            {
                var kv = part.Split('=');
                if (kv.Length != 2) continue;
                string n = kv[0].Trim();
                if (!m.HasProperty(n)) continue;
                var vals = kv[1].Split(',').Select(s => s.Trim()).ToArray();
                if (vals.Length == 1)
                {
                    if (float.TryParse(vals[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
                        m.SetFloat(n, f);
                    continue;
                }
                var v = m.GetVector(n);
                for (int i = 0; i < 4 && i < vals.Length; i++)
                    if (float.TryParse(vals[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
                        v[i] = f;
                m.SetVector(n, v);
            }
            foreach (var rend in tpl.GetComponentsInChildren<Renderer>(true)) rend.sharedMaterial = m;
        }

        private static string RelPath(Transform top, Transform t)
        {
            var parts = new List<string>();
            for (; t && t != top; t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts.ToArray());
        }

        /// <summary>Oasis pond (marker from the generator): an oval water surface, marker scale = diameters in metres.</summary>
        // (island passed in: marker.root is the world's "_shifting world", not the island — the oasis's grass went onto
        // whichever terrain came first in the world)
        private static void PlacePond(Transform island, Transform marker, Climate region, bool south, System.Random rng, Ground ground)
        {
            // under detail_props: DetailStreamer switches it off when the player is far
            var parent = marker.parent.Find("detail_props");
            if (!parent) parent = marker.parent;
            var ps = marker.parent.lossyScale;
            float a = marker.localScale.x * ps.x * 0.5f, b = marker.localScale.z * ps.z * 0.5f;
            var rot = marker.rotation;
            var water = PondWater.Create(parent, marker.position, ang =>
            {
                // ellipse in the marker's own axes
                var dir = Quaternion.Inverse(rot) * new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                return a * b / Mathf.Sqrt(b * b * dir.x * dir.x + a * a * dir.z * dir.z + 1e-6f);
            });
            PondWater.MakeSource(water, island.name + "@oasis", capacity: 0f, drop: 0f);   // the oasis never runs dry

            // an oasis is green: a grass ring around the water, reeds on the shore, a grove of trees
            System.Func<float, float> shore = ang =>
            {
                var dir = Quaternion.Inverse(rot) * new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                return a * b / Mathf.Sqrt(b * b * dir.x * dir.x + a * a * dir.z * dir.z + 1e-6f) / 1.3f;   // marker had a 1.3 margin
            };
            var terrain = island.GetComponentInChildren<Terrain>(true);
            if (terrain)
            {
                Ponds.GreenRing(terrain, marker.position, shore, Mathf.Max(a, b), outer: 6f);   // (an oasis: green all over its floor)
                var tp = terrain.GetPosition();
                DetailGrass.AddShore(terrain.terrainData, new Vector2(marker.position.x - tp.x, marker.position.z - tp.z), shore, Mathf.Max(a, b));
                var gs = terrain.GetComponent<DetailGrass.GrassState>();
                if (gs) DetailGrass.Apply(terrain, gs.seed, gs.type, gs.region);
            }
            var trees = Templates(region, "tree", south);
            if (trees != null && trees.Count > 0 && ground != null)
            {
                int count = rng.Next(7, 12);
                var tops = SculptedTops(island);
                for (int i = 0; i < count; i++)
                {
                    float ang = (float)rng.NextDouble() * Mathf.PI * 2f;
                    float r = shore(ang) + Mathf.Lerp(2.5f, 10f, (float)rng.NextDouble());
                    var p = marker.position + new Vector3(Mathf.Cos(ang) * r, 0f, Mathf.Sin(ang) * r);
                    if (ground.Slope(p) > 25f) continue;
                    p.y = ground.Height(p);
                    if (InSculpted(tops, p)) continue;                        // not in one of our rocks
                    var tpl = trees[rng.Next(trees.Count)];
                    var go = Object.Instantiate(tpl, parent);
                    go.name = "tree " + tpl.name.Split('/')[1];
                    go.transform.position = p;
                    go.transform.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f) * tpl.transform.localRotation;
                    go.transform.localScale = tpl.transform.localScale * Mathf.Lerp(0.85f, 1.2f, (float)rng.NextDouble());
                }
            }
        }
        // The top of our sculpted rocks on a 2 m grid, from their meshes' vertices (works while the island is switched
        // off, unlike colliders and renderer bounds): world cell → the highest rock there.
        internal static Dictionary<(int, int), float> SculptedTops(Transform island)
        {
            var d = new Dictionary<(int, int), float>();
            foreach (var mf in island.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!mf || !mf.sharedMesh || !mf.name.StartsWith("rock sculpted ") || !mf.sharedMesh.isReadable) continue;
                var m = mf.transform.localToWorldMatrix;
                foreach (var v in mf.sharedMesh.vertices)
                {
                    var w = m.MultiplyPoint3x4(v);
                    var k = (Mathf.FloorToInt(w.x / 2f), Mathf.FloorToInt(w.z / 2f));
                    if (!d.TryGetValue(k, out float y) || w.y > y) d[k] = w.y;
                }
            }
            return d;
        }

        // a plant whose foot is in a sculpted rock: rock over the foot in its own cell, or ≥ 2 m over it in a cell next
        // to it (a wall right there: the crown would be in it)
        internal static bool InSculpted(Dictionary<(int, int), float> tops, Vector3 foot)
        {
            if (tops == null || tops.Count == 0) return false;
            int cx = Mathf.FloorToInt(foot.x / 2f), cz = Mathf.FloorToInt(foot.z / 2f);
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (tops.TryGetValue((cx + dx, cz + dz), out float y) && y > foot.y + (dx == 0 && dz == 0 ? 0.3f : 2f)) return true;
            return false;
        }

        private static GameObject MakeTemplate(Transform src, string name, bool keepScripts = false)
        {
            var go = Object.Instantiate(src.gameObject, root);
            go.name = name;
            go.transform.localPosition = Vector3.zero;
            go.transform.localScale = src.lossyScale;               // keep vanilla world size (Al'Ankh scenery is 0.5)
            // keep the mesh's own tilt (rocks are Z-up exports standing on Euler x=270), drop the scenery yaw
            var e = src.rotation.eulerAngles;
            go.transform.localRotation = Quaternion.Euler(e.x, 0f, e.z);
            if (!keepScripts)
                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(mb);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
            go.SetActive(true);                                     // root is inactive, so nothing shows
            return go;
        }


        /// <summary>World-axis size of all meshes under <paramref name="go"/> (mesh bounds, renderer state irrelevant).</summary>
        private static Vector3 Size(GameObject go)
        {
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!mf.sharedMesh) continue;
                var b = mf.sharedMesh.bounds;
                var m = mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var w = m.MultiplyPoint3x4(c);
                    min = Vector3.Min(min, w); max = Vector3.Max(max, w);
                }
            }
            return float.IsInfinity(min.x) ? Vector3.zero : max - min;
        }

        // marker name → role; old bundles use Lion Fang prop names
        private static string RoleOf(string markerName)
        {
            string n = Strip(markerName);
            if (Roles.Contains(n) || n == "bamboo") return n;
            if (n == "palm_tree_small_2") return "bush";
            if (n.StartsWith("palm_tree_")) return "tree";
            if (n == "rock2") return "rock";
            return null;
        }

        private static readonly HashSet<string> SandLayers =
            new HashSet<string> { "Sand Cracked", "Sand Cracked Wet", "Sand Rocky", "bottom plane" };

        /// <summary>
        /// Replace every marker under <paramref name="island"/> with a prop of its role from <paramref name="region"/>.
        /// Ground rules: on sand only palms (south of the palm line) or bushes — regional broadleaf trees look alien
        /// on a beach; rocks and cliffs only up to a slope limit, sunk by the slope so the downhill side doesn't float.
        /// </summary>
        public static int ReplaceMarkers(Transform island, Climate region, int seed, float latitude, IslandType type)
        {
            var rng = new System.Random(seed);
            bool south = latitude <= Plugin.PalmMaxLatitude.Value;
            var ground = Ground.Of(island);
            int n = 0, dropped = 0;
            var rocks = new List<GameObject>(); var plants = new List<GameObject>();
            if (ground != null) Groves(island, ground, rng, type, region);
            foreach (var marker in island.GetComponentsInChildren<Transform>(true).ToList())
            {
                if (!marker || marker == island) continue;
                if (Strip(marker.name) == "pond")
                {
                    PlacePond(island, marker, region, south, rng, ground); n++;
                    Object.Destroy(marker.gameObject);
                    continue;
                }
                string role = RoleOf(marker.name);
                if (role == null) continue;

                var at = marker.position;
                bool onSand = ground != null && ground.Sand(at) > 0.5f;

                List<GameObject> list;
                if (role == "tree" && type == IslandType.Atoll)
                    list = Templates(Climate.Desert, "tree", true);              // atolls: palms and nothing else
                else if (role == "tree" && onSand)
                    list = south ? Templates(Climate.Desert, "tree", true)       // palms
                                 : Templates(region, "bush", false);             // north beach: bushes only
                else
                    list = Templates(region, role, south);

                if (list == null || list.Count == 0)
                {
                    Object.Destroy(marker.gameObject);
                    dropped++;
                    continue;
                }
                var tpl = list[rng.Next(list.Count)];

                var go = Object.Instantiate(tpl, marker.parent);
                go.name = role + " " + (tpl.name.StartsWith("Rock/") ? tpl.name.Substring(5) : tpl.name.Split('/')[1]);
                go.transform.localPosition = marker.localPosition;

                go.transform.localRotation = marker.localRotation * tpl.transform.localRotation; // marker yaw, then the mesh tilt
                // marker scale 1 == vanilla size, compensating for any scale on our island's parents
                var parentScale = marker.parent.lossyScale;
                var s = tpl.transform.localScale;
                go.transform.localScale = new Vector3(
                    s.x * marker.localScale.x / parentScale.x,
                    s.y * marker.localScale.y / parentScale.y,
                    s.z * marker.localScale.z / parentScale.z);
                if (role == "rock" || role == "cliff" || role == "stone") RegionalRockMaterial(go, region);
                go.SetActive(marker.gameObject.activeSelf);
                Object.Destroy(marker.gameObject);

                // Rocks/cliffs: judge the ground under the WHOLE rock, not the marker point. A 15–20 m rock on a
                // hillside is level with the ground at its pivot and juts out of the slope everywhere else.
                if ((role == "rock" || role == "cliff" || role == "stone") && ground != null && !SitOnGround(go, ground, role))
                {
                    Object.Destroy(go);
                    dropped++;
                    continue;
                }
                n++;
                if (role == "rock" || role == "cliff") rocks.Add(go); else if (role == "tree" || role == "bush") plants.Add(go);
            }
            if (dropped > 0) Plugin.Log.LogInfo($"{island.name}: {dropped} markers left empty (too steep / nothing allowed there)");
            // trees and bushes whose foot is inside a rock (groves and the jungle grid are planted before the rocks are
            // sized): the rock's drawn box, a bit narrower (boxes of turned rocks are wider than the rock), up to its top
            // (from the meshes: Renderer.bounds is empty while the island is switched off)
            // (not rubble: its box is the whole spread of small boulders)
            // (our sculpted rocks: tested by their real top, not a box — SculptedTops; a mesa wall's box is the whole
            // island, a grotto's far bigger than its rock: trees went missing next to them)
            var tops = SculptedTops(island);
            var boxes = rocks.Where(r => r).SelectMany(r => r.GetComponentsInChildren<MeshFilter>(true)).Where(mf => mf.sharedMesh).Select(mf =>
            {
                var mb = mf.sharedMesh.bounds; var m = mf.transform.localToWorldMatrix;
                var wb = new Bounds(m.MultiplyPoint3x4(mb.center), Vector3.zero);
                for (int i = 0; i < 8; i++)
                    wb.Encapsulate(m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
                return wb;
            }).ToList();
            int clipped = 0;
            foreach (var p in plants)
            {
                var q = p.transform.position;
                if (InSculpted(tops, q)) { Object.Destroy(p); clipped++; n--; continue; }
                foreach (var b in boxes)
                {
                    var e = b.extents * 0.8f + new Vector3(0.6f, 0f, 0.6f);
                    if (Mathf.Abs(q.x - b.center.x) < e.x && Mathf.Abs(q.z - b.center.z) < e.z && q.y < b.max.y)
                    { Object.Destroy(p); clipped++; n--; break; }
                }
            }
            if (clipped > 0) Plugin.Debug($"{island.name}: {clipped} trees/bushes removed from inside rocks");
            // trees (not bushes: their low fronds went into the ground) set down to the lowest ground under the trunk
            // (0.6 m round), at most 0.5 m below where they stood: set at the height under its middle, a thick palm on
            // a crest or a slope hung in the air on the downhill side
            if (ground != null)
                foreach (var p in island.GetComponentsInChildren<Transform>(true))
                {
                    if (!p) continue;
                    bool tree = p.name.StartsWith("tree "), bush = p.name.StartsWith("bush ") || p.name.StartsWith("bamboo");
                    if (!tree && !bush) continue;
                    var q = p.position; float mid = ground.Height(q);
                    // the lowest point of its meshes (some models' pivot is not at the foot: the desert cycad
                    // palm_tree_small_1 stood its trunk ~0.5 m over the ground) — lowered until that touches the ground
                    float meshLow = float.MaxValue;
                    // (the nearest LOD only: a far LOD — a flat card at the ground — made the "lowest point" the ground)
                    var lodGroup = p.GetComponentInChildren<LODGroup>(true);
                    var lod0 = lodGroup && lodGroup.GetLODs().Length > 0 ? new HashSet<Renderer>(lodGroup.GetLODs()[0].renderers.Where(r => r)) : null;
                    foreach (var mf in p.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (!mf.sharedMesh) continue;
                        if (lod0 != null && !lod0.Contains(mf.GetComponent<Renderer>())) continue;
                        // the trunk only, not the fronds: palm_tree_small_1 has a leaf mesh set 12 m aside and below its
                        // pivot (F9 dump) — the "lowest point" was a leaf, so the cycad was judged on the ground
                        var rend = mf.GetComponent<Renderer>();
                        if (rend && rend.sharedMaterial && rend.sharedMaterial.name.ToLowerInvariant().Contains("lea")) continue;
                        var mb = mf.sharedMesh.bounds; var m = mf.transform.localToWorldMatrix;
                        for (int i = 0; i < 8; i++)
                            meshLow = Mathf.Min(meshLow, m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))).y);
                    }
                    float pivotOverFoot = meshLow < float.MaxValue ? Mathf.Max(0f, meshLow - q.y) : 0f;   // the foot above the pivot by this much
                    if (bush)
                    {
                        float want = mid - pivotOverFoot - 0.03f;                     // its lowest point just on the ground
                        if (q.y > want + 0.05f) p.position = new Vector3(q.x, want, q.z);   // only ever down
                        continue;
                    }
                    float low = mid;
                    for (int k = 0; k < 8; k++)
                    {
                        float a = k * Mathf.PI / 4f;
                        low = Mathf.Min(low, ground.Height(q + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 0.6f));
                    }
                    low -= pivotOverFoot; mid -= pivotOverFoot;                       // (trees: the same foot offset)
                    // (limited to 0.5 m under the ground at its middle — not under where it stood: a marker's tree by a
                    // pond, whose banks were cut lower after the island was made, must come all the way down)
                    float y = Mathf.Max(low - 0.05f, mid - 0.5f);
                    if (y < q.y) p.position = new Vector3(q.x, y, q.z);
                }
            return n;
        }

        /// <summary>
        /// Trees grew evenly over sand and grass. Now: on sand only every other tree stays (not on atolls, all sand
        /// and palms), and each tree on grass gets 1–3 more around it on grass — groves on the green patches.
        /// Works on the markers before they are replaced, so the extras become regional trees like the rest.
        /// </summary>
        private static void Groves(Transform island, Ground ground, System.Random rng, IslandType type, Climate region)
        {
            var trees = island.GetComponentsInChildren<Transform>(true).Where(t => t && t != island && RoleOf(t.name) == "tree").ToList();
            int thinned = 0, added = 0;
            float density = Plugin.TreeDensityFor(region);          // [Props] TreeDensity: more trees kept on sand, bigger groves
            int Scaled(int n) { float x = n * density; int k = Mathf.FloorToInt(x); return k + (rng.NextDouble() < x - k ? 1 : 0); }
            foreach (var t in trees)
            {
                var p = t.position;
                if (ground.Sand(p) > 0.5f)
                {
                    if (type != IslandType.Atoll && rng.NextDouble() > Mathf.Min(1f, Plugin.TreesOnSand.Value * density)) { Object.DestroyImmediate(t.gameObject); thinned++; }
                    continue;
                }
                if (ground.Grass(p) < 0.5f) continue;
                int extra = Scaled(rng.Next(Plugin.GroveMin.Value, Plugin.GroveMax.Value + 1))
                            + (region == Climate.Asian || region == Climate.Oceanic ? Plugin.LushExtraTrees.Value : 0);   // jungle
                for (int i = 0; i < extra; i++)
                {
                    float a = (float)rng.NextDouble() * Mathf.PI * 2f, r = Mathf.Lerp(3.5f, 8f, (float)rng.NextDouble());
                    var q = p + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    if (ground.Grass(q) < 0.5f || ground.Slope(q) > 25f) continue;
                    q.y = ground.Height(q);
                    var m = Object.Instantiate(t.gameObject, t.parent).transform;
                    m.name = t.name;
                    m.position = q;
                    m.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f) * Quaternion.Inverse(Quaternion.Euler(0f, t.eulerAngles.y, 0f)) * t.rotation;
                    m.localScale = t.localScale * Mathf.Lerp(0.8f, 1.15f, (float)rng.NextDouble());
                    added++;
                }
            }
            // Emerald: bushes on the green become bamboo thickets — 5–10 stems standing close together
            int bamboo = 0;
            if (region == Climate.Asian)
                foreach (var t in island.GetComponentsInChildren<Transform>(true).Where(t => t && t != island && RoleOf(t.name) == "bush").ToList())
                {
                    var p = t.position;
                    if (ground.Grass(p) < 0.5f) continue;
                    if (rng.NextDouble() < 0.5) continue;            // half stay bushes (all bamboo left the jungle bare)
                    t.name = "bamboo";
                    int extra = rng.Next(4, 10);
                    for (int i = 0; i < extra; i++)
                    {
                        float a = (float)rng.NextDouble() * Mathf.PI * 2f, r = Mathf.Lerp(1.2f, 4.5f, (float)rng.NextDouble());
                        var q = p + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                        if (ground.Grass(q) < 0.4f || ground.Slope(q) > 30f) continue;
                        q.y = ground.Height(q);
                        var m = Object.Instantiate(t.gameObject, t.parent).transform;
                        m.name = "bamboo";
                        m.position = q;
                        m.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
                        m.localScale = t.localScale * Mathf.Lerp(0.75f, 1.25f, (float)rng.NextDouble());
                        bamboo++;
                    }
                }
            // Tower karst: jungle on every ledge. The generator's markers are few for that much ledge, and groves around
            // them mostly fall off the narrow ledges, so the ledges are filled directly: a 4 m grid over the terrain,
            // a tree on 40 % of the cells that are above the beach, not too steep and not sand, ≥ 4 m from other trees.
            int ledge = 0;
            if (type == IslandType.KarstTowers && trees.Count > 0)
            {
                var src = trees.FirstOrDefault(t => t) ?? island.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t && t != island && RoleOf(t.name) == "tree");
                var have = island.GetComponentsInChildren<Transform>(true).Where(t => t && t != island && RoleOf(t.name) == "tree")
                    .Select(t => new Vector2(t.position.x, t.position.z)).ToList();
                var c0 = ground.Corner; var sz = ground.Size;
                const float step = 4f;
                for (float x = step * 0.5f; x < sz.x && ledge < 400 && src; x += step)
                    for (float z = step * 0.5f; z < sz.z && ledge < 400; z += step)
                    {
                        if (rng.NextDouble() > 0.4) continue;
                        var q = c0 + new Vector3(x + ((float)rng.NextDouble() - 0.5f) * step, 0f, z + ((float)rng.NextDouble() - 0.5f) * step);
                        q.y = ground.Height(q);
                        if (q.y - island.position.y < 4f || ground.Slope(q) > 32f || ground.Sand(q) > 0.5f) continue;
                        var q2 = new Vector2(q.x, q.z);
                        if (have.Any(h => (h - q2).sqrMagnitude < 16f)) continue;
                        var m = Object.Instantiate(src.gameObject, src.parent).transform;
                        m.name = src.name;
                        m.position = q;
                        m.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f) * Quaternion.Inverse(Quaternion.Euler(0f, src.eulerAngles.y, 0f)) * src.rotation;
                        m.localScale = src.localScale * Mathf.Lerp(0.75f, 1.15f, (float)rng.NextDouble());
                        have.Add(q2);
                        ledge++;
                    }
            }
            // Emerald / Lagoon jungle: the vanilla jungle is a closed wood of tree_east with bushes under it — ours had
            // groves in open grass. The green is filled: a 6 m grid, a tree on 30 % and a bush on 25 % of the cells on
            // grass (not steep, above the beach), trees ≥ 5 m apart. Markers again, so they become the region's props.
            int jungle = 0;
            if ((region == Climate.Asian || region == Climate.Oceanic) && type != IslandType.KarstTowers)
            {
                var treeSrc = island.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t && t != island && RoleOf(t.name) == "tree");
                var bushSrc = island.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t && t != island && RoleOf(t.name) == "bush");
                var have = island.GetComponentsInChildren<Transform>(true).Where(t => t && t != island && RoleOf(t.name) == "tree")
                    .Select(t => new Vector2(t.position.x, t.position.z)).ToList();
                var c0 = ground.Corner; var sz = ground.Size;
                const float step = 6f;
                float treeP = 0.30f * Plugin.JungleDensity.Value, bushP = 0.25f * Plugin.JungleDensity.Value;
                for (float x = step * 0.5f; x < sz.x && jungle < 600; x += step)
                    for (float z = step * 0.5f; z < sz.z && jungle < 600; z += step)
                    {
                        double roll = rng.NextDouble();
                        bool tree = roll < treeP, bush = !tree && roll < treeP + bushP;
                        if (!tree && !bush) continue;
                        var src = tree ? treeSrc : bushSrc;
                        if (!src) continue;
                        var q = c0 + new Vector3(x + ((float)rng.NextDouble() - 0.5f) * step, 0f, z + ((float)rng.NextDouble() - 0.5f) * step);
                        if (ground.Grass(q) < 0.5f || ground.Slope(q) > 28f) continue;
                        q.y = ground.Height(q);
                        if (q.y - island.position.y < 2f) continue;
                        var q2 = new Vector2(q.x, q.z);
                        if (tree) { if (have.Any(h => (h - q2).sqrMagnitude < 25f)) continue; have.Add(q2); }
                        var m = Object.Instantiate(src.gameObject, src.parent).transform;
                        m.name = src.name;
                        m.position = q;
                        m.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f) * Quaternion.Inverse(Quaternion.Euler(0f, src.eulerAngles.y, 0f)) * src.rotation;
                        m.localScale = src.localScale * Mathf.Lerp(0.8f, 1.15f, (float)rng.NextDouble());
                        jungle++;
                    }
            }
            if (thinned + added + bamboo + ledge + jungle > 0)
                Plugin.Log.LogInfo($"{island.name}: groves +{added} trees on grass, -{thinned} on sand, +{bamboo} bamboo, +{ledge} on karst ledges, +{jungle} jungle");
        }

        // region's own set, else the desert one (always harvested from Lion Fang) — but palms never north of the line
        private static List<GameObject> Templates(Climate region, string role, bool palmsAllowed)
        {
            if (role == "bamboo")
            {
                var bush = Templates(region, "bush", palmsAllowed);
                var bb = bush?.Where(x => x.name.EndsWith("/bamboo")).ToList();
                return bb != null && bb.Count > 0 ? bb : bush;
            }
            if (role == "rock" || role == "cliff" || role == "stone")
            {
                var on = rockPool.Where(t => RockEnabled(RockId(t))).ToList();
                return on.Count > 0 ? on : null;
            }
            if (sets.TryGetValue(region, out var r) && r.TryGetValue(role, out var l) && l.Count > 0) return l;
            if (role == "cliff") return Templates(region, "rock", palmsAllowed);
            if (region != Climate.Desert && (palmsAllowed || role == "rock")) return Templates(Climate.Desert, role, true);
            return null;
        }

        /// <summary>
        /// Sample the terrain under the rock's footprint (5×5 over 80 % of its world bounds). Too much relief = the
        /// ground is steeper than the role allows → false. Otherwise drop the rock so its lowest point sits below
        /// the LOWEST ground under it: buried on the uphill side, flush on the downhill side, never hanging.
        /// </summary>
        private static bool SitOnGround(GameObject go, Ground ground, string role)
        {
            var (min, max) = Bounds(go);
            if (float.IsInfinity(min.x)) return true;
            var c = (min + max) * 0.5f;
            var half = (max - min) * 0.4f;
            float hMin = float.MaxValue, hMax = float.MinValue;
            for (int i = 0; i < 5; i++)
                for (int j = 0; j < 5; j++)
                {
                    float h = ground.Height(new Vector3(c.x + half.x * (i / 2f - 1f), 0f, c.z + half.z * (j / 2f - 1f)));
                    hMin = Mathf.Min(hMin, h); hMax = Mathf.Max(hMax, h);
                }
            float span = Mathf.Max(2f * Mathf.Max(half.x, half.z), 0.5f);
            float slope = Mathf.Atan((hMax - hMin) / span) * Mathf.Rad2Deg;
            float limit = role == "stone" ? 90f : role == "cliff" ? Plugin.CliffMaxSlope.Value : Plugin.RockMaxSlope.Value;
            if (slope > limit) return false;
            float height = max.y - min.y;
            float bury = height * (role == "cliff" ? 0.25f : role == "stone" ? 0.3f : 0.15f);
            // tall, narrow rocks (Medi's rock2 is 12.7 m high on a 8 m base) stood like menhirs: bury them until no
            // more than RockMaxVisibleRatio × their base width sticks out
            float width = Mathf.Max(max.x - min.x, max.z - min.z);
            bury = Mathf.Max(bury, height - width * Plugin.RockMaxVisibleRatio.Value);
            go.transform.position += Vector3.up * (hMin - bury - min.y);
            return true;
        }

        private static (Vector3 min, Vector3 max) Bounds(GameObject go)
        {
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!mf.sharedMesh) continue;
                var b = mf.sharedMesh.bounds;
                var m = mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var p = m.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                }
            }
            return (min, max);
        }

        /// <summary>Splat + height lookup on the island's terrain at a world position.</summary>
        private class Ground
        {
            private Terrain t;
            private float[,,] alpha;
            private bool[] sandLayer;
            private int grassLayer = -1;

            public static Ground Of(Transform island)
            {
                var t = island.GetComponentInChildren<Terrain>(true);
                if (!t || !t.terrainData) return null;
                var td = t.terrainData;
                var layers = td.terrainLayers ?? new TerrainLayer[0];
                return new Ground
                {
                    t = t,
                    alpha = td.GetAlphamaps(0, 0, td.alphamapResolution, td.alphamapResolution),
                    sandLayer = layers.Select(l => l && SandLayers.Contains(l.name)).ToArray(),
                    grassLayer = System.Array.FindIndex(layers, l => l && l.name == "Grass Leaves"),
                };
            }

            private Vector2 Norm(Vector3 world)
            {
                var p = world - t.GetPosition();
                var s = t.terrainData.size;
                return new Vector2(Mathf.Clamp01(p.x / s.x), Mathf.Clamp01(p.z / s.z));
            }

            public float Sand(Vector3 world)
            {
                var uv = Norm(world);
                int res = alpha.GetLength(0);
                int x = Mathf.Clamp(Mathf.RoundToInt(uv.x * (res - 1)), 0, res - 1);
                int z = Mathf.Clamp(Mathf.RoundToInt(uv.y * (res - 1)), 0, res - 1);
                float s = 0f;
                for (int l = 0; l < sandLayer.Length && l < alpha.GetLength(2); l++) if (sandLayer[l]) s += alpha[z, x, l];
                return s;
            }

            public float Height(Vector3 world) => t.SampleHeight(world) + t.GetPosition().y;
            public Vector3 Corner => t.GetPosition();
            public Vector3 Size => t.terrainData.size;

            public float Grass(Vector3 world)
            {
                if (grassLayer < 0) return 0f;
                var uv = Norm(world);
                int res = alpha.GetLength(0);
                int x = Mathf.Clamp(Mathf.RoundToInt(uv.x * (res - 1)), 0, res - 1);
                int z = Mathf.Clamp(Mathf.RoundToInt(uv.y * (res - 1)), 0, res - 1);
                return alpha[z, x, grassLayer];
            }

            public float Slope(Vector3 world)
            {
                var uv = Norm(world);
                return t.terrainData.GetSteepness(uv.x, uv.y);
            }
        }

        // "scenes=38,13; tree=tree_east,tree_east 2; bush=bush; rock=rock2"
        private static (List<int> scenes, Dictionary<string, List<string>> roles) ParseSpec(string spec)
        {
            var scenes = new List<int>();
            var roles = new Dictionary<string, List<string>>();
            foreach (var part in (spec ?? "").Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) continue;
                string key = part.Substring(0, eq).Trim().ToLowerInvariant();
                var values = part.Substring(eq + 1).Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                if (key == "scenes") scenes.AddRange(values.Select(v => int.TryParse(v, out int i) ? i : -1).Where(i => i > 0));
                else roles[key] = values;
            }
            return (scenes, roles);
        }

        private static string Fmt(Vector3 v) => $"{v.x:0.#}×{v.y:0.#}×{v.z:0.#}m";

        internal static string Strip(string n) => Regex.Replace(n, @"(\s*\(\d+\)|\s*\(Clone\))+$", "").Trim();
    }
}
