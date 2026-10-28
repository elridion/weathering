# Weathering

Let the land heal itself.

In vanilla Valheim every terrain edit is permanent: a pit dug for copper, a patch of hoed dirt or an
abandoned field stays exactly as you left it, forever. Weathering lets the ground outside your bases
recover on its own, the way you would expect nature to.

> **Warning:** existing earthworks outside bases will weather too, starting 3 in-game days (the
> default) after you install the mod. Removing the mod does not bring them back.

## What heals

- **Dirt and cultivated ground** fade back to natural ground, so grass returns.
- **Dug holes and raised mounds** slump over time: a steep pit widens into a shallow, soft-edged
  ditch, a raised wall into a mound. The earth is moved, not removed, and the ground never returns
  to its original shape.
- Every spot has its own timer. Nothing changes until it was last edited 3 in-game days ago (the
  default), and digging next to an old pit does not freeze the old pit. Sleeping counts as time
  passing.
- **Felled trees** regrow: 3 in-game days (the default) after a tree was felled, its sapling appears
  where it stood and grows like one you planted. A stump left standing keeps its tree from
  regrowing until the stump is removed. Trees with no sapling (such as autumn birches) do not
  regrow, and rocks and ore never come back.
- Healing happens on the server (or the host) while no player is nearby, so you never watch the
  ground move or a sapling pop up: you find it weathered when you come back. "Nearby" is counted in
  zones (64 m squares): a zone heals only while every player is more than the game's simulation
  distance plus one zone away in every direction. With the default simulation distance that means
  at least 6 zones (about 400 m) away, up to 9 zones (about 600 m) at the highest setting.
- **Overnight:** while everyone sleeps, spots near the sleeping players that are past their delay
  heal too, and felled trees there regrow, as of the coming morning; you wake up to the change. Your
  base and everything else that never heals stays protected. A zone still heals at most once per
  in-game day, catching up only the time since it last healed, so a single night does not wash an
  area out.

## Console commands

On the server or host (in single player, your own game). Handy for testing:

- `weathering_age <days>` (needs `devcommands`): makes terrain edits and felled trees within 3
  zones of you that many in-game days older. Walk away to let them heal and regrow; the command
  says how many zones away everyone must be.
- `weathering_status`: how many zones wait to heal and felled trees wait to regrow, and whether
  the zone you stand in is waiting, when it last healed and whether it can heal right now (with
  how many zones away the nearest player is and how many it needs), and whether everyone is asleep
  so that zones near players heal overnight.
- `weathering_clear_sites`: removes every spot where a felled tree waits to regrow. Run it before
  removing the mod (see below).

## Removing the mod

First run `weathering_clear_sites` on the server or host. Felled trees remember their spot with an
invisible marker; without the mod, players on a dedicated server get a "Missing prefab hash" log
line for every marker near them, every frame. Healed terrain stays as it is and its data stays
valid for vanilla Valheim, but nothing heals any more.

## What never heals

These areas neither heal nor regrow trees:

- **Player bases:** anything inside the area of a workbench, campfire, hearth or other station,
  plus a margin, and around Warden stones (even switched-off ones).
- **Paved roads:** paved ground keeps its surface and height.
- **Ground near your buildings:** a margin around every player-built piece, so an outpost portal or
  a lone bridge does not end up floating or buried.
- Ground near fires and stations the world generated with a player-base area (some location
  campfires, for example) never heals either.
- Terrain shaped by the world itself (locations, dungeons) and deep snow.

## Configuration

`BepInEx/config/elridion.weathering.cfg`. Everything is under `General`, server-synced and admin-only.

| Key | Default | Description |
| --- | --- | --- |
| `DelayDays` | 3 | In-game days after a spot was last edited before it starts to heal, and after a tree was felled before its sapling appears (0.1-30) |
| `FadeDays` | 2 | In-game days dirt and cultivated paint take to fade fully (0.1-30) |
| `ErosionRate` | 1 | Relative strength of height erosion: 1 is the default look, 0 turns height erosion off, 2 is twice as fast (0-5) |
| `BaseMargin` | 10 | Extra protected distance in meters around every player base area (0-50) |
| `PieceMargin` | 4 | Protected distance in meters around any player-built piece (0-20) |
| `ExtraBasePieces` | (empty) | Extra prefabs that count as a base, as a comma-separated list of `prefab:radius` in meters, for example `piece_example:30` (radius above 0, up to 500). Use it for modded pieces that vanilla does not treat as a base |

## Requirements

- BepInEx for Valheim
- [Jotunn](https://thunderstore.io/c/valheim/p/ValheimModding/Jotunn/)

Every client and the server need the mod installed, at the same minor version.

## Credits

- Mod icon: "Hills" by Delapouite from [game-icons.net](https://game-icons.net), licensed under
  [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/). Recoloured and outlined.
