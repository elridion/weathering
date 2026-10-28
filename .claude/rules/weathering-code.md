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
  (no peer and not the host's player within Near + Far + 1 zones, no instance loaded on the
  server; a dedicated server's own reference position is the origin and is ignored). Never
  write terrain data of a zone someone may have loaded: a loaded `TerrainComp` re-saves its whole
  array over it, and clients drop updates whose data revision is not newer than theirs. Claim the
  ZDO with `zdo.SetOwner(ZDOMan.GetSessionID())` before writing and leave ownership to vanilla.
- Clients hold only the ZDOs near them, so anything that needs every base or Warden stone around a
  zone (up to 180 m) must run on the server.
- Private vanilla members: patch by string name, call through a cached `AccessTools.MethodDelegate`,
  read fields through `AccessTools.FieldRefAccess` or triple-underscore parameters. Never call them
  directly even though the publicized assembly compiles it.
- Vanilla never writes heights into a neighbouring zone: each `TerrainOp` is sent to every heightmap
  it overlaps and each edits its own copy, so border vertices must end identical in both zones.
