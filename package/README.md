# Weathering

Let the land heal itself.

In vanilla Valheim every terrain edit is permanent: a pit dug for copper, a patch of hoed dirt or an
abandoned field stays exactly as you left it, forever. Weathering lets the ground outside your bases
recover on its own, the way you would expect nature to.

> **Warning:** existing earthworks outside bases will weather too, starting 3 in-game days after you
> install the mod. Removing the mod does not bring them back.

## What heals

- **Dirt and cultivated ground** fade back to natural ground, so grass returns.
- **Dug holes and raised mounds** slump over time: a steep pit widens into a shallow, soft-edged
  ditch, a raised wall into a mound. The earth is moved, not removed, and the ground never returns
  to its original shape.
- Every spot has its own timer. Nothing changes until it was last edited 3 in-game days ago, and
  digging next to an old pit does not freeze the old pit. Sleeping counts as time passing.

## What never heals

- **Player bases:** anything inside the area of a workbench, campfire, hearth or other station,
  plus a margin, and around Warden stones (even switched-off ones).
- **Paved roads:** paved ground keeps its surface and height.
- **Ground near your buildings:** a margin around every player-built piece, so an outpost portal or
  a lone bridge does not end up floating or buried.
- Terrain shaped by the world itself (locations, dungeons) and deep snow.

## Configuration

`BepInEx/config/elridion.weathering.cfg`. Everything is under `General`, server-synced and admin-only.

| Key | Default | Description |
| --- | --- | --- |
| `DelayDays` | 3 | In-game days after a spot was last edited before it starts to heal (0.1-30) |
| `FadeDays` | 2 | In-game days dirt and cultivated paint take to fade fully (0.1-30) |
| `ErosionRate` | 1 | Relative strength of height erosion; 0 turns it off (0-5) |
| `BaseMargin` | 10 | Extra protected distance in meters around every player base area (0-50) |
| `PieceMargin` | 4 | Protected distance in meters around any player-built piece (0-20) |
| `ExtraBasePieces` | (empty) | Extra prefabs that count as a base, as `prefab:radius` separated by commas |

## Requirements

- BepInEx for Valheim
- [Jotunn](https://thunderstore.io/c/valheim/p/ValheimModding/Jotunn/)

Every client and the server need the mod installed, at the same minor version.

## Credits

- Mod icon: "Hills" by Delapouite from [game-icons.net](https://game-icons.net), licensed under
  [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/). Recoloured and outlined.
