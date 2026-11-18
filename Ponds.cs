using System.Linq;
using UnityEngine;

namespace TerraIncognita
{
    /// <summary>
    /// Small fresh-water bodies carved into our islands at spawn time (no prefab regeneration needed): volcanic
    /// spring, rain ponds on plateaus and saddles, puddles on rocky islets, a brackish pit on an atoll motu.
    /// Each island's own TerrainData copy (made by TerrainRotation) is edited:
    ///   • spot: fairly level, its whole rim out to 1.6 R higher than the water, so the pond can't spill;
    ///   • bowl: deepest in the middle, shore at R, banks back to the ground by 1.5 R (SetHeights → collider too);
    ///   • wet sand painted over bottom and shore, grass planted again without the pond;
    ///   • prop markers inside are removed before props are placed; a "pond" marker gets the round water disc.
    /// Deterministic per island seed. The oasis keeps the pond built into its crater by the generator.
    /// </summary>
    internal static class Ponds
    {
        private const string WetSand = "Sand Cracked Wet";

        public static int Carve(SpawnedIsland s)
        {
            if (!Plugin.PondsEnabled.Value || !s.Root) return 0;
            if (s.Root.GetComponentsInChildren<Transform>(true).Any(t => t.name == "pond")) return 0;   // oasis
            // basinOnly: rugged islands get a stone basin (ring of rocks holding a spring) instead of a sunk pond
            float chance, r0, r1, depth, minH = 2.5f;
            bool basinOnly = false;
            switch (s.Type)
            {
                case IslandType.VolcanicBay:  chance = 0.7f; r0 = 6f; r1 = 9f; depth = 1.2f; break;   // spring
                case IslandType.MesaPlateau:  chance = 0.5f; r0 = 5f; r1 = 8f; depth = 0.9f; break;   // rain pond
                case IslandType.TwinPeaks:    chance = 0.5f; r0 = 5f; r1 = 8f; depth = 0.9f; break;
                case IslandType.CrescentCove: chance = 0.4f; r0 = 4f; r1 = 7f; depth = 0.8f; break;
                case IslandType.SunkenRidge:  chance = 0.3f; r0 = 3f; r1 = 5f; depth = 0.6f; break;
                case IslandType.Atoll:        chance = 0.6f; r0 = 3f; r1 = 5f; depth = 0.6f; minH = 1.6f; break; // brackish
                case IslandType.RockyIslet:   chance = 0.6f; r0 = r1 = depth = 0f; basinOnly = true; break;
                case IslandType.SeaStacks:    chance = 0.5f; r0 = r1 = depth = 0f; basinOnly = true; break;
                default: return 0;
            }
            var rng = new System.Random(s.Seed * 97 + 3);
            if (rng.NextDouble() >= chance * Plugin.PondChanceScale.Value) return 0;

            var t = s.Root.GetComponentInChildren<Terrain>(true);
            if (!t || !t.terrainData) return 0;
            var g = new Grid(s.Root, t);
            Physics.SyncTransforms();
            g.rocks = s.Root.GetComponentsInChildren<MeshCollider>(true).Where(c => c && c.sharedMesh && c.name.StartsWith("rock sculpted ")).ToList();
            // (a pond cut the ground from under a game rock set on a generator marker: keep clear of those too —
            // rock2 ~3 m round × its scale, a cliff ~8 m)
            g.propRocks = s.Root.GetComponentsInChildren<Transform>(true)
                .Where(t2 => { string n = PropLibrary.Strip(t2.name); return n == "rock2" || n == "rock" || n == "cliff"; })
                .Select(t2 => (t2.position, (PropLibrary.Strip(t2.name) == "cliff" ? 8f : 3f) * Mathf.Max(1f, Mathf.Max(t2.lossyScale.x, t2.lossyScale.z))))
                .ToList();

            if (!basinOnly && SunkPond(s, g, rng, r0, r1, depth, minH)) return 1;
            if (s.Type != IslandType.Atoll && StoneBasin(s, g, rng)) return 1;      // atoll: sand only, no rocks
            Plugin.Log.LogInfo($"Pond on {s.Root.name}: no spot found");
            return 0;
        }

