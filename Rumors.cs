using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace TerraIncognita
{
    /// <summary>
    /// Tavern talk about the new islands. Buy the rumour man a drink (the game's TavernRumorsDude): now and then,
    /// instead of the trade gossip, he tells of an island out there — a rough compass direction and days of sailing.
    ///   1. someone went ashore: what kind of island, and whether there is fresh water;
    ///   2. sailors saw it in passing: only its outline (tall / low / rocks);
    ///   3. a ship sailed that way and never came back — only when a wreck lies there; a treasure wreck "carried
    ///      something precious". The island itself isn't mentioned.
    /// </summary>
    internal static class Rumors
    {
        private class Entry
        {
            public Transform Root;
            public IslandType Type;
            public float Height;
            public bool Water;
            public string Boat;      // wreck: "dhow" / "junk" / "merchant ship", else null
            public int Treasure;     // 226 silver / 22 gold / 20 gems, else 0
        }

        private static readonly List<Entry> islands = new List<Entry>();

        public static void Register(IList<SpawnedIsland> spawned)
        {
            islands.Clear();
            var heights = IslandSpawner.TypeHeights();
            foreach (var s in spawned)
            {
                if (!s.Root) continue;
                Wrecks.Hulls.TryGetValue(s.Root.name, out var hull);
                Wrecks.Treasure.TryGetValue(s.Root.name, out int treasure);
                islands.Add(new Entry
                {
                    Root = s.Root,
                    Type = s.Type,
                    Height = heights.TryGetValue(s.Type, out float h) ? h : 30f,
                    Water = s.Root.GetComponentsInChildren<WaterSource>(true).Length > 0,
                    Boat = hull.boat == null ? null : BoatWord(hull.boat),
                    Treasure = treasure,
                });
            }
            Plugin.Log.LogInfo($"Rumours: {islands.Count} islands to talk about ({islands.Count(e => e.Boat != null)} with a wreck, {islands.Count(e => e.Treasure != 0)} with treasure)");
        }

        private static string BoatWord(string boat)
        {
            var b = boat.ToLowerInvariant();
            return b.Contains("dhow") ? "dhow" : b.Contains("junk") ? "junk" : "merchant ship";
        }

        /// <summary>A rumour about a new island within reach of this port, or null (then the game's own gossip).</summary>
        public static string For(Port port)
        {
            var fom = FloatingOriginManager.instance;
            if (!port || !fom || islands.Count == 0) return null;
            var pp = fom.ShiftingPosToRealPos(port.transform.position);
            float perDay = Mathf.Max(1f, Plugin.RumorKmPerDay.Value) * 1000f, reach = Plugin.RumorMaxDays.Value * perDay;
            var near = new List<(Entry e, Vector2 d)>();
            foreach (var e in islands)
            {
                if (!e.Root) continue;
                var ip = fom.ShiftingPosToRealPos(e.Root.position);
                var d = new Vector2(ip.x - pp.x, ip.z - pp.z);
                if (d.magnitude <= reach) near.Add((e, d));
            }
            if (near.Count == 0) return null;
            // nearer islands come up more often
            float total = near.Sum(n => 1f / (n.d.magnitude + 10000f));
            float roll = Random.value * total;
            var pick = near[near.Count - 1];
            foreach (var n in near) { roll -= 1f / (n.d.magnitude + 10000f); if (roll <= 0f) { pick = n; break; } }

            float wind = WindFactor(port, pick.d);
            string dir = Compass(pick.d), days = Days(pick.d.magnitude / perDay * wind);
            var e2 = pick.e;
            float kind = Random.value;
            if (e2.Boat != null && (kind < 0.4f || e2.Treasure != 0 && kind < 0.6f)) return Lost(e2, dir);
            string text = kind < 0.7f ? Landed(e2, dir, days) : Seen(e2, dir, days);
            if (wind >= 1.2f) text += One(" Against the wind, mind.", " Hard beating into the wind, though.", " Mind, you'd be tacking all the way.");
            else if (wind < 0.95f) text += One(" The wind's with you that way.", " Easy going with the trade wind behind you.");
            return text;
        }

        // ---------------------------------------------------------------- prevailing wind (the game's Wind.GetCurrentTradeWind)
        // The game mixes a regional prevailing wind into every new wind (half and half): south of 30°N none; north of
        // northTradeWindBorder it blows towards the east-northeast; west of 2°W towards the northeast; south of
        // southTradeWindBorder towards the west-southwest (the trades). The vector points where the wind blows to.
        private static Vector3 Prevailing(Vector3 globe)
        {
            var w = Wind.instance;
            if (!w || globe.z < 30f) return Vector3.zero;
            if (globe.z > w.northTradeWindBorder) return new Vector3(1f, 0f, 0.5f).normalized;
            if (globe.x < -2f) return new Vector3(0.75f, 0f, 0.75f).normalized;
            if (globe.z < w.southTradeWindBorder) return new Vector3(-1f, 0f, -0.5f).normalized;
            return Vector3.zero;
        }

        /// <summary>Days ×: running before the wind 0.85, reaching 1, close-hauled 1.25, dead against it 1.5.</summary>
        private static float WindFactor(Port port, Vector2 course)
        {
            var fom = FloatingOriginManager.instance;
            if (!fom) return 1f;
            var w = Prevailing(fom.GetGlobeCoords(port.transform));
            if (w == Vector3.zero || course.sqrMagnitude < 1f) return 1f;
            float a = Vector2.Angle(course, new Vector2(w.x, w.z));      // 0 = sailing where the wind blows
            return a <= 60f ? 0.85f : a <= 110f ? 1f : a <= 150f ? 1.25f : 1.5f;
        }

        // ---------------------------------------------------------------- texts
        private static string One(params string[] a) => a[Random.Range(0, a.Length)];

        private static string Landed(Entry e, string dir, string days)
        {
            string who = One("A fisherman", "An old sailor", "A ship's boy", "A pearl diver", "A smuggler, after a few drinks,");
            string text = One(
                $"{who} told me he went ashore on {Describe(e.Type)}, {days} to the {dir} of here.",
                $"{who} swears there's {Describe(e.Type)} {days} to the {dir}. Not on any chart, he says.",
                $"They say {Describe(e.Type)} lies {days} to the {dir}. {who} landed there once.");
            string water = e.Water
                ? One(" There's fresh water there, he said.", " Found a pool of sweet water, too.", " Filled his casks there.")
                : One(" Not a drop of fresh water on it, mind.", " Bring your own water, he warned.", " Nothing to drink there but rain.");
            return text + water;
        }

        private static string Seen(Entry e, string dir, string days)
        {
            string shape = e.Type == IslandType.SeaStacks || e.Type == IslandType.RockyIslet || e.Type == IslandType.KarstTowers
                ? One("a cluster of rocks", "rocks standing out of the sea", "jagged rocks")
                : e.Height >= 45f ? One("a tall island", "a high island, mountains on it", "a big peak rising from the sea")
                : e.Height <= 10f ? One("a low strip of land, barely above the waves", "something flat and pale, like sand")
                : One("an island", "a small island", "land where there shouldn't be any");
            return One(
                $"A crew coming in spoke of {shape} on the horizon, {days} to the {dir}.",
                $"Sailors say they sighted {shape} {days} to the {dir}. Nobody went closer.",
                $"Ever heard of {shape} out to the {dir}? {Capital(days)}, they reckon. Odd, it's on no map.");
        }

        private static string Lost(Entry e, string dir)
        {
            string boat = e.Boat == "merchant ship" ? "a merchant ship" : "a " + e.Boat;
            string text = One(
                $"Some time ago {boat} sailed off to the {dir} and was never heard from again.",
                $"{Capital(boat)} left here heading {dir}. Never came back.",
                $"I knew the crew of {boat} that went out to the {dir}. Still waiting for them.");
            if (e.Treasure != 0)
            {
                string what = e.Treasure == 226 ? "a hold full of silver" : e.Treasure == 22 ? "gold" : "a chest of gemstones";
                text += One($" They say she carried {what}.", $" Word is she had {what} aboard.", $" With {what} on board, too.");
            }
            return text;
        }

        private static string Describe(IslandType t)
        {
            switch (t)
            {
                case IslandType.Atoll: return One("a coral atoll with a lagoon inside", "a ring of coral and palms");
                case IslandType.Sandbar: return One("a bare white sandbar", "a sandbank with hardly anything on it");
                case IslandType.CrescentCove: return One("a crescent-shaped island with a quiet cove", "an island curled round a sheltered bay");
                case IslandType.MesaPlateau: return One("a flat-topped island walled by cliffs", "a table of rock with cliffs all round");
                case IslandType.OasisCrater: return One("an island with a green hollow in its middle", "an old crater, green inside");
                case IslandType.TwinPeaks: return One("an island with two peaks", "a twin-peaked island");
                case IslandType.VolcanicBay: return One("an old volcano with a bay in its side", "a burnt-out mountain with a bay");
                case IslandType.SeaStacks: return One("a cluster of rock pillars", "stone towers standing in the sea");
                case IslandType.SunkenRidge: return One("a long, low ridge of an island", "a narrow island like a sunken wall");
                case IslandType.RockyIslet: return One("a little rocky islet", "a rock of an island");
                case IslandType.KarstTowers: return "tall limestone towers";
                default: return "an uncharted island";
            }
        }

        private static readonly string[] Points =
        {
            "north", "north-northeast", "northeast", "east-northeast", "east", "east-southeast", "southeast", "south-southeast",
            "south", "south-southwest", "southwest", "west-southwest", "west", "west-northwest", "northwest", "north-northwest"
        };

        // real XZ: +z is north, +x is east
        private static string Compass(Vector2 d)
        {
            float a = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
            if (a < 0f) a += 360f;
            return Points[Mathf.RoundToInt(a / 22.5f) % 16];
        }

        private static string Days(float days) =>
            days < 0.6f ? "half a day's sail" : days < 1.4f ? "a day's sail" : days < 2.5f ? "two days' sail" : $"{Mathf.RoundToInt(days)} days' sail";

        private static string Capital(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s.Substring(1);

        // the game's speech bubble: ~33 characters a line
        internal static string Wrap(string v, int size = 33)
        {
            var lines = new List<string>();
            var line = "";
            foreach (var w in v.Split(' '))
            {
                if (line.Length > 0 && line.Length + 1 + w.Length > size) { lines.Add(line); line = w; }
                else line = line.Length == 0 ? w : line + " " + w;
            }
            if (line.Length > 0) lines.Add(line);
            return string.Join("\n", lines.ToArray());
        }
    }

    /// <summary>The drink is accepted exactly as the game does it; only the words are ours, now and then.</summary>
    [HarmonyPatch(typeof(TavernRumorsDude), "ClickDrinkButton")]
    internal static class TavernRumorPatch
    {
        private static readonly AccessTools.FieldRef<TavernRumorsDude, ShipItemBottle> Drink =
            AccessTools.FieldRefAccess<TavernRumorsDude, ShipItemBottle>("currentDrink");

        private static bool Prefix(TavernRumorsDude __instance)
        {
            if (!Plugin.RumorsEnabled.Value || Random.value >= Plugin.RumorChance.Value) return true;
            var drink = Drink(__instance);
            if (!drink || drink.health < drink.GetCapacity() || !drink.sold) return true;
            if (__instance.portIndex < 0 || __instance.portIndex >= Port.ports.Length) return true;
            string rumour = Rumors.For(Port.ports[__instance.portIndex]);
            if (rumour == null) return true;

            UISoundPlayer.instance.PlayUISound(UISounds.smallPop, 0.75f, 1f);
            Juicebox.juice.TweenScale(__instance.speechUI, Vector3.one * 0.9f, 0.1f, JuiceboxTween.bounceInOut, returnToOrigin: true);
            __instance.text.text = Rumors.Wrap(rumour);
            Plugin.Debug("Rumour at " + Port.ports[__instance.portIndex].GetPortName() + ": " + rumour);
            drink.DestroyItem();
            Drink(__instance) = null;
            __instance.drinkButton.SetActive(false);
            return false;
        }
    }
}
