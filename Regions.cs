using System.Text.RegularExpressions;
using UnityEngine;

namespace TerraIncognita
{
    /// <summary>
    /// Climate regions of the vanilla world. Each new island takes the region of the nearest vanilla island whose
    /// name carries a region letter ("island 6 A  (lion fang)", "island 26 Lagoon Temple"); open-sea islands thus
    /// follow the closest archipelago. Climate picks the grass palette and the tree / bush / rock set.
    /// </summary>
    internal enum Climate { Desert, Asian, Oceanic, North }

    internal static class Regions
    {
        private static readonly Regex Letter = new Regex(@"^island \d+\s+(A|E|M|Lagoon)\b");

        public static Climate? FromIslandName(string name)
        {
            // Happy Bay carries "M" but lies between Emerald and the open sea, far south of Aestrin: its waters are Asian
            if ((name ?? "").Contains("HappyBay")) return Climate.Asian;
            var m = Letter.Match(name ?? "");
            if (!m.Success) return null;
            switch (m.Groups[1].Value)
            {
                case "A": return Climate.Desert;        // Al'Ankh
                case "E": return Climate.Asian;         // Emerald
                case "Lagoon": return Climate.Oceanic;  // Emerald Lagoon
                default: return Climate.North;          // Medi / Aestrin
            }
        }

        public static Climate Nearest(Vector2 real, FloatingOriginManager origin)
        {
            Climate best = Climate.Desert;
            float bestD = float.MaxValue;
            if (!IslandDistanceTracker.instance) return best;
            foreach (var h in IslandDistanceTracker.instance.islands)
            {
                if (!h) continue;
                var r = FromIslandName(h.name);
                if (r == null) continue;
                var p = origin.ShiftingPosToRealPos(h.GetPosition());
                float d = (new Vector2(p.x, p.z) - real).sqrMagnitude;
                if (d < bestD) { bestD = d; best = r.Value; }
            }
            return best;
        }
    }
}
