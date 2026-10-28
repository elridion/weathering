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

- **Paint fades.** Dirt and cultivated paint on the ground fades back to natural ground, so grass
  returns (grass is drawn from the paint by the clutter system; nothing else is needed for it).
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
  ever examined. Healing runs on the server for idle zones, at most one pass per zone per in-game
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
  of sectors per frame; a new sweep starts at most every 10 s) and processes at most one zone every
  0.5 s real time. A zone is a candidate when its flag is set, it is idle, and its last pass is at
  least one in-game day old. The first time a zone is seen it is only stamped (edits from before the
  mod start their delay) and its last-pass time set; healing starts a day later.
- Idle: the zone's Chebyshev zone distance from every peer's reference position, and from the
  host's own when the server has a local player, is above Near + Far + 1 of the largest simulation
  distance in use (8 zones with the game's maximum, Near 5 + Far 2), and the server has no instance
  of its terrain compiler loaded. Nobody can have it loaded, so nothing re-saves over the result.
  (A dedicated server's own reference position stays at the world origin and is ignored.)
- With `ErosionRate` 0, steep slopes alone do not keep a zone flagged.
- A pass decodes the saved `TCData`, heals it (catch-up per spot: each vertex fades for the days
  since its own delay ran out, at most the time since the zone's last pass; erosion runs for the
  most such days among the height edits past their delay, frozen ones included since their edges
  still erode the ground next to them, capped at 240 iterations), and
  writes it back in vanilla's layout after claiming the ZDO for the server, like vanilla server code
  does. Ownership is left to vanilla's release loop. A zone whose data cannot be read logs one
  warning and loses its flag.
- Time is world time, so sleeping counts as passing days. One in-game day =
  `EnvMan.instance.m_dayLengthSec` (`long`, default 1,200 s; verified).
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

### Paint fade

For each unfrozen vertex with modified paint: move dirt (r) and cultivated (g) toward 0 over
`FadeDays` (default 2) after the delay, keeping alpha (the vegetation mask) untouched; when r and g
reach 0 and b is 0, clear `m_modifiedPaint` for that vertex so the generated mask shows again.
Paved weight (b) is never reduced; a vertex with b above 0.5 (vanilla's own cleared threshold) is
paved. Deep North vertices are skipped for paint (g holds snow depth there; verified in
`PaintCleared`). Paint colours (verified): dirt (1,0,0,1), cultivated (0,1,0,1), paved (0,0,1,1).

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
re-examined when an edit happens in the zone again. Consequence: removing a workbench does not
start healing the ground it protected until something is edited in that zone. (Acceptable for v1;
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
zones, are always frozen.

### Multiplayer

Revised during implementation: healing runs only on the server, on zones no peer is near, and is
saved to the ZDO with a new data revision, so a player who comes back receives the healed terrain
like any terrain edit. Clients only record edit stamps on the zones they own. Workspace rule:
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
  `ZNetView`, rotation, `plantTime` now) and the marker is destroyed after claiming it.

## Configuration (initial)

| Key | Default | Meaning |
| --- | --- | --- |
| `DelayDays` | 3 | In-game days after the last edit of a spot before it starts healing |
| `FadeDays` | 2 | In-game days for dirt and cultivated paint to fade fully |
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
