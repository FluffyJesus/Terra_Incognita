using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace TerraIncognita
{
    /// <summary>
    /// Keep in sync with the Unity generator: enum order/names ↔ IslandArchetype, Prefix ↔ prefab name prefix.
    
    /// </summary>
    internal enum IslandType
    {
        Atoll, VolcanicBay, RockyIslet,          // (Wreck islet removed: wrecks will spawn by chance on islands)
        MesaPlateau, SeaStacks, Sandbar, TwinPeaks, CrescentCove, OasisCrater, SunkenRidge,
        KarstTowers                              // tower karst (Phang Nga): Emerald / Lagoon only
    }

    internal class IslandTypeInfo
    {
        public string Prefix;
        public float ScatterWeight;   // inside archipelagos (outskirts ring)
        public float RouteWeight;     // beside trade routes / open sea
        public float GrassDensity;   // grass/fern details per ~1.2 m cell on full "Grass Leaves" (species come from the region)
        public float PlaceholderSize;  // cylinder diameter when no prefab exists
        public float PlaceholderHeight;
    }

    internal static class IslandTypes
    {
        // Open sea favours low, wave-worn or hazardous shapes (sandbars, stacks, sunken ridges, wrecks);
        // archipelago outskirts favour full islands.
        public static readonly Dictionary<IslandType, IslandTypeInfo> Info = new Dictionary<IslandType, IslandTypeInfo>
        {
            [IslandType.Atoll]        = new IslandTypeInfo { Prefix = "atoll",    ScatterWeight = 0.12f, RouteWeight = 0.16f, PlaceholderSize = 400, PlaceholderHeight = 20,
                                        GrassDensity = 6.2f },
            [IslandType.VolcanicBay]  = new IslandTypeInfo { Prefix = "volcanic", ScatterWeight = 0.14f, RouteWeight = 0.06f, PlaceholderSize = 300, PlaceholderHeight = 80,
                                        GrassDensity = 6.5f },
            [IslandType.RockyIslet]   = new IslandTypeInfo { Prefix = "rocky",    ScatterWeight = 0.14f, RouteWeight = 0.10f, PlaceholderSize = 150, PlaceholderHeight = 40,
                                        GrassDensity = 6f },
            [IslandType.MesaPlateau]  = new IslandTypeInfo { Prefix = "mesa",     ScatterWeight = 0.10f, RouteWeight = 0.04f, PlaceholderSize = 250, PlaceholderHeight = 60,
                                        GrassDensity = 5f },
            [IslandType.SeaStacks]    = new IslandTypeInfo { Prefix = "stacks",   ScatterWeight = 0.07f, RouteWeight = 0.14f, PlaceholderSize = 120, PlaceholderHeight = 90,
                                        GrassDensity = 1.2f },
            [IslandType.Sandbar]      = new IslandTypeInfo { Prefix = "sandbar",  ScatterWeight = 0.05f, RouteWeight = 0.14f, PlaceholderSize = 200, PlaceholderHeight = 6,
                                        GrassDensity = 2.8f },
            [IslandType.TwinPeaks]    = new IslandTypeInfo { Prefix = "twin",     ScatterWeight = 0.10f, RouteWeight = 0.04f, PlaceholderSize = 300, PlaceholderHeight = 70,
                                        GrassDensity = 5.4f },
            [IslandType.CrescentCove] = new IslandTypeInfo { Prefix = "crescent", ScatterWeight = 0.10f, RouteWeight = 0.06f, PlaceholderSize = 300, PlaceholderHeight = 40,
                                        GrassDensity = 5f },
            [IslandType.OasisCrater]  = new IslandTypeInfo { Prefix = "oasis",    ScatterWeight = 0.08f, RouteWeight = 0.02f, PlaceholderSize = 300, PlaceholderHeight = 45,
                                        GrassDensity = 5.7f },
            [IslandType.SunkenRidge]  = new IslandTypeInfo { Prefix = "ridge",    ScatterWeight = 0.04f, RouteWeight = 0.12f, PlaceholderSize = 250, PlaceholderHeight = 40,
                                        GrassDensity = 3.6f },
            [IslandType.KarstTowers]  = new IslandTypeInfo { Prefix = "karst",    PlaceholderSize = 300, PlaceholderHeight = 70,
                                        GrassDensity = 7.5f },
        };

        public static IslandType? FromPrefabName(string name)
        {
            string n = name.ToLowerInvariant();
            foreach (var kv in Info)
                if (n.StartsWith(kv.Value.Prefix + "_") || n == kv.Value.Prefix) return kv.Key;
            return null;
        }

        // Base share in the world (%): neutral shapes common, striking silhouettes rare so they stay special.
        private static readonly Dictionary<IslandType, float> Base = new Dictionary<IslandType, float>
        {
            [IslandType.RockyIslet] = 18, [IslandType.CrescentCove] = 14, [IslandType.VolcanicBay] = 13,
            [IslandType.Atoll] = 12, [IslandType.Sandbar] = 10, [IslandType.OasisCrater] = 8,
            [IslandType.TwinPeaks] = 8, [IslandType.MesaPlateau] = 7, [IslandType.SunkenRidge] = 5, [IslandType.SeaStacks] = 5,
            [IslandType.KarstTowers] = 8,
        };

        // Regional character: harsh rock in the north, sand and tablelands around Al'Ankh, volcanic and tropical in
        // the east; the open sea (route islands) gets lone peaks, drowned ridges and shoals.
        private static float Mult(IslandType t, Climate c, bool openSea)
        {
            // tower karst belongs to the Emerald seas: there it is a signature shape, nowhere else
            if (t == IslandType.KarstTowers)
                return c == Climate.Asian || c == Climate.Oceanic ? (openSea ? 1.2f : 2.5f) : 0f;
            if (openSea)
                switch (t)
                {
                    case IslandType.SunkenRidge: return 1.8f; case IslandType.SeaStacks: return 1.6f;
                    case IslandType.Sandbar: return 1.5f; case IslandType.VolcanicBay: case IslandType.Atoll: return 1.3f;
                    case IslandType.OasisCrater: return 0.6f; case IslandType.MesaPlateau: case IslandType.TwinPeaks: return 0.7f;
                    case IslandType.CrescentCove: return 0.8f; default: return 1f;
                }
            switch (c)
            {
                case Climate.North:
                    switch (t)
                    {
                        case IslandType.SeaStacks: return 1.8f; case IslandType.SunkenRidge: return 1.6f;
                        case IslandType.RockyIslet: case IslandType.MesaPlateau: return 1.5f; case IslandType.CrescentCove: return 1.3f;
                        case IslandType.Sandbar: return 0.4f; case IslandType.OasisCrater: return 0.6f; case IslandType.VolcanicBay: return 0.8f;
                        default: return 1f;
                    }
                case Climate.Desert:
                    switch (t)
                    {
                        case IslandType.MesaPlateau: return 1.6f; case IslandType.Sandbar: return 1.5f;
                        case IslandType.Atoll: return 1.3f; case IslandType.RockyIslet: case IslandType.OasisCrater: return 1.2f;
                        case IslandType.VolcanicBay: return 0.7f; case IslandType.TwinPeaks: return 0.8f;
                        default: return 1f;
                    }
                default:   // Asian / Oceanic
                    switch (t)
                    {
                        case IslandType.VolcanicBay: return 1.7f; case IslandType.TwinPeaks: return 1.6f;
                        case IslandType.Atoll: case IslandType.OasisCrater: return 1.4f;
                        case IslandType.MesaPlateau: return 0.6f; case IslandType.Sandbar: return 0.8f;
                        default: return 1f;
                    }
            }
        }

        /// <summary>
        /// Weighted pick among <paramref name="available"/> types (those with prefabs in the bundle; all types if
        /// none): base share × region / open sea × 0.4 per island of that type already placed (variety). Exactly ONE
        /// random number is consumed whatever the bundle contains, so island positions stay put.
        /// </summary>
        public static IslandType Pick(double x, bool onRoute, ICollection<IslandType> available, float latitude,
                                      Climate climate, IDictionary<IslandType, int> placed)
        {
            // tropical types (coral atolls, white sand bars) only south of the configured latitude
            var south = Plugin.SouthOnlyTypesSet();
            bool tooFarNorth = latitude > Plugin.SouthOnlyMaxLatitude.Value;
            var pool = Info.Where(kv => available == null || available.Count == 0 || available.Contains(kv.Key))
                           .Where(kv => !(tooFarNorth && south.Contains(kv.Key)))
                           .Select(kv => (kv.Key, w: (Base.TryGetValue(kv.Key, out float b) ? b : 5f) * Mult(kv.Key, climate, onRoute)
                                                     * Mathf.Pow(0.4f, placed != null && placed.TryGetValue(kv.Key, out int n) ? n : 0)))
                           .Where(t => t.w > 0f).ToList();
            if (pool.Count == 0)   // bundle holds only southern types: ignore the latitude rule rather than fail
                return Pick(x, onRoute, available, float.NegativeInfinity, climate, placed);
            float total = pool.Sum(t => t.w), acc = 0f;
            foreach (var (type, w) in pool)
            {
                acc += w / total;
                if (x < acc) return type;
            }
            return pool.Last().Key;
        }    }
}
