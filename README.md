# Terra Incognita

<img width="2559" height="1439" alt="Screenshot_53" src="https://github.com/user-attachments/assets/4faa6957-01b6-43b8-9b3a-e5934f1e4621" />

*A Sailwind mod: uncharted islands to find.*

The world gets dozens of small uninhabited islands that are on no chart. Some lie inside the archipelagos, some
beside the main trade routes. All of them stand beyond the horizon as seen from any port, so you only find them by
sailing out and looking.

## What you'll find

<img width="2559" height="1439" alt="Screenshot_57" src="https://github.com/user-attachments/assets/dbd33af5-9511-4971-89c8-40f55e733156" />

**New islands of many kinds.** Coral atolls with a lagoon, white sandbars, volcanoes with a crater lake or a thread
of smoke, rocky islets, flat-topped mesas walled by layered cliffs, sea stacks, twin peaks, crescent coves, an oasis
in an old crater and sunken ridges. Each island takes on the look of its region: its
trees, bushes, grass and rock colour match the nearest vanilla islands.

**Rock formations.** Sea stacks, arches, spires, hoodoos, boulder fields, lava flows running into the sea, and sea
caves to explore.

**Shipwrecks.** Boats that ran aground on the new islands, with their masts snapped and torn sails hanging. Their
cargo and the crew's things are still lying around. Now and then the survivors' camp is not far away.

<img width="2556" height="1436" alt="Screenshot_52" src="https://github.com/user-attachments/assets/57af2a1b-d73f-4cd0-87f1-95b2d9f8c845" />

**Hidden caves.** Some caves hold something someone wanted to keep out of sight.

<img width="2555" height="1422" alt="Screenshot_56" src="https://github.com/user-attachments/assets/0437ffa3-ab37-491e-81f1-947676217fc6" />

**Fresh water.** Springs, rain ponds, crater lakes and an oasis where you can fill your bottles. Ponds slowly refill
after you use them; the oasis and volcanic lake never runs dry.

**Food.** Coconuts under palms, mushrooms in the shade, oranges and apples under the trees. What you pick grows
back after a few days.

**Seagulls** over the islands, as on the vanilla ones.

**Tavern rumours.** Buy the rumour man a drink and he may tell you of an island: roughly which way and how many
days' sail.

<img width="1630" height="1610" alt="image" src="https://github.com/user-attachments/assets/2600aaf2-b1d6-45cd-8eb0-8116f1131a0a" />

## Installing

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx) for Sailwind.
2. Put the `TerraIncognita` folder (with `TerraIncognita.dll` and `terra_incognita`) into
   `Sailwind\BepInEx\plugins\`.
3. Start the game. The islands are built when you start a new game or load a save (a few seconds).

## Settings

`BepInEx\config\com.terraincognita.cfg` (created on the first start):

- **Every new game gets its own island layout.** It is kept in the save, so a loaded game always finds its
  islands where they were. **RandomSeedPerGame** off: every new game uses **WorldSeed** instead. WorldSeed is
  also the layout for saves made before per-game layouts (if you played an earlier version, leave it as it is).
- **ScatterPerRegion**, **RouteIslandCount**: how many islands in each archipelago and along the trade routes.
- Switches for wrecks, wreck loot, camps, forage, seagulls, ponds and rumours, and the wreck and cave chances.

## Good to know

- Loot from wrecks and caves is there once per game: what you take is gone for good.
- Removing the mod removes the islands. Anything you left on them ends up in the sea.
- Other island mods: the new islands keep clear of every island and port that exists when the world loads.
