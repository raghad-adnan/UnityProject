# MERGE_INTO_SALMA_LOG.md
**Target project:** `e:\UnityProject` (same repo, branch `salma` @ 69a0a3d)
**Reference implementation:** `SurfaceExport/Scripts/` (my surface-physics versions)
**Date:** 2026-07-01

---

# PHASE 2 — STEP 1: INSPECTION FINDINGS (read-only, no code changed yet)

## 1.1 Compile status
- Branch confirmed: `salma` (working tree clean except untracked docs + SurfaceExport/).
- **Static cross-reference check (done):** all symbols her `PaintDrawer.cs` uses resolve:
  - `SpatialHash` → defined in `Assets/SpatialGrid.cs` (⚠️ the file is named SpatialGrid.cs but the CLASS is `SpatialHash`).
  - `SPHSolver`, `PaintParticle`, `PaintParticlePool`, `ParticleState`, `PendulumMotion`, `ParticleRenderer10k` → all present as tracked scripts.
  - `SurfacePreset.From`, `preset.surfaceAbsorption`, `preset.surfaceRoughness` → present in her old `SurfacePreset.cs`.
  - `FluidConstants.Reynolds`, `FluidConstants.Weber` → present in her old `FluidConstants.cs`.
- **Could NOT verify:** a guaranteed *0-error Unity compile*. The available Unity MCP tools do not expose compiler output. Options: (a) read Unity's console after it recompiles the branch switch, or (b) run the standalone Roslyn headless compile. Not run yet — will do before Step 2 if you want.

## 1.2 Her floor / surface script
| Item | Value |
|---|---|
| **File** | `Assets/PaintDrawer.cs` |
| **Class** | `PaintPhysics` (same class name & GUID `a1727060f0d1c03439c39d1fc507934b` as my reference) |
| **Attached to** | GameObject **Painter**, fileID **1562502121** |
| **Component fileID** | **1562502122** |
| **Scene** | `Assets/Scenes/SampleScene.unity` (structure identical to Phase-1 SCENE_WIRING.md; both branches diverged from commit 2351192) |
| **SurfacePreset equivalent?** | YES — she has an OLD simple `SurfacePreset` (4 floats: surfaceSpread, surfaceAbsorption, surfaceRoughness, splashProbability; presets Smooth/Rough/Absorbent). No physical params, no substrateColor. |

## 1.3 Method inventory (categorised)

**SURFACE-PHYSICS methods (candidates to merge):**
| Method | What it does | Merge note |
|---|---|---|
| `PaintSplat` | deposition: Re/We, heuristic spread & radius, thickness, adhesion, absorb, color, splash droplets, crown, registers ActiveSplat | replace physics core, keep signature |
| `UpdateActiveSplats` | splat growth: simple ring grow, `life = 0.6s` | different lifecycle from mine |
| `AbsorbStep` | ⚠️ **GLOBAL timer** fading the WHOLE texture toward **white** | **the exact global-timer regression the plan forbids** — must become per-splat |
| `AbsorbPaint` | per-pixel `absorbedPaint` accumulation | keep or fold into per-splat |
| `ApplyAdhesion` | thickness × stick factor | heuristic, no physics equivalent in mine |
| `AddPaintThickness` | triangular thickness deposit | mine deposits film in metres instead |
| `GetPaintAreaCoverage` | counts non-white pixels (coverage %) | mine compares to substrateColor |

**RENDERING helpers (write to `pixelBuffer`, NOT direct texture):**
`StampCircle`, `StampRing`, `StampEllipse`, `StampStreak`, `StampCrown`, `ScatterDroplets`, `StampLine`, `PutPixel`, `Clear` (fills buffer **white**).

**EMISSION / BUCKET / PARTICLE methods — DO NOT TOUCH:**
`EmitStep`, `SpawnParticle`, `ComputeHolePattern`, `UpdateParticles`, `BuildNeighborsCache`, `ApplyInteractionCached`, `UpdateInstancedRenderer`, `EnsureSphereMesh`, `Start`, `Update` (orchestration), `SavePainting`, `RefillPaint`, `OnGUI`.

## 1.4 SurfacePreset.cs and FluidConstants.cs already exist (NOT new files)
The plan assumed these are additive/new. **They are not** — old versions are already tracked on salma's branch (created way back at commit d8b2902).
- My new `SurfacePreset` is a **superset**: it keeps the 4 bridge properties her code reads (`surfaceAbsorption`, `surfaceRoughness`, `surfaceSpread`, `splashProbability`) as computed properties, and adds the physical params + `substrateColor`. → replacing her file keeps her `PaintDrawer` **compiling**, but **changes surface behaviour** (e.g. Wood absorption 0.02 → porosity 0.30).
- My new `FluidConstants` is a **superset**: it keeps her `Reynolds`/`Weber`/etc. with identical signatures and adds all the splash/spread/absorption physics. → replacing is compile-safe & additive.

