using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace TerraIncognita
{
    /// <summary>
    /// Things to pick up on the new islands: forage (coconuts under palms, mushrooms, oranges, apples) that grows
    /// back 4–10 days after it is taken, and loot by the wrecks, there once per game.
    ///
    /// Works like the game's WorldItemSpawner: an item is laid out (kinematic, not saved) once the player is near;
    /// when it is picked up it registers to be saved like any other item and its spot goes on cooldown. Unpicked
    /// items aren't saved — after a load they are simply laid out again. Spot state per island lives in
    /// GameState.modData ("TerraIncognita.forage.&lt;island&gt;" = "spot:day,..."; day -1 = taken for good).
    /// </summary>
    internal class ForageIsland : MonoBehaviour
    {
        internal class Spot
        {
            public Vector3 local;       // relative to the island root
            public int item;            // PrefabsDirectory.directory index
            public float yaw;
            public bool once;           // loot: never comes back
            public bool onTerrain;      // forage: ground = terrain only (not a tree's collider); loot: wreck decks too
            public bool physics;        // loot: tumbled and left to physics (forage: laid neatly, held still)
            public bool upright;        // mushrooms: stood on their stem (the item model lies on its side)
            public ShipItem spawned;
            public int nextDay;         // 0 = available, -1 = gone for good
        }

        internal readonly List<Spot> spots = new List<Spot>();
        internal static readonly List<ForageIsland> All = new List<ForageIsland>();
        private const string KeyPrefix = "TerraIncognita.forage.";
        private const float Near = 250f;
        private Terrain terrain;
        private float tick;

        private void Start()
        {
            terrain = GetComponentInChildren<Terrain>(true);
            All.Add(this);
            Load();
        }

        private void OnDestroy() => All.Remove(this);

        private string Key => KeyPrefix + name;

        public void Load()
        {
            // a save was loaded: what lay out unpicked goes (laid out again below if still due)
            foreach (var s in spots) { if (s.spawned && !s.spawned.held) Destroy(s.spawned.gameObject); s.spawned = null; s.nextDay = 0; }
            if (GameState.modData != null && GameState.modData.TryGetValue(Key, out var v))
                foreach (var part in v.Split(','))
                {
                    var kv = part.Split(':');
                    if (kv.Length == 2 && int.TryParse(kv[0], out int i) && int.TryParse(kv[1], out int d) && i >= 0 && i < spots.Count)
                        spots[i].nextDay = d;
                }
        }

        private void Save()
        {
            if (GameState.modData == null) return;
            var sb = new StringBuilder();
            for (int i = 0; i < spots.Count; i++)
                if (spots[i].nextDay != 0) sb.Append(sb.Length > 0 ? "," : "").Append(i).Append(':').Append(spots[i].nextDay);
            if (sb.Length > 0) GameState.modData[Key] = sb.ToString(); else GameState.modData.Remove(Key);
        }

        private void Update()
        {
            if (!GameState.playing || spots.Count == 0) return;
            tick -= Time.deltaTime;
            if (tick > 0f) return;
            tick = 0.5f;
            var cam = Camera.main;
            if (!cam) return;
            var camPos = cam.transform.position;
            bool near = (camPos - transform.position).sqrMagnitude < 900f * 900f;
            bool changed = false;
            int today = GameState.day;
            for (int i = 0; i < spots.Count; i++)
            {
                var s = spots[i];
                if (s.spawned != null)
                {
                    if (!s.spawned || s.spawned.held)
                    {
                        // picked up: from now on a normal item, saved with the game; the spot rests
                        if (s.spawned)
                        {
                            var sp = s.spawned.GetComponent<SaveablePrefab>();
                            if (sp) sp.RegisterToSave();
                            var rb = s.spawned.GetItemRigidbody();
                            if (rb) rb.debugForceKinematic = false;
                        }
                        s.spawned = null;
                        s.nextDay = s.once ? -1 : today + 4 + new System.Random(name.GetHashCode() ^ i ^ today).Next(0, 7);   // 4–10 days
                        changed = true;
                    }
                    continue;
                }
                if (!near || s.nextDay < 0) continue;
                if (s.nextDay > 0)
                {
                    if (today < s.nextDay) continue;
                    s.nextDay = 0; changed = true;           // grown back
                }
                var world = transform.TransformPoint(s.local);
                if ((world - camPos).sqrMagnitude < Near * Near) Spawn(s, world);
            }
            if (changed) Save();
        }

        private void Spawn(Spot s, Vector3 world)
        {
            var dir = PrefabsDirectory.instance ? PrefabsDirectory.instance.directory : null;
            if (dir == null || s.item < 0 || s.item >= dir.Length || !dir[s.item]) { s.nextDay = -1; return; }
            float ground;
            if (!Ground(world, s.onTerrain, out ground)) return;
            var prefab = dir[s.item];
            // the prefab's own rotation (a mushroom's model lies on its side without it), turned about the vertical,
            // tilted to the slope; loot: thrown about any which way and dropped
            Quaternion rot;
            if (s.physics)
            {
                var r = new System.Random(s.local.GetHashCode());
                rot = Quaternion.Euler((float)r.NextDouble() * 360f, s.yaw, (float)r.NextDouble() * 360f);
            }
            else if (s.upright)
            {
                var nrm = GroundNormal(world);
                rot = Quaternion.AngleAxis(s.yaw, nrm) * Quaternion.FromToRotation(UpAxis(s.item, prefab), nrm);
            }
            else
                rot = Quaternion.FromToRotation(Vector3.up, GroundNormal(world)) * Quaternion.Euler(0f, s.yaw, 0f) * prefab.transform.rotation;
            var go = Instantiate(prefab, new Vector3(world.x, ground + 1f, world.z), rot);
            go.transform.SetParent(transform, true);
            var item = go.GetComponent<ShipItem>();
            if (!item) { Destroy(go); s.nextDay = -1; return; }
            // rest it on the ground: the lowest point of its meshes just touching (loot: a little above, it falls)
            // only what is drawn: food carries switched-off meshes (bitten states) that hung it in the air
            var rs = go.GetComponentsInChildren<MeshRenderer>().Where(rr => rr.enabled && rr.gameObject.activeInHierarchy).ToArray();
            if (rs.Length > 0)
            {
                var b = rs[0].bounds; foreach (var rr in rs) b.Encapsulate(rr.bounds);
                go.transform.position += Vector3.up * (ground + (s.physics ? 0.4f : -Mathf.Min(0.03f, b.size.y * 0.1f)) - b.min.y);   // forage: pressed in a little
            }
            // a wreck's barrels: a fifth stove in and empty, the rest part full (same every time this spot is visited;
            // once picked up, the game saves what's in it)
            var bottle = go.GetComponent<ShipItemBottle>();
            if (s.physics && bottle && bottle.GetCapacity() >= 30f)
            {
                var r = new System.Random(s.local.GetHashCode() * 7 + 3);
                bottle.health = r.NextDouble() < 0.2 ? 0f : Mathf.Round(bottle.GetCapacity() * Mathf.Lerp(0.15f, 1f, (float)r.NextDouble()));
            }
            // a bottle left behind is full (the prefab's own fill was 5 whatever the size: the big one holds 10)
            else if (s.physics && bottle && bottle.GetCapacity() >= 5f && bottle.health > 0f)
                bottle.health = bottle.GetCapacity();
            item.sold = true;                                        // the player's to take (not shop stock)
            if (bottle) bottle.UpdateLookText();
            var good = go.GetComponent<Good>();
            if (good) good.RegisterAsMissionless();
            s.spawned = item;
            if (!s.physics) StartCoroutine(Freeze(item));
        }

        // A mushroom's stem axis in its prefab root's space: its meshes' centre lies off the pivot towards the cap
        // along the stem (pivot at the foot); no clear offset → the longest axis. Cached per item.
        private static readonly Dictionary<int, Vector3> upAxes = new Dictionary<int, Vector3>();
        private static Vector3 UpAxis(int item, GameObject prefab)
        {
            if (upAxes.TryGetValue(item, out var up)) return up;
            // field mushroom: unreadable mesh, box 0.225 × 0.225 × 0.202 centred on the pivot — round across X/Y (the
            // cap), the odd axis Z is the stem; same convention as the forest mushroom, which stands on +Z
            if (item == Forage.FieldMushroom) return upAxes[item] = Vector3.forward;
            var toRoot = prefab.transform.worldToLocalMatrix;
            // only what is drawn: switched-off meshes (other states of the item) pulled the centre aside — the field
            // mushroom lay on its side
            var shown = prefab.GetComponentsInChildren<MeshFilter>(true)
                .Where(mf => mf.sharedMesh && mf.GetComponent<MeshRenderer>() && mf.GetComponent<MeshRenderer>().enabled && mf.gameObject.activeSelf
                             && (mf.transform == prefab.transform || mf.transform.parent.gameObject.activeSelf)).ToList();
            if (shown.Count == 0) shown = prefab.GetComponentsInChildren<MeshFilter>(true).Where(mf => mf.sharedMesh && mf.GetComponent<MeshRenderer>()).ToList();
            string meshes = string.Join(", ", shown.Select(mf => $"{mf.name}{(mf.sharedMesh.isReadable ? "" : " (unreadable)")} rot {mf.transform.localEulerAngles.ToString("0")} size {mf.sharedMesh.bounds.size.ToString("0.###")} centre {mf.sharedMesh.bounds.center.ToString("0.###")}").ToArray());
            if (CapAxis(shown, toRoot, out up, out string how))
            {
                upAxes[item] = up;
                Plugin.Log.LogInfo($"Forage: {prefab.name} (root rot {prefab.transform.eulerAngles.ToString("0")}) stands on local {up} by cap width ({how}); meshes: {meshes}");
                return up;
            }
            Plugin.Log.LogInfo($"Forage: {prefab.name} (root rot {prefab.transform.eulerAngles.ToString("0")}): no cap found by width ({how}), meshes: {meshes}");
            var b = new Bounds(); bool any = false;
            foreach (var mf in shown)
            {
                var m = toRoot * mf.transform.localToWorldMatrix; var mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var p = m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            up = Vector3.up;
            if (any)
            {
                int best = -1; float bestR = 0.15f;
                for (int a = 0; a < 3; a++) { float r = Mathf.Abs(b.center[a]) / Mathf.Max(1e-4f, b.extents[a]); if (r > bestR) { bestR = r; best = a; } }
                if (best < 0) { best = b.size.x >= b.size.y && b.size.x >= b.size.z ? 0 : b.size.y >= b.size.z ? 1 : 2; up = Vector3.zero; up[best] = 1f; }
                else { up = Vector3.zero; up[best] = Mathf.Sign(b.center[best]); }
            }
            upAxes[item] = up;
            Plugin.Log.LogInfo($"Forage: {prefab.name} stands on local {up} by bounds (centre {b.center.ToString("0.###")}, size {b.size.ToString("0.###")})");
            return up;
        }

        // A mushroom is wide at the cap and thin at the foot: along the stem the two ends of the vertex cloud differ
        // most in width (across it both ends are cap rim). Up = towards the wide end. Needs readable meshes.
        private static bool CapAxis(List<MeshFilter> mfs, Matrix4x4 toRoot, out Vector3 up, out string how)
        {
            up = Vector3.up; how = "";
            var pts = new List<Vector3>();
            foreach (var mf in mfs)
            {
                if (!mf.sharedMesh.isReadable) return false;
                var m = toRoot * mf.transform.localToWorldMatrix;
                foreach (var v in mf.sharedMesh.vertices) pts.Add(m.MultiplyPoint3x4(v));
            }
            if (pts.Count < 8) return false;
            int best = -1; float bestScore = 0f; float bestSign = 1f;
            for (int a = 0; a < 3; a++)
            {
                float lo = pts.Min(p => p[a]), hi = pts.Max(p => p[a]), len = hi - lo;
                if (len < 1e-4f) continue;
                float Width(bool top)
                {
                    var sl = pts.Where(p => top ? p[a] > hi - len * 0.25f : p[a] < lo + len * 0.25f).ToList();
                    if (sl.Count == 0) return 0f;
                    int b1 = (a + 1) % 3, b2 = (a + 2) % 3;
                    return Mathf.Max(sl.Max(p => p[b1]) - sl.Min(p => p[b1]), sl.Max(p => p[b2]) - sl.Min(p => p[b2]));
                }
                float wt = Width(true), wb = Width(false);
                float score = Mathf.Abs(Mathf.Log(Mathf.Max(wt, 1e-4f) / Mathf.Max(wb, 1e-4f)));
                how += $"{"xyz"[a]}: {wb:0.###}→{wt:0.###}  ";
                if (score > bestScore) { bestScore = score; best = a; bestSign = wt >= wb ? 1f : -1f; }
            }
            if (best < 0 || bestScore < 0.4f) return false;               // no clear cap (ratio < 1.5)
            up = Vector3.zero; up[best] = bestSign;
            return true;
        }

        private Vector3 GroundNormal(Vector3 world)
        {
            if (!terrain) return Vector3.up;
            var p = world - terrain.GetPosition(); var sz = terrain.terrainData.size;
            var n = terrain.terrainData.GetInterpolatedNormal(p.x / sz.x, p.z / sz.z);
            return n.y > 0.5f ? n : Vector3.up;
        }

        private static IEnumerator Freeze(ShipItem item)
        {
            yield return new WaitForEndOfFrame();
            if (item && !item.held) { var rb = item.GetItemRigidbody(); if (rb) rb.debugForceKinematic = true; }
        }

        // ground under a point: the terrain, or (loot) the first solid thing below — a wreck's deck — but not trees
        private bool Ground(Vector3 world, bool terrainOnly, out float y)
        {
            y = 0f;
            float ty = terrain ? terrain.SampleHeight(world) + terrain.GetPosition().y : float.MinValue;
            if (terrainOnly) { y = ty; return terrain; }
            var hits = Physics.RaycastAll(new Vector3(world.x, world.y + 25f, world.z), Vector3.down, 60f, ~0, QueryTriggerInteraction.Ignore);
            float best = ty;
            foreach (var h in hits)
                if (h.collider && h.point.y > best && InWreck(h.collider.transform)) best = h.point.y;
            y = best;
            return best > float.MinValue;
        }

        private static bool InWreck(Transform t)
        {
            for (; t; t = t.parent) if (t.name.StartsWith("wreck ")) return true;
            return false;
        }

        internal static ForageIsland Of(Transform root)
        {
            var f = root.GetComponent<ForageIsland>();
            return f ? f : root.gameObject.AddComponent<ForageIsland>();
        }
    }

    /// <summary>Where things grow: decided once per island from its seed, after props and wrecks are placed.</summary>
    internal static class Forage
    {
        internal const int Coconut = 49, Banana = 44, ForestMushroom = 144, FieldMushroom = 145, Orange = 147, Apple = 149;

        public static void Plan(IList<SpawnedIsland> islands)
        {
            if (!Plugin.ForageEnabled.Value) return;
            int total = 0;
            foreach (var s in islands)
            {
                if (!s.Root) continue;
                var f = ForageIsland.Of(s.Root);
                var rng = new System.Random(s.Seed * 211 + 13);
                var terrain = s.Root.GetComponentInChildren<Terrain>(true);
                if (!terrain) continue;
                int before = f.spots.Count;
                var trees = s.Root.GetComponentsInChildren<Transform>(true).Where(t => t && t.name.StartsWith("tree ")).ToList();
                bool south = s.Latitude < 36f;
                foreach (var t in trees)
                {
                    bool palm = t.name.ToLowerInvariant().Contains("palm");
                    int item = -1, min = 1, max = 2; double chance = 0;
                    if (palm) { if (south) { item = Coconut; chance = 0.33 * Plugin.ForageScale.Value; min = 1; max = 3; } }
                    else if (s.Climate == Climate.Asian || s.Climate == Climate.Oceanic) { item = Orange; chance = 0.10 * Plugin.ForageScale.Value; max = 2; }
                    else if (s.Climate == Climate.North && t.name.Contains("tree_fat_002")) { item = Apple; chance = 0.18 * Plugin.ForageScale.Value; max = 3; }   // the broad tree (tree_fat is slim, cypress-like)
                    if (item < 0 || rng.NextDouble() >= chance) continue;
                    int n = rng.Next(min, max + 1);
                    for (int k = 0; k < n; k++) Add(f, s, terrain, t.position, 0.8f, palm ? 2.5f : 2f, item, rng, false);
                }
                // bananas: the low desert palm (bush "palm_tree_small_2") grown up ×1.6–1.9 into a banana plant, 1–2 under it
                foreach (var b in s.Root.GetComponentsInChildren<Transform>(true).Where(t => t && t.name.StartsWith("bush ") && t.name.Contains("palm_tree_small_2")).ToList())
                {
                    if (!south || rng.NextDouble() >= 0.35 * Plugin.ForageScale.Value) continue;
                    b.localScale *= Mathf.Lerp(1.6f, 1.9f, (float)rng.NextDouble());
                    int n = rng.Next(1, 3);
                    for (int k = 0; k < n; k++) Add(f, s, terrain, b.position, 0.6f, 1.8f, Banana, rng, false);
                }
                // mushrooms: small clusters in the shade of trees, on green islands (north, Emerald, Lagoon)
                if (s.Climate != Climate.Desert && s.Type != IslandType.Atoll && s.Type != IslandType.Sandbar)
                {
                    var shady = trees.Where(t => !t.name.ToLowerInvariant().Contains("palm"))
                        .Concat(s.Root.GetComponentsInChildren<Transform>(true).Where(t => t && t.name.StartsWith("bush ") && !t.name.Contains("palm")))
                        .ToList();
                    int clusters = shady.Count == 0 ? 0 : Mathf.RoundToInt(rng.Next(2, 6) * Plugin.ForageScale.Value);
                    for (int c = 0; c < clusters; c++)
                    {
                        var t = shady[rng.Next(shady.Count)];
                        int kind = ForestMushroom;                         // field mushrooms grow in the open (below)
                        float a = (float)rng.NextDouble() * Mathf.PI * 2f, r = Mathf.Lerp(1.5f, 4f, (float)rng.NextDouble());
                        var centre = t.position + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                        int n = rng.Next(3, 6);
                        for (int k = 0; k < n; k++) Add(f, s, terrain, centre, 0f, 0.9f, kind, rng, false, true);
                    }
                    // field mushrooms: small rings anywhere on the grass — not tied to trees (may still end up near one)
                    int open = Mathf.RoundToInt(rng.Next(2, 6) * Plugin.ForageScale.Value);
                    var tp0 = terrain.GetPosition(); var tsz = terrain.terrainData.size;
                    for (int c = 0, tries = 0; c < open && tries < 600; tries++)
                    {
                        var q = tp0 + new Vector3((float)rng.NextDouble() * tsz.x, 0f, (float)rng.NextDouble() * tsz.z);
                        if (GrassAt(terrain, q) < 0.5f) continue;
                        q.y = terrain.SampleHeight(q) + tp0.y;
                        if (q.y - s.Root.position.y < 1f) continue;
                        int n = rng.Next(2, 4);
                        for (int k = 0; k < n; k++) Add(f, s, terrain, q, 0f, 0.8f, FieldMushroom, rng, false, true);
                        c++;
                    }
                }
                total += f.spots.Count - before;
            }
            Plugin.Log.LogInfo($"Forage: {total} spots on {islands.Count} islands");
        }

        /// <summary>A spot at a random point within r0..r1 of `centre` (world), on dry land.</summary>
        internal static bool Add(ForageIsland f, SpawnedIsland s, Terrain terrain, Vector3 centre, float r0, float r1, int item,
                                 System.Random rng, bool once, bool onGrass = false)
        {
            for (int tries = 0; tries < 6; tries++)
            {
                float a = (float)rng.NextDouble() * Mathf.PI * 2f, r = Mathf.Lerp(r0, r1, (float)rng.NextDouble());
                var p = centre + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                float gy = terrain.SampleHeight(p) + terrain.GetPosition().y;
                if (gy - s.Root.position.y < 0.3f) continue;         // not in the sea
                if (onGrass && GrassAt(terrain, p) < 0.4f) continue;  // mushrooms: never on sand or rock
                p.y = gy;
                f.spots.Add(new ForageIsland.Spot { local = s.Root.InverseTransformPoint(p), item = item, yaw = (float)rng.NextDouble() * 360f, once = once, onTerrain = true,
                                                        upright = item == ForestMushroom || item == FieldMushroom });
                return true;
            }
            return false;
        }

        // grass cover (terrain layers named "Grass…") at a world point; alphamaps cached per terrain while planning
        private static Terrain cachedT;
        private static float[,,] cachedA;
        private static int[] grassLayers;
        internal static float GrassAt(Terrain t, Vector3 world)
        {
            var td = t.terrainData;
            if (t != cachedT)
            {
                cachedT = t;
                cachedA = td.GetAlphamaps(0, 0, td.alphamapResolution, td.alphamapResolution);
                var ls = td.terrainLayers ?? new TerrainLayer[0];
                grassLayers = Enumerable.Range(0, ls.Length).Where(i => ls[i] && ls[i].name.StartsWith("Grass")).ToArray();
            }
            int res = cachedA.GetLength(0);
            var p = world - t.GetPosition();
            int x = Mathf.Clamp(Mathf.RoundToInt(p.x / td.size.x * (res - 1)), 0, res - 1), z = Mathf.Clamp(Mathf.RoundToInt(p.z / td.size.z * (res - 1)), 0, res - 1);
            float g = 0f;
            foreach (int i in grassLayers) if (i < cachedA.GetLength(2)) g += cachedA[z, x, i];
            return g;
        }

        /// <summary>Loot by a wreck: once per game; may lie on its deck.</summary>
        internal static void AddLoot(SpawnedIsland s, Vector3 world, int item, float yaw)
        {
            var f = ForageIsland.Of(s.Root);
            f.spots.Add(new ForageIsland.Spot { local = s.Root.InverseTransformPoint(world), item = item, yaw = yaw, once = true, onTerrain = false, physics = true });
        }
    }

    /// <summary>After a save is loaded, spots read their state again.</summary>
    [HarmonyPatch(typeof(SaveLoadManager), "LoadModData")]
    internal static class ForageLoadPatch
    {
        private static void Postfix()
        {
            foreach (var f in ForageIsland.All) if (f) f.Load();
        }
    }
}
