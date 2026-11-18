using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TerraIncognita
{
    internal struct Placement
    {
        public IslandType Type;
        public Vector2 RealPos;   // world XZ, unshifted
        public float Yaw;
        public int Variant;       // picks a prefab variant inside the bundle
        public string Origin;     // "scatter:emerald" / "route:3-17" — for logs
    }

    /// <summary>Deterministic: same seed + same vanilla world => same islands, every launch.</summary>
    internal static class IslandPlacer
    {
        private const int MaxTries = 400;

        public static List<Placement> Place(WorldSurvey w, int seed, ICollection<IslandType> available,
                                            System.Func<Vector2, Climate> climateOf)
        {
            var rng = new System.Random(seed);
            var result = new List<Placement>();
            var occupied = new List<Vector2>(w.Land);
            var placed = new Dictionary<IslandType, int>();
            IslandType Peek(double x, bool route, Vector2 at) => IslandTypes.Pick(x, route, available, Latitude(at), climateOf(at), placed);
            void Commit(IslandType t) => placed[t] = placed.TryGetValue(t, out int k) ? k + 1 : 1;
            float clearance = Plugin.MinClearanceToLand.Value;

            // Out of sight from the ports: the game sinks islands below the horizon by d²/2R (IslandHorizon,
            // R = 515662), and the eye at height e sees 2e further down — a top at H is hidden beyond √(2R(H + 2e)).
            // e = [Generation] HiddenFromHeight (a masthead, 20 m). Measured from every port, so the side of an
            // archipelago where its ports stand at the edge pushes islands further out. H = the type's tallest prefab.
            var heights = IslandSpawner.TypeHeights();
            var allPorts = w.Archipelagos.SelectMany(a => a).ToList();
            float Hide(IslandType t) => Mathf.Sqrt(2f * 515662f * ((heights.TryGetValue(t, out float h) ? h : 60f) + 2f * Plugin.HiddenFromHeight.Value));
            bool Hidden(Vector2 p, float d) => allPorts.All(q => (q - p).sqrMagnitude >= d * d);

            bool Free(Vector2 p) => occupied.All(o => (o - p).sqrMagnitude > clearance * clearance);
            float F(double a, double b) => (float)(a + rng.NextDouble() * (b - a));

            // 1) Scatter inside each archipelago: disk around the region's port centroid.
            for (int ai = 0; ai < w.Archipelagos.Count; ai++)
            {
                var ports = w.Archipelagos[ai];
                var center = new Vector2(ports.Average(p => p.x), ports.Average(p => p.y));
                float radius = ports.Max(p => (p - center).magnitude) * 1.15f;
                string region = $"archipelago{ai}@({center.x:F0},{center.y:F0})";

                for (int n = 0; n < Plugin.ScatterPerRegion.Value; n++)
                {
                    for (int i = 0; i < MaxTries; i++)
                    {
                        // ring on the archipelago's outskirts (uniform by area), reaching out as far as the island must
                        // stand to be under the horizon: the type is chosen first, it sets that distance
                        var p0 = center;
                        var t = Peek(rng.NextDouble(), false, p0);
                        float hide = Mathf.Max(Hide(t), Plugin.ScatterMinDistanceToPort.Value);
                        float rin = radius * Plugin.ScatterRingInner.Value, rout = radius * Plugin.ScatterRingOuter.Value + hide;
                        float ang = F(0, Mathf.PI * 2), r = Mathf.Sqrt(F(rin * rin, rout * rout));
                        var p = center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * r;
                        if (!Free(p) || DistToRoutes(w, p) < Plugin.ScatterRouteClearance.Value) continue;
                        if (!Hidden(p, hide)) continue;
                        if (climateOf(p) != climateOf(p0)) continue;          // stays in its archipelago's region

                        Commit(t);
                        result.Add(Make(rng, p, t, "scatter:" + region));
                        occupied.Add(p);
                        break;
                    }
                }
            }

            // 2) Beside the main sea lanes ([Generation] TradeRoutes): a point along a lane (its ends are near ports and get
            //    rejected by the horizon test anyway), pushed sideways.
            if (w.MainRoutes.Count > 0)
            {
                for (int n = 0; n < Plugin.RouteIslandCount.Value; n++)
                {
                    for (int i = 0; i < MaxTries; i++)
                    {
                        var (a, b) = w.MainRoutes[rng.Next(w.MainRoutes.Count)];
                        var dir = b - a;
                        if (dir.sqrMagnitude < 1f) continue;
                        var normal = new Vector2(-dir.y, dir.x).normalized * (rng.Next(2) == 0 ? -1 : 1);
                        var p = a + dir * F(0.15, 0.85) + normal * F(Plugin.RouteOffsetMin.Value, Plugin.RouteOffsetMax.Value);
                        // must not end up sitting on some *other* route either
                        if (!Free(p) || DistToRoutes(w, p) < Plugin.RouteOffsetMin.Value * 0.8f) continue;
                        var t = Peek(rng.NextDouble(), true, p);
                        if (!Hidden(p, Hide(t))) continue;                    // not in sight of a port either

                        Commit(t);
                        result.Add(Make(rng, p, t, "route"));
                        occupied.Add(p);
                        break;
                    }
                }
            }

            int wanted = w.Archipelagos.Count * Plugin.ScatterPerRegion.Value + (w.MainRoutes.Count > 0 ? Plugin.RouteIslandCount.Value : 0);
            Plugin.Log.LogInfo($"Placed {result.Count} of {wanted} islands (seed {seed}), out of sight from {Plugin.HiddenFromHeight.Value:0} m at every port: " +
                               string.Join(", ", result.Select(r => $"{r.Type} {allPorts.Min(q => (q - r.RealPos).magnitude) / 1000f:0.0}/{Hide(r.Type) / 1000f:0.0} km").ToArray()));
            return result;
        }

        private static Placement Make(System.Random rng, Vector2 p, IslandType t, string origin) => new Placement
        {
            Type = t, RealPos = p, Yaw = (float)(rng.NextDouble() * 360), Variant = rng.Next(1000), Origin = origin
        };

        // globe: 9000 units per degree, z = 0 at 36°N (FloatingOriginManager.globeOffset)
        private static float Latitude(Vector2 real) => real.y / 9000f + 36f;

        private static float DistToRoutes(WorldSurvey w, Vector2 p)
        {
            float best = float.MaxValue;
            foreach (var (a, b) in w.MainRoutes)
            {
                var ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-3f));
                best = Mathf.Min(best, (a + ab * t - p).magnitude);
            }
            return best;
        }
    }
}
