using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace TerraIncognita
{
    /// <summary>
    /// Drinking water from a pond: click the water with a bottle / bucket / barrel in hand to fill it (fresh water,
    /// liquid 1), like bailing the bilge (BoatDamageWaterButton). Stone basins hold 30–60 units, ponds 90–120, the
    /// oasis is endless. A used pond fills back day by day (full again from empty in 5–7 days); the surface sinks with what is left (the round
    /// water disc slides down into its bowl, so it also shrinks), and an empty pond shows no water.
    ///
    /// State per pond (units, day of the next refill) lives in GameState.modData — the game writes that dictionary
    /// into the save file, so every save slot keeps its own ponds.
    /// </summary>
    internal class WaterSource : GoPointerButton
    {
        public string id;
        public float capacity;              // 0 = endless (oasis)
        public Transform surface;          // the visible water (moves); this object (the click target) stays put
        public float fullY, emptyY;         // surface local height when full / empty
        private float stock;
        private int lastDay;                // last day the refill was counted
        private bool loaded;
        private float fillCooldown;
        private Collider col;
        private bool hasWater;

        internal static readonly List<WaterSource> All = new List<WaterSource>();
        private const string KeyPrefix = "TerraIncognita.water2.";   // v2: "stock;lastDay" (v1 refilled all at once)
        private const float WaterLiquid = 1f;

        public override void Start()
        {
            base.Start();
            col = GetComponent<Collider>();
            All.Add(this);
            Load();
        }

        private void OnDestroy() => All.Remove(this);

        public void Load()
        {
            loaded = true;
            if (capacity <= 0f) { stock = 1f; Apply(); return; }
            if (GameState.modData != null && GameState.modData.TryGetValue(KeyPrefix + id, out var s))
            {
                var p = s.Split(';');
                if (p.Length == 2 && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float st)
                    && int.TryParse(p[1], out int ld))
                { stock = Mathf.Clamp(st, 0f, capacity); lastDay = ld; Refill(); Apply(); return; }
            }
            stock = capacity;                                   // first visit: full
            lastDay = GameState.day;
            Save();
            Apply();
        }

        private int RefillDays() => 5 + new System.Random(id.GetHashCode()).Next(0, 3);   // 5–7 from empty to full

        // rain and the spring: every new day adds capacity / RefillDays (counted once per day passed)
        private bool Refill()
        {
            int d = GameState.day - lastDay;
            if (d <= 0) return false;
            lastDay = GameState.day;
            if (stock >= capacity) { Save(); return false; }
            stock = Mathf.Min(capacity, stock + capacity * d / RefillDays());
            Save();
            return true;
        }

        private void Save()
        {
            if (capacity <= 0f || GameState.modData == null) return;
            GameState.modData[KeyPrefix + id] = stock.ToString("0.##", CultureInfo.InvariantCulture) + ";" + lastDay;
        }

        private void Apply()
        {
            float k = capacity <= 0f ? 1f : stock / capacity;
            hasWater = capacity <= 0f || stock >= 0.5f;
            if (surface)
            {
                var lp = surface.localPosition;
                surface.localPosition = new Vector3(lp.x, Mathf.Lerp(emptyY, fullY, k), lp.z);
                surface.gameObject.SetActive(hasWater);
            }
            lookText = capacity <= 0f ? "fresh water" : $"fresh water ({Mathf.FloorToInt(stock)})";
        }

        public override void ExtraLateUpdate()
        {
            if (fillCooldown > 0f) fillCooldown -= Time.deltaTime;
            // clickable only with a bottle in hand: otherwise the trigger hid items lying in the pond from the pointer
            if (col) col.enabled = hasWater && PlayerHoldsBottle();
            if (!loaded || capacity <= 0f) return;
            if (Refill()) Apply();                               // rain / the spring, a little every day
        }

        private static GoPointer[] pointers;
        private static int checkedFrame = -1;
        private static bool holdsBottle;

        /// <summary>Does any player pointer (mouse crosshair / VR hands) hold a bottle? Cached per frame.</summary>
        private static bool PlayerHoldsBottle()
        {
            if (Time.frameCount == checkedFrame) return holdsBottle;
            checkedFrame = Time.frameCount;
            if (pointers == null || pointers.Length == 0 || System.Array.Exists(pointers, p => !p))
                pointers = Object.FindObjectsOfType<GoPointer>();
            holdsBottle = false;
            foreach (var p in pointers) if (p && p.GetHeldItem() is ShipItemBottle) { holdsBottle = true; break; }
            return holdsBottle;
        }

        public override bool OnItemClick(PickupableItem heldItem)
        {
            if (fillCooldown > 0f || !(heldItem is ShipItemBottle bottle)) return false;
            if (bottle.amount != WaterLiquid && bottle.health > 0f) return false;     // holds something else
            float room = bottle.GetRemainingCapacity();
            if (room <= 0f) return false;
            float portion = Mathf.Min(room, bottle.GetCapacity() <= 10f ? 5f : 15f); // a scoop / a pail at a time
            if (capacity > 0f) portion = Mathf.Min(portion, Mathf.Floor(stock));
            if (portion <= 0f) return false;

            bottle.FillBottle(WaterLiquid, portion);             // sound, look text and mass handled by the game
            if (capacity > 0f) { stock -= portion; Save(); Apply(); }
            fillCooldown = 0.66f;
            return false;
        }
    }


    /// <summary>After a save is loaded, ponds read their state from it again (they may have spawned before the load).</summary>
    [HarmonyPatch(typeof(SaveLoadManager), "LoadModData")]
    internal static class WaterSourceLoadPatch
    {
        private static void Postfix()
        {
            foreach (var w in WaterSource.All) if (w) w.Load();
        }
    }
}
