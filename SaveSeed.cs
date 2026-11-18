using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using HarmonyLib;

namespace TerraIncognita
{
    /// <summary>
    /// One island layout per game: a new game rolls a seed, the save keeps it (GameState.modData, which the game
    /// writes into the save file), loading reads it back first. The islands are built in between the menu click and
    /// the game's own start / load. A save without it (made before this) uses [Generation] WorldSeed.
    /// </summary>
    internal static class SaveSeed
    {
        private static bool passThrough;

        private static void Lock(StartMenu menu, int d)
        {
            var f = Traverse.Create(menu).Field("animsPlaying");
            f.SetValue(f.GetValue<int>() + d);              // the menu's buttons do nothing while it is > 0
        }

        internal static bool Run(StartMenu menu, int seed, System.Action original)
        {
            if (passThrough || Plugin.Spawned) { Plugin.Seed = Plugin.Seed != 0 ? Plugin.Seed : seed; return true; }
            if (Plugin.Spawning) return false;
            Lock(menu, 1);
            Plugin.Instance.SpawnThen(seed, () =>
            {
                Lock(menu, -1);
                passThrough = true;
                try { original(); }
                finally { passThrough = false; }
            });
            return false;
        }

        /// <summary>The seed stored in the save about to load; 0 if none (or unreadable).</summary>
        internal static int FromSave(int backupIndex)
        {
            try
            {
                string path = backupIndex != 0 ? SaveSlots.GetBackupPath(SaveSlots.currentSlot, backupIndex) : SaveSlots.GetCurrentSavePath();
                SaveContainer save;
                using (var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    save = (SaveContainer)new BinaryFormatter().Deserialize(fs);
                if (save.modData != null && save.modData.TryGetValue(Plugin.SeedKey, out var v) && int.TryParse(v, out int s) && s != 0)
                    return s;
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("Reading the island seed from the save: " + e.Message); }
            return 0;
        }
    }

    [HarmonyPatch(typeof(StartMenu), "StartNewGame")]
    internal static class NewGameSeedPatch
    {
        private static bool Prefix(StartMenu __instance)
        {
            int seed = Plugin.Spawned ? Plugin.Seed : Plugin.NewGameSeed();
            var menu = __instance;
            return SaveSeed.Run(menu, seed, () => AccessTools.Method(typeof(StartMenu), "StartNewGame").Invoke(menu, null));
        }

        // the new game's save carries its seed from the first save on
        private static void Postfix()
        {
            if (GameState.modData != null && Plugin.Seed != 0) GameState.modData[Plugin.SeedKey] = Plugin.Seed.ToString();
        }
    }

    [HarmonyPatch(typeof(StartMenu), "LoadGame")]
    internal static class LoadGameSeedPatch
    {
        private static bool Prefix(StartMenu __instance, int backupIndex)
        {
            int seed = Plugin.Spawned ? Plugin.Seed : SaveSeed.FromSave(backupIndex);
            if (seed == 0)
            {
                seed = Plugin.WorldSeed.Value != 0 ? Plugin.WorldSeed.Value : Plugin.NewGameSeed();
                Plugin.Log.LogInfo($"This save has no island seed (made before per-game seeds): using {seed}");
            }
            var menu = __instance;
            return SaveSeed.Run(menu, seed, () => AccessTools.Method(typeof(StartMenu), "LoadGame").Invoke(menu, new object[] { backupIndex }));
        }
    }

    // loading replaces GameState.modData with the save's: put the seed back (an old save gets it from now on)
    [HarmonyPatch(typeof(SaveLoadManager), "LoadGame")]
    internal static class LoadModDataSeedPatch
    {
        private static void Postfix()
        {
            if (GameState.modData != null && Plugin.Seed != 0) GameState.modData[Plugin.SeedKey] = Plugin.Seed.ToString();
        }
    }
}
