using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TerraIncognita
{
    /// <summary>
    /// Recon helpers for development. Every dump is a plain-text file in
    /// BepInEx\plugins\TerraIncognita\dumps — send these files to the developer.
    ///   F8  — nearest island: full hierarchy, components, fields, terrain, materials, depth cache
    ///   F9  — object under the crosshair: its whole root (works for scenery scenes, wrecks, props)
    ///   F10 — world: every island and port in real coordinates, regions, mission routes, offset
    /// </summary>
    internal class DevTools : MonoBehaviour
    {
        private static string DumpDir => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Plugin.Instance.Info.Location), "dumps");

        private void Update()
        {
            if (!Plugin.DevToolsEnabled.Value) return;
            try
            {
                if (Plugin.DumpIslandKey.Value.IsDown()) DumpNearestIsland();
                if (Plugin.DumpLookKey.Value.IsDown()) DumpLookedAt();
                if (Plugin.DumpWorldKey.Value.IsDown()) DumpWorld();
                if (Plugin.NearestModIslandsKey.Value.IsDown()) ReportModIslands();
                if (Plugin.ItemCensusKey.Value.IsDown()) ItemCensus();
                else if (Plugin.BoatCensusKey.Value.IsDown()) BoatCensus();
                else if (Plugin.SceneryCensusKey.Value.IsDown() && !censusRunning) StartCoroutine(SceneryCensus());
            }
            catch (Exception e) { Plugin.Log.LogError("Dump failed: " + e); }
        }

        // ------------------------------------------------------------------ Ctrl+F7: item census
        // items.txt: prefab index | name | item kind (+ food); the world's WorldItemSpawners grouped by item (count,
        // respawn, a few places with their island); Seagulls flocks (path, height, particles, sound).
        private static void ItemCensus()
        {
            var sb = new StringBuilder();
            var dir = PrefabsDirectory.instance ? PrefabsDirectory.instance.directory : null;
            sb.AppendLine($"# item prefabs: index | name | kind");
            if (dir != null)
                for (int i = 0; i < dir.Length; i++)
                {
                    var go = dir[i];
                    if (!go) continue;
                    var si = go.GetComponent<ShipItem>();
                    string kind = si ? si.GetType().Name : "-";
                    if (go.GetComponent<Good>()) kind += " good";
                    if (go.GetComponent<ShipItemFood>()) kind += " food";
                    sb.AppendLine($"{i} | {go.name} | {kind}");
                }

            sb.AppendLine().AppendLine("# world item spawners: item | count | respawn (min–max) | some places (island)");
            var spawners = Resources.FindObjectsOfTypeAll<WorldItemSpawner>().Where(s => s && s.gameObject.scene.IsValid()).ToList();
            foreach (var g in spawners.GroupBy(s => s.itemPrefab ? s.itemPrefab.name : "-").OrderByDescending(g => g.Count()))
            {
                var places = g.Take(4).Select(s =>
                {
                    var t = s.transform; string top = t.name;
                    for (var p = t.parent; p; p = p.parent) { top = p.name; if (p.parent && p.parent.name == "_shifting world") break; }
                    return top;
                }).Distinct().ToArray();
                sb.AppendLine($"{g.Key} | {g.Count()} | {g.Min(s => s.respawnTime):0}–{g.Max(s => s.respawnTime):0} | {string.Join("; ", places)}");
            }

            sb.AppendLine().AppendLine("# seagull flocks: path | local pos | particles max | clip");
            foreach (var s in Resources.FindObjectsOfTypeAll<Seagulls>().Where(s => s && s.gameObject.scene.IsValid()))
            {
                var t = s.transform; string path = t.name;
                for (var p = t.parent; p && p.name != "_shifting world"; p = p.parent) path = p.name + "/" + path;
                var ps = s.GetComponent<ParticleSystem>(); var au = s.GetComponent<AudioSource>();
                sb.AppendLine($"{path} | {t.localPosition} | {(ps ? ps.main.maxParticles.ToString() : "-")} | {(au && au.clip ? au.clip.name : "-")}");
            }
            Directory.CreateDirectory(DumpDir);
            string file = System.IO.Path.Combine(DumpDir, "items.txt");
            File.WriteAllText(file, sb.ToString());
            Plugin.Log.LogInfo("Item census → " + file);
        }

        // ------------------------------------------------------------------ F7: scenery census

        private bool censusRunning;

        private class PropStat
        {
            public int count; public HashSet<string> islands = new HashSet<string>();
            public string mesh, mat; public Vector3 size; public bool collider;
        }

        /// <summary>
        /// Loads every island's scenery scene in turn (one at a time, unloads it again) and writes, per region,
        /// the props found there: object name, count, world size (W×H×D, m), collider, material/shader.
        /// Source for regional tree/rock/cliff sets. Region = letter in the island name (A, E, M, Lagoon).
        /// </summary>
        private IEnumerator SceneryCensus()
        {
            censusRunning = true;
            var islands = (IslandDistanceTracker.instance ? IslandDistanceTracker.instance.islands : null)
                // Lagoon's scenery scenes (26–29, 31: Crest clip surfaces, Water4 ponds) kill the game when loaded from afar
                ?.Where(h => h && h.islandIndex > 0 && !h.name.Contains("Lagoon")).GroupBy(h => h.islandIndex).Select(g => g.First())
                .OrderBy(h => h.islandIndex).ToList() ?? new List<IslandHorizon>();
            Notify($"Scenery census: {islands.Count} scenes, this takes a minute…");

            var regions = new SortedDictionary<string, Dictionary<string, PropStat>>();
            int done = 0;
            foreach (var h in islands)
            {
                var m = System.Text.RegularExpressions.Regex.Match(h.name, @"^island \d+\s+(A|E|M|Lagoon)\b");
                string region = m.Success ? m.Groups[1].Value : "other";
                if (!regions.TryGetValue(region, out var stats)) regions[region] = stats = new Dictionary<string, PropStat>();
                string isl = h.name.Replace("island ", "").Trim();

                yield return PropLibrary.VisitScene(h.islandIndex, scene =>
                {
                    foreach (var mf in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MeshFilter>(true)))
                    {
                        if (!mf.sharedMesh || mf.name.StartsWith("_mesh combiner")) continue;
                        string key = PropLibrary.Strip(mf.name);
                        if (!stats.TryGetValue(key, out var s))
                        {
                            var r = mf.GetComponent<Renderer>();
                            var mat = r ? r.sharedMaterial : null;
                            stats[key] = s = new PropStat
                            {
                                mesh = mf.sharedMesh.name,
                                mat = mat ? $"{mat.name} [{(mat.shader ? mat.shader.name : "-")}]" : "-",
                                size = WorldSize(mf),
                                collider = mf.GetComponentInChildren<Collider>(true) || mf.GetComponentInParent<Collider>(),
                            };
                        }
                        s.count++;
                        s.islands.Add(isl);
                    }
                });
                if (++done % 5 == 0) Notify($"Scenery census: {done}/{islands.Count}");
            }

            var sb = new StringBuilder();
            sb.AppendLine("# per region: count | object name | mesh | world size W×H×D m | col = has collider | islands | material [shader]");
            sb.AppendLine("# top 45 by count + every big object (any side > 12 m) — cliffs, big rocks, buildings");
            foreach (var kv in regions)
            {
                var isl = kv.Value.Values.SelectMany(s => s.islands).Distinct().OrderBy(x => x).ToArray();
                Header(sb, $"REGION {kv.Key}: {string.Join(", ", isl)}");
                var all = kv.Value.OrderByDescending(p => p.Value.count).ToList();
                var top = all.Take(45).ToList();
                var big = all.Skip(45).Where(p => Mathf.Max(p.Value.size.x, p.Value.size.y, p.Value.size.z) > 12f).Take(40);
                foreach (var p in top.Concat(big))
                {
                    var s = p.Value;
                    string where = s.islands.Count > 3 ? $"{s.islands.Count} islands" : string.Join("; ", s.islands.ToArray());
                    sb.AppendLine($"{s.count,5} | {p.Key} | {s.mesh} | {s.size.x:0.#}×{s.size.y:0.#}×{s.size.z:0.#} | {(s.collider ? "col" : "-")} | {where} | {s.mat}");
                }
                sb.AppendLine($"  ({all.Count} distinct objects in total)");
            }
            Write("scenery_census", sb);
            censusRunning = false;
        }

        // ------------------------------------------------------------------ Shift+F7: boat census (wrecks)

        /// <summary>
        /// Every boat in memory (scene instances and prefabs): its visual model (boatModel) as a list of meshes —
        /// path, mesh, vertices, world size, material — plus masts and the shipyards' sail prefabs. Source for wrecks:
        /// which meshes are hull/deck/trim (keep) and which are sails, ropes, fittings (drop).
        /// </summary>
        private void BoatCensus()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# boats: per boat — masts, then meshes under boatModel: count × path | mesh (verts) | size W×H×D m | material [shader] | inactive?");
            // vanilla boats only (numbers below 150; modded boats are 180+ and filled the file before)
            var boats = Resources.FindObjectsOfTypeAll<BoatRefs>().Where(b => b && b.gameObject.scene.IsValid())
                .Where(b => { var m = System.Text.RegularExpressions.Regex.Match(b.name, @"\((\d+)\)"); return m.Success && int.Parse(m.Groups[1].Value) < 150; })
                .GroupBy(b => StripClone(b.name).Trim()).Select(g => g.OrderByDescending(b => b.gameObject.scene.IsValid()).First())
                .OrderBy(b => b.name).ToList();
            foreach (var b in boats)
            {
                var model = b.boatModel ? b.boatModel : b.transform;
                Header(sb, $"{b.name}  ({(b.gameObject.scene.IsValid() ? "scene '" + b.gameObject.scene.name + "'" : "prefab")}, model '{model.name}')");
                foreach (var m in b.masts ?? new Mast[0])
                    if (m) sb.AppendLine($"  mast {Path(m.transform).Replace(Path(b.transform), "~")} h={m.mastHeight:0.#} sails={m.sails?.Count ?? 0} " +
                                         $"start={(m.startSailPrefab ? m.startSailPrefab.name : "-")}{(m.startSailPrefabs != null && m.startSailPrefabs.Length > 0 ? " +" + string.Join(",", m.startSailPrefabs.Where(s => s).Select(s => s.name).ToArray()) : "")}");
                var groups = model.GetComponentsInChildren<MeshFilter>(true).Where(mf => mf.sharedMesh)
                    .GroupBy(mf => RelPath(model, mf.transform, 3) + " | " + mf.sharedMesh.name)
                    .OrderBy(g => g.Key).ToList();
                foreach (var g in groups.Take(45))
                {
                    var mf = g.First();
                    var r = mf.GetComponent<Renderer>();
                    var size = WorldSize(mf);
                    sb.AppendLine($"  {g.Count(),3} × {g.Key} ({mf.sharedMesh.vertexCount}) | {size.x:0.#}×{size.y:0.#}×{size.z:0.#} | " +
                                  $"{(r && r.sharedMaterial ? r.sharedMaterial.name + " [" + r.sharedMaterial.shader.name + "]" : "-")}" +
                                  $"{(mf.gameObject.activeInHierarchy ? "" : " | inactive")}");
                }
                if (groups.Count > 45) sb.AppendLine($"  ... {groups.Count - 45} more mesh groups");
            }

            Header(sb, "SHIPYARD SAIL PREFABS");
            var sails = Resources.FindObjectsOfTypeAll<Shipyard>().Where(s => s && s.sailPrefabs != null)
                .SelectMany(s => s.sailPrefabs).Where(p => p).Select(p => p.name).Distinct().OrderBy(n => n);
            sb.AppendLine(string.Join(", ", sails.ToArray()));

            Header(sb, "MATERIALS that look like wreck/dirt/rot");
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>().Where(m => m && System.Text.RegularExpressions.Regex.IsMatch(m.name, "(?i)dirt|wreck|rot|old|broken|burn"))
                         .GroupBy(m => m.name).Select(g => g.First()).Take(40))
                sb.AppendLine($"  {m.name} [{(m.shader ? m.shader.name : "-")}] tex={(m.HasProperty("_MainTex") && m.mainTexture ? m.mainTexture.name : "-")}");
            Write("boat_census", sb);
        }

        private static string RelPath(Transform top, Transform t, int maxDepth)
        {
            var parts = new List<string>();
            for (; t && t != top; t = t.parent) parts.Insert(0, StripClone(t.name));
            if (parts.Count > maxDepth) parts = parts.Take(maxDepth - 1).Concat(new[] { "…" + parts.Last() }).ToList();
            return string.Join("/", parts.ToArray());
        }

        /// <summary>Axis-aligned world size of a mesh (works with disabled renderers, unlike Renderer.bounds).</summary>
        private static Vector3 WorldSize(MeshFilter mf)
        {
            var b = mf.sharedMesh.bounds;
            var m = mf.transform.localToWorldMatrix;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var w = m.MultiplyPoint3x4(c);
                min = Vector3.Min(min, w); max = Vector3.Max(max, w);
            }
            return max - min;
        }

        // ------------------------------------------------------------------ F11

        /// <summary>Console + log: the 5 nearest mod islands with distance, compass direction and health.</summary>
        private void ReportModIslands()
        {
            var me = Refs.observerMirror ? Refs.observerMirror.transform.position : Vector3.zero;
            var islands = FindObjectsOfType<IslandHorizon>().Where(h => h.name.StartsWith("terra incognita")).ToList();
            Notify($"Terra Incognita: {islands.Count} mod islands in the world");
            foreach (var h in islands.OrderBy(h => Vector3.Distance(h.transform.position, me)).Take(5))
            {
                var d = h.transform.position - me;
                float dist = new Vector2(d.x, d.z).magnitude;
                float bearing = (Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg + 360f) % 360f;   // 0 = north (+z), 90 = east
                string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
                string compass = names[Mathf.RoundToInt(bearing / 45f) % 8];
                int renderers = h.GetComponentsInChildren<Renderer>().Count(r => r.enabled && r.gameObject.activeInHierarchy);
                int terrains = h.GetComponentsInChildren<Terrain>().Length;
                Notify($"  {h.name}: {dist:F0}u {compass} ({bearing:F0}°), y={h.transform.position.y:F1}, " +
                       $"active={h.gameObject.activeInHierarchy}, terrains={terrains}, renderers={renderers}");
            }
        }

        // ------------------------------------------------------------------ F8

        private void DumpNearestIsland()
        {
            var player = Refs.observerMirror ? Refs.observerMirror.transform.position : Vector3.zero;
            var island = FindObjectsOfType<IslandHorizon>()
                .OrderBy(h => Vector3.Distance(h.GetPosition(), player)).FirstOrDefault();
            if (!island) { Notify("No IslandHorizon found"); return; }

            var sb = new StringBuilder();
            Header(sb, "NEAREST ISLAND " + island.name);
            sb.AppendLine($"distance to player: {Vector3.Distance(island.GetPosition(), player):F1}");
            DumpTree(sb, island.transform, 0, 4);

            // scenery scene of this island, if loaded
            if (island.islandIndex > 0)
            {
                var sc = SceneManager.GetSceneByBuildIndex(island.islandIndex);
                if (sc.isLoaded)
                {
                    // names only: what kinds of props exist and how many; details via F9 on a specific prop
                    Header(sb, $"SCENERY SCENE '{sc.name}' (build index {island.islandIndex}) — prop census");
                    var census = sc.GetRootGameObjects()
                        .SelectMany(r => r.GetComponentsInChildren<MeshFilter>(true))
                        .Where(mf => mf.sharedMesh)
                        .GroupBy(mf => mf.sharedMesh.name)
                        .OrderByDescending(g => g.Count());
                    foreach (var g in census.Take(60))
                    {
                        var r = g.First().GetComponent<Renderer>();
                        sb.AppendLine($"  {g.Count(),4} x mesh '{g.Key}'  e.g. {g.First().name}  mat: {(r ? Mat(r.sharedMaterial) : "-")}");
                    }
                }
                else sb.AppendLine($"\nScenery scene {island.islandIndex} not loaded (come closer than {island.islandLoadDistance}).");
            }
            Write("island_" + Safe(island.name), sb);
        }

        // ------------------------------------------------------------------ F9

        private void DumpLookedAt()
        {
            var cam = Camera.main ? Camera.main : FindObjectsOfType<Camera>().FirstOrDefault(c => c.enabled && c.targetTexture == null);
            if (!cam) { Notify("No camera"); return; }
            var ray = new Ray(cam.transform.position, cam.transform.forward);
            var hits = Physics.RaycastAll(ray, 300f, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance).ToArray();
            if (hits.Length == 0) { Notify("Nothing under the crosshair (needs a collider)"); return; }

            var sb = new StringBuilder();
            Header(sb, "LOOKED AT");
            foreach (var h in hits.Take(5))
                sb.AppendLine($"  hit {h.distance:F1}m: {Path(h.collider.transform)}  [scene '{h.collider.gameObject.scene.name}', layer {LayerMask.LayerToName(h.collider.gameObject.layer)}]");

            // renderers the ray passes through, colliders or not (water planes have none): bounds hit before the
            // first collider + 5 m — with materials, shaders and scripts, to find what draws e.g. a lake
            Header(sb, "RENDERERS ON THE RAY (no collider needed)");
            float limit = hits[0].distance + 5f;
            foreach (var r in FindObjectsOfType<Renderer>()
                         .Select(r => (r, ok: r.bounds.IntersectRay(ray, out float d), d: r.bounds.IntersectRay(ray, out float d2) ? d2 : 0f))
                         .Where(x => x.ok && x.d <= limit && x.r.enabled && x.r.gameObject.activeInHierarchy)
                         .OrderBy(x => x.d).Take(25))
            {
                var rr = r.r;
                string mats = string.Join(", ", rr.sharedMaterials.Select(m => m ? $"{m.name} [{(m.shader ? m.shader.name : "-")}] queue {m.renderQueue}" : "null").ToArray());
                string comps = string.Join(", ", rr.GetComponents<Component>().Select(c => c ? c.GetType().Name : "-").ToArray());
                var mf = rr.GetComponent<MeshFilter>();
                sb.AppendLine($"  {r.d:F1}m {Path(rr.transform)} | y {rr.bounds.center.y:F1} size {rr.bounds.size} | mesh {(mf && mf.sharedMesh ? mf.sharedMesh.name : "-")} | {mats} | {comps}");
            }

            var target = hits[0].collider.transform;
            // walk up to the prop's own root, but never into a scenery-scene root / _shifting world
            // (those hold the whole island and blow past the size cap)
            var top = target;
            for (int i = 0; i < 3 && top.parent && top.parent.parent
                            && !top.parent.GetComponent<IslandSceneryScene>()
                            && !top.parent.GetComponent<FloatingOriginManager>()
                            && !top.parent.GetComponent<IslandHorizon>(); i++)
                top = top.parent;
            Header(sb, "CONTEXT: " + Path(top));
            DumpTree(sb, top, 0, 4);
            Write("look_" + Safe(target.name), sb);
        }

        // ------------------------------------------------------------------ F10

        private void DumpWorld()
        {
            var fom = FloatingOriginManager.instance;
            var sb = new StringBuilder();
            Header(sb, "WORLD");
            sb.AppendLine($"outCurrentOffset: {V(fom.outCurrentOffset)}   shiftDistance: {fom.shiftDistance}");
            var me = Refs.observerMirror.transform;
            sb.AppendLine($"player real: {V(fom.ShiftingPosToRealPos(me.position))}  globe: {V(fom.GetGlobeCoords(me))}");

            sb.AppendLine("\n# islands: name | islandIndex | real pos | rotY | scale | load/emerged/sunk distances");
            foreach (var h in FindObjectsOfType<IslandHorizon>().OrderBy(h => h.islandIndex))
                sb.AppendLine($"{h.name} | {h.islandIndex} | {V(fom.ShiftingPosToRealPos(h.GetPosition()))} | {h.transform.eulerAngles.y:F0} | {V(h.transform.lossyScale)} | {h.islandLoadDistance}/{h.fullyEmergedDistance}/{h.fullySunkDistance}");

            sb.AppendLine("\n# ports: index | name | region | island | real pos | hub | destinations");
            foreach (var p in (Port.ports ?? new Port[0]).Where(p => p))
            {
                var dests = p.GetDestinationPorts() ?? new Port[0];
                sb.AppendLine($"{p.portIndex} | {p.GetPortName()} | {p.region} | {(p.island ? p.island.name : "-")} | {V(fom.ShiftingPosToRealPos(p.transform.position))} | {p.hubPort} | {string.Join(",", dests.Where(d => d).Select(d => d.portIndex.ToString()).ToArray())}");
            }

            sb.AppendLine("\n# Region trigger volumes");
            foreach (var r in FindObjectsOfType<Region>())
            {
                var col = r.GetComponent<Collider>();
                sb.AppendLine($"{r.name} | {r.portRegion} | center {V(fom.ShiftingPosToRealPos(col ? col.bounds.center : r.transform.position))} | size {(col ? V(col.bounds.size) : "-")}");
            }

            sb.AppendLine("\n# loaded scenes");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                sb.AppendLine($"{s.buildIndex} | {s.name} | roots: {string.Join(", ", s.GetRootGameObjects().Select(g => g.name).Take(15).ToArray())}");
            }

            Write("world", sb);

            // separate file: long list, only grepped by name when needed
            var shaders = new StringBuilder();
            foreach (var n in Resources.FindObjectsOfTypeAll<Shader>().Select(s => s.name).Distinct().OrderBy(n => n))
                shaders.AppendLine(n);
            Write("shaders", shaders);
        }

        // ------------------------------------------------------------------ tree / component dump

        private static void DumpTree(StringBuilder sb, Transform t, int depth, int maxDepth)
        {
            string pad = new string(' ', depth * 2);
            var go = t.gameObject;
            sb.AppendLine($"{pad}- {go.name}{(go.activeSelf ? "" : " (INACTIVE)")}  layer={LayerMask.LayerToName(go.layer)} tag={go.tag} " +
                          $"lpos={V(t.localPosition)} lrot={V(t.localEulerAngles)} lscale={V(t.localScale)}");
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) { sb.AppendLine($"{pad}    [missing script]"); continue; }
                if (c is Transform) continue;
                sb.AppendLine($"{pad}    [{c.GetType().FullName}]{(c is Behaviour b && !b.enabled ? " (disabled)" : "")}");
                DumpComponent(sb, c, pad + "      ");
            }
            if (depth >= maxDepth)
            {
                if (t.childCount > 0) sb.AppendLine($"{pad}  ... {t.childCount} children not shown");
                return;
            }
            // collapse long runs of identical props (trees, rocks) to keep files readable
            var groups = t.Cast<Transform>().GroupBy(c => StripClone(c.name)).ToList();
            foreach (var g in groups)
            {
                var list = g.ToList();
                DumpTree(sb, list[0], depth + 1, maxDepth);
                if (list.Count > 1) sb.AppendLine($"{pad}  ... +{list.Count - 1} more '{g.Key}'");
            }
        }

        private static readonly HashSet<string> SkipFields = new HashSet<string> { "useGUILayout", "runInEditMode", "hideFlags", "name", "tag" };

        private static void DumpComponent(StringBuilder sb, Component c, string pad)
        {
            switch (c)
            {
                case MeshFilter mf:
                    sb.AppendLine($"{pad}mesh: {(mf.sharedMesh ? $"{mf.sharedMesh.name} ({mf.sharedMesh.vertexCount} verts, bounds {V(mf.sharedMesh.bounds.size)})" : "null")}");
                    return;
                case Renderer r:
                    sb.AppendLine($"{pad}enabled={r.enabled} bounds={V(r.bounds.size)}");
                    foreach (var m in r.sharedMaterials)
                    {
                        sb.AppendLine($"{pad}material: {Mat(m)}");
                        if (m && m.mainTexture) sb.AppendLine($"{pad}  tex {m.mainTexture.width}px tiling {m.mainTextureScale} -> {SaveTexture(m.mainTexture)}");
                    }
                    var rm = r.GetComponent<MeshFilter>()?.sharedMesh;
                    if (rm && rm.isReadable && rm.uv.Length > 0)
                    {
                        var uvs = rm.uv;
                        sb.AppendLine($"{pad}uv range x {uvs.Min(u => u.x):0.##}..{uvs.Max(u => u.x):0.##}, y {uvs.Min(u => u.y):0.##}..{uvs.Max(u => u.y):0.##}");
                    }
                    else if (rm) sb.AppendLine($"{pad}uv: mesh not readable");
                    return;
                case Terrain tr:
                    var td = tr.terrainData;
                    sb.AppendLine($"{pad}terrainData: {(td ? td.name : "null")} size={(td ? V(td.size) : "-")} heightRes={(td ? td.heightmapResolution : 0)} alphaRes={(td ? td.alphamapResolution : 0)}");
                    sb.AppendLine($"{pad}materialType={tr.materialType} template: {Mat(tr.materialTemplate)} drawInstanced={tr.drawInstanced}");
                    sb.AppendLine($"{pad}pixelError={tr.heightmapPixelError} basemapDist={tr.basemapDistance} shadows={tr.shadowCastingMode}treeDist={tr.treeDistance} detailDist={tr.detailObjectDistance}");
                    if (td)
                    {
                        // coverage per layer (sampled every 4th texel) — tells whether the splat map is really painted
                        var cover = new float[td.alphamapLayers];
                        if (td.alphamapLayers > 0)
                        {
                            int ar = td.alphamapResolution, samples = 0;
                            var a = td.GetAlphamaps(0, 0, ar, ar);
                            for (int z = 0; z < ar; z += 4)
                                for (int x = 0; x < ar; x += 4, samples++)
                                    for (int l = 0; l < cover.Length; l++) cover[l] += a[z, x, l];
                            for (int l = 0; l < cover.Length; l++) cover[l] = cover[l] * 100f / Mathf.Max(1, samples);
                        }
                        var tl = td.terrainLayers ?? new TerrainLayer[0];
                        for (int li = 0; li < tl.Length; li++)
                            if (tl[li]) sb.AppendLine($"{pad}layer: {tl[li].name} coverage={(li < cover.Length ? cover[li] : 0):F1}% diffuse={(tl[li].diffuseTexture ? tl[li].diffuseTexture.name : "-")} normal={(tl[li].normalMapTexture ? tl[li].normalMapTexture.name : "-")} tile={V(tl[li].tileSize)}");
                        foreach (var tp in td.treePrototypes) sb.AppendLine($"{pad}tree prototype: {(tp.prefab ? tp.prefab.name : "-")}");
                        sb.AppendLine($"{pad}tree instances: {td.treeInstanceCount}, detail prototypes: {td.detailPrototypes.Length}");
                    }
                    return;
                case TerrainCollider tc:
                    sb.AppendLine($"{pad}terrainData: {(tc.terrainData ? tc.terrainData.name : "null")} material={(tc.sharedMaterial ? tc.sharedMaterial.name : "-")}");
                    return;
                case MeshCollider mc:
                    sb.AppendLine($"{pad}mesh: {(mc.sharedMesh ? mc.sharedMesh.name : "null")} convex={mc.convex} trigger={mc.isTrigger} physMat={(mc.sharedMaterial ? mc.sharedMaterial.name : "-")}");
                    return;
                case Collider col:
                    sb.AppendLine($"{pad}trigger={col.isTrigger} bounds={V(col.bounds.size)} physMat={(col.sharedMaterial ? col.sharedMaterial.name : "-")}");
                    return;
            }

            // generic: public + [SerializeField] fields of game / Crest scripts
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (var type = c.GetType(); type != null && type != typeof(MonoBehaviour) && type != typeof(Component); type = type.BaseType)
            {
                foreach (var f in type.GetFields(F | BindingFlags.DeclaredOnly))
                {
                    if (SkipFields.Contains(f.Name)) continue;
                    if (!f.IsPublic && f.GetCustomAttributes(typeof(SerializeField), true).Length == 0) continue;
                    object v;
                    try { v = f.GetValue(c); } catch { continue; }
                    sb.AppendLine($"{pad}{f.Name} = {Fmt(v)}");
                }
            }
        }

        // ------------------------------------------------------------------ formatting

        private static string Fmt(object v, int depth = 0)
        {
            switch (v)
            {
                case null: return "null";
                case UnityEngine.Object o: return o ? $"<{o.GetType().Name}:{o.name}>" : "null(destroyed)";
                case string s: return '"' + s + '"';
                case Vector3 v3: return V(v3);
                case IEnumerable e when depth == 0:
                    var items = e.Cast<object>().ToList();
                    return $"[{items.Count}] " + string.Join(", ", items.Take(12).Select(i => Fmt(i, 1)).ToArray()) + (items.Count > 12 ? ", ..." : "");
                case IEnumerable _: return "[...]";
                default: return v.ToString();
            }
        }

        // a 512 px PNG copy of a (possibly unreadable) texture, via the GPU; once per texture name
        private static string SaveTexture(Texture tex)
        {
            try
            {
                Directory.CreateDirectory(DumpDir);
                string file = System.IO.Path.Combine(DumpDir, "tex_" + Safe(tex.name) + ".png");
                if (File.Exists(file)) return System.IO.Path.GetFileName(file);
                int w = Mathf.Min(512, tex.width), h = Mathf.Max(1, w * tex.height / Mathf.Max(tex.width, 1));
                var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var prev = RenderTexture.active;
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var t2 = new Texture2D(w, h, TextureFormat.RGB24, false);
                t2.ReadPixels(new Rect(0, 0, w, h), 0, 0); t2.Apply();
                RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
                File.WriteAllBytes(file, t2.EncodeToPNG());
                UnityEngine.Object.Destroy(t2);
                return System.IO.Path.GetFileName(file);
            }
            catch (Exception e) { return "save failed: " + e.Message; }
        }

        private static string Mat(Material m) => m
            ? $"{m.name} shader='{(m.shader ? m.shader.name : "null")}' tex={(m.HasProperty("_MainTex") && m.mainTexture ? m.mainTexture.name : "-")}"
            : "null";

        private static string V(Vector3 v) => $"({v.x:0.##}, {v.y:0.##}, {v.z:0.##})";
        private static string Path(Transform t) => t.parent ? Path(t.parent) + "/" + t.name : t.name;
        private static string StripClone(string n) => System.Text.RegularExpressions.Regex.Replace(n, @"\s*(\(\d+\)|\(Clone\))$", "");
        private static string Safe(string n) => string.Concat(n.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));

        private static void Header(StringBuilder sb, string title) => sb.AppendLine($"\n===== {title} =====");

        private const int MaxChars = 120_000; // ~30k tokens hard cap per file

        private static void Write(string name, StringBuilder sb)
        {
            if (sb.Length > MaxChars)
            {
                sb.Length = MaxChars;
                sb.AppendLine("\n... TRUNCATED (file cap)");
            }
            Directory.CreateDirectory(DumpDir);
            string file = System.IO.Path.Combine(DumpDir, $"{name}_{DateTime.Now:HHmmss}.txt");
            File.WriteAllText(file, sb.ToString());
            Notify("Dump written: " + file);
        }

        private static void Notify(string msg)
        {
            Plugin.Log.LogMessage(msg);   // LogMessage shows on the BepInEx console too
        }
    }
}
