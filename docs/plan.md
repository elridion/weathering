# Weathering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A new Valheim mod, Weathering, that fades dirt and cultivated paint and erodes player
height edits outside player bases, 3 in-game days after each spot was last edited; plus a small
Warden change that saves each stone's radius on its ZDO.

**Architecture:** A Harmony patch on `TerrainComp.InternalDoOperation` stamps every changed vertex
with world time into the zone's own ZDO. A runner on the zone owner periodically picks flagged,
owned `TerrainComp`s, builds a frozen mask (recent stamp, paved, player base, near a piece, zone
border), applies paint fade and offset-field diffusion for the elapsed time, then saves and pokes
the heightmap through vanilla's private `Save` / `Poke`. Bases come from a prefab scan for
`PlayerBase` effect areas and a sector scan of ZDOs, so unloaded neighbours count.

**Tech Stack:** C# net462, BepInEx, Jotunn 2.30.2, Harmony; publicized `assembly_valheim`.

**Spec:** `weathering/docs/design.md` (read it first; this plan does not repeat its reasoning).
Decompiled vanilla sources for reference: `C:\Users\elridion\.claude\jobs\26baac2f\tmp\decomp\`
(TerrainComp.cs, Heightmap.cs, EffectArea.cs, ZDOMan.cs, ZDO.cs, ZoneSystem.cs, ZNet.cs, EnvMan.cs,
ZNetScene.cs, ZDOVars.cs). This folder is temporary; if it is gone, re-decompile with ilspycmd.

## Global Constraints

- Follow `../CLAUDE.md` and `../.claude/rules/` (code-style, valheim-modding, release). Read them.
- `net462`, `LangVersion` latest, nullable disabled, `JotunnLib` 2.30.2 in both csproj places.
- Plugin GUID `elridion.weathering`, name `Weathering`, version `0.1.0`, namespace `Weathering`.
- `NetworkCompatibility(EveryoneMustHaveMod, VersionStrictness.Minor)`.
- Persisted identifiers, never renamed once shipped: ZDO keys `weathering_stamps` (byte[]),
  `weathering_pending` (bool), `weathering_last` (long, world time in whole seconds of the last heal
  pass). Warden key `spawnward_radius` (float).
- Private vanilla members: patch by string name, call through cached `AccessTools.MethodDelegate`,
  read fields through `AccessTools.FieldRefAccess` or triple-underscore parameters. Never call them
  directly even though the publicized assembly compiles it.
- Never hardcode the grid size: use the `TerrainComp`'s `m_width` / `m_pitch` (`index = y * pitch + x`).
- Paint colours (verified): dirt r, cultivated g, paved b, alpha = vegetation mask, never touched.
  Paved means b > 0.5. Deep North vertices: no paint fade.
- Height: `d = levelDelta + smoothDelta`; write back `levelDelta = Clamp(d, -8, 8)`, `smoothDelta = 0`.
- One in-game day = `EnvMan.instance.m_dayLengthSec` seconds (default 1200). Time =
  `ZNet.instance.GetTimeSeconds()`.
- Healing runs only where `TerrainComp.IsOwner()` is true.
- No test projects in this workspace. "Verify" = `dotnet build -c Release` from the mod folder with
  only the known `MSB3245` warning. Every in-game behaviour stays untested until the user checks it.
- Git: work on `main`, commit per task, subject plain imperative sentence case, no prefix, no period.
  Never commit `bin/`, `obj/`, `dist/`. Weathering has no remote yet: do not create one. Warden: push
  `main` only if the user approves (ask at the end).

## Review Focus

1. **Zone with a `TerrainComp` but no stamps (edits before install):** expect every modified vertex
   stamped with first-seen time and healing `DelayDays` later (Task 3, `StampStore.EnsureStamped`).
2. **Paint-only edits and Reset/ClearVegetation paint (b = 0, r = g = 0):** expect them left as they
   are and never counted as "something to heal", so the flag clears (Task 5, `Healer.IsPaintPending`).
3. **Ownership changes mid-life (player leaves, server takes over):** expect no double-heal and no
   heal on a non-owner; `weathering_last` makes a second owner continue, not restart (Task 6).
4. **A base piece in an unloaded neighbour zone, and a Warden stone up to 180 m away:** expect the
   ground frozen even though nothing near it is loaded (Task 4, sector reach from max radius).
5. **Huge catch-up after months away:** expect one bounded pass (step cap) without a hitch and no
   NaN or > ±8 deltas (Task 5 `Erode` caps iterations; Task 6 clamps).

---

### Task 1: Warden saves each stone's radius

**Files:**
- Modify: `warden/src/SpawnWard.cs` (field near `s_enabledHash` at line 13; `UpdateStatus` ~line 170)
- Modify: `warden/CLAUDE.md` (invariants: add ZDO key `spawnward_radius`)
- Modify: `warden/.claude/rules/warden-code.md` (one line: why the radius is saved, who reads it)

**Interfaces:**
- Produces: ZDO float `spawnward_radius` on every `piece_spawnward`, written on the owner whenever
  it differs from `Radius` by more than 0.01 m. Read by Weathering Task 4.

- [ ] **Step 1:** Add `private static readonly int s_radiusHash = "spawnward_radius".GetStableHashCode();`
  and, in `UpdateStatus` after `RefreshLevel()`, a private `SaveRadius()` that returns unless
  `m_nview.IsOwner()`, and sets the float only when it changed (avoid a ZDO revision every second).
- [ ] **Step 2:** Update the two docs. No CHANGELOG entry and no version bump (no player-visible
  change; the user decides on releases).
- [ ] **Step 3:** Verify: `dotnet build -c Release` in `warden/`; only `MSB3245`.
- [ ] **Step 4:** Commit in the warden repo: `Save each warden stone's radius on its ZDO` with a body
  saying Weathering reads it to protect ground around stones in unloaded zones.

