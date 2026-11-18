using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TerraIncognita
{
    /// <summary>
    /// Snapshot of the vanilla world in REAL coordinates (independent of floating-origin shifts).
    /// real = shifted - FloatingOriginManager.outCurrentOffset
    /// </summary>
    internal class WorldSurvey
    {
        public readonly List<Vector2> Land = new List<Vector2>();          // every known island / port, XZ
        public readonly List<(Vector2 a, Vector2 b)> Routes = new List<(Vector2, Vector2)>();
        /// <summary>The sea lanes from [Generation] TradeRoutes — what route islands follow and scattered ones avoid.</summary>
        public readonly List<(Vector2 a, Vector2 b)> MainRoutes = new List<(Vector2, Vector2)>();
        /// <summary>Port clusters (single-linkage). Region enum is unreliable: Chronos is "medi" 190 km away.</summary>
        public readonly List<List<Vector2>> Archipelagos = new List<List<Vector2>>();
        private readonly List<Vector2> portPositions = new List<Vector2>();

        // From the F10 dump: archipelago ports are 2–6 km apart, regions are ~90 km apart.
        private const float ClusterLink = 8000f;
        private const int MinPortsPerArchipelago = 3;
        private const int TestPortIndex = 7; // "Test Port", sits inside Neverdin

        public static WorldSurvey Take(FloatingOriginManager origin)
        {
            var s = new WorldSurvey();
            Vector2 Real(Vector3 shifted)
            {
                var r = origin.ShiftingPosToRealPos(shifted);
                return new Vector2(r.x, r.z);
            }

            foreach (var t in Refs.islands)
                if (t) s.Land.Add(Real(t.position));

            if (IslandDistanceTracker.instance)
                foreach (var h in IslandDistanceTracker.instance.islands)
                    if (h) s.Land.Add(Real(h.GetPosition()));

            var seen = new HashSet<(int, int)>();
            foreach (var port in Port.ports.Where(p => p && p.portIndex != TestPortIndex))
            {
                var pos = Real(port.transform.position);
                s.Land.Add(pos);
                s.portPositions.Add(pos);

                // "Trade routes" = the port -> destination graph that missions are generated from.
                var dests = port.GetDestinationPorts();
                if (dests == null) continue;
                foreach (var d in dests.Where(d => d))
                {
                    var key = port.portIndex < d.portIndex ? (port.portIndex, d.portIndex) : (d.portIndex, port.portIndex);
                    if (seen.Add(key))
                        s.Routes.Add((pos, Real(d.transform.position)));
                }
            }

            var byName = new Dictionary<string, Vector2>();
            foreach (var p in Port.ports.Where(p => p && p.portIndex != TestPortIndex))
            {
                var k = Key(p.GetPortName());
                if (!byName.ContainsKey(k)) byName[k] = Real(p.transform.position);
            }
            s.PickMainRoutes(byName);
            s.Cluster();
            Plugin.Log.LogInfo($"Survey: {s.Land.Count} land points, {s.Routes.Count} routes, {s.Archipelagos.Count} archipelagos " +
                               $"({string.Join(", ", s.Archipelagos.Select(a => a.Count + " ports").ToArray())})");
            return s;
        }

        private static string Key(string name) => new string((name ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        /// <summary>[Generation] TradeRoutes "A - B; C - D" by port name; empty / nothing matched = all mission lines.</summary>
        private void PickMainRoutes(Dictionary<string, Vector2> byName)
        {
            var missing = new List<string>();
            foreach (var pair in (Plugin.TradeRoutes.Value ?? "").Split(';', '\n'))
            {
                var ends = pair.Split(new[] { " - ", "-", "—", "–" }, System.StringSplitOptions.RemoveEmptyEntries)
                               .Select(e => e.Trim()).Where(e => e.Length > 0).ToArray();
                if (ends.Length != 2) { if (pair.Trim().Length > 0) missing.Add(pair.Trim()); continue; }
                bool okA = byName.TryGetValue(Key(ends[0]), out var a), okB = byName.TryGetValue(Key(ends[1]), out var b);
                if (okA && okB) MainRoutes.Add((a, b));
                else missing.AddRange(new[] { okA ? null : ends[0], okB ? null : ends[1] }.Where(n => n != null));
            }
            if (missing.Count > 0)
                Plugin.Log.LogWarning("TradeRoutes: unknown ports " + string.Join(", ", missing.Distinct().ToArray()) +
                                      ". Known: " + string.Join(", ", byName.Keys.ToArray()));
            if (MainRoutes.Count == 0) MainRoutes.AddRange(Routes);
            Plugin.Log.LogInfo($"Main trade routes: {MainRoutes.Count}");
        }

        private void Cluster()
        {
            var left = new List<Vector2>(portPositions);
            while (left.Count > 0)
            {
                var cluster = new List<Vector2> { left[0] };
                left.RemoveAt(0);
                for (int i = 0; i < cluster.Count; i++)
                    for (int j = left.Count - 1; j >= 0; j--)
                        if ((left[j] - cluster[i]).sqrMagnitude < ClusterLink * ClusterLink)
                        {
                            cluster.Add(left[j]);
                            left.RemoveAt(j);
                        }
                if (cluster.Count >= MinPortsPerArchipelago) Archipelagos.Add(cluster);
            }
        }
    }
}
