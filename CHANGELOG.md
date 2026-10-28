# Changelog

## 0.1.0

- First release: dirt and cultivated ground outside player bases fades back to grass, and dug pits
  and raised mounds slump into soft ditches and dips, 3 in-game days (the default) after the spot was last edited.
- Paved roads, player bases, ground near your buildings and Warden stones never weather.
- Existing earthworks outside bases weather too; removing the mod does not bring them back.
- Felled trees regrow: 3 in-game days (the default) after felling, the tree's sapling appears where it stood,
  once its stump is gone. Never inside player bases, near Warden stones or near your buildings.
  Trees with no sapling (such as autumn birches) do not regrow.
- Ground heals on the server (or host) while no player is nearby, so you find it weathered when
  you come back. Test commands for the server or host: `weathering_age` and `weathering_status`.
- Overnight healing: while everyone sleeps, spots past their delay near the sleeping players heal
  and felled trees there regrow, so you wake up to the change. A zone still heals at most once a day.
- `weathering_clear_sites` removes every felled-tree regrowth spot; run it on the server or host
  before removing the mod.
