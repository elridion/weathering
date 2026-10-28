# Changelog

## 0.2.0

- Overnight healing: while everyone sleeps, spots past their delay near the sleeping players heal
  and felled trees there regrow, so you wake up to the change. A zone still heals at most once a day.
- Each healing pass is spread over several frames, so big catch-ups (such as after a long sleep or
  a long absence) run more smoothly.
- Earthworks from before the mod was installed now heal everywhere, not only in zones that were
  edited again after installing.
- The area around the world spawn now heals and regrows on dedicated servers too.
- Pits and mounds across the corner where four zones meet now heal like any other instead of
  staying at full depth there.
- No more brief step along a zone border after the zone next to it healed.
- Ground the cultivator turned back to grass no longer snaps back to its old look after the delay
  (most visible in the Mistlands and Ashlands).
- Regrown saplings sit on the ground as it is now, even if it was dug or raised since the tree fell.
  A spot dug down into water gets no sapling.
- Trees felled inside player bases no longer leave a regrowth spot behind.
- Players on 0.1.x cannot play together with 0.2.0.

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
- `weathering_clear_sites` removes every felled-tree regrowth spot; run it on the server or host
  before removing the mod.