## 1.5 STRUCTURAL MISMATCHES (per plan Step 2.4 — do NOT force wholesale replacement)
1. **Rendering architecture.** Salma draws into a `Color32[] pixelBuffer` and does ONE `SetPixels32+Apply` per frame. My surface code calls `texture.SetPixel/GetPixel` per stamp. Importing my stamps wholesale would break her single-apply optimisation and her AbsorbStep (which edits pixelBuffer). → **Adapt:** re-route my physics to write into her `pixelBuffer`.
2. **Splat lifecycle.** Hers: `{px,py,radius,growRate,color,age,life}`. Mine: 18 fields (radiusMeters, tv, volumeM3, thetaEqRad, absorptionLimited, localWetTime, localAbsorbFrac, currentColor, film flow…). → **Adapt:** extend her ActiveSplat / swap in mine but keep pixelBuffer drawing.
3. **Absorption model.** Hers = global white-fade timer. Mine = per-splat `localWetTime`/`localAbsorbFrac` toward `substrateColor`. Plan mandates the per-splat model. → **Replace** AbsorbStep; also `Clear()` must fill with `substrateColor` not white.
4. **Unit/scale system.** Mine needs `pixelsPerUnit`, `canvasMetersWidth/Height`, `ComputeUnitScale()`, real-metre drop diameter (Tate), Madejski β, Washburn depth. Hers uses fixed `/10f` UV mapping and pixel-radius heuristics (`size*spread*40`). → biggest gap; the metre-scale machinery must be added for the real physics to work.
5. **Serialized fields.** Hers serialises `adhesionStrength, absorptionRate, surfaceGravity, maxPaintThickness, thicknessAdd, crownWeberThreshold, density(=water), surfaceTension(=water)`. Per plan Step 2.3 these must **not** be removed/renamed (scene wiring). Merge must keep them even if unused, and only ADD new fields where strictly required.
6. **Viscosity in Re/We.** Hers passes `p.viscosityEffect` directly as µ. Mine converts to Pa·s (`paintViscosityPaS * max(0.05, viscosityEffect)`).

## 1.6 Proposed merge strategy (for your approval — NOT executed)
- **A.** Replace `SurfacePreset.cs` + `FluidConstants.cs` with the superset versions (compile-safe; unlocks the physics + substrateColor). Keep her file names/GUIDs.
- **B.** Inside her `PaintPhysics`, keep ALL emission/particle code and ALL serialized fields untouched. Only rewrite the **body** of `PaintSplat`, `UpdateActiveSplats`, and replace `AbsorbStep` with the per-splat absorption — but keep them writing to **her `pixelBuffer`** (not direct texture) so her single-Apply pipeline and FPS optimisation survive.
- **C.** Add the minimum new machinery required (metre-scale: `ComputeUnitScale`, `pixelsPerUnit`; per-splat ActiveSplat fields; `substrateColor` in Clear) as additive members — no renames.
- **D.** Leave `SpatialGrid.cs` (class `SpatialHash`), rope, bucket, particle system fully untouched.

**STATUS (Step 1): reported. User approved full application of the surface physics.**

---

# PHASE 2 — STEP 2: CHANGES MADE

User instruction: *"apply the saved surface physics COMPLETELY to this project."* Approach chosen:
port the full physical surface model but keep it writing into Salma's `pixelBuffer` pipeline, and
touch nothing in emission/particle/bucket/rope.

