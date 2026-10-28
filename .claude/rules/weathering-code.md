---
paths:
  - "**/src/**/*.cs"
---

# Weathering code specifics

Adds to the workspace-wide `code-style.md` and `valheim-modding.md` in `../.claude/rules/`.

## Verified vanilla facts

- Paint colours: dirt r, cultivated g, paved b, alpha = vegetation mask (never touched). Paved means
  b > 0.5. Deep North vertices hold snow depth in g: no paint fade there.
- Final height is base + `levelDelta` + `smoothDelta`, clamped to base +-8. Erosion works on
  `d = levelDelta + smoothDelta` and writes back `levelDelta = Clamp(d, -8, 8)`, `smoothDelta = 0`.
- Never hardcode the grid size: use the `TerrainComp`'s `m_width` and `m_pitch`, or without one
  the zone prefab's `Heightmap.m_width` (`HealRunner.EnsureGrid`) (`index = y * pitch + x`).
- One in-game day is `EnvMan.instance.m_dayLengthSec` seconds (default 1200). Time is
  `ZNet.instance.GetTimeSeconds()`.
- Healing runs only on the server, on saved `TCData` of zones that pass `HealRunner.IsZoneIdle`
  (no peer and not the host within Near + Far + 1 zones; a dedicated server's own reference
  position is the origin and is ignored, and the zones it keeps loaded there count as idle because
  `Commit` reloads them through `HealApply.ReloadLoaded`). Never write terrain data of a zone a
  peer may have loaded: a loaded `TerrainComp` re-saves its whole array over it, and clients drop
  updates whose data revision is not newer than theirs. Claim the
  ZDO with `zdo.SetOwner(ZDOMan.GetSessionID())` before writing and leave ownership to vanilla.
- A heal pass spans several frames (`HealRunner` prepare, erode slices, finish), one pass at a
  time. Write no ZDO before the finish frame, and there re-check `IsZoneIdle` and that every ZDO
  read still has the `DataRevision` it had when decoded; otherwise drop the pass unwritten.
- Sleep mode (`HealRunner.IsSleepMode`, server while `EnvMan.IsTimeSkipping()`) drops the idle
  test and runs a whole pass in one frame. Then the owner applies: the server writes a zone only
  when `WriterOf` says the server (idle, or owned by the server, nobody or a gone peer); a zone a
  connected peer owns is never written by the server but sent through `HealApply`
  (`Weathering_ApplyHeal`) with the revision the pass read, and the peer writes only if it still
  owns it at that revision. Borders are mirrored only into neighbours with the same writer.
- Zones with no `weathering_last` (never seen) are examined without the pending flag; every path
  that finishes with such a zone must set `weathering_last`, or it is decoded again every round.
- Clients hold only the ZDOs near them, so anything that needs every base or Warden stone around a
  zone (up to 180 m) must run on the server.
- Private vanilla members: patch by string name, call through a cached `AccessTools.MethodDelegate`,
  read fields through `AccessTools.FieldRefAccess` or triple-underscore parameters. Never call them
  directly even though the publicized assembly compiles it.
- Vanilla never writes heights into a neighbouring zone: each `TerrainOp` is sent to every heightmap
  it overlaps and each edits its own copy, so border vertices must end identical in both zones.
