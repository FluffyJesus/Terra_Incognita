using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TerraIncognita
{
    /// <summary>
    /// Procedural sea rocks the heightmap can't make (one height per point: no sheer walls, no overhangs, no arches):
    ///   spire — a tapering, slightly bent fang;
    ///   stack — a pillar standing in the sea, undercut at the waterline, flat-ish top;
    ///   arch  — a bridge of rock over the water, both feet in the sea.
    /// Built as a mesh at spawn: an irregular cross-section (7–10 sides) swept along a bent spine, faceted (flat
    /// shading, the game's low-poly look), textured with the island terrain's own rock layer projected per face —
    /// so it matches the island's cliffs. MeshCollider (it's a rock: ships hit it, the player can climb it).
    /// </summary>
    internal static class RockSculptor
    {
        /// <summary>
        /// Rocks placed by hand in the editor (Tools › Terra Incognita › 4. Rock sculptor, "rock sculpted …"): they
        /// come from the bundle with the PaintedRock shader and their shading baked in — dressed here in the
        /// region's rock colour, made solid, on the depth layer (Crest shallows/foam) and tagged as stone.
        /// </summary>
        private static bool loggedColours;

        // No grass growing through the rocks: where a ray from above meets the rock before (or within 0.3 m under)
        // the ground — at a cell's centre or 0.6 m to any side, so blades by the foot don't poke through its flanks.
        private static void ClearGrassUnder(SpawnedIsland s, Terrain t, List<MeshFilter> rocks)
        {
            if (!t || !t.terrainData) return;
            Physics.SyncTransforms();
            var td = t.terrainData; var tp = t.GetPosition();
            int added = 0;
            foreach (var f in rocks)
            {
                if (!f) continue;
                var mc = f.GetComponent<MeshCollider>(); var mr = f.GetComponent<MeshRenderer>();
                if (!mc || !mr) continue;
                var b = mr.bounds; b.Expand(new Vector3(2f, 0f, 2f));
                var area = Rect.MinMaxRect(b.min.x - tp.x, b.min.z - tp.z, b.max.x - tp.x, b.max.z - tp.z);
                float top = b.max.y + 5f;
                var offs = new[] { Vector2.zero, new Vector2(0.6f, 0f), new Vector2(-0.6f, 0f), new Vector2(0f, 0.6f), new Vector2(0f, -0.6f) };
                DetailGrass.AddMask(td, area, q =>
                {
                    if (!mc) return false;
                    foreach (var o in offs)
                    {
                        var w = new Vector3(tp.x + q.x + o.x, 0f, tp.z + q.y + o.y);
                        float gy = t.SampleHeight(w) + tp.y;
                        var ray = new Ray(new Vector3(w.x, top, w.z), Vector3.down);
                        if (mc.Raycast(ray, out var hit, top - gy + 2f) && hit.point.y > gy - 0.3f) return true;
                    }
                    return false;
                });
                added++;
            }
            if (added == 0) return;
            var gs = t.GetComponent<DetailGrass.GrassState>();
            if (gs) DetailGrass.Apply(t, gs.seed, gs.type, gs.region);
        }

        public static int Dress(SpawnedIsland s)
        {
            if (!s.Root) return 0;
            var rocks = s.Root.GetComponentsInChildren<MeshFilter>(true).Where(f => f.name.StartsWith("rock sculpted ")).ToList();
            if (rocks.Count == 0) return 0;
            var t = s.Root.GetComponentInChildren<Terrain>(true);
            Material mat = null;
            var src = rocks.Select(f => f.GetComponent<MeshRenderer>()).FirstOrDefault(r => r && r.sharedMaterial)?.sharedMaterial;
            try { mat = MaterialFor(t, s.Climate, src); }
            catch (System.Exception e) { Plugin.Log.LogError($"Sculpted rock material failed: {e}"); }
            int depth = LayerMask.NameToLayer("TerrainDepth");
            // a rock may belong to some climates only ("… cN": granite domes in the north, "cD": desert mushroom rocks)
            char here = s.Climate == Climate.Desert ? 'D' : s.Climate == Climate.Asian ? 'A' : s.Climate == Climate.Oceanic ? 'O' : 'N';
            int dropped = 0;
            foreach (var f in rocks.ToList())
            {
                var tag = f.name.Split(' ').FirstOrDefault(p => p.Length > 1 && p[0] == 'c' && p.Substring(1).All(ch => "DAON".IndexOf(ch) >= 0));
                if (tag != null && tag.IndexOf(here, 1) < 0) { Object.DestroyImmediate(f.gameObject); rocks.Remove(f); dropped++; }
            }
            if (dropped > 0) Plugin.Debug($"{s.Root.name}: {dropped} sculpted rocks of other climates removed");
            if (!loggedColours && rocks.Count > 0)
            {
                loggedColours = true;
                int baked = rocks.Count(f => f.sharedMesh && f.sharedMesh.isReadable && f.sharedMesh.colors32.Length > 0);
                int unreadable = rocks.Count(f => f.sharedMesh && !f.sharedMesh.isReadable);
                Plugin.Log.LogInfo($"Sculpted rocks on {s.Root.name}: {baked}/{rocks.Count} with baked shading" +
                                   (unreadable > 0 ? $", {unreadable} meshes unreadable (can't tell)" : ""));
            }
            foreach (var f in rocks)
            {
                var go = f.gameObject;
                var mr = go.GetComponent<MeshRenderer>();
                var mc = go.GetComponent<MeshCollider>();
                if (!mc) mc = go.AddComponent<MeshCollider>();
                if (!mc.sharedMesh) mc.sharedMesh = f.sharedMesh;
                mc.enabled = true;                                                  // (a mesa wall's came switched off in its prefab)
                if (mr && mat) mr.sharedMaterial = mat;
                if (depth >= 0) go.layer = depth;
                go.tag = "Stone";
            }
            // (no grass under the rocks: the editor paints the ground there to the rock layer — RockSculptorWindow.
            // RepaintUnderRocks — and grass only grows on the grass layer; ClearGrassUnder below is the old runtime way)
            return rocks.Count;
        }

        // ---------------------------------------------------------------- cave loot
        // Grottos come from the editor with hiding places on their floor ("cave spot chamber / branch / end"). Now and
        // then ([Rocks] CaveLootChance per grotto) something is hidden in one, chambers first: in 70 % a smugglers'
        // stash (2–4 barrels and bottles of drink), in 30 % a crate of silver, gold or — rarely — gems. Once per game,
        // like the wrecks' loot; the same caves every launch (island seed).
        private static readonly int[] Stash = { 11, 11, 13, 206, 228, 12, 59, 59, 56 };   // rum, wine, mead, cider, beer; bottles
        private static readonly int[] Hoard = { 226, 226, 226, 22, 22, 20 };              // silver, gold, gems
        private static readonly int[] Barrels = { 13, 13, 11, 12, 228, 206 };             // wine (most), rum, beer, cider, mead

        public static int CaveLoot(SpawnedIsland s)
        {
            if (!s.Root || Plugin.CaveLootChance.Value <= 0f) return 0;
            int placed = 0, cave = 0;
            foreach (var g in s.Root.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("rock sculpted grotto") || t.name.StartsWith("rock sculpted mesawall")).ToList())
            {
                var rng = new System.Random(s.Seed * 41 + 7 + cave++ * 977);
                if (rng.NextDouble() >= Plugin.CaveLootChance.Value) continue;
                var spots = g.Cast<Transform>().Where(t => t.name.StartsWith("cave spot")).ToList();
                if (spots.Count == 0) continue;
                // room for a barrel: a 0.55 m ball half a metre up touches no collider (the spot is 0.6 m over the floor)
                Physics.SyncTransforms();
                bool Free(Vector3 p) => !Physics.CheckSphere(p + Vector3.up * 0.5f, 0.55f, ~0, QueryTriggerInteraction.Ignore);
                spots = spots.Where(t => Free(t.position)).ToList();                   // a niche too tight: not used
                if (spots.Count == 0) { Plugin.Debug($"Cave loot on {s.Root.name}: no spot with room in {g.name}"); continue; }
                int Weight(Transform t) => t.name.EndsWith("chamber") ? 3 : t.name.EndsWith("branch") ? 2 : 1;
                int roll = rng.Next(spots.Sum(Weight));
                var spot = spots.First(t => (roll -= Weight(t)) < 0);
                var items = new List<int>();
                bool hoard = rng.NextDouble() < 0.3;
                // a mesa wall's sea cave is small: 1–2 barrels (no bottles strewn about) or the one crate
                bool small = g.name.StartsWith("rock sculpted mesawall");
                if (hoard) items.Add(Hoard[rng.Next(Hoard.Length)]);
                else if (small) for (int i = 0, n = rng.Next(1, 3); i < n; i++) items.Add(Barrels[rng.Next(Barrels.Length)]);
                else for (int i = 0, n = rng.Next(2, 5); i < n; i++) items.Add(Stash[rng.Next(Stash.Length)]);
                // each piece where it has room — clear of the rock and of the pieces already set down: a few tries,
                // closing in on the spot; a piece with no room is left out
                var taken = new List<Vector3>();
                int left = 0;
                foreach (int item in items)
                {
                    float spread = items.Count > 1 ? (small ? 0.7f : 1f) : 0.2f;
                    bool ok = false;
                    for (int tr = 0; tr < 10 && !ok; tr++)
                    {
                        var off = new Vector3((float)rng.NextDouble() * 2f - 1f, 0f, (float)rng.NextDouble() * 2f - 1f) * spread * (1f - tr / 10f);
                        var at = spot.position + off;
                        if (!Free(at) || taken.Any(q => (q - at).magnitude < 0.9f)) continue;
                        Forage.AddLoot(s, at, item, (float)rng.NextDouble() * 360f);
                        taken.Add(at); placed++; ok = true;
                    }
                    if (!ok) left++;
                }
                if (taken.Count == 0) continue;
                if (left > 0) items.RemoveRange(items.Count - left, left);             // (for the log line)
                Plugin.Debug($"Cave loot on {s.Root.name}: {(hoard ? "hoard" : "smugglers' stash")} {string.Join(",", items.Select(i => i.ToString()).ToArray())} in the {spot.name.Substring(10)}");
                if (Wrecks.Placed.ContainsKey(s.Root.name)) Wrecks.Placed[s.Root.name] += $", CAVE {(hoard ? "HOARD" : "STASH")}";
            }
            return placed;
        }

        // ---------------------------------------------------------------- placement (automatic; off by default since
        // the rocks are placed by hand in the editor)
        public static int Place(SpawnedIsland s)
        {
            if (!Plugin.SculptedRocks.Value || !s.Root) return 0;
            var t = s.Root.GetComponentInChildren<Terrain>(true);
            if (!t || !t.terrainData) return 0;
            var rng = new System.Random(s.Seed * 97 + 41);
            float scale = Plugin.SculptedRockAmount.Value;

            // what each kind of island gets (counts are maxima: a spot must be found for each)
            int stacks = 0, arches = 0, spires = 0;
            double R() => rng.NextDouble();
            switch (s.Type)
            {
                case IslandType.SeaStacks: stacks = 2 + rng.Next(3); arches = R() < 0.6 ? 1 : 0; break;
                case IslandType.RockyIslet: spires = 1 + rng.Next(2); stacks = R() < 0.6 ? 1 : 0; break;
                case IslandType.Sandbar:
                case IslandType.SunkenRidge:
                case IslandType.Atoll: stacks = R() < 0.4 ? 1 : 0; break;
                case IslandType.VolcanicBay:
                case IslandType.TwinPeaks: spires = R() < 0.5 ? 1 : 0; stacks = R() < 0.4 ? 1 : 0; arches = R() < 0.2 ? 1 : 0; break;
                default: stacks = R() < 0.35 ? 1 : 0; arches = R() < 0.15 ? 1 : 0; break;
            }
            stacks = Mathf.RoundToInt(stacks * scale); arches = Mathf.RoundToInt(arches * scale); spires = Mathf.RoundToInt(spires * scale);
            if (stacks + arches + spires == 0) return 0;

            var td = t.terrainData; var tp = t.GetPosition();
            float sea = s.Root.position.y;
            float H(Vector3 p) => t.SampleHeight(p) + tp.y - sea;              // height above the sea (− = depth)
            Vector3 Rand() => tp + new Vector3((float)R() * td.size.x, 0f, (float)R() * td.size.z);

            // keep clear of wrecks, of each other and of the island's big placed rocks
            var busy = new List<(Vector3 p, float r)>();
            if (Wrecks.Hulls.TryGetValue(s.Root.name, out var hull)) busy.Add((hull.hull.center, hull.hull.extents.magnitude + 12f));
            foreach (var r in s.Root.GetComponentsInChildren<Transform>(true).Where(x => x && (x.name.StartsWith("rock ") || x.name.StartsWith("cliff "))))
                busy.Add((r.position, 8f));
            bool Free(Vector3 p, float r) => busy.All(b => new Vector2(b.p.x - p.x, b.p.z - p.z).magnitude > b.r + r);

            var mat = MaterialFor(t, s.Climate);
            int made = 0;
            for (int i = 0; i < stacks; i++)
                for (int tries = 0; tries < 300; tries++)
                {
                    var p = Rand(); float h = H(p);
                    if (h > -1.2f || h < -9f) continue;                          // standing in the sea, 1–9 m deep
                    float rad = Mathf.Lerp(3.5f, 7f, (float)R());
                    if (!Free(p, rad + 6f)) continue;
                    // not out in the blue: land within ~70 m
                    if (!Ring(p, 70f, 12).Any(q => H(q) > 0.5f)) continue;
                    float height = Mathf.Lerp(12f, 30f, (float)R()) + (-h);
                    Spawn(s, Stack(rng, rad, height, -h), p.WithY(sea + h - 2f), (float)R() * 360f, mat, "stack");
                    busy.Add((p, rad)); made++; break;
                }
            for (int i = 0; i < arches; i++)
                for (int tries = 0; tries < 400; tries++)
                {
                    var p = Rand(); float h = H(p);
                    if (h > -1.5f || h < -8f) continue;
                    float yaw = (float)R() * 360f, span = Mathf.Lerp(16f, 30f, (float)R());
                    var along = Quaternion.Euler(0f, yaw, 0f) * Vector3.right * (span * 0.5f);
                    float ha = H(p - along), hb = H(p + along);
                    if (ha > 1.5f || hb > 1.5f || ha < -12f || hb < -12f) continue;  // both feet in the shallows or on the shore
                    if (!Free(p, span * 0.5f + 6f)) continue;
                    if (!Ring(p, 80f, 12).Any(q => H(q) > 0.5f)) continue;
                    float clear = Mathf.Lerp(9f, 16f, (float)R());               // headroom under the arch
                    var mesh = Arch(rng, span, clear + 3f, Mathf.Lerp(2.6f, 4f, (float)R()), Mathf.Max(-ha, -hb, 0f) + 2f);
                    Spawn(s, mesh, p.WithY(sea), yaw, mat, "arch");
                    busy.Add((p, span * 0.5f)); made++; break;
                }
            for (int i = 0; i < spires; i++)
                for (int tries = 0; tries < 300; tries++)
                {
                    var p = Rand(); float h = H(p);
                    if (h < -4f || h > 25f) continue;                            // shallows, beach or low ground
                    float rad = Mathf.Lerp(3f, 6f, (float)R());
                    if (!Free(p, rad + 5f)) continue;
                    // the lowest ground under its foot: it must not float on a slope
                    float low = Ring(p, rad, 8).Select(H).Concat(new[] { h }).Min();
                    float height = Mathf.Lerp(18f, 40f, (float)R());
                    Spawn(s, Spire(rng, rad, height + (h - low) + 2f), p.WithY(sea + low - 2f), (float)R() * 360f, mat, "spire");
                    busy.Add((p, rad)); made++; break;
                }
            if (made > 0)
                Plugin.Log.LogInfo($"Sculpted rocks on {s.Root.name}: " + string.Join(", ", s.Root.GetComponentsInChildren<Transform>(true)
                    .Where(x => x.name.StartsWith("rock sculpted ")).GroupBy(x => x.name.Substring(14)).Select(g => g.Count() + " " + g.Key).ToArray()));
            return made;
        }

        private static IEnumerable<Vector3> Ring(Vector3 c, float r, int n)
        {
            for (int i = 0; i < n; i++) { float a = i * Mathf.PI * 2f / n; yield return c + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r); }
        }

        private static Vector3 WithY(this Vector3 v, float y) => new Vector3(v.x, y, v.z);

        private static void Spawn(SpawnedIsland s, Mesh mesh, Vector3 at, float yaw, Material mat, string kind)
        {
            var go = new GameObject("rock sculpted " + kind);
            go.transform.SetParent(s.Root, true);
            go.transform.position = at;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.localScale = new Vector3(1f / s.Root.lossyScale.x, 1f / s.Root.lossyScale.y, 1f / s.Root.lossyScale.z);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            int depth = LayerMask.NameToLayer("TerrainDepth");                  // Crest: shallows and foam around it
            if (depth >= 0) go.layer = depth;
            go.tag = "Stone";
        }

        // ---------------------------------------------------------------- shapes
        // A spine: points with a radius each; the section is the same irregular polygon (per rock), each ring a bit
        // jittered and turned. Spine runs along +Y from 0.
        private struct Knot { public Vector3 p; public float r; }

        private static Mesh Spire(System.Random rng, float rad, float height)
        {
            var knots = new List<Knot>();
            int n = 9;
            var lean = new Vector3((float)rng.NextDouble() - 0.5f, 0f, (float)rng.NextDouble() - 0.5f) * 0.35f;
            for (int i = 0; i <= n; i++)
            {
                float f = i / (float)n;
                var off = lean * height * f * f;                                  // bends more towards the tip
                knots.Add(new Knot { p = new Vector3(off.x, f * height, off.z), r = rad * Mathf.Lerp(1f, 0.12f, Mathf.Pow(f, 0.8f)) });
            }
            return Sweep(rng, knots, capTop: true, tipRise: 1.5f);
        }

        private static Mesh Stack(System.Random rng, float rad, float height, float depth)
        {
            var knots = new List<Knot>();
            int n = 10;
            var lean = new Vector3((float)rng.NextDouble() - 0.5f, 0f, (float)rng.NextDouble() - 0.5f) * 0.18f;
            float water = (depth + 2f) / height;                                  // waterline along the spine (base is 2 m under the bed)
            for (int i = 0; i <= n; i++)
            {
                float f = i / (float)n;
                // undercut: the sea gnaws a neck just above the waterline
                float neck = 1f - 0.28f * Mathf.Exp(-Mathf.Pow((f - water - 0.05f) / 0.08f, 2f));
                float top = f > 0.85f ? Mathf.Lerp(1f, 0.75f, (f - 0.85f) / 0.15f) : 1f;
                var off = lean * height * f;
                knots.Add(new Knot { p = new Vector3(off.x, f * height, off.z), r = rad * neck * top * Mathf.Lerp(1.15f, 0.9f, f) });
            }
            return Sweep(rng, knots, capTop: true, tipRise: 0.8f);
        }

        // a half-ellipse from foot to foot across X; feet go `sink` below the sea
        private static Mesh Arch(System.Random rng, float span, float rise, float rad, float sink)
        {
            var knots = new List<Knot>();
            int n = 16;
            for (int i = 0; i <= n; i++)
            {
                float a = Mathf.PI * i / n;                                       // 0..π
                float x = -Mathf.Cos(a) * span * 0.5f, y = Mathf.Sin(a) * rise;
                float foot = Mathf.Pow(Mathf.Abs(Mathf.Cos(a)), 3f);             // thicker towards the feet
                knots.Add(new Knot { p = new Vector3(x, y, 0f), r = rad * (1f + 0.6f * foot) });
            }
            // the feet run on down into the sea, straight
            knots.Insert(0, new Knot { p = knots[0].p + Vector3.down * sink + Vector3.left * 1.5f, r = knots[0].r * 1.15f });
            knots.Add(new Knot { p = knots[knots.Count - 1].p + Vector3.down * sink + Vector3.right * 1.5f, r = knots[knots.Count - 1].r * 1.15f });
            return Sweep(rng, knots, capTop: false, tipRise: 0f, capBothEnds: true);
        }

        private static Mesh Sweep(System.Random rng, List<Knot> knots, bool capTop, float tipRise, bool capBothEnds = false)
        {
            int sides = 7 + rng.Next(4);
            var section = new float[sides];
            for (int k = 0; k < sides; k++) section[k] = Mathf.Lerp(0.75f, 1.2f, (float)rng.NextDouble());
            var rings = new List<Vector3[]>();
            Vector3 prevNormal = Vector3.forward;
            for (int i = 0; i < knots.Count; i++)
            {
                var tan = (knots[Mathf.Min(i + 1, knots.Count - 1)].p - knots[Mathf.Max(i - 1, 0)].p).normalized;
                // a frame that turns smoothly along the spine (no sudden twist on bends)
                var nrm = Vector3.ProjectOnPlane(prevNormal, tan);
                if (nrm.sqrMagnitude < 1e-4f) nrm = Vector3.ProjectOnPlane(Vector3.right, tan);
                nrm.Normalize(); prevNormal = nrm;
                var bin = Vector3.Cross(tan, nrm);
                float twist = ((float)rng.NextDouble() - 0.5f) * 0.35f;
                var ring = new Vector3[sides];
                for (int k = 0; k < sides; k++)
                {
                    float a = (k + twist) * Mathf.PI * 2f / sides;
                    float r = knots[i].r * section[k] * Mathf.Lerp(0.88f, 1.12f, (float)rng.NextDouble());
                    ring[k] = knots[i].p + (nrm * Mathf.Cos(a) + bin * Mathf.Sin(a)) * r;
                }
                rings.Add(ring);
            }

            // each triangle knows where its outside is and is turned to face it (caps came out reversed before)
            var tris = new List<(Vector3[] t, Vector3 outward)>();
            Vector3 Centre(Vector3[] r) => r.Aggregate(Vector3.zero, (acc, x) => acc + x) / r.Length;
            for (int i = 0; i + 1 < rings.Count; i++)
            {
                var axis = (Centre(rings[i]) + Centre(rings[i + 1])) * 0.5f;
                for (int k = 0; k < sides; k++)
                {
                    int k1 = (k + 1) % sides;
                    var a = rings[i][k]; var b = rings[i][k1]; var c = rings[i + 1][k1]; var d = rings[i + 1][k];
                    tris.Add((new[] { a, c, b }, (a + b + c) / 3f - axis));
                    tris.Add((new[] { a, d, c }, (a + c + d) / 3f - axis));
                }
            }
            void Cap(Vector3[] ring, Vector3 centre, Vector3 outward)
            {
                for (int k = 0; k < sides; k++) tris.Add((new[] { centre, ring[k], ring[(k + 1) % sides] }, outward));
            }
            if (capTop || capBothEnds)
            {
                var last = rings[rings.Count - 1];
                var tipDir = (knots[knots.Count - 1].p - knots[knots.Count - 2].p).normalized;
                Cap(last, Centre(last) + tipDir * tipRise, tipDir);
            }
            if (capBothEnds) Cap(rings[0], Centre(rings[0]), (knots[0].p - knots[1].p).normalized);
            return Build(tris);
        }

        // flat-shaded (every triangle its own vertices); UVs projected on the face's main plane, in metres / tile
        private static Mesh Build(List<(Vector3[] t, Vector3 outward)> tris)
        {
            var v = new List<Vector3>(tris.Count * 3); var uv = new List<Vector2>(tris.Count * 3); var idx = new List<int>(tris.Count * 3);
            foreach (var (t, outward) in tris)
            {
                bool flip = Vector3.Dot(Vector3.Cross(t[1] - t[0], t[2] - t[0]), outward) < 0f;
                var a = t[0]; var b = flip ? t[2] : t[1]; var c = flip ? t[1] : t[2];
                var n = Vector3.Cross(b - a, c - a);
                float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
                Vector2 P(Vector3 p) => ay >= ax && ay >= az ? new Vector2(p.x, p.z) : ax >= az ? new Vector2(p.z, p.y) : new Vector2(p.x, p.y);
                foreach (var p in new[] { a, b, c, a, c, b }) { idx.Add(v.Count); v.Add(p); uv.Add(P(p) / TileMetres); }   // both windings: no holes
            }
            var m = new Mesh { name = "terra incognita sculpted rock" };
            if (v.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(idx, 0);
            m.RecalculateNormals(); m.RecalculateTangents(); m.RecalculateBounds();
            return m;
        }

        // ---------------------------------------------------------------- material: painted rock (our shader)
        private static float TileMetres = 12f;                                  // UVs of the automatic rocks

        // The region's rock colour: the average of the vanilla cliffs' painted atlases (Gold Rock for the desert
        // and oceanic islands, Dragon Cliffs for Emerald, Fort Aestrin for the north).
        private static Color RegionRock(Climate c) =>
              c == Climate.North ? new Color32(161, 149, 136, 255)
            : c == Climate.Asian ? new Color32(148, 122, 108, 255)
            : new Color32(161, 141, 118, 255);

        private const string PaintedShader = "TerraIncognita/PaintedRock";
        private static Shader painter;                                          // from the bundle (the editor material)
        private static readonly Dictionary<Climate, Material> painted = new Dictionary<Climate, Material>();

        private static RockPaint paint;
        internal static RockPaint Paint => paint ?? (paint = RockPaint.Parse(Plugin.SculptedPaint.Value));

        /// <summary>
        /// The rocks' material for a climate: the PaintedRock shader (no texture: 3D blotches + shading baked into
        /// the vertex colours by the editor) in the region's colour. `src` = a rock's bundled material, which brings
        /// the shader along. Without it (old bundle): plain Standard in the region's colour.
        /// </summary>
        private static Material MaterialFor(Terrain t, Climate c, Material src = null)
        {
            if (!painter && src && src.shader && src.shader.name == PaintedShader && src.shader.isSupported) painter = src.shader;
            if (painted.TryGetValue(c, out var m) && m && (m.shader == painter || !painter)) return m;
            m = new Material(painter ? painter : Shader.Find("Standard")) { name = "terra incognita painted rock " + c };
            m.color = RegionRock(c);
            if (!painter) m.SetFloat("_Glossiness", 0.05f);
            Paint.ApplyTo(m);
            Plugin.Log.LogInfo($"Sculpted rocks ({c}): {(painter ? "painted rock shader" : "no painted shader in the bundle, plain colour")}");
            painted[c] = m;
            return m;
        }

        /// <summary>Push the current paint settings into every rock material (F4 window).</summary>
        internal static void Repaint()
        {
            foreach (var m in painted.Values) if (m) Paint.ApplyTo(m);
            foreach (var m in vanilla.Values) if (m) { Paint.ApplyTo(m); m.SetFloat("_VertexShading", 0f); }
        }

        // The painted material for the vanilla rocks placed on our islands (their meshes have no baked shading: the
        // shader's defaults stand in), so they match the sculpted ones next to them. Null until a grotto/rock bundle
        // brought the shader (then the caller keeps the game's own rock material).
        private static readonly Dictionary<Climate, Material> vanilla = new Dictionary<Climate, Material>();
        internal static Material VanillaRockMaterial(Climate c)
        {
            if (!painter || !painter.isSupported) return null;
            if (vanilla.TryGetValue(c, out var m) && m) return m;
            m = new Material(painter) { name = "terra incognita painted vanilla rock " + c, color = RegionRock(c) };
            Paint.ApplyTo(m);
            m.SetFloat("_VertexShading", 0f);
            vanilla[c] = m;
            return m;
        }

        internal static void SavePaint()
        {
            Plugin.SculptedPaint.Value = Paint.ToString();
            Plugin.Instance.Config.Save();
        }

        internal static void ResetPaint() { paint = new RockPaint(); Repaint(); }
    }
}
