# Weathering: design

Working name `Weathering` (the folder, plugin and package name are the builder's call; see the end).
Status: design, not built. Written 2026-10-28.

## Intent

In vanilla Valheim every terrain edit is permanent: a pit dug for copper in the Black Forest, a
patch of hoed dirt, an abandoned field stay exactly as they were left, forever. The world slowly
fills with scars far from any base. This mod lets the land heal on its own outside player bases,
the way the player expects nature to: churned and farmed ground goes back to grass, and dug holes
and raised mounds slump into soft ditches and dips. It does not undo the player's work wholesale;
the shape of what was done stays readable, it just weathers.

Prior art: RustyMods' Regenerative Terrain (deprecated on Thunderstore) reverts terrain toward its
original state, regrows trees and rocks, and protects ground near crafting stations. This mod keeps
the "heals over time, except at your base" idea, but never resets height to the original, uses the
vanilla player base areas, and leaves vegetation for later.

## Goals

- **Grass grows back.** Dirt and cultivated paint on the ground turns back into natural ground from
  its edges inward, so grass returns (grass is drawn from the paint by the clutter system; nothing
  else is needed for it).
- **Paved stays.** Paved paint never fades. Paved roads keep their surface and their height.
- **Height erodes, never resets.** Height edits (`m_levelDelta` / `m_smoothDelta` offsets from the
  generated terrain) spread out over time, like material sliding down slopes: a steep pit widens
  into a shallow, soft-edged ditch, a raised wall into a mound. The earth is moved, not removed.
  The terrain does not return to its generated shape.
- **Delay per spot.** Nothing about an edited spot changes until it is 3 in-game days old. Each
  vertex remembers when it was last edited; editing near an old pit does not freeze the old pit.
- **Player bases are untouched.** Anything inside a vanilla `PlayerBase` effect area (workbench,
  campfire, hearth and other stations; exact list comes from the prefabs) plus a margin never
  heals, and neither does anything near a Warden stone.
- **No noticeable performance cost.** Zones carry a "needs healing" flag; only flagged zones are
  ever examined, plus each zone once when the mod first sees it. Healing runs on the server for
  idle zones, at most one pass per zone per in-game
  day, each spot catching up only the time since its own delay ran out (see When healing runs).

## Non-goals (v1)

- Rocks and ore regrowing. Parked in `ideas.md`.
- Restoring height to the original generated terrain.
- Healing world-generation terrain changes (locations, dungeons, vanilla `TerrainModifier`s). Only
  player edits stored in `TerrainComp` are touched.
- Deep snow paint (`PaintType.DeepSnow`, `TerrainComp.SetSnowMask`): left alone.

## Anti-goals (failure even if it "works")

- Ground inside or at the edge of a base changes. A garden fading, a foundation's levelled ground
  sinking, a moat filling in at home: any of that is a failure.
- A paved road fades or sags.
- Visible seams or steps at zone borders after erosion.
- Hitches when zones load or while standing in an eroding area, or network spam in multiplayer.
- A pit "healing" while the player watches it in the first 3 days, or snapping in one big jump.
- Corrupting or making vanilla-incompatible `TerrainComp` data: removing the mod must leave a
  valid world in which the current (weathered) terrain simply stays.

## Decisions made with the user

- Based on the Regenerative Terrain idea, minus height resets.
- Erosion: "a hole smooths out over time into a ditch". Implemented as diffusion of the height
  offset field (see below), which conserves volume and keeps the generated slope underneath.
- Base protection uses the vanilla PlayerBase areas.
- Paved roads stay; only dirt and cultivated fade.
- Per-spot timing (each vertex its own 3 days), plus a per-zone flag set when edits happen and
  cleared when nothing in the zone is left to heal.
- Tree regrowth is part of v1 (see Tree regrowth); rocks and ore stay parked in `ideas.md`.
- Warden saves each stone's radius on its ZDO for Weathering to read; switched-off stones still
  protect.
- Edits made before install heal too, `DelayDays` after install; no setting.

## How it works

### Terrain data (verified against `assembly_valheim.dll`)

One `TerrainComp` per zone (64 m) with a `ZNetView`; its ZDO stores the compressed edit data.
Per-vertex arrays: `m_modifiedHeight` (bool), `m_levelDelta`, `m_smoothDelta` (float),
`m_modifiedPaint` (bool), `m_paintMask` (Color). Grid width `m_width` (expected 64, so 65 x 65
vertices; verify). Edits arrive through `ApplyOperation` / `RPC_ApplyOperation` / `DoOperation` /
`InternalDoOperation`, persisted by `Save`, applied to the mesh by `ApplyToHeightmap`. It has a
neighbour grid (`m_neighborGrid`, `TryGetNeighbor`, `m_neighborHeights`) the builder should read
before designing border handling. `TerrainModifier.PaintType`: Dirt, Cultivate, Paved, Reset,
ClearVegetation, DeepSnow. Paint channel meaning (expected r = dirt, g = cultivated, b = paved)
must be verified in `PaintCleared` / `UpdatePaintMask` before coding the fade.

### Recording edits

Hook the edit path on the zone owner (`InternalDoOperation` or `DoOperation`; pick the one that
runs exactly once per edit on the authoritative side). Snapshot the arrays before, diff after, and
stamp every vertex whose height or paint changed with the current world time
(`ZNet.instance.GetTimeSeconds()`). Diffing is robust to whatever the operation did internally.
Set the zone's "needs healing" flag.

Store the stamps and flag on the `TerrainComp`'s ZDO under the mod's own keys (prefix with the
mod name). Stamps: one value per vertex, quantised (for example whole in-game hours as `ushort`
since a world epoch, or similar) and compressed; zero means "never edited or done". Keep the size
small: 4,225 vertices.

**Edits made before the mod was installed** have no stamp. Decided with the user: they heal like
new edits. They are stamped with the time the zone is first seen by the mod, so they start healing
`DelayDays` after install. No setting. The store page must warn that existing earthworks outside
bases will weather and that removing the mod does not bring them back.

### When healing runs

Revised during implementation (it first said "on the zone owner, when a zone loads"): a client only
holds the ZDOs synced near it, so it could miss a base or Warden stone up to 180 m away, and writing
a zone while a client has it loaded races with that client's own `TerrainComp.Save`, which re-saves
the whole array. Healing therefore runs on the server only, on the saved terrain data of zones
nobody is near, without instantiating anything.

- Only on the server (`ZNet.IsServer()`, so a dedicated server or the host), once the base index is
  built.
- The server lists the terrain compiler ZDOs incrementally (`GetAllZDOsWithPrefabIterative`, a slice
  of sectors per frame; a new sweep starts at most every 10 s) and heals one zone at a time, the
  next pass starting at least 0.5 s real time after the previous one ended.
- A pass is spread over frames so big catch-ups do not stutter: one frame decodes and builds the
  masks and grows grass back, erosion runs 24 iterations per frame, and a last frame writes. Nothing is
  written before that frame. The pass is dropped unwritten (the zone stays flagged and is retried)
  if the zone stops being idle, the world changes, or at the write the data revision of the zone or
  of a linked neighbour changed since it was read, or a linked neighbour is no longer idle. A zone is a candidate when its flag is set, it is idle, and its last pass is at
  least one in-game day old. A zone the mod has never seen (no last-pass time) is a candidate
  without the flag, so earthworks from before install heal even where nobody edits again. The first
  time a zone is seen it is only stamped (edits from before the mod start their delay), its last-pass
  time set, and its flag set only if anything in it can heal; healing starts a day later. A zone
  whose data cannot be read gets a last-pass time too, so it is not decoded again every round.
- Idle: the zone's Chebyshev zone distance from every peer's reference position, and from the
  host's own when the server has a local player, is above Near + Far + 1 of the largest simulation
  distance in use (8 zones with the game's maximum, Near 5 + Far 2). No peer can have it loaded,
  so nothing re-saves over the result. A dedicated server's own reference position stays at the
  world origin and is ignored; the zones it keeps loaded there count as idle (no player is there to
  edit them), and after writing such a zone the server makes its loaded `TerrainComp` reload at once
  (`CheckLoad`). A host's own reference position always counts.
- With `ErosionRate` 0, steep slopes alone do not keep a zone flagged.
- A pass decodes the saved `TCData`, heals it (catch-up per spot: grass spreads into each vertex
  for the days since its own delay ran out, at most the time since the zone's last pass; erosion runs for the
  most such days among the height edits past their delay, frozen ones included since their edges
  still erode the ground next to them, capped at 240 iterations), and
  writes it back in vanilla's layout after claiming the ZDO for the server, like vanilla server code
  does. Ownership is left to vanilla's release loop. A zone whose data cannot be read logs one
  warning and loses its flag.
- Time is world time, so sleeping counts as passing days. One in-game day =
  `EnvMan.instance.m_dayLengthSec` (`long`, default 1,200 s; verified).
- **Overnight healing** (decided with the user). While everyone sleeps the server fast-forwards
  time to the next morning over about 12 s (`EnvMan.SkipToMorning` sets the private `m_skipTime` and
  `m_skipToTime`; `UpdateTimeSkip` advances the net time; public `IsTimeSkipping()`). During that
  skip (sleep mode, server only) zones near players heal too: a zone need not be idle, and "now"
  for the pass is the skip target, so the night counts. Pending flag, per-spot delay, protected
  circles, Deep North, frozen mask and erosion are unchanged; a zone still heals at most once per
  in-game day. There is no wait between passes and a whole pass (prepare, all erosion, write) runs
  in one frame, since screens are dark; further passes start in that frame until about 8 ms are
  spent. Both runners restart at the front of their queue when the skip begins and keep going
  round it until it ends. The data revision check at the write still applies, and after a write
  the server or peer makes a loaded `TerrainComp` reload at once (`CheckLoad`).
  Who writes a zone that is not idle: if its terrain compiler ZDO is owned by the server itself
  (single player, the host's own zones) or by nobody or a peer that is gone, the server claims and
  writes it as usual, and a loaded `TerrainComp` reloads it by itself (`CheckLoad` on a data
  revision change). If a connected peer owns it, the server writes nothing and sends that peer the
  routed RPC `Weathering_ApplyHeal` with the new `TCData`, stamps, last-pass time and flag, and the
  data revision the pass read; the peer applies it only if it still owns the ZDO and its revision
  still matches, else ignores it and the zone stays pending. Border vertices are mirrored only into
  neighbours with the same writer (both written by the server, or both owned by the same peer,
  sent in the same message and applied all or nothing); other borders stay frozen for that pass.
  The peer also refuses terrain data it cannot decode for its grid. A sleep-mode pass always
  finishes within its frame. A normal pass still running when the skip begins finishes in sleep
  mode, skipping the idle check, but writes only while the server is still the writer of the zone
  and its linked neighbours; after the skip, passes need the zone idle again.
  A pass's "now" is the skip target, so a server stopped mid-skip saves a world time behind the
  last-pass time of the zones it healed that night. A world time behind the last pass counts as
  due, so after a restart those zones get one more pass. Harmless: with no time elapsed since the
  last pass it spreads and erodes nothing and only resets the last-pass time to the real now.
  Regrowth sites near players regrow during sleep mode as well (the sapling ZDO is created and
  the marker destroyed on the server, as vanilla's server does near players).
- Test commands on the server or host: `weathering_age <days>` (ages edits within 3 zones) and
  `weathering_status`. `weathering_clear_sites` destroys every regrowth marker; run before removing
  the mod, since clients of a dedicated server log "Missing prefab hash" each frame for markers
  whose prefab is gone. Stump-blocked markers are never dropped automatically (decided with the user).
### Which vertices may heal

A vertex is **frozen** (left as is and treated as a fixed boundary for erosion) when any of:

- it was edited less than `DelayDays` (default 3) ago;
- its paint is paved (above a paved threshold);
- it lies inside a player base area plus `BaseMargin`;
- it lies within `PieceMargin` of any player-built piece (my call, see below).

### Grass spread (paint)

Revised with the user (it first faded every vertex uniformly over `FadeDays`): grass grows back in
from the edges of dirt and cultivated ground, `GrassSpreadMeters` (default 2) metres per in-game day.
Code: `Healer.SpreadGrass`, fed per vertex by `HealRunner.Prepare`.

- **Sources:** vertices with no dirt (r) or cultivated (g) weight (at most 0.01, vanilla's brush
  edges leave faint tints): unpainted ground, and the cultivator's grass (`PaintType.Reset` paints
  0,0,0 and keeps alpha). Paved vertices (b above 0.5) are never sources. Frozen vertices count as
  sources when they hold no dirt (grass inside a base margin is real grass), never when they do
  (protected or fresh dirt must not seed grass). In the Deep North g is snow depth, so a vertex
  there is a source when it has no dirt, whatever its g.
- **Reclaimable:** unfrozen vertices with modified paint, r or g above 0, not paved and not in the
  Deep North (g holds snow depth there; verified in `PaintCleared`; Deep North vertices are never
  changed). Unfrozen implies the vertex's own delay ran out.
- **Distance:** each pass computes every vertex's distance in metres to the nearest source with a
  chamfer distance transform on the vertex grid (straight steps of the vertex spacing, diagonal
  steps of spacing times the square root of 2; forward and backward sweeps, repeated up to 4 times
  while anything changes, so a front can wind around obstacles). Only sources and reclaimable
  vertices carry the front: frozen dirt (fresh edits, bases, piece margins, Warden stones, held zone
  borders), Deep North dirt and paved ground block it, so grass never jumps through fresh dirt or
  across a road. Buffers are reused; no allocation per pass.
- **Reach:** each vertex's reach is `GrassSpreadMeters` times its own due days, the same catch-up
  basis as erosion (the days since its own delay ran out, at most the time since the zone's last
  pass). A reclaimable vertex within its reach becomes grass (r = g = 0). Up to a soft edge of 1 m
  further it loses the fraction of its weight it lies inside that band ((reach + 1 m - distance) /
  1 m), so the front is ragged rather than a hard ring. The full reach is cleared, not reach minus
  the band, so the front really advances `GrassSpreadMeters` a day; below one vertex spacing a day
  the band takes weight off in steps, so slow spread still arrives. Alpha (the vegetation mask) is
  never touched; when r and g reach 0 and b is 0, `m_modifiedPaint` is cleared so the generated mask
  shows again. Nothing new is saved: the next pass recomputes from the paint as it is, partly
  reclaimed vertices are not sources until r and g are 0 but are nearer the front, so it keeps
  moving.
- **Fallback:** dirt grass can never reach (a strip enclosed by a paved road and a base edge) fades
  uniformly as the old rule did: once a vertex's own delay ran out more than 10 in-game days ago it
  loses weight over 2 in-game days for the time since then (also capped by the time since the last
  pass). Each vertex takes the larger of its spread and fallback amounts. Big patches whose centre
  grass has not reached in those 10 days fade there too.
- **Zone borders:** each zone's border vertices are seeded from the linked neighbours' vertices one
  step across the border (the border row itself is shared, so the useful one is the neighbour's
  next row in), straight across at the vertex spacing and diagonally at spacing times the square
  root of 2. Border vertices facing an unlinked neighbour are frozen anyway (dirt there blocks,
  grass there is a source), so an unlinked neighbour gives no seeds and needs no decoding; that
  includes zones without a terrain compiler, whose shared border vertices this zone holds frozen.
  A shared vertex that may heal first takes the lowest r and g of every linked copy (a copy without
  modified paint counts as 0), and after the pass its r and g are mirrored into the linked
  neighbours like heights, so every copy of a border vertex ends identical. Mirroring never touches
  b, alpha or a Deep North vertex. A corner holding dirt links its diagonal neighbour like a corner
  with a height edit, so it can heal too.
- **Done:** a zone stays flagged while any reclaimable vertex still holds dirt or cultivated weight;
  dirt the grass cannot reach still clears through the fallback, so the flag does not stay set forever.

Paint colours (verified): dirt (1,0,0,1), cultivated (0,1,0,1), paved (0,0,1,1).

### Erosion

Work on the offset field `d = levelDelta + smoothDelta` (verified: final height is base +
levelDelta + smoothDelta, clamped to base ±8), never on absolute height, so the generated slope
underneath is kept. Write results back as `levelDelta = clamp(d, -8, 8)`, `smoothDelta = 0`, as
vanilla's Level and Raise do.

Each step is a few iterations of an explicit diffusion (for example 4-neighbour averaging with
rate `k`, stable for `k <= 0.25`) over unfrozen vertices, with frozen vertices as fixed values.
Diffusion conserves volume: pits widen and shallow, mounds spread. Frozen vertices are fixed
values (Dirichlet boundary), so earth may flow into or out of frozen ground and eroding ground
blends into it without a step. Volume is therefore only conserved away from frozen ground; at the
edge of a base, road or frozen zone border a pit may fill in or a mound drain away. No visible steps
outranks exact volume (my call). Optionally limit flow to where
the local slope exceeds a talus angle (thermal erosion) so gentle shapes stop changing earlier; the
builder may choose whichever looks right, but steep edges must soften first.

Vertices that receive offset from a neighbour become modified (`m_modifiedHeight = true`) and
inherit the oldest of the stamps they received from, so spread earth is never "fresh" again.

### Done rule (clearing the flag)

A zone's flag is cleared when every non-frozen modified vertex is settled:

- paint: no dirt or cultivated weight left;
- height: no pair with an unfrozen vertex is steeper than `TalusSlope` (0.35 m per m), and offsets
  below `ResetEpsilon` (for example 2 cm) are zeroed and their `m_modifiedHeight` cleared. End
  state: deep pits settle into a permanent dip with 0.35 m per m slopes, never flat ground.

Vertices still waiting out their delay keep the flag set (otherwise a zone checked before its
edits are old enough would never heal). Vertices frozen by a base, paved paint, a piece or a zone
border do not keep the flag set on their own. They are
re-examined when an edit happens in the zone again. Consequence: removing a workbench or another
piece does not start healing the ground it protected until something is edited in that zone (the
store page says so). (Acceptable for v1;
a cheap extra check on load could be added later.)

### Base areas

Use the vanilla `EffectArea` of type `PlayerBase` as the definition, but do not rely on physics
queries (`EffectArea.IsPointInsideArea`), because they only see loaded objects and neighbours of a
loading zone may not be loaded yet.

Instead, at startup scan every prefab in `ZNetScene` for a child `EffectArea` with the
`PlayerBase` flag and record prefab hash to radius (from its collider, scaled). When deciding
whether a vertex is in a base, query placed objects by ZDO (the zone and its 8 neighbours,
`ZDOMan` sector lookup) and test distance against the recorded radius plus `BaseMargin`. This works
for unloaded neighbours and on a dedicated server.

Warden stones add their `PlayerBase` area at runtime only (and only with Warden's `PlayerBaseArea`
config on), sized per stone by its level, so neither the startup scan nor physics queries can know
them in unloaded zones. Decided with the user:

- **Warden change (separate commit in the Warden repo):** each stone writes its current radius to
  its own ZDO (new key, for example `spawnward_radius`, float) whenever its level or radius changes,
  on the owner. It is a new persisted identifier, so it joins Warden's invariants list.
- **Weathering** finds `piece_spawnward` ZDOs around a zone and protects their saved radius plus
  `BaseMargin`. It never counts upgrade pieces or copies Warden's radius rules.
- Stones without a saved radius (placed with an older Warden and not loaded since) use the maximum
  radius from Warden's config when Warden is installed, else 180 m.
- A switched-off stone (`spawnward_enabled` false) still protects (decided with the user).
- The search for stones must reach the largest possible radius (180 m with Warden defaults, so
  about 7 x 7 zones of sectors); it runs once per heal pass, filtered by prefab hash.

A config list of extra prefab names with radii covers other mods.

### Zone borders

Vertices on a zone's edge exist in both zones' `TerrainComp`s. Vanilla never writes heights into a
neighbour: a `TerrainOp` is sent to every heightmap it overlaps and each edits its own copy
(verified). Both copies must end with identical values or a seam appears. Rule for v1 (revised with
server-side healing): border vertices are frozen in a pass, except when the neighbouring zone has
saved terrain data and is idle too; then the border line is eroded with this zone and the resulting
heights are written into the neighbour's copy, which is saved and flagged. Corners, shared by four
zones, are frozen unless the corner is modified and both side neighbours and the diagonal one are
linked (their data loaded, idle or the same writer); then the corner moves only if no copy is fresh
or paved and its value is written into all three. (A corner that never moves would keep a pit dug
across it at full depth for good, a permanent talus cone around it.) A neighbour whose copy a pass
changed is healed next, whenever its own last pass was, so its side of the border catches up
instead of keeping a small step for up to a day.

### Multiplayer

Revised during implementation: healing runs only on the server, on zones no peer is near, and is
saved to the ZDO with a new data revision, so a player who comes back receives the healed terrain
like any terrain edit. Overnight, zones near players heal too, written by the server only where
it owns them and otherwise applied by the owning peer (see When healing runs). Clients only record edit stamps on the zones they own. Workspace rule:
`NetworkCompatibility(EveryoneMustHaveMod, VersionStrictness.Minor)`. Gameplay config is
admin-only and synced.

### Tree regrowth

Decided with the user (added to v1): only trees regrow, no rocks and no ore. A felled tree comes back
as the vanilla sapling of that tree, but only where its stump is gone: a stump left standing blocks
regrowth for that spot. Nothing regrows inside player bases or near player pieces (the same base
and piece margins as terrain). The delay is the same `DelayDays`, counted from the felling.
Regrowth happens on the server while nobody is near the zone.

- A felled tree leaves no ZDO, so the site is recorded when it falls: a postfix on the private
  `TreeBase.SpawnLog`, which vanilla calls once per felled tree on its owner, spawns an invisible
  persistent marker `weathering_regrowth_site` at the tree's position and rotation. Its ZDO holds
  `weathering_tree` (tree prefab hash), `weathering_stump` (hash of the `m_stubPrefab` vanilla
  spawns, 0 if none) and `weathering_felled` (StampStore minutes, at least 1).
- Tree to sapling comes from every `Plant` prefab's `m_grownPrefabs`, rebuilt on each ZNetScene
  load; a tree no sapling grows into gets no marker.
- The server sweeps the markers like the terrain compilers (incremental, a new sweep at most every
  10 s, a few sites acted on per frame). A site is due `DelayDays` after `weathering_felled` and is
  handled only while its zone passes the heal runner's idle test. A due site inside any protected
  circle (base, Warden stone, piece) or whose tree no longer maps to a sapling is destroyed; one with
  its stump within 1 m is kept and rechecked an in-game day later; otherwise the sapling ZDO is
  created the way `ZNetView.Awake` creates one (persistent, type, distant flags from the sapling's
  `ZNetView`, rotation, `plantTime` now, `weathering_regrown` true) and the marker is destroyed
  after claiming it.
- Vanilla `Plant.UpdateHealth` judges every sapling like a player-planted one: biome, too hot
  (Ashlands), too cold (Mountain, Deep North), roof (ray up), grow space (overlap sphere of
  `m_growRadius` against trunks, rocks, logs, pieces and healthy plants). Natural forests are denser
  than that allows, so many regrown saplings would stay unhealthy for ever (or destroy themselves
  where `m_destroyIfCantGrow` is set). A postfix on `UpdateHealth` (`PlantPatches`) sets the status
  back to healthy for any plant whose ZDO has `weathering_regrown`, whatever the reason: the tree
  stood on that very spot, and the site was already dropped if it lies in a protected circle. It
  runs on every peer with the sapling loaded, so hover text and model show healthy too, and the
  owner's `Plant.Grow` grows it into the tree as usual. Other plants' grow-space check sees it as a
  healthy plant and is blocked by it, as by any healthy sapling. Without the mod such a sapling is
  an ordinary one again and may stay unhealthy; the world stays valid.

## Configuration (initial)

| Key | Default | Meaning |
| --- | --- | --- |
| `DelayDays` | 3 | In-game days after the last edit of a spot before it starts healing |
| `GrassSpreadMeters` | 2 | Meters per in-game day grass grows back into dirt and cultivated ground (replaced `FadeDays`) |
| `ErosionRate` | builder's call | Strength of erosion per in-game day |
| `BaseMargin` | 10 m | Extra protected distance around every player base area |
| `PieceMargin` | 4 m | Protected distance around any player-built piece |
| `ExtraBasePieces` | (empty) | Extra prefabs that count as bases, with radii |
## My calls (not discussed with the user)

- Name `Weathering`.
- Erosion as diffusion of the offset field.
- `BaseMargin` default 10 m.
- Protect ground near any player-built piece (`PieceMargin`), so an outpost portal or a lone
  bridge outside a base does not end up floating or buried. Trees and rocks are not protected;
  they may float or sink slightly near eroding edges, as in vanilla when digging under them.
- Frozen-by-base vertices do not keep the flag set.
- At frozen ground, blending wins over volume conservation (no steps at bases, roads, borders).

## Left to the builder

- Exact hook points and Harmony patch targets, after reading the decompiled code.
- Stamp encoding and ZDO key names.
- Erosion kernel, iteration count per step, `ErosionRate` default, step cap for long catch-ups.
- Border handling mechanism within the anti-goals above.
- Scheduling of loaded-zone re-checks.

## Verification

Release build passes with no new warnings. All in-game behaviour (fade, erosion look, seams,
base protection, multiplayer sync, performance) is untested until the user checks it in Valheim.
Useful for testing: a console command or config to scale time (for example one in-game hour counts
as a day) and a command to force a heal pass on the current zone.