        // ---------------------------------------------------------------- volcano crater: a lake or smoke
        // The crater the generator leaves in a volcano's top: its rim = the highest ground (> 72 % of the summit), the
        // rim's centroid its centre, the lowest point within 60 % of the rim's radius its floor (a bowl only if the
        // floor lies ≥ 5 m under the rim). Half the craters ([Ponds] off: none) hold a lake — filled to 30–55 % of the
        // way up to the lowest pass in the rim (≤ 8 m deep), shore found by walking out from the floor on 64
        // headings, with the ponds' wet shore, reeds and a fresh-water source; the others breathe a thin smoke.
        public static string Crater(SpawnedIsland s)
        {
            if (s.Type != IslandType.VolcanicBay || !s.Root) return null;
            var t = s.Root.GetComponentInChildren<Terrain>(true);
            if (!t || !t.terrainData) return null;
            var g = new Grid(s.Root, t);
            var rng = new System.Random(s.Seed * 61 + 11);
            float sx = 0f, sz = 0f; int n = 0;
            for (int z = 0; z < g.res; z += 2) for (int x = 0; x < g.res; x += 2)
                if (g.M(z, x) > g.maxH * 0.72f) { sx += x; sz += z; n++; }
            if (n < 12) return null;
            float ccx = sx / n, ccz = sz / n, rimC = 0f, rimH = 0f;
            for (int z = 0; z < g.res; z += 2) for (int x = 0; x < g.res; x += 2)
                if (g.M(z, x) > g.maxH * 0.72f) { rimC += Mathf.Sqrt((x - ccx) * (x - ccx) + (z - ccz) * (z - ccz)); rimH += g.M(z, x); }
            rimC /= n; rimH /= n;                                                     // (rim radius in cells)
            int fx = Mathf.RoundToInt(ccx), fz = Mathf.RoundToInt(ccz); float floorH = float.MaxValue;
            int rr = Mathf.CeilToInt(rimC * 0.6f);
            for (int dz = -rr; dz <= rr; dz++) for (int dx = -rr; dx <= rr; dx++)
            {
                int x = Mathf.RoundToInt(ccx) + dx, z = Mathf.RoundToInt(ccz) + dz;
                if (!g.In(x, z) || dx * dx + dz * dz > rr * rr) continue;
                float m = g.M(z, x); if (m < floorH) { floorH = m; fx = x; fz = z; }
            }
            if (rimC * g.cell < 8f || floorH > rimH - 5f) return null;
            // the lowest pass: on each heading, the highest ground out to 1.4 × the rim; the lowest of those
            float pass = float.MaxValue;
            for (int k = 0; k < 48; k++)
            {
                float a = k * Mathf.PI * 2f / 48f, top = float.MinValue;
                for (float d = 0f; d <= rimC * 1.4f; d += 1f)
                {
                    int x = fx + Mathf.RoundToInt(Mathf.Cos(a) * d), z = fz + Mathf.RoundToInt(Mathf.Sin(a) * d);
                    if (g.In(x, z)) top = Mathf.Max(top, g.M(z, x));
                }
                pass = Mathf.Min(pass, top);
            }
            var floorWorld = s.Root.TransformPoint(new Vector3(s.Root.InverseTransformPoint(g.World(fx, fz)).x, floorH, s.Root.InverseTransformPoint(g.World(fx, fz)).z));
            // the editor's choice (Rock sculptor: "volcano crater lake|smoke|none r…" — the boulders and the bare floor
            // are made there); an island without one: a lake or smoke at random, the floor bared here
            var marker = s.Root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t2 => t2.name.StartsWith("volcano crater"));
            string chosen = marker ? marker.name.Split(' ').Skip(2).FirstOrDefault() : null;
            if (chosen == "none") { Plugin.Log.LogInfo($"Crater on {s.Root.name}: left as it is (editor)"); return "none"; }
            bool lake = Plugin.PondsEnabled.Value && (chosen != null ? chosen == "lake" : rng.NextDouble() < 0.5);
            float level = floorH + Mathf.Min((pass - floorH) * Mathf.Lerp(0.3f, 0.55f, (float)rng.NextDouble()), 8f);
            if (lake && level - floorH >= 1f)
            {
                var shore = new float[64];
                for (int k = 0; k < 64; k++)
                {
                    float a = k * Mathf.PI * 2f / 64f, d = 0f;
                    for (; d <= rimC * 1.5f; d += 0.5f)
                    {
                        int x = fx + Mathf.RoundToInt(Mathf.Cos(a) * d), z = fz + Mathf.RoundToInt(Mathf.Sin(a) * d);
                        if (!g.In(x, z) || g.M(z, x) > level) break;
                    }
                    shore[k] = Mathf.Max(1f, d * g.cell);
                }
                float Shore(float a)
                {
                    float f = Mathf.Repeat(a, Mathf.PI * 2f) / (Mathf.PI * 2f) * 64f;
                    int i0 = Mathf.FloorToInt(f) % 64, i1 = (i0 + 1) % 64;
                    return Mathf.Lerp(shore[i0], shore[i1], f - Mathf.Floor(f));
                }
                float meanR = shore.Average(), maxR = shore.Max();
                Finish(s, g, fx, fz, meanR, meanR * 1.1f, level, a => Shore(a) * 1.05f,
                       capacity: 400, drop: 1f, reedShore: Shore, maxShore: maxR);
                Plugin.Log.LogInfo($"Crater lake on {s.Root.name}: {2f * meanR:F0} m across, {level - floorH:F1} m deep, {level:F0} m above sea");
                return "lake";
            }
            CraterSmoke(s.Root, floorWorld, rimC * g.cell);
            // a smoking crater is bare: its grass turned to rock, small boulders strewn over the floor (thicker towards
            // the middle; "stone" markers — PropLibrary puts the region's rocks there, sat into the ground)
            float craterR = rimC * g.cell;
            if (chosen == null)                                                         // (the editor did it where it chose)
            {
                BareCrater(g, g.World(fx, fz), craterR);
                AddStones(s, g, fx, fz, rng, 14 + rng.Next(9), a => 0f, 0f, craterR * 0.7f, 0.5f, 1.3f, randomAngles: true);
                var gsC = g.t.GetComponent<DetailGrass.GrassState>();
                if (gsC) DetailGrass.Apply(g.t, gsC.seed, gsC.type, gsC.region);     // no grass blades left on the rock
            }
            Plugin.Log.LogInfo($"Crater smoke on {s.Root.name}: crater {2f * rimC * g.cell:F0} m across, floor {floorH:F0} m above sea, " +
                               $"lowest pass {pass - floorH:F1} m over it — " + (!lake ? "rolled smoke" : $"rolled a lake, too shallow ({level - floorH:F1} m)"));
            return "smoke";
        }