## Files changed (exactly 3 .cs files — no scene, no .meta)
| File | Change | GUID kept? |
|---|---|---|
| `Assets/SurfacePreset.cs` | content replaced with superset (physical params + `substrateColor` + the 4 bridge properties Salma's code reads) | ✅ `02f45c0d…` unchanged |
| `Assets/FluidConstants.cs` | content replaced with superset (all Salma's funcs + splash/spread/absorption physics) | ✅ `c7385509…` unchanged |
| `Assets/PaintDrawer.cs` | surface layer rewritten; emission/particle layer preserved | ✅ `a1727060…` unchanged |

## Inside PaintDrawer.cs — what changed vs what was preserved

**PRESERVED VERBATIM (emission / particle / bucket — untouched):**
`EmitStep`, `SpawnParticle`, `ComputeHolePattern`, `UpdateParticles`, `BuildNeighborsCache`,
`ApplyInteractionCached`, `UpdateInstancedRenderer`, `EnsureSphereMesh`, the `pixelBuffer`
single-Apply pipeline, the neighbour cache, the FPS counter, and every optimization Salma added.

**SERIALIZED FIELDS — all of Salma's kept (scene wiring intact):**
All object refs (`paintPoint`, `canvasRenderer`, `bucketMotion`, `particleRenderer`) and all value
fields kept with identical names/types. The now-unused legacy fields (`adhesionStrength`,
`absorptionRate`, `surfaceGravity`, `maxPaintThickness`, `thicknessAdd`) were KEPT (not removed) so
their Inspector values in SampleScene.unity are not silently dropped.

**NEW fields added (additive only — no renames):** `paintViscosityPaS`, `substrateThicknessMeters`,
`maxFilmThicknessMeters`, `pixelsPerUnit`, `canvasMetersWidth/Height`, `canvasTiltControlDeg`,
`canvasTiltRollDeg`, `lastDropDiameter`, `K`, `canvasTiltDeg`.

**SURFACE METHODS replaced with the full physical model:**
| Method | Before (Salma) | After (imported physics) |
|---|---|---|
| `PaintSplat` | heuristic spread `size*40`, Lerp(color*.35,color) | Weber/Reynolds/Ca/Oh/K, Wenzel apparent angle, Madejski β max-spread, film thickness `h=V/πR²`, Beer-Lambert colour over **substrateColor**, Stow-Hadfield splash w/ roughness-lowered threshold |
| `UpdateActiveSplats` | ring grow, `life=0.6s` | Tanner `(t/tv)^(1/10)` growth + thin-film downhill flow + **per-splat** absorption |
| `AbsorbStep` (global white-fade timer) | **DELETED** | replaced by per-splat `UpdateSplatAbsorption` (Lucas-Washburn on each splat's own `localWetTime`) |
| `AddPaintThickness` | arbitrary units | metres (`filmThickness`, capped at `maxFilmThicknessMeters`) |
| `AbsorbPaint` | per-pixel accumulate w/ `absorptionRate` | diagnostic-only reset (logic moved to per-splat) |
| `Clear` | fill **white** | fill **substrateColor** |
| `GetPaintAreaCoverage` | count non-white | count differs-from-substrate |
| `PutPixel` | intensity `1 - absorption*3` | intensity `1 - porosity` |

**NEW surface methods added:** `ComputeUnitScale`, `ApplyCanvasTilt`, `UpdateSurfaceFlow`,
`UpdateSplatAbsorption`, `StampCrown`(physical finger/jet), `ScatterDroplets`(ballistic, deterministic),
`StampLine`. **Removed unused:** `StampEllipse`, `StampStreak`, `AbsorbStep` (all uncalled after merge).

**`ActiveSplat` class** replaced (7 fields → full 18-field physical splat with per-splat wet-clock).

## Rope / bucket / particle systems: NOT TOUCHED.
`SpatialGrid.cs` (class `SpatialHash`), rope scripts, PendulumMotion, PaintStream, ParticleRenderer10k
were not modified.

---

# PHASE 2 — STEP 3: VERIFICATION

1. **Scene diff:** `git status` shows ONLY `Assets/FluidConstants.cs`, `Assets/PaintDrawer.cs`,
   `Assets/SurfacePreset.cs` modified. `SampleScene.unity` is **NOT** in the diff → no GameObject,
   component list, or Inspector reference changed. (`Packages/packages-lock.json` was already modified
   before this task, unrelated.)
2. **Dangling references:** grep for `ApplyAdhesion|AbsorbStep|StampEllipse|StampStreak|absorbTimer`
   in PaintDrawer.cs → none. No call site references a removed member.
3. **Symbol resolution (static):**
   - PaintParticle exposes `sleepTimer, color, size, viscosityEffect, approxMass` (all read by surface code) ✅
   - FluidConstants superset exposes all 18 physics functions used ✅
   - SurfacePreset superset exposes `substrateColor` + physical params + the 4 bridge properties ✅
   - Scene object-ref fields (`paintPoint/canvasRenderer/bucketMotion/particleRenderer`) still declared
     with identical names/types ✅
4. **COULD NOT VERIFY without Unity:** a guaranteed 0-error compile. The Unity MCP tools don't expose
   compiler output. Recommended: focus the Unity Editor (it recompiles the changed scripts) and check
   the Console, OR let me run the standalone Roslyn headless compile. Static analysis found no errors.

## Behavioural notes (expected, not bugs)
- Blank floor is no longer white: Metal reads grey, Wood brown, Canvas/Paper cream (substrateColor).
- Surface differences now come from real parameters (Wood absorbs a lot, Metal beads/splashes),
  replacing Salma's Smooth/Rough/Absorbent heuristic.
- Drop size still comes from Salma's emission (`baseSize`), which feeds the new splat physics; if splats
  look too big/small, tune `baseSize`/`maxParticles` (emission) rather than the surface code.

**STATUS: Step 2 + Step 3 complete. Awaiting a Unity compile/Play confirmation.**
