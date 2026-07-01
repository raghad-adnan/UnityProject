# EXPORT_LOG.md
**Date:** 2026-07-01  
**Branch at export:** feature/paint-simulation-updates  
**HEAD commit:** 26b98b0 (Merge Salma's rope physics into surface-physics branch)

---

## Step 1 — Scripts Copied (COMPLETE)

All four scripts + their `.meta` files copied to `SurfaceExport/Scripts/`:

| File | Source GUID | Status |
|---|---|---|
| SurfacePreset.cs | 02f45c0d57a0f194988af86d0ebb2ea8 | ✅ Copied |
| SurfacePreset.cs.meta | — | ✅ Copied |
| PaintDrawer.cs | a1727060f0d1c03439c39d1fc507934b | ✅ Copied |
| PaintDrawer.cs.meta | — | ✅ Copied |
| FluidConstants.cs | c7385509345f73745b517d1968bd0bd5 | ✅ Copied |
| FluidConstants.cs.meta | — | ✅ Copied |
| SpatialGrid.cs | d0ee84f63e006b7429fd99775e16922e | ✅ Copied |
| SpatialGrid.cs.meta | — | ✅ Copied |

**Verification:** These files were confirmed to be the current HEAD versions. No uncommitted changes to these four files were detected. The prior audit (see `SURFACE_PHYSICS_AUDIT.md` in project root) verified that all five physics approximations (SplashThresholdRough, cosθ_Young in Washburn, Tanner–de Gennes hybrid, substrateColor, Wenzel warning) are present and functional in these exact files.

**Dependency note:** `PaintDrawer.cs` (class `PaintPhysics`) depends on:
- `SurfacePreset` (struct) — in SurfacePreset.cs ✅ included
- `FluidConstants` (static class) — in FluidConstants.cs ✅ included
- `SpatialGrid` — in SpatialGrid.cs ✅ included
- `PaintParticle`, `PaintParticlePool`, `ParticleState` — in **PaintParticle.cs** and **PaintParticlePool.cs** — **NOT COPIED** (not listed in the export scope but REQUIRED to compile; see "Not Exported" below)
- `SPHSolver` — in **SPHSolver.cs** — **NOT COPIED** (same reason)
- `PendulumMotion` — in **PendulumMotion.cs** — **NOT COPIED** (referenced via `public PendulumMotion bucketMotion`)

---

## Step 2 — Scene Wiring Document (COMPLETE)

**File:** `SurfaceExport/SCENE_WIRING.md`

Contents verified:
- ✅ Full transform hierarchy of all painting system GameObjects
- ✅ Every component's fileID for: Canvas, Painter, Bucket, PaintPoint (both), PaintStream, inner, Pivot, HandlePoint, toppoint
- ✅ PaintPhysics (PaintDrawer): all serialized fields and values documented, including stale fields
- ✅ PendulumMotion: all serialized fields documented
- ✅ PaintStream: all serialized fields documented
- ✅ All 3 Inspector-assigned object references in PaintPhysics (paintPoint → fileID 558370783, canvasRenderer → fileID 1371810641, bucketMotion → fileID 1953271167) resolved to named GameObjects + component types
- ✅ All materials documented (Bucket_Outer_Mat, Bucket_inner_Mat, PaintCanvasMat, RopeMat, built-in Default-Diffuse, Sprites/Default)
- ✅ Parent/child relationships documented
- ✅ 7 critical rebuild notes added

---

## Step 3 — Unity Editor Export (SKIPPED — connection present but tool not available)

Unity Editor was confirmed LIVE and responding (MCP port 6400, status OK).  
However, the available MCP tools do **not** include prefab creation or `Assets > Export Package` functionality. The tools available are: `create_gameobject`, `delete_gameobject`, `get_hierarchy`, `get_project_structure`, `modify_gameobject`, `read_file`, `create_script`, `status`.

**No `.unitypackage` was created.**  
**No `SurfaceRig.prefab` was created.**

`SCENE_WIRING.md` is the authoritative rebuild reference instead.

---

## What Was NOT Exported / Cannot Be Verified Here

| Item | Reason |
|---|---|
| `PaintParticle.cs` + `.meta` | Not in the Step 1 scope. Required to compile PaintDrawer.cs in a fresh project. |
| `PaintParticlePool.cs` + `.meta` | Same — required dependency. |
| `SPHSolver.cs` + `.meta` | Same — required dependency. |
| `PendulumMotion.cs` + `.meta` | Same — required by PaintPhysics.bucketMotion field. |
| `PaintStream.cs` + `.meta` | The PaintStream GO is part of the rig but its script was not in export scope. |
| `Bucket_Outer_Mat.mat` + `.meta` | Material asset not copied. Recreate as Standard shader, _Color (0.4,0.4,0.4). |
| `Bucket_inner_Mat.mat` + `.meta` | Material asset not copied. Recreate as Standard shader, _Color (0.9,0.9,0.9). |
| `SurfaceRig.prefab` | Not created (no MCP prefab tool). |
| `SurfaceRig.unitypackage` | Not created (no MCP export tool). |
| Play Mode verification | The Editor is open but no Play Mode test was run as part of this export. The physics was tested in a prior session (see `SURFACE_PHYSICS_AUDIT.md`). |
| `SimulationManager.cs` | Present in project but has no direct dependency on the 4 surface-physics scripts; not included in scope. |

---

## Summary

| Task | Result |
|---|---|
| 4 scripts + .meta copied | ✅ Complete |
| SCENE_WIRING.md (full wiring) | ✅ Complete |
| .unitypackage created | ❌ Skipped — MCP lacks export tool |
| Prefab created | ❌ Skipped — MCP lacks prefab tool |

The `SurfaceExport/Scripts/` folder and `SurfaceExport/SCENE_WIRING.md` together are sufficient to rebuild the surface/painting system in a new project, provided the 5 additional dependency scripts (PaintParticle, PaintParticlePool, SPHSolver, PendulumMotion, PaintStream) are also copied from this project.
