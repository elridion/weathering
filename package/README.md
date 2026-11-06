# Weathering

The land outside your bases heals over time. Dug pits slump, grass grows back over dirt and fields, and felled trees return as saplings.

## Features

- Dirt and cultivated ground turn back into grass, which spreads in from the edges with a soft,
  ragged front.
- Dug holes and raised mounds slowly slump into shallow, soft-edged ditches and mounds. The earth
  is moved, not removed, so the ground never returns to its original shape.
- Felled trees regrow: their sapling appears where they stood and always grows. A stump left
  standing holds the tree back until you remove it. Rocks and ore never come back.
- Each spot starts to heal 3 in-game days (the default) after it was last touched. Sleeping
  counts as time passing.
- Healing happens while no player is nearby (about 400 m away at the default simulation distance), so you never watch the
  ground move. While everyone sleeps, spots near the sleepers heal too, and you wake up to the
  change.

## What never heals

- Player bases: the area of any workbench, campfire, hearth or other station, plus a margin, and
  around Warden stones (even switched-off ones).
- Paved roads, and the ground around every player-built piece, so a lone portal or bridge is not
  left floating or buried.
- Terrain shaped by the world itself (locations, dungeons), and ground the cultivator turned back
  to grass.

## Installation

- Needs BepInEx and [Jotunn](https://thunderstore.io/c/valheim/p/ValheimModding/Jotunn/).
- Healing runs on the server (or the host). The server and every player need the mod, at the same
  minor version.
- Existing earthworks outside bases weather too, starting 3 in-game days after you install the
  mod.
- Before uninstalling, run `weathering_clear_sites` on the server or host: it removes the
  invisible markers that felled trees leave while they wait to regrow, which would otherwise
  spam "Missing prefab hash" log lines. Healed terrain stays as it is and is valid for vanilla
  Valheim. A sapling the mod planted becomes an ordinary sapling.
- Console commands on the server or host: `weathering_status` shows what is waiting to heal, and
  `weathering_age <days>` (needs `devcommands`) ages nearby edits and felled trees for testing.

## Configuration

Settings are in `BepInEx/config/elridion.weathering.cfg`, under `General`. They are synced from
the server and only admins can change them.

| Key | Default | Description |
| --- | --- | --- |
| `DelayDays` | 3 | In-game days before an edited spot starts to heal and a felled tree's sapling appears (0.1-30). |
| `GrassSpreadMeters` | 2 | Meters per in-game day that grass spreads back into dirt and cultivated ground (0.25-16). |
| `ErosionRate` | 1 | Strength of height erosion: 0 turns it off, 2 is twice as fast (0-5). |
| `BaseMargin` | 10 | Extra protected meters around every player base area (0-50). |
| `PieceMargin` | 4 | Protected meters around any player-built piece (0-20). |
| `ExtraBasePieces` | (empty) | Modded pieces that count as a base, as `prefab:radius` in meters, comma-separated, for example `piece_example:30` (radius up to 500). |

## Credits

- The icon is "Hills" by Delapouite from [game-icons.net](https://game-icons.net), licensed under
  [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/). Recoloured and outlined.
