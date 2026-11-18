using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TerraIncognita
{
    /// <summary>
    /// Survivors' bivouac: on an island with a wreck and fresh water, a chance of a small camp on level ground near
    /// the wreck — a sailcloth tent (one of three), a dead fire pit in front of its door, some things they left
    /// behind (bottles, firewood, a mug, a pot, a rod…). Models are the user's (bundle, "ti camp …").
    /// </summary>
    internal static class Camp
    {
        // left behind: empty bottle, water bottle, fuel wood, mug, fishing rod, hook, pot, pipes, candle, lantern
        private static readonly int[] Leftovers = { 40, 40, 55, 71, 100, 95, 99, 156, 300, 302, 130, 114 };
        internal static readonly HashSet<string> Camps = new HashSet<string>();
        private const float TentScale = 1.25f;                                   // (the models at 1:1 looked small next to the player)

        public static int PlaceAll(IList<SpawnedIsland> spawned)
        {
            Camps.Clear();
            if (!Plugin.CampsEnabled.Value) return 0;
            var prefabs = IslandSpawner.CampPrefabs();
            var fire = prefabs.FirstOrDefault(p => p.name.ToLowerInvariant().Contains("campfire"));
            var tents = prefabs.Where(p => p.name.ToLowerInvariant().Contains("tent")).ToList();
            if (!fire || tents.Count == 0)
            {
                Plugin.Log.LogInfo($"Camps: no camp models in the bundle ({prefabs.Count} found)");
                return 0;
            }
            return spawned.Count(s => Place(s, tents, fire));
        }

        private static bool Place(SpawnedIsland s, List<GameObject> tents, GameObject fire)
        {
            if (!s.Root || !Wrecks.Hulls.TryGetValue(s.Root.name, out var wreck) || !wreck.t) return false;
            var ponds = s.Root.GetComponentsInChildren<WaterSource>(true);   // (not required any more: preferred spots)
            var rng = new System.Random(s.Seed * 29 + 3);
            if (rng.NextDouble() >= Plugin.CampChance.Value) return false;

            var t = wreck.t; var tpos = t.GetPosition(); var td = t.terrainData;
            float sea = s.Root.position.y;
            float G(float x, float z) => t.SampleHeight(new Vector3(x, 0f, z)) + tpos.y;
            // things to keep clear of: trees, rocks, the wreck and its junk, ponds
            var keep = new List<(Vector2 p, float r)>();
            foreach (Transform c in s.Root.GetComponentsInChildren<Transform>(true))
            {
                string n = c.name;
                float r = n.StartsWith("tree ") || n.StartsWith("bamboo") ? 4f
                        : n.StartsWith("bush ") ? 3f                               // (the fire's pad left bushes hanging)
                        : n.StartsWith("rock ") || n.StartsWith("cliff ") ? 6f
                        : n.StartsWith("wreck decor") ? 2.5f : 0f;
                if (n.StartsWith("rock sculpted "))
                {
                    var mr = c.GetComponent<MeshRenderer>();
                    r = mr ? Mathf.Max(mr.bounds.extents.x, mr.bounds.extents.z) + 3f : 0f;
                }
                if (r > 0f) keep.Add((new Vector2(c.position.x, c.position.z), r));
            }
            foreach (var w in ponds) keep.Add((new Vector2(w.transform.position.x, w.transform.position.z), 12f));
            var hb = wreck.hull;
            keep.Add((new Vector2(hb.center.x, hb.center.z), Mathf.Max(hb.extents.x, hb.extents.z) + 4f));
            bool Clear(Vector2 p, float r) => keep.All(k => (k.p - p).magnitude > k.r + r);
            int nClear = 0, nSlope = 0, nHeight = 0, nBumpy = 0;                    // why spots were passed over (log)
            bool Level(Vector2 p, float r, float maxDiff, out float y, out float mean)
            {
                float lo = float.MaxValue, hi = float.MinValue, sum = 0f;
                for (int i = 0; i < 9; i++)
                {
                    var q = i == 0 ? p : p + new Vector2(Mathf.Cos(i * Mathf.PI / 4f), Mathf.Sin(i * Mathf.PI / 4f)) * r;
                    float h = G(q.x, q.y); lo = Mathf.Min(lo, h); hi = Mathf.Max(hi, h); sum += h;
                }
                y = lo; mean = sum / 9f;
                if (lo - sea <= 1f || hi - sea >= 25f) { nHeight++; return false; }   // (≤ 12 m left the plateaus out)
                if (hi - lo >= maxDiff) { nSlope++; return false; }
                return true;
            }

            var tent = tents[rng.Next(tents.Count)];
            var hull = new Vector2(hb.center.x, hb.center.z);
            float reach = Mathf.Max(hb.extents.x, hb.extents.z);
            // Where: near the wreck first (8–40 m off the hull), then near fresh water (14–35 m from a pond), then
            // anywhere low on the island (≤ 12 m up). The ground must be level as it is — no digging a pad for the
            // tent: a levelled patch on a slope showed at once that someone had put it there.
            var land = new List<Vector2>();
            for (int i = 0; i < 4000 && land.Count < 400; i++)
            {
                var q = new Vector2(tpos.x + (float)rng.NextDouble() * td.size.x, tpos.z + (float)rng.NextDouble() * td.size.z);
                float h = G(q.x, q.y) - sea;
                if (h > 1.5f && h < 12f) land.Add(q);
            }
            for (int attempt = 0; attempt < 1500; attempt++)
            {
                Vector2 p, face;
                if (attempt < 600)
                {
                    float a = (float)rng.NextDouble() * Mathf.PI * 2f, d = reach + Mathf.Lerp(8f, 40f, (float)rng.NextDouble());
                    p = hull + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * d;
                    face = hull;                                                    // the door looks out to the wreck
                }
                else if (attempt < 1000 && ponds.Length > 0)
                {
                    var w = ponds[rng.Next(ponds.Length)].transform.position;
                    float a = (float)rng.NextDouble() * Mathf.PI * 2f, d = Mathf.Lerp(14f, 35f, (float)rng.NextDouble());
                    face = new Vector2(w.x, w.z);
                    p = face + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * d;          // …or to the water
                }
                else
                {
                    if (land.Count == 0) break;
                    p = land[rng.Next(land.Count)];
                    face = hull;
                }
                if (p.x < tpos.x + 5f || p.y < tpos.z + 5f || p.x > tpos.x + td.size.x - 5f || p.y > tpos.z + td.size.z - 5f) continue;
                // the door looks out to what it faces (±40°); the fire 4–5 m in front of it. The models' pivot is at the
                // door (+Z), the tent reaching ~4 m back: level ground is wanted under its body.
                var toFace = (face - p).sqrMagnitude > 1f ? (face - p).normalized : Vector2.up;
                float yaw = Mathf.Atan2(toFace.x, toFace.y) * Mathf.Rad2Deg + Mathf.Lerp(-40f, 40f, (float)rng.NextDouble());
                var fwd = new Vector2(Mathf.Sin(yaw * Mathf.Deg2Rad), Mathf.Cos(yaw * Mathf.Deg2Rad));
                var body = p - fwd * 1.8f * TentScale;
                // (≤ 0.8 m across: the tent tilts with the slope anyway; at 0.5 only the flat sandbars and atolls kept camps)
                if (!Clear(body, 3f * TentScale)) { nClear++; continue; }
                if (!Level(body, 2.6f * TentScale, 0.8f, out float ty, out float tMean)) continue;
                var fp = p + fwd * Mathf.Lerp(4f, 5f, (float)rng.NextDouble()) * TentScale;
                if (!Clear(fp, 1.5f)) { nClear++; continue; }
                if (!Level(fp, 1.3f, 0.7f, out float fy, out float fMean)) continue;      // (its own small pad levels it)
                bool dig = false;
                // the fire pit on a small level pad (1.2 m round, easing out over 1 m: the ground there is nearly level
                // already) — on a slope its stones sank in on the uphill side
                Flatten(t, fp, 1.2f, 1f, fMean);
                fy = fMean;
                // anything planted on the ground the pad changed: back down onto it (never up)
                foreach (Transform c in s.Root.GetComponentsInChildren<Transform>(true))
                {
                    if (!(c.name.StartsWith("tree ") || c.name.StartsWith("bush ") || c.name.StartsWith("bamboo"))) continue;
                    if (new Vector2(c.position.x - fp.x, c.position.z - fp.y).magnitude > 3f) continue;
                    float gy = G(c.position.x, c.position.z);
                    if (c.position.y > gy + 0.02f) c.position = new Vector3(c.position.x, gy, c.position.z);
                }

                // seat the tent on the ground: tilted with the slope under its body, then raised until no point of
                // the ground under its footprint pokes up through the floor (the poles sink a few cm instead)
                var tp = new Vector3(body.x, 0f, body.y) - tpos;
                var gn = td.GetInterpolatedNormal(tp.x / td.size.x, tp.z / td.size.z);
                var rot = Quaternion.FromToRotation(Vector3.up, gn) * Quaternion.Euler(0f, yaw + Plugin.CampTentYaw.Value, 0f);
                var plane = new Plane(gn, new Vector3(body.x, G(body.x, body.y), body.y));
                float lift = float.MinValue, sag = float.MaxValue;
                foreach (float lx in new[] { -1.9f, 0f, 1.9f })                     // (out to the stakes)
                    foreach (float lz in new[] { 0.3f, -1.8f, -3.9f })
                    {
                        var w = new Vector3(p.x, 0f, p.y) + rot * new Vector3(lx, 0f, lz) * TentScale;
                        float gy = G(w.x, w.z);
                        // ground height over (+) or under (−) the tilted plane at this point
                        float planeY = (-plane.distance - gn.x * w.x - gn.z * w.z) / gn.y;
                        lift = Mathf.Max(lift, gy - planeY); sag = Mathf.Min(sag, gy - planeY);
                    }
                // (at 0.5 m one side's stakes hung in the air) — and not lifted all the way to the highest bump: the
                // floor sinks a little there, the low side hangs less
                if (lift - sag > 0.35f) { nBumpy++; continue; }                         // too bumpy under it
                float doorY = (-plane.distance - gn.x * p.x - gn.z * p.y) / gn.y;
                var tentGo = Put(tent, s.Root, new Vector3(p.x, doorY + sag + (lift - sag) * 0.4f, p.y), 0f);
                tentGo.transform.rotation = rot;
                tentGo.transform.localScale = Vector3.one * TentScale;
                var fireGo = Put(fire, s.Root, new Vector3(fp.x, fy - 0.03f, fp.y), (float)rng.NextDouble() * 360f);
                // no grass growing through the tent's floor or the fire pit
                DetailGrass.AddClearing(td, new Vector2(body.x - tpos.x, body.y - tpos.z), 3.2f * TentScale);
                DetailGrass.AddClearing(td, new Vector2(fp.x - tpos.x, fp.y - tpos.z), 1.8f);
                var gs = t.GetComponent<DetailGrass.GrassState>();
                if (gs) DetailGrass.Apply(t, gs.seed, gs.type, gs.region);

                // what they left: by the fire and in the tent; firewood stacked by the fire
                int n = rng.Next(3, 6), placed = 0;
                for (int i = 0; i < n; i++)
                {
                    int item = Leftovers[rng.Next(Leftovers.Length)];
                    bool inTent = rng.NextDouble() < 0.4;
                    var c = inTent ? p + Random2(rng) * 0.8f : fp + Random2(rng).normalized * Mathf.Lerp(1.3f, 2.5f, (float)rng.NextDouble());
                    Forage.AddLoot(s, new Vector3(c.x, G(c.x, c.y) + 0.4f, c.y), item, (float)rng.NextDouble() * 360f);
                    placed++;
                }
                var side = new Vector2(fwd.y, -fwd.x) * (rng.NextDouble() < 0.5 ? 1f : -1f);
                for (int i = 0, k = rng.Next(1, 4); i < k; i++)
                {
                    var c = fp + side * (1.6f + i * 0.35f);
                    Forage.AddLoot(s, new Vector3(c.x, G(c.x, c.y) + 0.4f, c.y), 71, yaw + 90f + (float)rng.NextDouble() * 10f);
                }
                Camps.Add(s.Root.name);
                if (Wrecks.Placed.ContainsKey(s.Root.name)) Wrecks.Placed[s.Root.name] += $", CAMP ({tent.name.Substring(8)})";
                Plugin.Debug($"Camp on {s.Root.name}: {tent.name} {(ty - sea):0.0} m up, {(p - hull).magnitude:0} m from the wreck, {placed} things left" +
                             (dig ? ", ground levelled" : ""));
                return true;
            }
            Plugin.Log.LogInfo($"Camp on {s.Root.name}: no spot — {nClear} too close to trees/rocks/water/the wreck, {nSlope} too sloped, " +
                               $"{nHeight} too low or high (1–25 m), {nBumpy} too bumpy under the tent; {land.Count} land points");
            return false;
        }

        // set the terrain to `y` (world) within r of c, easing back to the old ground over `blend` metres
        private static void Flatten(Terrain t, Vector2 c, float r, float blend, float y)
        {
            var td = t.terrainData; var tp = t.GetPosition();
            int hr = td.heightmapResolution;
            float cell = td.size.x / (hr - 1);
            int cx = Mathf.RoundToInt((c.x - tp.x) / cell), cz = Mathf.RoundToInt((c.y - tp.z) / cell);
            int rc = Mathf.CeilToInt((r + blend) / cell) + 1;
            int x0 = Mathf.Max(0, cx - rc), z0 = Mathf.Max(0, cz - rc);
            int w = Mathf.Min(hr - 1, cx + rc) - x0 + 1, h = Mathf.Min(hr - 1, cz + rc) - z0 + 1;
            if (w <= 0 || h <= 0) return;
            var hts = td.GetHeights(x0, z0, w, h);
            float target = (y - tp.y) / td.size.y;
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    float d = new Vector2((x0 + x - cx) * cell, (z0 + z - cz) * cell).magnitude;
                    float k = d <= r ? 1f : 1f - Mathf.SmoothStep(0f, 1f, (d - r) / Mathf.Max(blend, 0.1f));
                    if (k > 0f) hts[z, x] = Mathf.Lerp(hts[z, x], target, k);
                }
            td.SetHeights(x0, z0, hts);
        }

        private static Vector2 Random2(System.Random rng) =>
            new Vector2((float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f);

        private static GameObject Put(GameObject prefab, Transform parent, Vector3 world, float yaw)
        {
            var go = Object.Instantiate(prefab, parent);
            go.name = prefab.name.Substring(3);                                  // "camp Tent_Wedge"
            go.transform.position = world;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            IslandSpawner.FixShaders(go.transform);
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!mf.sharedMesh) continue;
                if (mf.GetComponent<Collider>()) continue;                          // (the editor's prefab brings its own)
                // a mesh not readable in the bundle can't be a MeshCollider (Unity logs an error each time): its box
                if (mf.sharedMesh.isReadable) mf.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
                else { var bc = mf.gameObject.AddComponent<BoxCollider>(); bc.center = mf.sharedMesh.bounds.center; bc.size = mf.sharedMesh.bounds.size; }
            }
            return go;
        }
    }
}