### Task 2: Weathering project skeleton

**Files (create in `weathering/`):**
- `Weathering.csproj` (copy `warden/Warden.csproj`; drop asset and translation items; description
  "Dug pits, churned dirt and abandoned fields outside your base slowly weather back into the land.")
- `src/WeatheringPlugin.cs`, `.gitignore` (copy Spearbreaker's), `LICENSE` (copy Warden's),
  `CHANGELOG.md` (`## 0.1.0` - first release bullets), `README.md` (developer page, links
  `package/README.md`), `package/README.md` (player page: what heals, what never does, config table,
  the warning "existing earthworks outside bases will weather too; removing the mod does not bring
  them back", credits), `package/icon.png` 256 x 256 plus `tools/make_icon.ps1` modelled on
  `warden/tools/make_icon.ps1` with a CC BY game-icons.net icon (credit it), `hexium.json`
  (copy Warden's shape), `.github/workflows/build.yml` (copy Spearbreaker's, rename),
  `CLAUDE.md` (Warden's shape: where things are, invariants = the persisted identifiers above),
  `.claude/rules/weathering-code.md` (start with the verified vanilla facts from Global Constraints).
- Keep `docs/design.md`, `docs/ideas.md`, `docs/plan.md`.

**Interfaces:**
- Produces: `internal class WeatheringPlugin : BaseUnityPlugin` with public static `ConfigEntry`s,
  all section `General`, admin-only, with ranges:
  `DelayDays` float 3 (0.1-30), `FadeDays` float 2 (0.1-30), `ErosionRate` float 1 (0-5, relative
  strength; 1 = default look), `BaseMargin` float 10 (0-50 m), `PieceMargin` float 4 (0-20 m),
  `ExtraBasePieces` string "" (`prefab:radius` comma list).

- [ ] **Step 1:** `git init` in `weathering/`, create the files, `Awake` binds config and `PatchAll`.
- [ ] **Step 2:** Verify build and `-t:Package` produces `dist/Weathering-0.1.0.zip`.
- [ ] **Step 3:** Commit `Add the Weathering mod skeleton` (include the docs).

### Task 3: Record edits per vertex

**Files:**
- Create: `src/StampStore.cs`, `src/TerrainPatches.cs`

**Interfaces:**
- Produces:
  - `internal static class StampStore`
    - `int[] Load(TerrainComp tc)` – per-vertex stamps in whole in-game minutes since world time 0
      (`0` = none), length `pitch²`; decompress `weathering_stamps` with `Utils.Decompress`; a length
      mismatch returns a fresh array.
    - `void Save(TerrainComp tc, int[] stamps)` – `Utils.Compress` of the ints; owner only.
    - `void EnsureStamped(TerrainComp tc, int[] stamps, int now)` – any vertex with
      `m_modifiedHeight` or (`m_modifiedPaint` and r/g > 0) and stamp 0 gets `now`; sets pending.
    - `bool IsPending(TerrainComp tc)` / `void SetPending(TerrainComp tc, bool value)`.
    - `int Now()` – `(int)(ZNet.instance.GetTimeSeconds() / 60)`.
  - `TerrainPatches`: prefix on `TerrainComp.InternalDoOperation` (string name) copies
    `m_levelDelta`, `m_smoothDelta`, `m_paintMask` into reusable static buffers; postfix diffs, stamps
    changed vertices with `Now()`, saves stamps, sets pending. Runs only when `tc.IsOwner()`.

- [ ] **Step 1:** Implement; read arrays through `AccessTools.FieldRefAccess<TerrainComp, T>`.
- [ ] **Step 2:** Verify build.
- [ ] **Step 3:** Commit `Stamp terrain edits with the time they were made`.

### Task 4: Base index

**Files:**
- Create: `src/BaseIndex.cs`

**Interfaces:**
- Consumes: Warden ZDO float `spawnward_radius` (Task 1); `WeatheringPlugin.BaseMargin`,
  `PieceMargin`, `ExtraBasePieces`.
- Produces:
  - `internal readonly struct ProtectedCircle { Vector2 Center; float Radius; }` (XZ plane).
  - `internal static class BaseIndex`
    - `void Build()` – once on `ZNetScene` ready (Jotunn `PrefabManager.OnPrefabsRegistered` or a
      postfix on `ZNetScene.Awake`): for every prefab in `ZNetScene.instance.m_prefabs`, any child
      `EffectArea` with `(m_type & EffectArea.Type.PlayerBase) != 0` and a `SphereCollider` records
      prefab hash → `radius * max(lossyScale)`. Adds `ExtraBasePieces` entries.
    - `void Collect(Vector2s zone, List<ProtectedCircle> result)` – scans ZDO sectors in a square of
      `ceil((MaxRadius + BaseMargin) / 64)` zones around `zone` via `ZDOMan`'s `m_objectsBySector`
      (`ZoneSystem.SectorToIndex`) and `m_objectsByOutsideSector`. Adds base circles (radius +
      `BaseMargin`), Warden stones (saved radius, else `WardenMaxRadius()`, + `BaseMargin`; on or off),
      and, for the zone and its 8 neighbours only, any ZDO with a non-zero creator
      (`ZDOVars.s_creator`) as a `PieceMargin` circle.
    - `float WardenMaxRadius()` – if `Chainloader.PluginInfos` has `elridion.warden`, read its
      `General` `BaseRadius`, `RadiusPerUpgrade`, `MaxStackedUpgrades` and return
      `Base + PerUpgrade * (3 + MaxStacked)`; else 180.

- [ ] **Step 1:** Implement; skip `Collect` work cheaply when no base prefab hashes are known.
- [ ] **Step 2:** Verify build.
- [ ] **Step 3:** Commit `Find player bases and pieces around a zone from saved objects`.

### Task 5: Healing math

**Files:**
- Create: `src/Healer.cs` (no Unity scene access; arrays in, arrays out)

**Interfaces:**
- Produces: `internal static class Healer`
  - `bool FadePaint(Color[] paint, bool[] modifiedPaint, bool[] frozen, bool[] deepNorth, float amount)`
    – `amount` = elapsed fade fraction (elapsed days / `FadeDays`); per non-frozen, non-deep-north
    vertex with `modifiedPaint` and b ≤ 0.5: r, g move toward 0 by `amount` (absolute, clamp at 0);
    when r, g, b are all 0, clear `modifiedPaint`. Returns true if anything is still pending.
  - `bool IsPaintPending(Color c)` – `c.b <= 0.5f && (c.r > 0 || c.g > 0)`.
  - `bool Erode(float[] offset, bool[] frozen, int pitch, int iterations, float rate)` – explicit
    4-neighbour diffusion, `rate` ≤ 0.25, frozen vertices fixed, out-of-grid neighbours ignored
    (treated as equal to self). Before diffusing, a vertex flows only if some neighbour difference
    exceeds `TalusSlope` (constant 0.35 m per m) so gentle shapes settle. Returns true if any
    unfrozen neighbour difference is still above `SettledSlope` (0.05 m per m).
  - `void ZeroTiny(float[] offset, bool[] modifiedHeight, bool[] frozen)` – unfrozen `|d| < 0.02`
    → 0 and `modifiedHeight = false`.
  - Constants near the top: `TalusSlope`, `SettledSlope`, `ResetEpsilon = 0.02f`,
    `IterationsPerDay = 24`, `MaxIterationsPerPass = 240`.

- [ ] **Step 1:** Implement. `Erode` uses a scratch buffer, no allocation per call after the first.
- [ ] **Step 2:** Verify build.
- [ ] **Step 3:** Commit `Add paint fade and erosion math`.

### Task 6: Heal runner

**Files:**
- Create: `src/HealRunner.cs` (MonoBehaviour added to the plugin's GameObject in `Awake`)

**Interfaces:**
- Consumes: `StampStore` (Task 3), `BaseIndex` (Task 4), `Healer` (Task 5).
- Produces: `internal class HealRunner : MonoBehaviour` with `public static void HealNow(TerrainComp tc)`.

Behaviour:
- Every 2 s real time, walk `TerrainComp.s_instances` (private static; FieldRefAccess) and queue
  owned ones with pending set whose `weathering_last` is at least one in-game day old, or which have
  never healed (first sight: run `EnsureStamped`, set `weathering_last = now`, no heal yet).
  Process at most one per frame.
- `HealNow(tc)`:
  1. Load arrays and stamps. `elapsedDays = (now - last) / dayLength`.
  2. Frozen mask (stamp 0 means never edited, since first sight stamps every modified vertex; such
     vertices are free to receive earth): frozen when `stamp != 0 && now - stamp < DelayDays` (in
     minutes: `DelayDays * dayLength / 60`); paved; inside any `ProtectedCircle`; border vertex (x or y is 0 or
     `m_width`) unless the neighbour comp (use `TryGetNeighbor` or `FindTerrainCompiler` at the
     neighbour's centre) is loaded and `IsOwner()`; Deep North mask from
     `WorldGenerator.instance.GetBiome(worldPos) == Heightmap.Biome.DeepNorth`.
  3. Paint: `FadePaint(..., amount = elapsedDays / FadeDays)`; the per-vertex delay is already in
     the frozen mask, so a vertex starts fading on the first pass after its delay ends.
  4. Height: `offset = level + smooth`; `iterations = min(round(elapsedDays * IterationsPerDay *
     ErosionRate), MaxIterationsPerPass)`; `Erode`, `ZeroTiny`, write back with clamp. Vertices that
     became non-zero inherit `m_modifiedHeight = true` and the oldest neighbouring stamp.
  5. Border pairs: when the neighbour is owned and loaded, erode the shared line once and copy the
     values to the neighbour's matching vertices; save and poke the neighbour too.
  6. Save via cached `AccessTools.MethodDelegate` to private `TerrainComp.Save(bool)`, then
     `tc.m_hmap.Poke(1)` (public method; `m_hmap` via FieldRefAccess), then `ClutterSystem` grass reset
     the way `DoOperation` does. Write stamps, `weathering_last = now`, pending = paint pending ||
     erosion pending (frozen-only leftovers do not keep it).
- Console commands (Jotunn `ConsoleCommand`, admin/cheat only): `weathering_heal` heals the zone
  under the player now ignoring the one-day wait; `weathering_age <days>` shifts the current zone's
  stamps and `weathering_last` back by `days` so the delay can be tested without waiting.

- [ ] **Step 1:** Implement runner and commands.
- [ ] **Step 2:** Verify build and package.
- [ ] **Step 3:** Commit `Heal flagged zones on their owner`.

### Task 7: Docs pass and final check

- [ ] **Step 1:** Make `package/README.md` config table match `WeatheringPlugin.Awake` exactly
  (Key, Default, Description with range); `CLAUDE.md` "where things are" matches `src/`.
- [ ] **Step 2:** Verify `dotnet build -c Release -t:Package`; only `MSB3245`.
- [ ] **Step 3:** Commit `Document Weathering's settings and layout`.
- [ ] **Step 4:** Report to the user: package path, the in-game test list (fade, erosion look, no
  seams at zone borders, bases and Warden stones protected, paved roads kept, multiplayer sync,
  no hitches), and ask whether to push Warden's commit.
