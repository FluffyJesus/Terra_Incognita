using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TerraIncognita
{
    /// <summary>
    /// Shipwrecks run aground on our islands, built at runtime from the game's own boats (no models in the bundle):
    ///   • template: the visible hull/deck/trim/mast meshes of a vanilla boat (sails, ropes, colliders, Crest masks,
    ///     water planes, fittings dropped), with the hull's damage overlay at full damage;
    ///   • masts: some kept, some snapped short, some gone;
    ///   • placement: a gentle stretch of shore; the bow rides up the beach, the hull heels over 12–30° and sits
    ///     half buried in the sand, the stern in the shallows; a few loose planks/hatches/doors lie around.
    /// Deterministic per island seed. Config [Wrecks].
    /// </summary>
    internal static class Wrecks
    {
        private static Transform root;
        private static readonly Dictionary<GameObject, float> bowSign = new Dictionary<GameObject, float>();   // +1: bow at +long axis
        private static readonly List<(string boat, GameObject tpl, float length)> templates = new List<(string, GameObject, float)>();

        // ---------------------------------------------------------------- templates
        private static readonly Regex BoatNo = new Regex(@"\((\d+)\)");
        private static readonly Regex DropName = new Regex(@"(?i)sail|rope|col\b|col_|_col|mask|embark|trigger|splash|winch|pulley|coiled|mooring|controller|flag|hammock|damage_water|push|wind shadow|telltale|ratline|shroud|stay|netting|cloth|furled|lantern|light|topmast|_extension");   // topmasts don't follow a snapped/leaning mast
        private static readonly Regex DropMat = new Regex(@"(?i)test water|no name|unnamed|default-material|trigger only|rope|cloth|glass|telltale|ParticleSplashes|Standard \(Instance\)$|^Standard$");
        private static readonly Regex DropShader = new Regex(@"(?i)crest|masked|water4|particles");

        public static void Harvest()
        {
            if (root) return;
            root = new GameObject("TerraIncognita Wrecks").transform;
            root.gameObject.SetActive(false);
            Object.DontDestroyOnLoad(root.gameObject);

            var wanted = (Plugin.WreckBoats.Value ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            var boats = Resources.FindObjectsOfTypeAll<BoatRefs>()
                .Where(b => b && b.gameObject.scene.IsValid())
                .Where(b =>
                {
                    string n = PropLibrary.Strip(b.name);
                    if (wanted.Count > 0) return wanted.Any(w => n.StartsWith(w, System.StringComparison.OrdinalIgnoreCase));
                    // vanilla boats carry small numbers (dhow small (10) … junk large (70)); modded ones 180+
                    var m = BoatNo.Match(b.name);
                    // junk large left out: its colliders are odd (decided 2026-11-15)
                    return m.Success && int.Parse(m.Groups[1].Value) < 150 && !Regex.IsMatch(n, @"(?i)junk large");
                })
                .GroupBy(b => PropLibrary.Strip(b.name)).Select(g => g.First()).ToList();

            foreach (var b in boats)
            {
                try
                {
                    var tpl = Build(b);
                    if (!tpl) continue;
                    // length of the hull itself: yards, bowsprits and booms reach far past it
                    var size = LocalBounds(tpl, "hull|").size;
                    if (size == Vector3.zero) size = Size(tpl);
                    float len = Mathf.Max(size.x, size.z);
                    if (len > Plugin.WreckMaxLength.Value) { Object.Destroy(tpl); continue; }
                    templates.Add((PropLibrary.Strip(b.name), tpl, len));
                }
                catch (System.Exception e) { Plugin.Log.LogWarning($"Wreck from {b.name}: {e.Message}"); }
            }
            if (Plugin.DevToolsEnabled.Value) DumpTemplates();
            Plugin.Log.LogInfo($"Wrecks: {templates.Count} hulls ({string.Join(", ", templates.Select(t => $"{t.tpl.name.Substring(6)} {t.length:0}m").ToArray())})");
        }

        // dumps/wreck_templates.txt: every part of every wreck template — name | mesh | materials [shader] — with
        // "RT" where the mesh/material was made at runtime and "+blk" for a property block. To trace stray parts.
        private static void DumpTemplates()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var t in templates)
                {
                    sb.AppendLine($"== {t.tpl.name}");
                    foreach (var mf in t.tpl.GetComponentsInChildren<MeshFilter>(true))
                    {
                        var r = mf.GetComponent<MeshRenderer>();
                        var mesh = mf.sharedMesh;
                        string mats = r ? string.Join(", ", r.sharedMaterials.Select(m => m ? $"{m.name}{(m.GetInstanceID() < 0 ? " RT" : "")} [{(m.shader ? m.shader.name : "-")}] col {(m.HasProperty("_Color") ? m.color.ToString() : "-")}" : "null").ToArray()) : "-";
                        sb.AppendLine($"  {mf.name} | {(mesh ? mesh.name + (mesh.GetInstanceID() < 0 ? " RT" : "") : "-")} | {mats}{(r && r.HasPropertyBlock() ? " +blk" : "")}");
                    }
                }
                string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Plugin.Instance.Info.Location), "dumps");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "wreck_templates.txt"), sb.ToString());
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("wreck_templates.txt: " + e.Message); }
        }

        private static GameObject Build(BoatRefs b)
        {
            var model = b.boatModel ? b.boatModel : b.transform;
            // masts: the boat's registered ones plus any under its model (a boat never switched on — medi medium far
            // away — has registered none yet)
            var allMasts = (b.masts ?? new Mast[0]).Concat(model.GetComponentsInChildren<Mast>(true)).Where(m => m).Distinct().ToArray();
            var sailObjs = new HashSet<Transform>(allMasts.Where(m => m.sails != null)
                .SelectMany(m => m.sails).Where(s => s).Select(s => s.transform));
            var mastTs = new HashSet<Transform>(allMasts.Select(m => m.transform));

            var tpl = new GameObject("wreck " + PropLibrary.Strip(b.name));
            tpl.transform.SetParent(root, false);
            var inv = Matrix4x4.TRS(model.position, model.rotation, Vector3.one).inverse;
            // The boat's chosen configuration (BoatCustomParts: per part one active option — masts, tillers, bowsprits…).
            // The activeSelf flags of world boats aren't set yet when we look, so all options looked installed.
            var offParts = new HashSet<Transform>(); var onParts = new HashSet<Transform>();
            var bcp = b.GetComponentInChildren<BoatCustomParts>(true) ?? b.GetComponentInParent<BoatCustomParts>();
            if (bcp && bcp.availableParts != null)
                foreach (var part in bcp.availableParts)
                {
                    if (part?.partOptions == null) continue;
                    for (int i = 0; i < part.partOptions.Count; i++)
                    {
                        var o = part.partOptions[i];
                        if (!o) continue;
                        var set = i == part.activeOption ? onParts : offParts;
                        set.Add(o.transform);
                        if (o.childOptions != null) foreach (var c in o.childOptions) if (c) set.Add(c.transform);
                        if (o.walkColObject) set.Add(o.walkColObject.transform);
                    }
                }
            // the boat's paint blocks (see Copy): parts painted through a property block, by material
            paintBlocks.Clear();
            foreach (var pr in model.GetComponentsInChildren<Renderer>(true))
                if (pr && pr.sharedMaterial && pr.HasPropertyBlock() && !paintBlocks.ContainsKey(MatKey(pr.sharedMaterial)))
                {
                    var blk = new MaterialPropertyBlock();
                    pr.GetPropertyBlock(blk);
                    paintBlocks[MatKey(pr.sharedMaterial)] = blk;
                }
            int kept = 0, modParts = 0;
            Color damage = RefsDirectory.instance ? RefsDirectory.instance.damageColor : new Color(0.25f, 0.2f, 0.15f, 1f);

            // include parts of boats switched off far away, but not optional parts the boat itself has turned off
            foreach (var r in model.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mf = r.GetComponent<MeshFilter>();
                if (!mf || !mf.sharedMesh || !r.enabled) continue;
                if (Under(r.transform, model, offParts)) continue;                                   // option not installed
                if (!Under(r.transform, model, onParts) && !ActiveUnder(r.transform, model)) continue;  // switched off otherwise
                if (AnyName(r.transform, model, OtherMods)) continue;
                if (AddedAtRuntime(r.transform, model)) { modParts++; continue; }
                if (Under(r.transform, model, sailObjs) || AnyName(r.transform, model, DropName)) continue;
                var mat = r.sharedMaterial;
                if (!mat || DropMat.IsMatch(mat.name) || (mat.shader && DropShader.IsMatch(mat.shader.name))) continue;
                bool overlay = mat.name.StartsWith("damage tex");
                if (overlay && Plugin.WreckDamage.Value <= 0f) continue;      // DamageOverlay 0 = no overlay at all

                var go = new GameObject(r.name);
                go.transform.SetParent(tpl.transform, false);
                var m = inv * r.transform.localToWorldMatrix;
                go.transform.localPosition = m.MultiplyPoint3x4(Vector3.zero);
                go.transform.localRotation = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
                go.transform.localScale = r.transform.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var nr = go.AddComponent<MeshRenderer>();
                if (overlay)
                {
                    // the boat's damage texture at full damage (HullDamageTexture: damageColor, alpha 1, queue 2002)
                    var om = new Material(mat);
                    om.SetColor("_Color", new Color(damage.r, damage.g, damage.b, Plugin.WreckDamage.Value));
                    om.renderQueue = 2002;
                    nr.sharedMaterial = om;
                }
                else
                {
                    // the hull's "dirt" layer is see-through (alpha ~0.9); Crest's underwater fog skips transparent
                    // materials, so under water the hull showed as a glowing lattice (brig on the reef) — dropped:
                    // as an extra pass (more materials than submeshes) it goes, as a submesh it is drawn invisible
                    var mats = r.sharedMaterials;
                    int sub = mf.sharedMesh.subMeshCount;
                    var keep = new List<Material>();
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var mm = mats[i];
                        bool dirt = mm && mm.name.StartsWith("dirt");
                        if (!dirt) { keep.Add(mm); continue; }
                        if (i < sub) keep.Add(Invisible);
                    }
                    nr.sharedMaterials = keep.ToArray();
                }
                // masts are groups: the mast itself ("mast*|id") and what hangs on it ("mast|id|…"), so a removed or
                // snapped mast takes its fittings along instead of leaving them floating in the air
                var mastT = MastOf(r.transform, model, mastTs);
                if (mastT) go.name = (r.transform == mastT ? "mast*|" : "mast|") + mastT.GetInstanceID() + "|" + r.name;
                if (Regex.IsMatch(r.name, @"(?i)hull") && !overlay) go.name = "hull|" + r.name;
                kept++;
            }
            Plugin.Debug($"Wreck {b.name}: parts {(bcp ? bcp.availableParts.Count : 0)} (options off {offParts.Count}), {modParts} meshes from other mods left out");
            if (kept == 0) { Object.Destroy(tpl); return null; }

            // Sails the boat carries now: their yards/booms (static meshes) and the cloth in its rest shape, both
            // grouped with their mast ("mast|id|…") so a lost or snapped mast takes them along.
            // Junks get bare battens: their cloth mesh isn't readable and the canvas is a particle texture.
            int sails = 0;
            bool junk = PropLibrary.Strip(b.name).ToLowerInvariant().Contains("junk");
            foreach (var mast in allMasts.Where(m => m && !AnyName(m.transform, model, TopMast)
                                                                && !AddedAtRuntime(m.transform, model) && !AnyName(m.transform, model, OtherMods)
                                                                && !Under(m.transform, model, offParts)))
            {
                var mastT = MastOf(mast.transform, model, mastTs) ?? mast.transform;
                string id = mastT.GetInstanceID().ToString();
                // staysails / jibs hang on the rigging, which the wreck doesn't have: left out
                if (Regex.IsMatch(mast.name, @"(?i)stay")) continue;
                // no sails yet (the mast's Start never ran): its starting sails, set up the way Mast.AttachInitialSail
                // does, in a switched-off probe so no game script wakes up; removed after copying
                GameObject probe = null;
                var sailList = (mast.sails ?? new List<GameObject>()).Where(s => s).ToList();
                if (sailList.Count == 0) sailList = StartSails(mast, out probe);
                try
                {
                foreach (var sailGo in sailList.Where(s => s && !Regex.IsMatch(s.name, @"(?i)jib|stays")))
                {
                    var sail = sailGo.GetComponent<Sail>();
                    var yards = new List<(Vector3 a, Vector3 b)>();
                    // the sail set (unfurled) while its yards and cloth are copied — a boom moves and the cloth unfolds
                    var restore = TornSails.SetSailSet(sail);
                    try
                    {
                    foreach (var mf in sailGo.GetComponentsInChildren<MeshFilter>(true))
                    {
                        var r = mf.GetComponent<MeshRenderer>();
                        if (!mf.sharedMesh || !r || !r.sharedMaterial) continue;
                        // parts the sail keeps switched off (a square sail's foot yard: hidden in the game, the stand-in
                        // Topsail Yards works with) — the sail object itself may be off (a probe), so up to it only
                        if (!r.enabled || !ActiveUnder(mf.transform, sailGo.transform)) continue;
                        if (AnyName(mf.transform, sailGo.transform, ModYards)) continue;   // Topsail Yards' foot yards etc.
                        // spars made by mods at runtime (Topsail Yards' stand-in yard: its own mesh and a new material) —
                        // the game's spars use asset meshes and materials (positive instance IDs)
                        if (mf.sharedMesh.GetInstanceID() < 0)
                        {
                            Plugin.Debug($"Wreck {b.name}: sail {sailGo.name}: spar '{PathUnder(mf.transform, sailGo.transform)}' made at runtime (a mod's) — left out");
                            continue;
                        }
                        if (Regex.IsMatch(mf.name, @"(?i)col|wind|rope|att|furled|cube|SAIL_")) continue;   // keep yards, booms, battens
                        if (r.sharedMaterial.shader && DropShader.IsMatch(r.sharedMaterial.shader.name)) continue;
                        var yg = Copy(tpl, inv, mf.transform, mf.sharedMesh, r.sharedMaterials, $"mast|{id}|yard:{mf.name}");
                        yards.Add(LongAxis(yg.transform, mf.sharedMesh.bounds));
                        Plugin.Debug($"Wreck {b.name}: sail {sailGo.name}: spar '{PathUnder(mf.transform, sailGo.transform)}' [{r.sharedMaterial.name}]" +
                                           $"{(r.HasPropertyBlock() ? " +block" : "")}");
                    }
                    if (junk) continue;
                    var smr = sail && sail.cloth ? sail.cloth.GetComponent<SkinnedMeshRenderer>() : sailGo.GetComponentInChildren<SkinnedMeshRenderer>(true);
                    var canvas = Canvas(sailGo, smr);
                    if (!smr || !smr.sharedMesh || !canvas) continue;
                    // the cloth skinned as the game rigs it with the sail set (see TornSails.Rag) — in template space
                    int seedS = b.name.GetHashCode() ^ (sails * 7919);
                    var rag = TornSails.Rag(sail, smr, inv, yards, out Vector4 depth, out string why);
                    if (!rag) { Plugin.Debug($"Wreck {b.name}: sail {sailGo.name} not usable as a rag ({why})"); continue; }
                    var rg = new GameObject($"mast|{id}|cloth");
                    rg.transform.SetParent(tpl.transform, false);
                    rg.AddComponent<MeshFilter>().sharedMesh = rag;
                    rg.AddComponent<MeshRenderer>().sharedMaterials = new[] { TornSails.Make(canvas, seedS, depth) };
                    sails++;
                    }
                    finally { restore(); }
                }
                }
                finally { if (probe) Object.DestroyImmediate(probe); }
            }
            if (sails > 0) tpl.name += $" ({sails} sails)";

            // where the bow is: the rudder sits at the stern; bow = the other end of the hull's long axis
            var rudder = b.GetComponentsInChildren<Component>(true).FirstOrDefault(c => c && (c.GetType().Name == "Rudder" || c.GetType().Name == "RudderNew"));
            var hb = LocalBounds(tpl, "hull|"); if (hb.size == Vector3.zero) hb = LocalBounds(tpl, null);
            var axisL = hb.size.x > hb.size.z ? Vector3.right : Vector3.forward;
            float bow = 0f;
            if (rudder) bow = -Mathf.Sign(Vector3.Dot(inv.MultiplyPoint3x4(rudder.transform.position) - hb.center, axisL));
            bowSign[tpl] = bow;
            Plugin.Debug($"Wreck {b.name}: bow {(bow == 0f ? "unknown (no rudder)" : (bow > 0 ? "+" : "-") + (axisL.x > 0 ? "x" : "z"))}");
            return tpl;
        }

        // Mast.AttachInitialSail: sail at the mast's position/rotation, parented to it, localPosition.z =
        // currentInstallHeight (prefab value) + heightOffset (ChangeInstallHeight adds mastHeight + offset,
        // UpdateInstallPosition subtracts mastHeight). Here under a switched-off probe child of the mast.
        private static List<GameObject> StartSails(Mast mast, out GameObject probe)
        {
            var list = new List<GameObject>();
            probe = null;
            var prefabs = new List<(GameObject p, float off)>();
            if (mast.startSailPrefab) prefabs.Add((mast.startSailPrefab, mast.startSailHeightOffset));
            if (mast.startSailPrefabs != null)
                for (int i = 0; i < mast.startSailPrefabs.Length; i++)
                    if (mast.startSailPrefabs[i])
                        prefabs.Add((mast.startSailPrefabs[i], mast.startSailsHeightOffsets != null && i < mast.startSailsHeightOffsets.Length ? mast.startSailsHeightOffsets[i] : 0f));
            if (prefabs.Count == 0) return list;
            probe = new GameObject("TerraIncognita sail probe");
            probe.SetActive(false);
            probe.transform.SetParent(mast.transform, false);
            var hField = HarmonyLib.AccessTools.Field(typeof(Sail), "currentInstallHeight");
            foreach (var (p, off) in prefabs)
            {
                var go = Object.Instantiate(p, probe.transform);
                go.name = p.name;
                var s = go.GetComponent<Sail>();
                float h0 = s && hField != null ? (float)hField.GetValue(s) : 0f;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localPosition = new Vector3(0f, 0f, h0 + off);
                list.Add(go);
            }
            Plugin.Debug($"Wreck: mast {mast.name} had no sails yet — starting sails {string.Join(", ", prefabs.Select(x => x.p.name).ToArray())}");
            return list;
        }

        // a yard/boom as a segment along its mesh's long axis (template space; copies sit directly under the template)
        private static (Vector3 a, Vector3 b) LongAxis(Transform t, Bounds mb)
        {
            int ax = mb.size.x > mb.size.y ? (mb.size.x > mb.size.z ? 0 : 2) : (mb.size.y > mb.size.z ? 1 : 2);
            Vector3 d = Vector3.zero; d[ax] = mb.extents[ax];
            var m = Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale);
            return (m.MultiplyPoint3x4(mb.center - d), m.MultiplyPoint3x4(mb.center + d));
        }

        // The sail's canvas: furled sails wear Refs.emptyMaterial (a particle cloud) on the cloth and keep the canvas in
        // ReefEffectAnimUniversal's private `unfurledMaterial` (Sail Damage notes).
        private static Material Canvas(GameObject sailGo, SkinnedMeshRenderer smr)
        {
            var mat = smr ? smr.sharedMaterial : null;
            if (mat && !(mat.shader && DropShader.IsMatch(mat.shader.name)) && !mat.name.ToLowerInvariant().Contains("empty")) return mat;
            foreach (var c in sailGo.GetComponentsInChildren<Component>(true))
            {
                if (!c || c.GetType().Name != "ReefEffectAnimUniversal") continue;
                var f = HarmonyLib.AccessTools.Field(c.GetType(), "unfurledMaterial");
                if (f?.GetValue(c) is Material um && um) return um;
            }
            return null;
        }

        private static bool Under(Transform t, Transform top, HashSet<Transform> set)
        {
            for (; t && t != top; t = t.parent) if (set.Contains(t)) return true;
            return false;
        }

        private static bool AnyName(Transform t, Transform top, Regex re)
        {
            for (; t && t != top; t = t.parent) if (re.IsMatch(t.name)) return true;
            return false;
        }

        // paint material name → a property block the boat's own parts carry (filled per boat in Build)
        private static readonly Dictionary<string, MaterialPropertyBlock> paintBlocks = new Dictionary<string, MaterialPropertyBlock>();

        private static Material invisible;
        private static Material Invisible
        {
            get
            {
                if (invisible) return invisible;
                var sh = Shader.Find("Legacy Shaders/Transparent/Cutout/Diffuse") ?? Shader.Find("Standard");
                invisible = new Material(sh) { name = "wreck nothing", color = new Color(0f, 0f, 0f, 0f) };
                if (invisible.HasProperty("_Cutoff")) invisible.SetFloat("_Cutoff", 1f);
                return invisible;
            }
        }

        private static string MatKey(Material m) => m.name.Replace(" (Instance)", "").Trim();

        private static string PathUnder(Transform t, Transform top)
        {
            var s = t.name;
            for (t = t.parent; t && t != top; t = t.parent) s = t.name + "/" + s;
            return s;
        }

        private static GameObject Copy(GameObject tpl, Matrix4x4 inv, Transform src, Mesh mesh, Material[] mats, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(tpl.transform, false);
            var m = inv * src.localToWorldMatrix;
            go.transform.localPosition = m.MultiplyPoint3x4(Vector3.zero);
            go.transform.localRotation = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
            go.transform.localScale = src.lossyScale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = mats;
            // the game colours some parts (sail booms/yards) through a property block: without it they came out white
            var srcR = src.GetComponent<Renderer>();
            if (srcR && srcR.HasPropertyBlock())
            {
                var mpb = new MaterialPropertyBlock();
                srcR.GetPropertyBlock(mpb);
                r.SetPropertyBlock(mpb);
            }
            // a sail set up from its prefab (never painted) borrows the boat's paint: the block of a hull part with
            // the same paint material (medi_medium_paint spars came out white)
            else if (srcR && srcR.sharedMaterial && paintBlocks.TryGetValue(MatKey(srcR.sharedMaterial), out var hullBlock))
                r.SetPropertyBlock(hullBlock);
            return go;
        }

        // topmasts / mast extensions (dhow large "mast_main_0_topmast", dhow medium "mast_0_extension"): separate masts
        // that don't follow a snapped or leaning lower mast
        // spars other mods hang on sails at runtime (Topsail Yards: TopsailFootYard / SparVisual / BracketVisual)
        private static readonly Regex ModYards = new Regex(@"(?i)TopsailFootYard|SparVisual|BracketVisual|LiftMastAnchor");
        private static readonly Regex TopMast =new Regex(@"(?i)topmast|_extension");

        // Parts other mods add to vanilla boats (Shipyard Expansion, Dinghies' davits, …) are instantiated at runtime;
        // Unity gives runtime-created objects negative instance IDs, objects loaded with the scene positive ones.
        private static bool AddedAtRuntime(Transform t, Transform top)
        {
            for (; t && t != top; t = t.parent) if (t.gameObject.GetInstanceID() < 0) return true;
            return false;
        }
        private static readonly Regex OtherMods =new Regex(@"(?i)^SE_parts");   // another mod's add-on parts on vanilla boats

        private static bool ActiveUnder(Transform t, Transform top)
        {
            for (; t && t != top; t = t.parent) if (!t.gameObject.activeSelf) return false;
            return true;
        }

        private static Transform MastOf(Transform t, Transform top, HashSet<Transform> masts)
        {
            Transform found = null;
            for (; t && t != top; t = t.parent)
                if (masts.Contains(t) || Regex.IsMatch(t.name, @"(?i)^mast|_mast|mast_|bowsprit")) found = t;   // outermost
            return found;
        }

        /// <summary>The lower end of a mast's long axis (world).</summary>
        private static Vector3 MastFoot(Transform mast)
        {
            var mf = mast.GetComponent<MeshFilter>();
            if (!mf || !mf.sharedMesh) return mast.position;
            var b = mf.sharedMesh.bounds;
            int ax = b.size.x > b.size.y ? (b.size.x > b.size.z ? 0 : 2) : (b.size.y > b.size.z ? 1 : 2);
            Vector3 dir = Vector3.zero; dir[ax] = b.extents[ax];
            var e1 = mast.TransformPoint(b.center - dir); var e2 = mast.TransformPoint(b.center + dir);
            return e1.y < e2.y ? e1 : e2;
        }

        /// <summary>Shorten a mast along its long axis, keeping its foot where it was, and lean it a little.</summary>
        private static void SnapMast(Transform mast, float keep, System.Random rng)
        {
            var mf = mast.GetComponent<MeshFilter>();
            if (!mf || !mf.sharedMesh) return;
            var b = mf.sharedMesh.bounds;
            int ax = b.size.x > b.size.y ? (b.size.x > b.size.z ? 0 : 2) : (b.size.y > b.size.z ? 1 : 2);
            Vector3 dir = Vector3.zero; dir[ax] = b.extents[ax];
            var e1 = mast.TransformPoint(b.center - dir); var e2 = mast.TransformPoint(b.center + dir);
            bool firstIsFoot = e1.y < e2.y;
            var s = mast.localScale; s[ax] *= keep; mast.localScale = s;
            var n1 = mast.TransformPoint(b.center - dir); var n2 = mast.TransformPoint(b.center + dir);
            var foot = firstIsFoot ? e1 : e2;
            mast.position += foot - (firstIsFoot ? n1 : n2);
            mast.RotateAround(foot, Random.onUnitSphere, Mathf.Lerp(3f, 12f, (float)rng.NextDouble()));
        }

        private static Vector3 Size(GameObject go)
        {
            var b = new Bounds();
            bool first = true;
            foreach (var r in go.GetComponentsInChildren<MeshFilter>(true))
            {
                var mb = r.sharedMesh.bounds;
                var m = go.transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var p = m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (first) { b = new Bounds(p, Vector3.zero); first = false; } else b.Encapsulate(p);
                }
            }
            return b.size;
        }

        private static Bounds LocalBounds(GameObject go, string onlyPrefix)
        {
            var b = new Bounds();
            bool first = true;
            foreach (var r in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (onlyPrefix != null && !r.name.StartsWith(onlyPrefix)) continue;
                var mb = r.sharedMesh.bounds;
                var m = go.transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var p = m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (first) { b = new Bounds(p, Vector3.zero); first = false; } else b.Encapsulate(p);
                }
            }
            return b;
        }

        // ---------------------------------------------------------------- placement
        // which archipelago a vanilla boat comes from
        private static Climate? HomeOf(string boat)
        {
            string n = boat.ToLowerInvariant();
            if (n.StartsWith("dhow")) return Climate.Desert;
            if (n.StartsWith("junk")) return Climate.Asian;
            if (n.StartsWith("medi")) return Climate.North;
            return null;
        }

        public static bool Place(SpawnedIsland s)
        {
            if (!Plugin.WrecksEnabled.Value || !s.Root || templates.Count == 0) return false;
            var rng = new System.Random(s.Seed * 131 + 71);
            // sandbars, sunken ridges and atolls are where ships actually come to grief: twice the chance
            bool reefy = s.Type == IslandType.Sandbar || s.Type == IslandType.SunkenRidge || s.Type == IslandType.Atoll;
            if (rng.NextDouble() >= Mathf.Min(1f, Plugin.WreckChance.Value * (reefy ? 2f : 1f))) return false;
            var t = s.Root.GetComponentInChildren<Terrain>(true);
            if (!t || !t.terrainData) return false;
            var td = t.terrainData;
            var tpos = t.GetPosition();
            float H(float wx, float wz) => s.Root.InverseTransformPoint(new Vector3(wx, t.SampleHeight(new Vector3(wx, 0f, wz)) + tpos.y, wz)).y;

            // near an archipelago 70 % of wrecks are local boats; by the trade routes anything goes
            var local = templates.Where(x => HomeOf(x.boat) == (s.Climate == Climate.Oceanic ? Climate.Asian : s.Climate)).ToList();
            var pool = !s.OnRoute && local.Count > 0 && rng.NextDouble() < 0.7 ? local : templates;
            var pick = pool[rng.Next(pool.Count)];
            float len = pick.length;
            // run aground bow first (65 %) or broadside to the shore (35 %) — never stern first
            bool broadside = rng.NextDouble() < 0.35;
            // sculpted rocks: the hull keeps clear of them by their colliders — rays from above over the hull's
            // footprint (+ 2 m round it) must meet no rock standing above the ground there. (Was a circle round each
            // rock's pivot: a mesa wall's pivot is the island's middle and its "radius" half the island — no wreck
            // ever found room on a mesa; a grotto's pivot is its mouth, off its middle.)
            Physics.SyncTransforms();
            var rocks = s.Root.GetComponentsInChildren<MeshCollider>(true).Where(c => c && c.sharedMesh && c.name.StartsWith("rock sculpted ")).ToList();
            // (an inactive collider never reports a hit: say so if the island isn't live yet)
            if (rocks.Any(c => !c.enabled || !c.gameObject.activeInHierarchy))
                Plugin.Log.LogWarning($"Wreck on {s.Root.name}: {rocks.Count(c => !c.enabled || !c.gameObject.activeInHierarchy)} sculpted rocks inactive — the hull can't be kept clear of them");
            bool RockAt(float x, float z)
            {
                foreach (var c in rocks)
                {
                    var b = c.bounds;
                    if (x < b.min.x || x > b.max.x || z < b.min.z || z > b.max.z) continue;
                    float gy = t.SampleHeight(new Vector3(x, 0f, z)) + tpos.y;
                    if (c.Raycast(new Ray(new Vector3(x, b.max.y + 5f, z), Vector3.down), out var hit, b.size.y + 10f) && hit.point.y > gy - 0.5f) return true;
                }
                return false;
            }
            bool NearRock(float x, float z) => RockAt(x, z);
            bool HullClear(float x, float z, Vector2 dir)
            {
                var side = new Vector2(-dir.y, dir.x);
                float half = len * 0.5f + 2f, beam = len * 0.15f + 2f;
                for (int i = -3; i <= 3; i++)
                    for (int j = -1; j <= 1; j++)
                    {
                        var p = new Vector2(x, z) + dir * (half * i / 3f) + side * (beam * j);
                        if (RockAt(p.x, p.y)) return false;
                    }
                return true;
            }
            for (int attempt = 0; attempt < 4000; attempt++)
            {
                float wx = tpos.x + (float)rng.NextDouble() * td.size.x, wz = tpos.z + (float)rng.NextDouble() * td.size.z;
                float h = H(wx, wz);
                if (h < -0.6f || h > 0.6f) continue;                                  // the waterline
                if (NearRock(wx, wz)) continue;
                // uphill direction (gradient over ±3 m) = where the bow runs up the beach
                float gx = H(wx + 3f, wz) - H(wx - 3f, wz), gz = H(wx, wz + 3f) - H(wx, wz - 3f);
                var up = new Vector2(gx, gz);
                float slope = Mathf.Atan(up.magnitude / 6f) * Mathf.Rad2Deg;
                if (slope < 2f || slope > 14f) continue;                               // a beach, not a cliff or a flat
                up.Normalize();
                Vector2 bowDir;
                if (broadside)
                {
                    // lying along the waterline: both ends near sea level
                    bowDir = new Vector2(-up.y, up.x) * (rng.NextDouble() < 0.5 ? 1f : -1f);
                    float h1 = H(wx + bowDir.x * len * 0.45f, wz + bowDir.y * len * 0.45f), h2 = H(wx - bowDir.x * len * 0.45f, wz - bowDir.y * len * 0.45f);
                    if (h1 < -1.5f || h1 > 1.2f || h2 < -1.5f || h2 > 1.2f) continue;
                }
                else
                {
                    // the whole length must be shore: bow end on land (but low), stern end in the water
                    float hb = H(wx + up.x * len * 0.45f, wz + up.y * len * 0.45f), hs = H(wx - up.x * len * 0.45f, wz - up.y * len * 0.45f);
                    if (hb < 0.2f || hb > 3.5f || hs > -0.3f || hs < -6f) continue;
                    bowDir = up;
                }
                if (!HullClear(wx, wz, bowDir)) continue;

                Build(s, pick, rng, new Vector3(wx, 0f, wz), bowDir, up, h, 0.4f);
                Plugin.Debug($"Wreck on {s.Root.name}: {pick.boat} ({len:0} m), {(broadside ? "broadside" : "bow")} on a beach {slope:0}°");
                return true;
            }
            // no beach: run it onto the reef — shallow, fairly even seabed, the hull half under water
            for (int attempt = 0; attempt < 4000; attempt++)
            {
                float wx = tpos.x + (float)rng.NextDouble() * td.size.x, wz = tpos.z + (float)rng.NextDouble() * td.size.z;
                float h = H(wx, wz);
                if (h < -2.8f || h > -0.4f) continue;
                if (NearRock(wx, wz)) continue;
                // heading for the island (bow toward its middle, ±30°) or swung broadside onto the reef
                var toIsland = new Vector2(tpos.x + td.size.x * 0.5f - wx, tpos.z + td.size.z * 0.5f - wz).normalized;
                float a = Mathf.Atan2(toIsland.y, toIsland.x) + Mathf.Lerp(-0.5f, 0.5f, (float)rng.NextDouble())
                          + (broadside ? Mathf.PI * 0.5f * (rng.NextDouble() < 0.5 ? 1f : -1f) : 0f);
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                float h1 = H(wx + dir.x * len * 0.45f, wz + dir.y * len * 0.45f), h2 = H(wx - dir.x * len * 0.45f, wz - dir.y * len * 0.45f);
                if (h1 < -3.5f || h2 < -3.5f || h1 > 1f || h2 > 1f) continue;
                if (!HullClear(wx, wz, dir)) continue;
                Build(s, pick, rng, new Vector3(wx, 0f, wz), dir, toIsland, h, 0.3f);
                Plugin.Debug($"Wreck on {s.Root.name}: {pick.boat} ({len:0} m) on the reef, {h:0.0} m deep");
                return true;
            }
            Plugin.Debug($"Wreck on {s.Root.name}: no beach or reef for {pick.boat}");
            return false;
        }
        private static void Build(SpawnedIsland s, (string boat, GameObject tpl, float length) pick, System.Random rng,
                                  Vector3 world, Vector2 bowDir, Vector2 up, float groundLocalY, float sinkFrac)
        {
            var w = Object.Instantiate(pick.tpl, s.Root);
            w.name = "wreck " + pick.boat;
            w.SetActive(true);
            // template long axis (local z for most boats, x for some) onto bowDir, the bow end there (rudder = stern)
            var hb = LocalBounds(w, "hull|");
            if (hb.size == Vector3.zero) hb = LocalBounds(w, null);
            bool alongX = hb.size.x > hb.size.z;
            bowSign.TryGetValue(pick.tpl, out float bs);
            if (bs == 0f) bs = rng.NextDouble() < 0.5 ? 1f : -1f;       // no rudder found: either way
            var fwd = new Vector3(bowDir.x, 0f, bowDir.y) * bs;           // where the +long axis must point
            var yaw = Quaternion.LookRotation(Quaternion.Euler(0f, Mathf.Lerp(-15f, 15f, (float)rng.NextDouble()), 0f) * fwd)
                      * (alongX ? Quaternion.Euler(0f, -90f, 0f) : Quaternion.identity);
            var hullFwd = yaw * (alongX ? Vector3.right : Vector3.forward);
            var side = Vector3.Cross(Vector3.up, hullFwd);
            float roll = Mathf.Lerp(12f, 30f, (float)rng.NextDouble()) * (rng.NextDouble() < 0.5 ? -1f : 1f);
            float pitch = Mathf.Lerp(3f, 8f, (float)rng.NextDouble()) * (Vector3.Dot(hullFwd, new Vector3(up.x, 0f, up.y)) > 0f ? -1f : 1f);  // land end up
            w.transform.position = world;
            w.transform.rotation = Quaternion.AngleAxis(roll, hullFwd) * Quaternion.AngleAxis(pitch, side) * yaw;

            // sit it in the sand: the lowest point of the hull ~40 % of its height below the ground here
            var bounds = new Bounds(world, Vector3.zero); bool any = false;
            foreach (var rr in w.GetComponentsInChildren<MeshRenderer>(true))
                if (rr.name.StartsWith("hull|")) { if (!any) { bounds = rr.bounds; any = true; } else bounds.Encapsulate(rr.bounds); }
            if (!any) foreach (var rr in w.GetComponentsInChildren<MeshRenderer>(true)) bounds.Encapsulate(rr.bounds);
            float groundWorldY = s.Root.TransformPoint(new Vector3(0f, groundLocalY, 0f)).y;
            w.transform.position += Vector3.up * (groundWorldY - bounds.size.y * sinkFrac - bounds.min.y);
            // The rig stays the boat's own (vanilla sail plan). Each mast: snapped short (45 %, taking its yards and
            // sail along), else standing — half of those leaning; a standing sail hangs as a rag 60 % of the time.
            // Junk battens are thin and brittle: each one is broken off half the time.
            int rags = 0;
            bool junkBoat = pick.boat.ToLowerInvariant().Contains("junk");
            var groups = w.GetComponentsInChildren<Transform>(true).Where(x => x && x.name.StartsWith("mast"))
                          .GroupBy(x => x.name.Split('|')[1]).ToList();
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var grp = groups[gi];
                bool hasMain = grp.Any(x => x.name.StartsWith("mast*|"));
                if (hasMain && rng.NextDouble() < 0.45)
                {
                    var main = grp.First(x => x.name.StartsWith("mast*|"));
                    SnapMast(main, Mathf.Lerp(0.3f, 0.6f, (float)rng.NextDouble()), rng);
                    foreach (var x in grp) if (x != main) Object.Destroy(x.gameObject);
                    continue;
                }
                // half the standing masts lean (4–12°), yards and sail along
                var mainT = grp.FirstOrDefault(x => x.name.StartsWith("mast*|"));
                if (mainT && rng.NextDouble() < 0.5)
                {
                    var foot = MastFoot(mainT);
                    var axis = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f) * Vector3.forward;
                    float ang = Mathf.Lerp(4f, 12f, (float)rng.NextDouble());
                    foreach (var x in grp) x.RotateAround(foot, axis, ang);
                }
                foreach (var x in grp.Where(x => x.name.EndsWith("|cloth")))
                    if (rng.NextDouble() < 0.6) rags++; else Object.Destroy(x.gameObject);
                if (junkBoat)
                    foreach (var x in grp.Where(x => x.name.Contains("|yard:")))
                        if (rng.NextDouble() < 0.5) Object.Destroy(x.gameObject);
            }
            // what's left of the sails: live cloth hanging from the yards (after the pose: pins are taken where they are)
            // (on first Start: the island may still be inactive here, and an inactive Cloth has no particles yet)
            foreach (var c in w.GetComponentsInChildren<Transform>(true).Where(x => x && x.name.EndsWith("|cloth")).ToList())
                if (c.gameObject) c.gameObject.AddComponent<RagHanger>();
            // the wreck is walkable/climbable: hull, decks, cabins, roofs — everything bigger than a crate except masts
            foreach (var h in w.GetComponentsInChildren<MeshFilter>(true).Where(x => !x.name.StartsWith("mast") && x.GetComponent<MeshRenderer>()
                         && x.GetComponent<MeshRenderer>().bounds.size.magnitude > 1.5f && !x.GetComponent<MeshRenderer>().sharedMaterial.name.StartsWith("damage tex")))
            {
                // a mesh the game keeps non-readable can't be a MeshCollider (Unity logs an error per part): its box
                if (h.sharedMesh.isReadable) h.gameObject.AddComponent<MeshCollider>().sharedMesh = h.sharedMesh;
                else { var bc = h.gameObject.AddComponent<BoxCollider>(); bc.center = h.sharedMesh.bounds.center; bc.size = h.sharedMesh.bounds.size; }
            }
            // masts and yards: a box along each spar, so loot and the player don't pass through them (not the cloth)
            foreach (var h in w.GetComponentsInChildren<MeshFilter>(true).Where(x => x.name.StartsWith("mast") && !x.name.EndsWith("|cloth") && x.sharedMesh))
            {
                var bc = h.gameObject.AddComponent<BoxCollider>();
                bc.center = h.sharedMesh.bounds.center; bc.size = h.sharedMesh.bounds.size;
            }
            // remove placed props (palms, bushes, rocks — named "<role> <prop>") the hull would sit in
            // (along the whole hull: within the beam + 4 m of its keel line, bow to stern + 4 m — a circle of 0.6 × the
            // length round its middle left rocks clipping its ends)
            var keel = new Vector2(bowDir.x, bowDir.y).normalized;
            float halfLen = pick.length * 0.5f + 4f, reachSide = pick.length * 0.12f + 4f;
            foreach (var t in s.Root.GetComponentsInChildren<Transform>(true).ToList())
            {
                if (!t || t == s.Root) continue;
                string n = t.name;
                if (!(n.StartsWith("tree ") || n.StartsWith("bush ") || n.StartsWith("rock ") || n.StartsWith("cliff "))) continue;
                if (n.StartsWith("rock sculpted ")) continue;                // hand-placed: the spot avoided them
                // a rock / cliff by its size, not its pivot: an enlarged game rock stood with its middle outside the
                // hull's strip and its flank through the hull. Its meshes' bounds (works while the island is inactive):
                // a circle round each mesh's middle
                bool hit = false;
                bool big = n.StartsWith("rock ") || n.StartsWith("cliff ");
                var spots = new List<(Vector3 c, float r)>();
                if (big)
                    foreach (var mf in t.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (!mf.sharedMesh) continue;
                        var mb = mf.sharedMesh.bounds; var ls = mf.transform.lossyScale;
                        spots.Add((mf.transform.TransformPoint(mb.center),
                                   new Vector2(mb.extents.x * Mathf.Abs(ls.x), mb.extents.z * Mathf.Abs(ls.z)).magnitude * 0.8f));
                    }
                if (spots.Count == 0) spots.Add((t.position, 0f));
                foreach (var (c, r) in spots)
                {
                    var d = new Vector2(c.x - world.x, c.z - world.z);
                    float along = Vector2.Dot(d, keel), across = Mathf.Abs(d.x * keel.y - d.y * keel.x);
                    if (Mathf.Abs(along) < halfLen + r && across < reachSide + r) { hit = true; break; }
                }
                if (hit) Object.DestroyImmediate(t.gameObject);
            }
            int loot = Loot(s, pick.boat, w, rng);
            Placed[s.Root.name] = pick.boat + (rags > 0 ? $" + {rags} sail{(rags > 1 ? "s" : "")}" : "") + (loot > 0 ? $", {loot} loot" : "");
        }

        // ---------------------------------------------------------------- loot (once per game, see Forage)
        // cargo by the boat's home waters (PrefabsDirectory indices, items.txt); ship's things anywhere; a rare find
        private static readonly int[] CargoA = { 2, 3, 24, 29, 11, 232 };          // dates, coconuts, spices, goods, rum, coffee
        private static readonly int[] CargoE = { 5, 28, 212, 19, 58, 233 };        // tea, silk, rice, eel, rice beer, salt
        private static readonly int[] CargoM = { 13, 12, 7, 228, 222, 219, 224 };  // wine, beer, cheese, cider, wool, leather, apples
        private static readonly int[] Ship = { 40, 40, 55, 71, 71, 99, 166, 132, 70, 100 };   // bottles, firewood, hook, oakum, lamp oil, bucket, mug
        private static readonly int[] Rare = { 160, 161, 80, 81, 82, 115, 92, 90, 95, 159 };      // spyglass, compass, map, chip log, quadrant, rod, hammer

        private static int Loot(SpawnedIsland s, string boat, GameObject w, System.Random rng)
        {
            if (!Plugin.WreckLootEnabled.Value) return 0;
            var home = HomeOf(boat);
            var cargo = home == Climate.Desert ? CargoA : home == Climate.Asian ? CargoE : CargoM;
            var items = new List<int>();
            int nCargo = rng.Next(1, 3), nShip = rng.Next(2, 5);
            for (int i = 0; i < nCargo; i++) items.Add(cargo[rng.Next(cargo.Length)]);
            for (int i = 0; i < nShip; i++) items.Add(Ship[rng.Next(Ship.Length)]);
            if (rng.NextDouble() < 0.2) items.Add(Rare[rng.Next(Rare.Length)]);

            var hb = new Bounds(w.transform.position, Vector3.zero); bool any = false;
            foreach (var r in w.GetComponentsInChildren<MeshRenderer>(true))
                if (r.name.StartsWith("hull|")) { if (!any) { hb = r.bounds; any = true; } else hb.Encapsulate(r.bounds); }
            var t = s.Root.GetComponentInChildren<Terrain>(true);
            if (t) Decor(s, t, hb, w, rng);
            Hulls[s.Root.name] = (s, hb, t, boat);
            int placed = 0;
            foreach (int item in items)
                if (DropLoot(s, hb, t, item, rng)) placed++;
            return placed;
        }

        /// <summary>Wrecked boats by island root name: for treasure (after all are placed) and tavern rumours.</summary>
        internal static readonly Dictionary<string, (SpawnedIsland s, Bounds hull, Terrain t, string boat)> Hulls =
            new Dictionary<string, (SpawnedIsland, Bounds, Terrain, string)>();
        /// <summary>Island root name → the treasure item its wreck carried (silver 226 / gold 22 / gems 20).</summary>
        internal static readonly Dictionary<string, int> Treasure = new Dictionary<string, int>();
        private static readonly int[] TreasureItems = { 226, 22, 20 };

        /// <summary>
        /// 1–2 wrecks of the whole world carried something precious: a few crates of silver, gold or gemstones on the
        /// deck or washed up around it (once per game, like the rest of the loot). The taverns talk of a ship that
        /// sailed off with it and never came back. Chosen by the world seed among the wrecks placed.
        /// </summary>
        public static void PlaceTreasure(int worldSeed)
        {
            Treasure.Clear();
            if (!Plugin.WreckLootEnabled.Value || Hulls.Count == 0) return;
            var rng = new System.Random(worldSeed * 17 + 5);
            var names = Hulls.Keys.OrderBy(k => k).ToList();
            int count = Mathf.Min(names.Count, rng.Next(1, 3));
            for (int i = 0; i < count; i++)
            {
                int k = rng.Next(names.Count);
                var name = names[k]; names.RemoveAt(k);
                var (s, hb, t, boat) = Hulls[name];
                if (!s.Root) continue;
                int item = TreasureItems[rng.Next(TreasureItems.Length)];
                int n = rng.Next(2, 5), placed = 0;
                for (int j = 0; j < n; j++) if (DropLoot(s, hb, t, item, rng)) placed++;
                if (placed == 0) continue;
                Treasure[name] = item;
                if (Placed.ContainsKey(name)) Placed[name] += $", TREASURE {placed}× {(item == 226 ? "silver" : item == 22 ? "gold" : "gems")}";
            }
            Plugin.Log.LogInfo($"Treasure wrecks: {Treasure.Count}");
        }

        private static bool DropLoot(SpawnedIsland s, Bounds hb, Terrain t, int item, System.Random rng)
        {
            {
                Vector3 p = Vector3.zero; bool ok = false;
                for (int tries = 0; tries < 10 && !ok; tries++)
                {
                    if (rng.NextDouble() < 0.5)
                    {
                        // on the deck: within the middle of the hull's footprint (the ground is found by a ray at spawn)
                        p = new Vector3(Mathf.Lerp(hb.min.x, hb.max.x, Mathf.Lerp(0.3f, 0.7f, (float)rng.NextDouble())), hb.max.y,
                                        Mathf.Lerp(hb.min.z, hb.max.z, Mathf.Lerp(0.3f, 0.7f, (float)rng.NextDouble())));
                        ok = true;
                    }
                    else if (t)
                    {
                        // washed up on the sand nearby: dry ground within ~a hull length
                        float a = (float)rng.NextDouble() * Mathf.PI * 2f, r = Mathf.Lerp(0.5f, 1.2f, (float)rng.NextDouble()) * hb.extents.magnitude;
                        p = hb.center + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                        float gy = t.SampleHeight(p) + t.GetPosition().y;
                        ok = gy - s.Root.position.y > 0.3f;
                    }
                }
                if (!ok) return false;
                Forage.AddLoot(s, p, item, (float)rng.NextDouble() * 360f);
                return true;
            }
        }

        // Empty crates and barrels strewn about the wreck — scenery, not items: the meshes of cargo prefabs, tumbled,
        // half sunk in the sand or the shallows, with box colliders.
        private static readonly int[] DecorItems = { 29, 29, 25, 21, 24, 11, 13, 10 };   // crates (goods, grain, iron), barrels

        private static void Decor(SpawnedIsland s, Terrain t, Bounds hb, GameObject w, System.Random rng)
        {
            var dir = PrefabsDirectory.instance ? PrefabsDirectory.instance.directory : null;
            if (dir == null) return;
            var hullLocal = LocalBounds(w, "hull|");
            if (hullLocal.size == Vector3.zero) hullLocal = LocalBounds(w, null);
            int n = rng.Next(3, 7);
            for (int i = 0; i < n; i++)
            {
                int idx = DecorItems[rng.Next(DecorItems.Length)];
                if (idx >= dir.Length || !dir[idx]) continue;
                float a = (float)rng.NextDouble() * Mathf.PI * 2f, r = Mathf.Lerp(0.4f, 1.3f, (float)rng.NextDouble()) * hb.extents.magnitude;
                var p = hb.center + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                float gy = t.SampleHeight(p) + t.GetPosition().y;
                if (gy - s.Root.position.y < -1.5f) continue;                       // not out in deep water
                // not in the hull: outside its box (in the wreck's own axes) grown by a metre
                var lp = w.transform.InverseTransformPoint(new Vector3(p.x, gy, p.z));
                var lb = hullLocal; lb.Expand(2f);
                if (lp.x > lb.min.x && lp.x < lb.max.x && lp.z > lb.min.z && lp.z < lb.max.z) continue;
                // not on cliffs: ground within 25° of level
                var tp = p - t.GetPosition(); var tsz = t.terrainData.size;
                var normal = t.terrainData.GetInterpolatedNormal(tp.x / tsz.x, tp.z / tsz.z);
                if (normal.y < Mathf.Cos(25f * Mathf.Deg2Rad)) continue;
                var d = StaticCopy(dir[idx], s.Root);
                if (!d) continue;
                // resting on a face, as things come to lie: crates mostly upright (else on a side / upside down),
                // barrels half upright, half rolled on their side; laid along the slope, a few degrees askew
                bool barrel = dir[idx].name.ToLowerInvariant().Contains("barrel");
                double f = rng.NextDouble();
                var face = barrel ? (f < 0.5 ? Quaternion.identity : Quaternion.Euler(90f, 0f, 0f))
                                  : (f < 0.6 ? Quaternion.identity : f < 0.8 ? Quaternion.Euler(90f, 0f, 0f) : f < 0.9 ? Quaternion.Euler(0f, 0f, 90f) : Quaternion.Euler(180f, 0f, 0f));
                var askew = Quaternion.Euler(Mathf.Lerp(-6f, 6f, (float)rng.NextDouble()), 0f, Mathf.Lerp(-6f, 6f, (float)rng.NextDouble()));
                d.transform.rotation = Quaternion.FromToRotation(Vector3.up, normal) * askew * Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f)
                                       * face * dir[idx].transform.rotation;
                d.transform.position = new Vector3(p.x, gy, p.z);
                var b = new Bounds(d.transform.position, Vector3.zero); bool any = false;
                foreach (var rr in d.GetComponentsInChildren<Renderer>()) { if (!any) { b = rr.bounds; any = true; } else b.Encapsulate(rr.bounds); }
                // rest on the lowest ground under its footprint (on a slope the downhill corner floated), then sink in a little
                float low = gy;
                for (int c = 0; c < 4; c++)
                {
                    var cp = new Vector3((c & 1) == 0 ? b.min.x : b.max.x, 0f, (c & 2) == 0 ? b.min.z : b.max.z);
                    low = Mathf.Min(low, t.SampleHeight(cp) + t.GetPosition().y);
                }
                d.transform.position += Vector3.up * (low - b.min.y - b.size.y * Mathf.Lerp(0.08f, 0.25f, (float)rng.NextDouble()));
            }
        }

        private static GameObject StaticCopy(GameObject prefab, Transform parent)
        {
            var root = new GameObject("wreck decor " + prefab.name);
            root.transform.SetParent(parent, false);
            var toRoot = prefab.transform.worldToLocalMatrix;
            int parts = 0;
            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                var r = mf.GetComponent<MeshRenderer>();
                if (!mf.sharedMesh || !r || !r.enabled || !ActiveUnder(mf.transform, prefab.transform)) continue;
                var go = new GameObject(mf.name);
                go.transform.SetParent(root.transform, false);
                var m = toRoot * mf.transform.localToWorldMatrix;
                go.transform.localPosition = m.MultiplyPoint3x4(Vector3.zero);
                go.transform.localRotation = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
                go.transform.localScale = new Vector3(m.GetColumn(0).magnitude, m.GetColumn(1).magnitude, m.GetColumn(2).magnitude);
                go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = r.sharedMaterials;
                var bc = go.AddComponent<BoxCollider>(); bc.center = mf.sharedMesh.bounds.center; bc.size = mf.sharedMesh.bounds.size;
                parts++;
            }
            if (parts == 0) { Object.Destroy(root); return null; }
            return root;
        }

        /// <summary>Island name → "boat [+ N sails]", for placements.txt.</summary>
        public static readonly Dictionary<string, string> Placed = new Dictionary<string, string>();
    }
}
