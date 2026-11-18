using System.Collections;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TerraIncognita
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.terraincognita";
        public const string Name = "Terra Incognita";
        public const string Version = "0.1.0";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        // --- config ---
        internal static ConfigEntry<int> WorldSeed;
        internal static ConfigEntry<bool> RandomSeedPerGame;
        /// <summary>The seed this game's islands were made with (from the save, or new for a new game).</summary>
        internal static int Seed;
        internal static ConfigEntry<int> ScatterPerRegion;
        internal static ConfigEntry<int> RouteIslandCount;
        internal static ConfigEntry<float> MinClearanceToLand;
        internal static ConfigEntry<float> RouteOffsetMin;
        internal static ConfigEntry<float> RouteOffsetMax;
        internal static ConfigEntry<string> TradeRoutes, TreeDensity;
        internal static float TreeDensityFor(Climate c)
        {
            foreach (var part in (TreeDensity.Value ?? "").Split(','))
            {
                var kv = part.Split(':');
                if (kv.Length == 2 && kv[0].Trim().Equals(c.ToString(), System.StringComparison.OrdinalIgnoreCase)
                    && float.TryParse(kv[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
                    return Mathf.Max(0f, f);
            }
            return 1f;
        }
        internal static ConfigEntry<float> ScatterRouteClearance;
        internal static ConfigEntry<float> ScatterRingInner, ScatterRingOuter, ScatterMinDistanceToPort;
        internal static ConfigEntry<float> PebblesOnSand, PebblesInGrass, RockMaxVisibleRatio, TreesOnSand;
        internal static ConfigEntry<int> GroveMin, GroveMax, LushExtraTrees;
        internal static ConfigEntry<float> LushGreen, LushGrassDensity, SeaweedDensity, JungleDensity, SeaweedMinDepth;
        internal static ConfigEntry<string> Seaweed;
        internal static ConfigEntry<string> DisabledRocks;
        internal static ConfigEntry<float> SouthOnlyMaxLatitude, PalmMaxLatitude, RockMaxSlope, CliffMaxSlope, TerrainPixelError;
        internal static ConfigEntry<string> SouthOnlyTypes, PondSource, RockSources, GrassOverrides, PondMaterial, PondStyle;
        internal static ConfigEntry<KeyboardShortcut> GrassWindowKey;
        internal static System.Collections.Generic.HashSet<IslandType> SouthOnlyTypesSet() =>
            new System.Collections.Generic.HashSet<IslandType>((SouthOnlyTypes.Value ?? "").Split(',')
                .Select(s => s.Trim()).Where(s => System.Enum.IsDefined(typeof(IslandType), s))
                .Select(s => (IslandType)System.Enum.Parse(typeof(IslandType), s)));
        internal static ConfigEntry<bool> DebugLog, PondsEnabled, WrecksEnabled;
        internal static ConfigEntry<float> WreckChance, WreckMaxLength, WreckDamage, CampChance, CampTentYaw;
        internal static ConfigEntry<bool> CampsEnabled;
        internal static ConfigEntry<bool> GullsEnabled, ForageEnabled, WreckLootEnabled, Verbose;
        internal static ConfigEntry<float> PondFlowSpeed, PondShade, PondOpacity, HiddenFromHeight, GullHeadingYaw, GullTilt, RumorChance, RumorKmPerDay, RumorMaxDays;
        internal static ConfigEntry<bool> RumorsEnabled, SculptedRocks;
        internal static ConfigEntry<float> SculptedRockAmount, CaveLootChance;
        internal static ConfigEntry<string> SculptedPaint;
        internal static ConfigEntry<KeyboardShortcut> RockTextureWindowKey;        internal static void Debug(string msg) { if (Verbose.Value) Log.LogInfo(msg); }
        internal static ConfigEntry<float> ForageScale;
        internal static ConfigEntry<string> WreckBoats;
        internal static ConfigEntry<float> PondChanceScale, PondSmoothness, PondRippleStrength, PondRippleSpeed;
        internal static ConfigEntry<string> PondColor, PondReeds;
        internal static ConfigEntry<float> PondReedDensity;
        internal static ConfigEntry<bool> SpawnIslands;
        private static readonly System.Collections.Generic.Dictionary<Climate, ConfigEntry<string>> vegetation =
            new System.Collections.Generic.Dictionary<Climate, ConfigEntry<string>>();
        private static readonly System.Collections.Generic.Dictionary<Climate, ConfigEntry<string>> props =
            new System.Collections.Generic.Dictionary<Climate, ConfigEntry<string>>();

        internal static string VegetationFor(Climate r) => vegetation.TryGetValue(r, out var e) ? e.Value : "";
        internal static string PropsFor(Climate r) => props.TryGetValue(r, out var e) ? e.Value : "";

        // From dumps\vegetation_by_island.txt (share of planted details on the region's vanilla islands)
        private static readonly System.Collections.Generic.Dictionary<Climate, string> DefaultVegetation =
            new System.Collections.Generic.Dictionary<Climate, string>
        {
            [Climate.Desert]  = "LGW_d46:1, LGW_d45:0.6, LGW_d25:0.4, rock_01_LOD (TERRAIN):0.25, LGW_d38:0.2, LGW_d42:0.06, LGW_d31:0.04, LGW_d29:0.03",
            [Climate.Asian]   = "LGW_d25:1, LGW_d36:0.5, LGW_d32:0.4, LGW_d16:0.3, LGW_d13:0.15, LGW_d24:0.1, LGW_d35:0.05, LGW_d33:0.05",
            [Climate.Oceanic] = "LGW_d46:1, LGW_d25:0.6, LGW_d32:0.3, LGW_d38:0.3, LGW_d07:0.15, LGW_d47:0.06, LGW_d44:0.06, LGW_d31:0.05, LGW_d29:0.03",
            [Climate.North]   = "LGW_d46:1, LGW_d41:0.4, LGW_d35:0.35, LGW_d22:0.25, LGW_d40:0.15, LGW_d21:0.12, LGW_d39:0.05, LGW_d42:0.05, LGW_d31:0.03",
        };

        // From dumps\scenery_census (F7). scenes = scenery scene build indices to search (= vanilla island number).
        private static readonly System.Collections.Generic.Dictionary<Climate, string> DefaultProps =
            new System.Collections.Generic.Dictionary<Climate, string>
        {
            // Oasis (scene 20): the agave-like trop_shrub_05 (the cycad palm_tree_small_1 dropped: its trunk hung in the air)
            [Climate.Desert]  = "scenes=6,20; tree=palm_tree_1,palm_tree_2,palm_tree_3,palm_tree_4; bush=palm_tree_small_2,trop_shrub_05",
            [Climate.Asian]   = "scenes=38,13,37,9,11,10,22,12,39; tree=tree_east,tree_east 2,tree_east,bamboo; bush=bush,bamboo",
            // Lagoon's own scenery scenes (26–29: Crest clip surfaces, Water4 ponds) crashed the game when loaded from far away —
            // its trees also grow in Emerald (swamp 37, jungle 38) and Medi (valley 35).
            [Climate.Oceanic] = "scenes=37,38,13,35; tree=tree_east,tree_east 2,tree_fat_002,tree_swamp; bush=bush",
            [Climate.North]   = "scenes=35,34,33,16,15,17,21,23; tree=tree_cypress,tree_cypress,tree_fat,tree_fat_002; bush=bush_04,bush_05,bush",
        };
        internal static ConfigEntry<bool> DevToolsEnabled;
        internal static ConfigEntry<KeyboardShortcut> DumpIslandKey, DumpLookKey, DumpWorldKey, NearestModIslandsKey, SceneryCensusKey, BoatCensusKey, ItemCensusKey, BeaconsKey, FlyKey;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            // The release config shows only what a player would change (on/off switches, the seed, how many islands,
            // the wreck and cave-loot chances). Everything else — tuning found while making the mod — lives in an
            // in-memory config that is never written to disk (its defaults are the values it was tuned to).
            var H = new ConfigFile(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Info.Location), "internal (never saved).cfg"), false)
                    { SaveOnConfigSet = false };

            WorldSeed = Config.Bind("Generation", "WorldSeed", 0,
                "Every new game gets its own random island layout, kept in its save. This seed is used only for saves " +
                "made before that (version 1.0 and older) and, if RandomSeedPerGame is off, for every new game. " +
                "0 = random.");
            RandomSeedPerGame = Config.Bind("Generation", "RandomSeedPerGame", true,
                "A new random island layout for each new game (stored in the save). Off: every new game uses WorldSeed.");
            ScatterPerRegion = Config.Bind("Generation", "ScatterPerRegion", 6,
                "Islands scattered at random inside each archipelago (Al'Ankh, Emerald, Aestrin).");
            RouteIslandCount = Config.Bind("Generation", "RouteIslandCount", 12,
                "Islands placed beside trade routes (port -> mission destination lines).");
            // first defaults (3 / 6) were too few for every type to turn up
            if (ScatterPerRegion.Value == 3) ScatterPerRegion.Value = 6;
            if (RouteIslandCount.Value == 6) RouteIslandCount.Value = 12;
            MinClearanceToLand = H.Bind("Generation", "MinClearanceToLand", 2500f,
                "Minimum distance (world units) from any vanilla island, port or other new island.");
            TradeRoutes = H.Bind("Generation", "TradeRoutes",
                "Gold Rock City - Dragon Cliffs; Gold Rock City - Oasis; Gold Rock City - Happy Bay; Gold Rock City - Fort Aestrin; " +
                "Oasis - Fort Aestrin; Oasis - Happy Bay; Fort Aestrin - Happy Bay; Fort Aestrin - Chronos; Fort Aestrin - Dragon Cliffs; " +
                "Dragon Cliffs - Chronos; Dragon Cliffs - Happy Bay; Kicia Bay - Gold Rock City; Kicia Bay - Dragon Cliffs",
                "Main sea lanes between capitals and key ports (port names as in the game, 'A - B; C - D'). Route islands are " +
                "placed beside these, and scattered islands keep clear of them. Empty = every port -> mission destination line.");
            RouteOffsetMin = H.Bind("Generation", "RouteOffsetMin", 1000f,
                "Route islands: minimum sideways offset from the straight route line.");
            RouteOffsetMax = H.Bind("Generation", "RouteOffsetMax", 5000f,
                "Route islands: maximum sideways offset from the straight route line.");
            ScatterRouteClearance = H.Bind("Generation", "ScatterRouteClearance", 500f,
                "Scattered islands keep at least this far from any route line.");
            HiddenFromHeight = H.Bind("Generation", "HiddenFromHeight", 20f,
                "New islands stand beyond the horizon as seen from this height (metres; 20 = a masthead) at every port: an island " +
                "of height H keeps √(2R(H + 2·this)) from all ports (R = the game's curvature radius 515662) — 8–12 km for 20–100 m islands.");
            ScatterRingInner = H.Bind("Generation", "ScatterRingInner", 0.8f,
                "Scattered islands go into a ring around the archipelago: inner edge as a fraction of its radius " +
                "(radius = farthest port from the centre). 0 = anywhere inside.");
            ScatterRingOuter = H.Bind("Generation", "ScatterRingOuter", 1.4f,
                "Outer edge of that ring, fraction of the archipelago radius. >1 = beyond the outermost ports.");
            ScatterMinDistanceToPort = H.Bind("Generation", "ScatterMinDistanceToPort", 4500f,
                "Scattered islands keep at least this far from any port of their archipelago.");
            SouthOnlyTypes = H.Bind("Generation", "SouthOnlyTypes", "Atoll, Sandbar",
                "Tropical island types (coral atolls, white sand bars) that never appear north of SouthOnlyMaxLatitude. " +
                "Names as in the [Vegetation]/type list: Atoll, VolcanicBay, RockyIslet, MesaPlateau, SeaStacks, Sandbar, " +
                "TwinPeaks, CrescentCove, OasisCrater, SunkenRidge.");
            SouthOnlyMaxLatitude = H.Bind("Generation", "SouthOnlyMaxLatitude", 36f,
                "Northern limit for SouthOnlyTypes, degrees N (Al'Ankh/Emerald ≈ 31°N, Lagoon ≈ 28°N, Medi/Aestrin ≈ 40°N).");
            TerrainPixelError = H.Bind("Generation", "TerrainPixelError", 2f,
                "Terrain mesh accuracy of the new islands (Unity heightmapPixelError, 1–200). Lower = steep slopes stop " +
                "flickering while the camera moves, at some GPU cost. Vanilla islands use 5.");
            PebblesOnSand = H.Bind("Vegetation", "PebblesOnSand", 0.05f,
                "Loose pebbles (the desert's rock_01 detail) on dry sand, expected count per ~1.2 m cell. 0 = none.");
            PebblesInGrass = H.Bind("Vegetation", "PebblesInGrass", 0.08f,
                "Share of pebbles mixed into the grass of regions whose palette doesn't list them (Desert has its own).");
            TreesOnSand = H.Bind("Props", "TreesOnSand", 0.5f,
                "Share of trees kept where they stand on sand (atolls keep all their palms). Trees belong on the green.");
            LushGreen = H.Bind("Vegetation", "LushGreen", 0.85f,
                "Emerald / Lagoon islands: how much of the bare ground above the beach (and rock up to ~50°) turns green, 0–1.");
            LushGrassDensity = H.Bind("Vegetation", "LushGrassDensity", 1.5f, "Emerald / Lagoon: grass density multiplier (jungle).");
            JungleDensity = H.Bind("Props", "JungleDensity", 1f,
                "Emerald / Lagoon: how thickly the green is filled with trees and bushes (1 = a tree on 30 % and a bush on 25 % of 6 m cells; 0 = off).");
            Seaweed = H.Bind("Vegetation", "Seaweed", "LGW_d38, LGW_d39, LGW_d40",
                "Grass types that are really seaweed (tick them in the grass window): planted under water by the shore, 0.4–3.5 m deep, in patches.");
            SeaweedDensity = H.Bind("Vegetation", "SeaweedDensity", 2f, "Seaweed per ~1.2 m cell in the thickest patches.");
            SeaweedMinDepth = H.Bind("Vegetation", "SeaweedMinDepth", 4f,
                "Seaweed grows no shallower than this (metres; 3 keeps it under water with a tide mod) and down to 4 m deeper.");
            LushExtraTrees = H.Bind("Props", "LushExtraTrees", 2, "Emerald / Lagoon: extra trees per grove on top of GroveMin–GroveMax.");
            GroveMin = H.Bind("Props", "GroveMin", 1, "Extra trees around each tree on grass: minimum.");
            GroveMax = H.Bind("Props", "GroveMax", 3, "Extra trees around each tree on grass: maximum (groves).");
            TreeDensity = H.Bind("Props", "TreeDensity", "Desert:1.3, North:1.5, Asian:1, Oceanic:1",
                "Tree multiplier per climate: scales the groves on grass and how many trees stay on sand.");
            RockMaxVisibleRatio = H.Bind("Props", "RockMaxVisibleRatio", 0.6f,
                "Tall rocks are sunk until at most this × their base width shows above ground (0.6: Medi's 12.7 m rock2 " +
                "shows ~5 m). Low rocks are unaffected.");
            DisabledRocks = H.Bind("Props", "DisabledRocks", "",
                "Rocks from RockSources never placed, e.g. \"35/rock2\" (scene/name, comma-separated). Set in the grass window.");
            GrassOverrides = H.Bind("Vegetation", "GrassOverrides",
                "LGW_d22:0.35, LGW_d35:0.05, LGW_d36:0.05, LGW_d39:0.2, LGW_d41:0.1, rock_01_LOD (TERRAIN):0.4",
                "Per grass type multiplier on top of the region palettes: name:multiplier, comma-separated " +
                "(0 = never planted, 0.3 = rarer, 2 = twice as much). Easiest to set in the grass window (GrassWindowKey).");
            GrassWindowKey = H.Bind("Vegetation", "GrassWindowKey", new KeyboardShortcut(KeyCode.F6),
                "Open the grass window: switch grass types on/off, thin them out, solo one to see which is which.");
            RockSources = H.Bind("Props", "RockSources", "6=rock2; 35,34,33,16,17,15=rock1,rock2",
                "Rocks for every region: <scene indices>=<object names>; groups separated by ';'. Of each name the most " +
                "upright instance is used, sized like Lion Fang's rock2 and recoloured per region (Medi/Aestrin blue-grey). " +
                "Emerald's rocks are left out on purpose (tilted, stretched, overgrown).");
            PondFlowSpeed = H.Bind("Ponds", "FlowSpeed", 0.15f, "Fey water: how fast the surface drifts, relative to Fey Valley's lake (1).");
            PondShade = H.Bind("Ponds", "Shade", 0.7f, "Fey water: colour brightness relative to Fey Valley's lake (1); lower = darker, easier to see in sun.");
            PondOpacity = H.Bind("Ponds", "Opacity", 2.1f,
                "Fey water: how quickly it turns opaque with depth, relative to Fey Valley's deep lake (1). Ponds are shallow: " +
                "6 ≈ fully coloured from ~0.8 m down.");
            PondStyle = H.Bind("Ponds", "Style", "fey",
                "Pond water: \"fey\" = the water of Fey Valley's lake (the game's own material); \"own\" = the mod's Standard water " +
                "(Color / Smoothness / Ripple settings below).");
            PondMaterial = H.Bind("Props", "PondMaterial", "_ReflectionColor=0.1,0.4,0.6,1",
                "Water4 material tweaks for ponds: _Name=x,y,z,w ('*' keeps a component) or _Name=value, separated by ';'. " +
                "Original values are logged (\"Pond material:\"). _DistortParams = bump, reflection distortion, Fresnel power, " +
                "Fresnel bias (onsen: 1.9, -0.5 = mostly see-through); _ReflectionColor alpha 1 = this colour instead of the " +
                "reflection texture, which nothing renders for our ponds (alpha < 1 mixes in white).");
            WrecksEnabled = Config.Bind("Wrecks", "Enabled", true,
                "Shipwrecks run aground on new islands, built from the game's own boats at runtime.");
            CampsEnabled = Config.Bind("Wrecks", "Camps", true,
                "Survivors' camps: on some islands with a wreck, a tent, a dead fire and things left behind (near the wreck or fresh water if there's level ground).");
            CampChance = H.Bind("Wrecks", "CampChance", 0.5f, "Chance of a camp on an island with a wreck and fresh water.");
            CampTentYaw = H.Bind("Wrecks", "CampTentYaw", 0f,
                "Degrees added to the tents' heading (if their door doesn't face the fire, try 180 or ±90).");
            WreckChance = Config.Bind("Wrecks", "Chance", 0.3f,
                "Chance per island to get a wreck (if it has a beach long enough). Same islands every launch (island seed).");
            WreckDamage = H.Bind("Wrecks", "DamageOverlay", 0.15f,
                "Strength of the hull's own damage texture on wrecks, 0–1 (1 looked painted over).");
            ForageEnabled = Config.Bind("Forage", "Enabled", true,
                "Coconuts under palms (south of 36°N), mushrooms in the shade (green islands), oranges (Emerald) and apples (north) " +
                "under trees. Picked ones grow back in 4–10 days.");
            ForageScale = H.Bind("Forage", "Amount", 1f, "Multiplies how many trees bear and how many mushroom clusters grow.");
            WreckLootEnabled = Config.Bind("Wrecks", "Loot", true,
                "Cargo and ship's things by the wrecks (on deck and washed up nearby), once per game: taken is gone for good.");
            GullsEnabled = Config.Bind("Gulls", "Enabled", true,
                "Seagull flocks over the new islands (the game's own flock and cries, cloned from a vanilla island).");
            SculptedRocks = H.Bind("Rocks", "AutoSculpted", false,
                "Generate sea rocks automatically at load (stacks, arches, spires). Off: they are placed by hand in the editor " +
                "(Tools › Terra Incognita › 4. Rock sculptor) and come with the islands.");
            SculptedRockAmount = H.Bind("Rocks", "SculptedAmount", 1f, "Multiplier on how many sculpted rocks each island gets (0 = none).");
            CaveLootChance = Config.Bind("Rocks", "CaveLootChance", 0.15f,
                "Chance per sea cave (grotto) that something is hidden in it: a smugglers' stash of drink (70 %) or a crate of silver, gold or gems (30 %).");
            SculptedPaint = H.Bind("Rocks", "SculptedPaint", new RockPaint().ToString(),
                "How the sculpted rocks are painted (PaintedRock shader: name=value; …). Easiest to tune live in the rock paint window (RockTextureWindowKey).");
            RockTextureWindowKey = H.Bind("Rocks", "RockTextureWindowKey", new KeyboardShortcut(KeyCode.F4),
                "Open the rock texture window: tune the sculpted rocks' painted texture live (developer tools on).");            RumorsEnabled = Config.Bind("Rumors", "Enabled", true,
                "Tavern rumour men (buy them a drink) sometimes talk of the new islands: direction and days of sailing.");
            RumorChance = H.Bind("Rumors", "Chance", 0.35f, "Share of drinks that buy an island rumour instead of the trade gossip.");
            // the map grid is 1° = 9000 units = "140 nm" (so a game nautical mile is ~64 units); boats make 6–12 kn →
            // ~9 kn × 24 h ≈ 216 nm ≈ 14 km a day (the speedometer's real knots agree with a ~50-minute game day)
            RumorKmPerDay = H.Bind("Rumors", "KmPerDay", 14f, "Sailing distance a rumour counts as one day (km = thousand world units).");
            if (Mathf.Approximately(RumorKmPerDay.Value, 40f)) RumorKmPerDay.Value = 14f;            // first guess was 40
            RumorMaxDays = H.Bind("Rumors", "MaxDays", 4f, "Islands further than this many days' sail from the port aren't talked about.");
            GullHeadingYaw = H.Bind("Gulls", "HeadingYaw", 0f,
                "Birds are turned to face the way they fly; if they still fly tail first set 180 (90 / -90 if sideways).");
            GullTilt = H.Bind("Gulls", "Tilt", 0f,
                "Extra pitch of the bird model, degrees (on top of the automatic level-out): 180 if they fly belly up, ±90 if on edge.");
            WreckMaxLength = H.Bind("Wrecks", "MaxLength", 40f, "Boats whose hull is longer than this (metres) are not used as wrecks.");
            if (Mathf.Approximately(WreckMaxLength.Value, 32f)) WreckMaxLength.Value = 40f;   // was measured with the yards
            WreckBoats = H.Bind("Wrecks", "Boats", "",
                "Boats to make wrecks from: name starts, comma-separated (e.g. \"dhow small, dhow medium, junk\"). " +
                "Empty = every vanilla boat found in the world (modded boats are left out).");
            PondsEnabled = Config.Bind("Ponds", "Enabled", true,
                "Carve small ponds into new islands at spawn (spring on volcanoes, rain ponds, puddles, brackish pit on atolls). " +
                "Same place every launch (island seed). The oasis pond is part of its crater and stays regardless.");
            PondReeds = H.Bind("Ponds", "Reeds", "LGW_d25",
                "Grass type planted as reeds around ponds (tall blade grass by default). Empty = none.");
            PondReedDensity = H.Bind("Ponds", "ReedDensity", 3f,
                "Reeds per ~1.2 m cell in the thickest clumps along the shore.");
            PondColor = H.Bind("Ponds", "Color", "0.06,0.17,0.18,0.93",
                "Water colour R,G,B,A (0–1). A = opacity: 0.93 = dark pond water, the bottom barely shows.");
            PondSmoothness = H.Bind("Ponds", "Smoothness", 0.1f,
                "Surface gloss 0–1: higher = sharper sky reflection and sun highlight.");
            PondRippleStrength = H.Bind("Ponds", "RippleStrength", 0.06f,
                "Ripples on the water surface (normal map strength). 0 = mirror-flat.");
            PondRippleSpeed = H.Bind("Ponds", "RippleSpeed", 0.02f,
                "How fast the ripples drift (texture tiles of 1.5 m per second).");
            // earlier defaults (pale / see-through, coarse fast ripples) → current ones, unless changed by hand
            if (PondColor.Value == "0.16,0.42,0.45,0.55" || PondColor.Value == "0.07,0.2,0.22,0.8") PondColor.Value = (string)PondColor.DefaultValue;
            if (Mathf.Approximately(PondRippleStrength.Value, 0.6f) || Mathf.Approximately(PondRippleStrength.Value, 0.15f))
                PondRippleStrength.Value = (float)PondRippleStrength.DefaultValue;
            if (Mathf.Approximately(PondRippleSpeed.Value, 0.08f)) PondRippleSpeed.Value = (float)PondRippleSpeed.DefaultValue;
            PondChanceScale = H.Bind("Ponds", "ChanceScale", 1f,
                "Multiplies the per-type pond chances (volcano 70 %, rocky 60 %, atoll 60 %, mesa/twin 50 %, cove 40 %, " +
                "ridge 30 %). 2 = nearly always. Changing it moves/adds/removes ponds on existing islands.");
            if (PondMaterial.Value == "_ReflectionColor=0.55,0.68,0.74,1; _DistortParams=*,*,3,0.05") PondMaterial.Value = (string)PondMaterial.DefaultValue;   // first default made it milky
            PondSource = H.Bind("Props", "PondSource", "39=water",
                "Water surface for oasis ponds: <scenery scene build index>=<object name>. 39 = Emerald onsen (Water4 plane).");
            PalmMaxLatitude = H.Bind("Props", "PalmMaxLatitude", 36f,
                "Trees standing on sand: south of this latitude (°N) palms, north of it bushes only. " +
                "Trees on grass are always the region's own.");
            RockMaxSlope = H.Bind("Props", "RockMaxSlope", 35f,
                "Rocks on ground steeper than this (degrees) are not placed — they would hang out of the slope.");
            CliffMaxSlope = H.Bind("Props", "CliffMaxSlope", 50f,
                "Same for cliffs (big rocks set into steep slopes).");
            // Regions: Desert = Al'Ankh, Asian = Emerald, Oceanic = Emerald Lagoon, North = Medi/Aestrin.
            // Every new island takes the region of the nearest vanilla island.
            const string propHelp = "Game props borrowed at runtime for islands of this region. " +
                                    "scenes = scenery scene build indices to search (vanilla island number, e.g. 6 = Lion Fang); " +
                                    "then role=object names for tree and bush (repeat a name to make it more common). Rocks and cliffs are always " +
                                    "Lion Fang's rock2 (upright, sized for the generator); a region's rock= only lends its material. " +
                                    "Markers in island prefabs are replaced by a random prop of their role.";
            const string vegHelp = "Detail prototypes (grass/fern/flower texture names from vanilla terrains) planted on the " +
                                   "'Grass Leaves' areas: name:share, comma-separated. Shares are relative (1 = main cover, " +
                                   "0.05 = rare); the island type decides overall density. Unknown names are listed in the log.";
            foreach (Climate r in System.Enum.GetValues(typeof(Climate)))
            {
                props[r] = H.Bind("Props", r.ToString(), DefaultProps[r], propHelp);
                if ((r == Climate.Oceanic && props[r].Value.Contains("scenes=26"))   // the crashing first default
                    || props[r].Value.Contains("cliff=rock2,flatrocks") || props[r].Value.Contains("cliff=rock1,rock2")
                    || (r == Climate.Oceanic && props[r].Value.EndsWith("cliff=rock2")))   // old rock/cliff sets
                    props[r].Value = DefaultProps[r];
                if (r == Climate.Desert && props[r].Value.Trim() == "scenes=6; tree=palm_tree_1,palm_tree_2,palm_tree_3,palm_tree_4; bush=palm_tree_small_2")
                    props[r].Value = DefaultProps[r];                        // the old default, before the Oasis plants
                // rocks moved to RockSources: strip leftover rock=/cliff= parts from older configs
                props[r].Value = System.Text.RegularExpressions.Regex.Replace(props[r].Value, @"\s*;\s*(rock|cliff)=[^;]*", "");
                vegetation[r] = H.Bind("Vegetation", r.ToString(), DefaultVegetation[r], vegHelp);
            }
            DebugLog = H.Bind("Debug", "LogPlacements", false,
                "Write every placed island (world + globe coordinates) to the BepInEx log.");
            SpawnIslands = H.Bind("Debug", "SpawnIslands", true,
                "Turn off to use only the recon dump keys without touching the world.");
            DevToolsEnabled = Config.Bind("Debug", "DevTools", false,
                "Developer tools: dump hotkeys (F7–F11), the grass & rocks window (F6), dumps/placements.txt with moveBoat commands.");
            Verbose = H.Bind("Debug", "VerboseLog", false, "Detailed log lines (wreck parts, sails, timings) for debugging.");
            DumpIslandKey = H.Bind("Debug", "DumpNearestIslandKey", new KeyboardShortcut(KeyCode.F8),
                "Dump the nearest island (hierarchy, components, terrain, materials) to plugins\\TerraIncognita\\dumps.");
            DumpLookKey = H.Bind("Debug", "DumpLookedAtKey", new KeyboardShortcut(KeyCode.F9),
                "Dump the object under the crosshair and its parents.");
            DumpWorldKey = H.Bind("Debug", "DumpWorldKey", new KeyboardShortcut(KeyCode.F10),
                "Dump all islands, ports, regions, routes and loaded shaders.");
            NearestModIslandsKey = H.Bind("Debug", "NearestModIslandsKey", new KeyboardShortcut(KeyCode.F11),
                "Print the 5 nearest Terra Incognita islands (distance, direction, state) to the BepInEx console.");
            BoatCensusKey = H.Bind("Debug", "BoatCensusKey", new KeyboardShortcut(KeyCode.F7, KeyCode.LeftShift),
                "List every boat in memory (hull/deck/mast meshes, materials) and the shipyards' sail prefabs — for wrecks.");
            ItemCensusKey = H.Bind("Debug", "ItemCensusKey", new KeyboardShortcut(KeyCode.F7, KeyCode.LeftControl),
                "List the game's item prefabs (index, name, kind), the world's item spawners (what, where, respawn) and seagull flocks.");
            SceneryCensusKey = H.Bind("Debug", "SceneryCensusKey", new KeyboardShortcut(KeyCode.F7),
                "Load every island's scenery scene in turn and list its props (trees, rocks, cliffs) per region.");
            BeaconsKey = H.Bind("Debug", "BeaconsKey", new KeyboardShortcut(KeyCode.B, KeyCode.LeftControl),
                "Pink columns over every loaded camp, wreck and pond.");
            FlyKey = H.Bind("Debug", "FlyKey", new KeyboardShortcut(KeyCode.F, KeyCode.LeftControl),
                "Fly mode: WASD along the view, Space up, C down, Shift faster, mouse wheel = speed.");


            gameObject.AddComponent<DevTools>();
            gameObject.AddComponent<DevFly>();
            gameObject.AddComponent<GrassWindow>();
            gameObject.AddComponent<RockTextureWindow>();
            new Harmony(Guid).PatchAll();
            Log.LogInfo($"{Name} {Version} loaded");
        }

        // The islands wait for the start menu: a new game picks a seed, a loaded game reads its own from the save
        // (GameState.modData) — then they are built, and only then does the game start / load (things left on our
        // islands are restored at world coordinates: the ground must be there).
        internal const string SeedKey = "TerraIncognita.seed";
        private static FloatingOriginManager origin;
        internal static bool Spawning, Spawned;

        internal void BeginSpawn(FloatingOriginManager o) => origin = o;

        internal static int NewGameSeed()
        {
            int s = RandomSeedPerGame.Value || WorldSeed.Value == 0 ? new System.Random().Next(1, int.MaxValue) : WorldSeed.Value;
            return s;
        }

        /// <summary>Build the islands for `seed`, then run `then` (the game's own new game / load).</summary>
        internal void SpawnThen(int seed, System.Action then)
        {
            Seed = seed;
            if (!SpawnIslands.Value || Spawned || !origin) { then(); return; }
            StartCoroutine(SpawnAndContinue(then));
        }

        private IEnumerator SpawnAndContinue(System.Action then)
        {
            Spawning = true;
            Log.LogInfo($"Terra Incognita: building islands, seed {Seed}");
            var it = SpawnWhenWorldReady(origin);
            while (true)
            {
                object cur;
                try { if (!it.MoveNext()) break; cur = it.Current; }
                catch (System.Exception e) { Log.LogError("Island spawn failed: " + e); break; }
                yield return cur;
            }
            Spawning = false; Spawned = true;
            then();
        }

        private IEnumerator SpawnWhenWorldReady(FloatingOriginManager origin)
        {
            // Port.Start / IslandHorizon.Start run on the same or next frame as FloatingOriginManager.Start.
            yield return null;
            yield return null;
            while (Port.ports == null || Refs.islands == null)
                yield return null;

            var sw = System.Diagnostics.Stopwatch.StartNew(); var times = new System.Text.StringBuilder();
            void Lap(string what) { times.Append($"{what} {sw.ElapsedMilliseconds} ms, "); sw.Reset(); sw.Start(); }
            var survey = WorldSurvey.Take(origin);
            // types present in the bundle; the placer picks only among them (positions don't depend on this)
            var available = IslandSpawner.LoadPrefabs().Keys.ToList();
            var placements = IslandPlacer.Place(survey, Seed, available, at => Regions.Nearest(at, origin));
            var spawned = IslandSpawner.Spawn(placements, origin);
            Lap("place+spawn");

            // ponds are carved now (before props, so markers in the water can be dropped)
            var withPond = spawned.Where(s => Ponds.Carve(s) > 0).Select(s => s.Root.name).ToList();
            // volcano craters: a lake in some, a thread of smoke from the others
            foreach (var s in spawned)
                if (Ponds.Crater(s) == "lake" && !withPond.Contains(s.Root.name)) withPond.Add(s.Root.name);
            IslandSpawner.MarkPonds(withPond);
            Lap("ponds");

            // props come from the game's scenery scenes; markers stay as they are until then
            bool needPond = spawned.Any(s => s.Root && s.Root.GetComponentsInChildren<Transform>(true).Any(t => t.name.StartsWith("pond")));
            sw.Stop();      // scene loading in between frames is not ours to count
            yield return PropLibrary.Harvest(spawned.Select(s => s.Climate).Distinct().ToList(), needPond);
            sw.Reset(); sw.Start();
            PondWater.UseFeyWater();
            int dressed = spawned.Sum(s => RockSculptor.Dress(s));
            if (dressed > 0) Log.LogInfo($"Hand-placed sculpted rocks: {dressed}");
            int replaced = spawned.Where(s => s.Root).Sum(s => PropLibrary.ReplaceMarkers(s.Root, s.Climate, s.Seed, s.Latitude, s.Type));
            Log.LogInfo($"Replaced {replaced} prop markers");
            Lap("props");

            // wrecks: hulls copied from the boats in the world (they exist by now)
            Wrecks.Harvest();
            Lap("wreck templates");
            int wrecks = spawned.Count(s => Wrecks.Place(s));
            Log.LogInfo($"Placed {wrecks} wrecks");
            Wrecks.PlaceTreasure(Seed);
            int caveLoot = spawned.Sum(s => RockSculptor.CaveLoot(s));
            Log.LogInfo($"Cave loot: {caveLoot} items");
            int camps = Camp.PlaceAll(spawned);
            Log.LogInfo($"Survivors' camps: {camps}");
            int sculpted = spawned.Sum(s => RockSculptor.Place(s));
            Log.LogInfo($"Sculpted rocks: {sculpted}");
            IslandSpawner.MarkWrecks(Wrecks.Placed);
            Lap("wrecks");
            Gulls.Place(spawned);
            Forage.Plan(spawned);
            Rumors.Register(spawned);
            Lap("gulls+forage");
            Debug("Spawn timing (main thread): " + times);
        }
    }

    /// <summary>All vanilla islands are children of "_shifting world"; spawn ours once it exists.</summary>
    [HarmonyPatch(typeof(FloatingOriginManager), "Start")]
    internal static class FloatingOriginStartPatch
    {
        private static void Postfix(FloatingOriginManager __instance) => Plugin.Instance.BeginSpawn(__instance);
    }
}