        // a thin column of smoke from the crater floor: big soft puffs rising slowly, drifting, fading out (the game's
        // lit cloud particle material if it can be found, else plain unlit particles); simulated in the island's
        // space so it moves with the floating origin
        private static void CraterSmoke(Transform root, Vector3 at, float craterR)
        {
            var go = new GameObject("crater smoke");
            go.transform.SetParent(root, false);
            go.transform.position = at + Vector3.up;
            go.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);                    // the cone's axis up
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true; main.duration = 10f; main.prewarm = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(18f, 28f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.2f, 2.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(Mathf.Clamp(craterR * 0.25f, 5f, 12f), Mathf.Clamp(craterR * 0.5f, 9f, 22f));
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new Color(0.86f, 0.86f, 0.84f, 0.35f);
            main.maxParticles = 80;
            main.simulationSpace = ParticleSystemSimulationSpace.Custom;
            main.customSimulationSpace = root;
            var em = ps.emission; em.rateOverTime = 1.6f;
            var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 8f; sh.radius = Mathf.Clamp(craterR * 0.15f, 2f, 6f);
            var vel = ps.velocityOverLifetime; vel.enabled = true; vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(0.4f, 1.0f); vel.y = new ParticleSystem.MinMaxCurve(0.2f, 0.5f); vel.z = new ParticleSystem.MinMaxCurve(-0.3f, 0.3f);
            var col = ps.colorOverLifetime; col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                         new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0.6f, 0.6f), new GradientAlphaKey(0f, 1f) });
            col.color = grad;
            var size = ps.sizeOverLifetime; size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 1.8f));
            var r = go.GetComponent<ParticleSystemRenderer>();
            var cloud = Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(m => m && m.name.StartsWith("Cloud Particles Lit"));
            if (cloud) r.sharedMaterial = cloud;
            else
            {
                var sh2 = Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended");
                if (sh2) r.sharedMaterial = new Material(sh2) { mainTexture = SoftDot() };
            }
            r.renderMode = ParticleSystemRenderMode.Billboard;
            ps.Play();
            Plugin.Debug($"Crater smoke material: {(r.sharedMaterial ? r.sharedMaterial.name + " [" + r.sharedMaterial.shader.name + "]" : "none")}");
        }

        private static Texture2D softDot;
        private static Texture2D SoftDot()
        {
            if (softDot) return softDot;
            const int N = 64;
            softDot = new Texture2D(N, N, TextureFormat.RGBA32, false) { name = "crater smoke puff", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            {
                float d = new Vector2(x - N / 2f + 0.5f, y - N / 2f + 0.5f).magnitude / (N / 2f);
                byte a = (byte)(Mathf.Clamp01(1f - d) * Mathf.Clamp01(1f - d) * 255f);
                px[y * N + x] = new Color32(255, 255, 255, a);
            }
            softDot.SetPixels32(px); softDot.Apply();
            return softDot;
        }

        /// <summary>Heightmap of one island in metres above the sea, editable.</summary>
        private class Grid
        {
            public readonly Terrain t; public readonly TerrainData td; public readonly float[,] h;
            public readonly int res; public readonly float cell, baseY, maxH;
            public Grid(Transform root, Terrain terrain)
            {
                t = terrain; td = t.terrainData; res = td.heightmapResolution;
                h = td.GetHeights(0, 0, res, res);
                cell = td.size.x / (res - 1);
                baseY = root.InverseTransformPoint(t.GetPosition()).y;      // terrain sits at -seaDepth under the root
                for (int z = 0; z < res; z += 4) for (int x = 0; x < res; x += 4) maxH = Mathf.Max(maxH, M(z, x));
            }
            public float M(int z, int x) => h[z, x] * td.size.y + baseY;

            // our sculpted rocks on the island (their colliders): a pond's cut must keep clear of them — it lowered the
            // ground a rock's buried part stood in (the mesh had been trimmed under the ground in the editor: a hole)
            public System.Collections.Generic.List<MeshCollider> rocks;
            /// <summary>Does a sculpted rock come within `within` metres under the ground anywhere in this disc?</summary>
            // the generator's rock / cliff markers (the game's rocks go there later): position and how far they reach
            public System.Collections.Generic.List<(Vector3 p, float r)> propRocks;
            public bool NearRock(int cx, int cz, float radius, float within)
            {
                var c = World(cx, cz);
                if (rocks == null || rocks.Count == 0) return false;
                // a 1.5 m grid over the whole disc (19 points on two rings missed a hoodoo's thin neck)
                var near = rocks.Where(rc => { var b = rc.bounds; return b.max.x > c.x - radius && b.min.x < c.x + radius && b.max.z > c.z - radius && b.min.z < c.z + radius; }).ToList();
                if (near.Count == 0) return false;
                for (float dz = -radius; dz <= radius; dz += 1.5f)
                    for (float dx = -radius; dx <= radius; dx += 1.5f)
                    {
                        if (dx * dx + dz * dz > radius * radius) continue;
                        var p = new Vector3(c.x + dx, 0f, c.z + dz);
                        float gy = t.SampleHeight(p) + t.GetPosition().y;
                        foreach (var rc in near)
                        {
                            var b = rc.bounds;
                            if (p.x < b.min.x || p.x > b.max.x || p.z < b.min.z || p.z > b.max.z) continue;
                            if (rc.Raycast(new Ray(new Vector3(p.x, b.max.y + 5f, p.z), Vector3.down), out var hit, b.size.y + 10f) && hit.point.y > gy - within) return true;
                        }
                    }
                return false;
            }
            public void Set(int z, int x, float m) => h[z, x] = (m - baseY) / td.size.y;
            public bool In(int x, int z) => x >= 0 && z >= 0 && x < res && z < res;
            public Vector3 World(int x, int z) => t.GetPosition() + new Vector3(x * cell, 0f, z * cell);

            /// <summary>Two 3×3 box passes over the annulus rIn..rOut (metres) — rounds off the edge of a cut.</summary>
            public void SmoothRing(int cx, int cz, float rIn, float rOut)
            {
                int rc = Mathf.CeilToInt(rOut / cell) + 1;
                for (int pass = 0; pass < 2; pass++)
                {
                    var src = (float[,])h.Clone();
                    for (int dz = -rc; dz <= rc; dz++)
                        for (int dx = -rc; dx <= rc; dx++)
                        {
                            int x = cx + dx, z = cz + dz;
                            if (x < 1 || z < 1 || x >= res - 1 || z >= res - 1) continue;
                            float d = Mathf.Sqrt(dx * dx + dz * dz) * cell;
                            if (d < rIn || d > rOut) continue;
                            float sum = 0f;
                            for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++) sum += src[z + j, x + i];
                            h[z, x] = sum / 9f;
                        }
                }
            }

            /// <summary>The ground's overall slope (m per m) round a cell: a plane fitted to 16 headings on rings at r and
            /// 1.5 r. A bowl or a level shelf comes out ~0 whatever its rim; a hillside comes out as its slope.</summary>
            public float Tilt(int cx, int cz, float r)
            {
                float gx = 0f, gz = 0f; int rings = 0;
                foreach (float rr in new[] { r, r * 1.5f })
                {
                    float sx = 0f, sz = 0f, mean = 0f; var hs = new float[16];
                    bool ok = true;
                    for (int k = 0; k < 16; k++)
                    {
                        float a = k * Mathf.PI / 8f;
                        int px = cx + Mathf.RoundToInt(Mathf.Cos(a) * rr / cell), pz = cz + Mathf.RoundToInt(Mathf.Sin(a) * rr / cell);
                        if (!In(px, pz)) { ok = false; break; }
                        hs[k] = M(pz, px); mean += hs[k];
                    }
                    if (!ok) continue;
                    mean /= 16f;
                    for (int k = 0; k < 16; k++) { float a = k * Mathf.PI / 8f; sx += (hs[k] - mean) * Mathf.Cos(a); sz += (hs[k] - mean) * Mathf.Sin(a); }
                    gx += sx * 2f / (16f * rr); gz += sz * 2f / (16f * rr); rings++;
                }
                return rings == 0 ? 99f : new Vector2(gx, gz).magnitude / rings;
            }

            /// <summary>Lowest/highest ground on rings from 0.5 to 1.6 × r around a cell; false if off the terrain.</summary>
            public bool Rim(int cx, int cz, float r, out float lo, out float hi)
            {
                lo = float.MaxValue; hi = float.MinValue;
                for (int k = 0; k < 16; k++)
                {
                    float a = k * Mathf.PI / 8f;
                    for (float f = 0.5f; f <= 1.61f; f += 0.37f)
                    {
                        int sx = cx + Mathf.RoundToInt(Mathf.Cos(a) * r * f / cell), sz = cz + Mathf.RoundToInt(Mathf.Sin(a) * r * f / cell);
                        if (!In(sx, sz)) return false;
                        float m = M(sz, sx);
                        lo = Mathf.Min(lo, m); hi = Mathf.Max(hi, m);
                    }
                }
                return true;
            }
        }

        // ---------------------------------------------------------------- sunk pond (level ground)
        // Outline: radius R × shape(angle), shape = 1 + three low harmonics with random phases (≈ 0.75–1.3): a smooth,
        // irregular shore instead of a circle. The bowl, banks, water surface and shore stones all follow it.
        private static bool SunkPond(SpawnedIsland s, Grid g, System.Random rng, float r0, float r1, float depth, float minH)
        {
            // most of the square is sea: draw many cells, test only those on land. No upper height limit:
            // "≤ 60 % of the summit" ruled out the flat tops (mesa plateau, saddles) — the best pond spots.
            int landTries = 0;
            for (int attempt = 0; attempt < 6000 && landTries < 700; attempt++)
            {
                int cx = rng.Next(g.res), cz = rng.Next(g.res);
                float h0 = g.M(cz, cx);
                if (h0 < minH || h0 > g.maxH * 0.97f) continue;
                landTries++;
                float R = Mathf.Lerp(r0, r1, (float)rng.NextDouble());
                // The ground's TILT over the area (a plane fitted to two rings round it), not the spread of heights: a
                // hollow has a high rim all round but no tilt — just where a pond belongs. Nearly level first (≲ 12°),
                // then ≲ 22° (on up to 25° of rim spread the cut left a terrace-like notch in the hillside; with the
                // spread capped at 8° nearly no ponds were left). Cliffs and crests (spread > 1.5 R) never.
                float tilt = landTries < 500 ? 0.21f : 0.40f;
                if (!g.Rim(cx, cz, R * 1.3f, out float lo, out float hi) || hi - lo > R * 1.5f) continue;
                if (g.Tilt(cx, cz, R * 1.3f) > tilt) continue;
                float level = Mathf.Min(lo, h0) - 0.35f;
                if (level < 1.2f) continue;                                  // clear of the sea and the surf
                // our sculpted rocks only where the ground really goes down (the banks ease out past 1.5 × the shore);
                // the game's trees and rocks there are taken away (Finish → RemoveMarkers) or set on the new ground
                if (g.NearRock(cx, cz, R * 1.3f * 1.5f + 1f, depth + 1f)) continue;

                float p1 = (float)rng.NextDouble() * 6.283f, p2 = (float)rng.NextDouble() * 6.283f, p3 = (float)rng.NextDouble() * 6.283f;
                System.Func<float, float> shape = a => 1f + 0.13f * Mathf.Sin(a + p1) + 0.09f * Mathf.Sin(2f * a + p2) + 0.05f * Mathf.Sin(3f * a + p3);

                int rc = Mathf.CeilToInt(R * 1.3f * 2f / g.cell);             // banks back to the ground over a full R
                // how much of the bank the cut would take away (above the water, outside it): more than ~1 m somewhere
                // and it leaves a bare scar in the slope — a dug hole, not a pond. Such spots are passed over.
                float scar = 0f;
                for (int dz = -rc; dz <= rc; dz += 2)
                    for (int dx = -rc; dx <= rc; dx += 2)
                    {
                        int x = cx + dx, z = cz + dz;
                        if (!g.In(x, z)) continue;
                        float d = Mathf.Sqrt(dx * dx + dz * dz) * g.cell / (R * shape(Mathf.Atan2(dz, dx)));
                        if (d <= 1.2f || d > 2f) continue;                          // (the shore's own lip doesn't count)
                        float m = g.M(z, x);
                        float target = Mathf.Lerp(level, m, 1f - (2f - d) * (2f - d));
                        scar = Mathf.Max(scar, m - target);
                    }
                if (scar > (landTries < 500 ? 1.3f : 2.0f)) continue;           // (1.0 counted the lip: nearly no ponds were left)
                for (int dz = -rc; dz <= rc; dz++)
                    for (int dx = -rc; dx <= rc; dx++)
                    {
                        int x = cx + dx, z = cz + dz;
                        if (!g.In(x, z)) continue;
                        float d = Mathf.Sqrt(dx * dx + dz * dz) * g.cell / (R * shape(Mathf.Atan2(dz, dx)));   // 1 = shore
                        if (d > 2f) continue;
                        float m = g.M(z, x);
                        // the ground must cross the water level at an angle: a profile flat at the shore (smoothstep)
                        // let tiny terrain wiggles poke through the surface → ragged, stepped edge
                        float target = d <= 1f ? level - depth * (1f - d * d)
                                               : Mathf.Lerp(level, m, 1f - (2f - d) * (2f - d));
                        if (target < m) g.Set(z, x, target);
                    }
                g.SmoothRing(cx, cz, R * 1.3f * 1.15f, R * 1.3f * 2.1f);       // soften the outer edge of the cut
                Finish(s, g, cx, cz, R, R * 2.6f + 2f, level, a => R * shape(a) * 1.1f,
                       capacity: rng.Next(90, 121), drop: depth * 0.9f, reedShore: a => R * shape(a), maxShore: R * 1.3f);
                // 3–6 stones on the shore, so it reads as a pond and not a hole
                AddStones(s, g, cx, cz, rng, rng.Next(3, 7), a => R * shape(a), 0.3f, 1.0f, 0.9f, 1.5f, randomAngles: true);
                Plugin.Log.LogInfo($"Pond on {s.Root.name}: R {R:F1} m, water {level:F1} m above sea, depth {depth:F1} m");
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- stone basin (rugged ground)
        // A spring held by a ring of stones: a small pad cut into the slope (≤ 35°), 8–11 stones (the rock pool, scaled
        // to ~2.2 m) around it, sunk by a third, water filled up to the stones (its edge runs under them).
        private static bool StoneBasin(SpawnedIsland s, Grid g, System.Random rng)
        {
            int landTries = 0;
            for (int attempt = 0; attempt < 6000 && landTries < 700; attempt++)
            {
                int cx = rng.Next(g.res), cz = rng.Next(g.res);
                float h0 = g.M(cz, cx);
                if (h0 < 2.0f || h0 > g.maxH * 0.97f) continue;
                landTries++;
                float Rb = Mathf.Lerp(1.25f, 1.75f, (float)rng.NextDouble());   // bowl radius
                float ringR = Rb + 0.9f;                                         // stone centres
                if (!g.Rim(cx, cz, ringR, out float lo, out float hi)) continue;
                if (g.NearRock(cx, cz, ringR + 5.5f, 1.5f)) continue;
                if (hi - lo > 2f * ringR * 1.6f * 0.7f) continue;                // ≲ 35° across the basin
                float pad = lo;                                                  // cut down to the lowest rim point
                float level = pad - 0.05f;                                       // up to the rim of the hollow, not above it
                if (pad < 1.5f) continue;

                int rc = Mathf.CeilToInt((ringR + 3f) / g.cell);
                for (int dz = -rc; dz <= rc; dz++)
                    for (int dx = -rc; dx <= rc; dx++)
                    {
                        int x = cx + dx, z = cz + dz;
                        if (!g.In(x, z)) continue;
                        float d = Mathf.Sqrt(dx * dx + dz * dz) * g.cell;
                        if (d > ringR + 3f) continue;
                        float m = g.M(z, x);
                        float target = d <= Rb ? pad - 0.5f * (1f - (d / Rb) * (d / Rb))   // shallow bowl, crossing the pad at an angle
                                               : Mathf.Lerp(pad, m, Smooth(ringR, ringR + 3f, d)); // pad under the stones, easing out
                        if (target < m) g.Set(z, x, target);
                    }
                g.SmoothRing(cx, cz, ringR + 0.5f, ringR + 3.5f);
                Finish(s, g, cx, cz, Rb, ringR + 3f, level, a => Rb,
                       capacity: rng.Next(30, 61), drop: 0.4f);
                int n = Mathf.RoundToInt(2f * Mathf.PI * ringR / 1.5f);
                AddStones(s, g, cx, cz, rng, n, a => ringR, 0f, 0f, 0.85f, 1.2f, randomAngles: false);
                Plugin.Log.LogInfo($"Stone basin on {s.Root.name}: water {2f * ringR:F1} m across, {level:F1} m above sea, {n} stones");
                return true;
            }
            return false;
        }

        private const float StoneSize = 2.2f, RockRefSize = 8.4f;   // pool rocks are normalised to 8.4 m

        /// <summary>"stone" markers around a shore (PropLibrary puts pool rocks there, sat into the ground).</summary>
        private static void AddStones(SpawnedIsland s, Grid g, int cx, int cz, System.Random rng, int n,
                                      System.Func<float, float> shore, float off0, float off1, float s0, float s1, bool randomAngles)
        {
            var centre = s.Root.InverseTransformPoint(g.World(cx, cz));
            float phase = (float)rng.NextDouble() * Mathf.PI * 2f;
            for (int i = 0; i < n; i++)
            {
                float a = randomAngles ? (float)rng.NextDouble() * Mathf.PI * 2f
                                       : phase + i * Mathf.PI * 2f / n + ((float)rng.NextDouble() - 0.5f) * 0.15f;
                // world angle → root-local direction (the island root may be turned by 90° steps)
                var dir = s.Root.InverseTransformDirection(new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)));
                float r = shore(a) + Mathf.Lerp(off0, off1, (float)rng.NextDouble());
                var st = new GameObject("stone").transform;
                st.SetParent(s.Root, false);
                st.localPosition = new Vector3(centre.x + dir.x * r, 0f, centre.z + dir.z * r);
                st.localRotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
                st.localScale = Vector3.one * (StoneSize / RockRefSize) * Mathf.Lerp(s0, s1, (float)rng.NextDouble());
            }
        }

        /// <summary>Write the heights, paint the shore, clear props, put the water in, plant the grass again.</summary>
        private static void Finish(SpawnedIsland s, Grid g, int cx, int cz, float R, float clearR, float level,
                                   System.Func<float, float> waterRadius, float capacity, float drop,
                                   System.Func<float, float> reedShore = null, float maxShore = 0f)
        {
            g.td.SetHeights(0, 0, g.h);
            var centre = g.World(cx, cz);
            PaintShore(g.td, g.t.GetPosition(), centre, R);
            RemoveMarkers(s.Root, centre, clearR);

            // the water surface (world angles = heightmap angles: Terrain is never rotated)
            var parent = s.Root.Find("detail_props");                     // DetailStreamer hides it far away
            if (!parent) parent = s.Root;
            var local = s.Root.InverseTransformPoint(centre);
            var water = PondWater.Create(parent, s.Root.TransformPoint(new Vector3(local.x, level, local.z)), waterRadius);
            // id: island + pond position on it — stable across launches (same seed, same bundle)
            PondWater.MakeSource(water, $"{s.Root.name}@{local.x:F0},{local.z:F0}", capacity, drop);

            if (reedShore != null)                                      // reeds around natural ponds (not basins)
            {
                GreenRing(g.t, centre, a => R * 1.05f, R * 1.05f);       // and grass round them, right from the wet edge
                var tp = g.t.GetPosition();
                DetailGrass.AddShore(g.td, new Vector2(centre.x - tp.x, centre.z - tp.z), reedShore, maxShore);
            }
            var gs = g.t.GetComponent<DetailGrass.GrassState>();
            if (gs) DetailGrass.Apply(g.t, gs.seed, gs.type, gs.region);    // no grass growing in the water
        }

        /// <summary>Paint a ring of grass around a pond (shore radius by angle), fading out by 2.8 × the radius.</summary>
        // outer: how far it reaches, in shore radii (an oasis: 6 — the whole crater floor, in patches past 2 radii;
        // at 2.8 its small pond left the floor all sand)
        internal static void GreenRing(Terrain t, Vector3 centre, System.Func<float, float> shore, float maxR, float outer = 2.8f)
        {
            var td = t.terrainData;
            var layers = td.terrainLayers;
            int grass = layers == null ? -1 : System.Array.FindIndex(layers, l => l && l.name == "Grass Leaves");
            if (grass < 0) return;
            var tp = t.GetPosition();
            int ar = td.alphamapResolution;
            float acell = td.size.x / (ar - 1);
            int cx = Mathf.RoundToInt((centre.x - tp.x) / acell), cz = Mathf.RoundToInt((centre.z - tp.z) / acell);
            int rc = Mathf.CeilToInt(maxR * (outer + 0.1f) / acell) + 1;
            int x0 = Mathf.Max(0, cx - rc), z0 = Mathf.Max(0, cz - rc);
            int w = Mathf.Min(ar - 1, cx + rc) - x0 + 1, hgt = Mathf.Min(ar - 1, cz + rc) - z0 + 1;
            if (w <= 0 || hgt <= 0) return;
            var a = td.GetAlphamaps(x0, z0, w, hgt);
            int n = a.GetLength(2);
            for (int z = 0; z < hgt; z++)
                for (int x = 0; x < w; x++)
                {
                    int dx = x0 + x - cx, dz = z0 + z - cz;
                    float d = Mathf.Sqrt(dx * dx + dz * dz) * acell / Mathf.Max(0.1f, shore(Mathf.Atan2(dz, dx)));   // 1 = shore
                    float k = Smooth(0.98f, 1.06f, d) * Smooth(outer, outer * 0.72f, d);   // right from the shore, wide (lush)
                    k *= 0.6f + 0.4f * Mathf.PerlinNoise(x * 0.3f, z * 0.3f);    // patchy edge
                    // further out (past 2 radii) it breaks into clumps: grass where the moisture lingers
                    if (d > 2f) k *= Mathf.Clamp01(Mathf.PerlinNoise(x * 0.07f + 17f, z * 0.07f + 3f) * 2.4f - 0.7f + (2.6f - d) * 0.3f);
                    if (k <= 0f) continue;
                    for (int l = 0; l < n; l++) a[z, x, l] *= 1f - k;
                    a[z, x, grass] += k;
                }
            td.SetAlphamaps(x0, z0, a);
        }

        // a smoking crater's floor and inner walls: the grass weight moved to the rock layer (sand and the rest left
        // as they are), fully within 0.75 R of the floor's middle, fading out by 0.95 R (R = the rim's radius)
        private static void BareCrater(Grid g, Vector3 centre, float R)
        {
            var td = g.td; var layers = td.terrainLayers;
            int rock = layers == null ? -1 : System.Array.FindIndex(layers, l => l && l.name.StartsWith("Rock"));
            int grass = layers == null ? -1 : System.Array.FindIndex(layers, l => l && l.name == "Grass Leaves");
            if (rock < 0 || grass < 0) return;
            var tp = g.t.GetPosition();
            int ar = td.alphamapResolution;
            float acell = td.size.x / (ar - 1);
            int cx = Mathf.RoundToInt((centre.x - tp.x) / acell), cz = Mathf.RoundToInt((centre.z - tp.z) / acell);
            int rc = Mathf.CeilToInt(R / acell) + 1;
            int x0 = Mathf.Max(0, cx - rc), z0 = Mathf.Max(0, cz - rc);
            int w = Mathf.Min(ar - 1, cx + rc) - x0 + 1, hgt = Mathf.Min(ar - 1, cz + rc) - z0 + 1;
            if (w <= 0 || hgt <= 0) return;
            var a = td.GetAlphamaps(x0, z0, w, hgt);
            for (int z = 0; z < hgt; z++)
                for (int x = 0; x < w; x++)
                {
                    float d = new Vector2(x0 + x - cx, z0 + z - cz).magnitude * acell;
                    float k = Smooth(R * 0.95f, R * 0.75f, d);
                    if (k <= 0f) continue;
                    float moved = a[z, x, grass] * k;
                    a[z, x, grass] -= moved; a[z, x, rock] += moved;
                }
            td.SetAlphamaps(x0, z0, a);
        }

        // wet sand on the bottom and a thin edge of shore, fading back to what was there by 1.12 R (was 1.3 R: a broad
        // sand ring round every pond — a pond is where things grow)
        private static void PaintShore(TerrainData td, Vector3 terrainPos, Vector3 centre, float R)
        {
            var layers = td.terrainLayers;
            int wet = layers == null ? -1 : System.Array.FindIndex(layers, l => l && l.name == WetSand);
            if (wet < 0) return;
            int ar = td.alphamapResolution;
            float acell = td.size.x / (ar - 1);
            int cx = Mathf.RoundToInt((centre.x - terrainPos.x) / acell), cz = Mathf.RoundToInt((centre.z - terrainPos.z) / acell);
            int rc = Mathf.CeilToInt(R * 1.3f / acell) + 1;
            int x0 = Mathf.Max(0, cx - rc), z0 = Mathf.Max(0, cz - rc);
            int w = Mathf.Min(ar - 1, cx + rc) - x0 + 1, hgt = Mathf.Min(ar - 1, cz + rc) - z0 + 1;
            if (w <= 0 || hgt <= 0) return;
            var a = td.GetAlphamaps(x0, z0, w, hgt);
            int n = a.GetLength(2);
            for (int z = 0; z < hgt; z++)
                for (int x = 0; x < w; x++)
                {
                    float d = new Vector2(x0 + x - cx, z0 + z - cz).magnitude * acell;
                    float k = Smooth(R * 1.12f, R * 1.0f, d);               // 1 inside → 0 outside: a thin wet edge, then grass
                    if (k <= 0f) continue;
                    for (int l = 0; l < n; l++) a[z, x, l] *= 1f - k;
                    a[z, x, wet] += k;
                }
            td.SetAlphamaps(x0, z0, a);
        }

        private static readonly string[] MarkerNames = { "tree", "bush", "rock", "cliff", "rock2" };

        private static void RemoveMarkers(Transform root, Vector3 centre, float radius)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true).ToList())
            {
                if (!t || t == root) continue;
                string n = PropLibrary.Strip(t.name);
                if (!(MarkerNames.Contains(n) || n.StartsWith("palm_tree_"))) continue;
                var d = t.position - centre;
                // a game rock / cliff goes when any of it reaches the pond, not only its centre (a stone basin sat
                // wedged against a big rock): rock ~3 m round × its scale, a cliff ~8 m
                float reach = n == "cliff" ? 8f : n == "rock" || n == "rock2" ? 3f : 0f;
                reach *= Mathf.Max(1f, Mathf.Max(t.lossyScale.x, t.lossyScale.z));
                if (new Vector2(d.x, d.z).magnitude < radius + reach) Object.DestroyImmediate(t.gameObject);
            }
        }

        private static float Smooth(float e0, float e1, float x)
        {
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3f - 2f * t);
        }
    }
}
