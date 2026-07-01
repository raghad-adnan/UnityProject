# MODULE MAP — VR Paint-Bucket Simulation (post Phase-0 restructure)

This is the authoritative "who-owns-what" map after the Phase-0 split. Hand each
teammate the files marked as theirs in `TASK_SPLIT_PROPOSAL.md`; everything else is
read-only for them.

> **How the paint component is split.** The old single file `PaintDrawer.cs` (which
> confusingly contained `class PaintPhysics`) is now FOUR files that all compile into
> the **same** `PaintPhysics` MonoBehaviour via C# `partial class`. They live on ONE
> GameObject (**Painter**) as ONE component — the split is purely at the source-file
> level so three people can edit different systems without touching the same file.
> Behaviour, serialization and the scene reference (script GUID
> `a1727060f0d1c03439c39d1fc507934b`) are unchanged.

---

## 1. Paint component — `PaintPhysics` (one component on the **Painter** GameObject)

| File | Responsibility (one line) | Attached to |
|------|---------------------------|-------------|
| `Assets/PaintPhysics.cs` | CORE: shared fields (fluid props, unit scale, tilt), `Start`/`Update` order, floor-tilt & the right-drag / S / R hotkeys (`OnGUI`) | **Painter** |
| `Assets/BucketEmission.cs` | Bucket reservoir + hole shape/geometry + physically-sized droplet emission (`EmitStep`, `SpawnParticle`, `ComputeHolePattern`, `DropDiameter`, `RefillPaint`) | **Painter** (same component) |
| `Assets/ParticleSimulation.cs` | In-air droplet physics: SPH, spatial grid, pool, gravity/damping/sleep, canvas-collision detection → calls the seam `OnDropletImpact(...)` | **Painter** (same component) |
| `Assets/SurfaceInteraction.cs` | Everything after paint touches the floor: impact splat, Madejski spread, Stow-Hadfield splash, Tanner spreading, thin-film flow, Lucas-Washburn absorption, all `Stamp*`, `Clear`, `SavePainting`, `GetPaintAreaCoverage` | **Painter** (same component) |

**Seam between people:** `ParticleSimulation.cs` calls `OnDropletImpact(canvas, hitPoint, particle, normal)`; `SurfaceInteraction.cs` implements it. That single method is the contract between the particle-physics owner and the surface owner.

## 2. Paint helper data/logic (leave unchanged unless your task needs them)

| File | Responsibility | Attached to |
|------|----------------|-------------|
| `Assets/FluidConstants.cs` | Static library of documented SI fluid relations (Weber, Reynolds, Madejski, Tanner, Washburn, Nusselt, …). No tuning knobs. | none (static class) |
| `Assets/SurfacePreset.cs` | Physical per-surface properties (contact angle, porosity, pore radius, roughness, substrate colour) for Canvas/Wood/Metal/Paper. | none (struct) |
| `Assets/PaintParticle.cs` | Per-droplet data record + `ParticleState` enum. | none (runtime instances) |
| `Assets/PaintParticlePool.cs` | Object pool that recycles droplet GameObjects. | none (runtime) |
| `Assets/SPHSolver.cs` | Poly6/Spiky/viscosity SPH kernels + pressure/viscosity forces. | none (runtime) |
| `Assets/SpatialGrid.cs` | Uniform-grid neighbour lookup for the SPH step. | none (runtime) |
| `Assets/PaintStream.cs` | Legacy stream `LineRenderer`. **Currently disabled** (`Start` disables the line, `Update` returns). | **PaintStream** GameObject |

## 3. Rope & pendulum

| File | Responsibility | Attached to |
|------|----------------|-------------|
| `Assets/PendulumMotion.cs` | The ACTUAL "rope physics": analytic 2-axis pendulum (tension, break/slack, buoyancy, drag, energy). Drives the bucket's position. | **Bucket** GameObject |
| `Assets/Ropefix.cs` (`class RopeFix`) | COSMETIC rope only: draws a sagging Bézier `LineRenderer` from Pivot → bucket attach point. Not a physics rope. | **Rope** GameObject (active) + a **dead duplicate on Bucket** (all refs null → does nothing) |

> ⚠️ There is **no** `RopePhysicsMSD.cs` and **no** `RopeTubeRenderer.cs` anywhere in the
> project — the task brief assumed a mass-spring-damper rope + tube renderer that does not
> exist here. See the rope section of `TASK_SPLIT_PROPOSAL.md`.

## 4. Camera / UI / management

| File | Responsibility | Attached to |
|------|----------------|-------------|
| `Assets/SimulationManager.cs` | Auto-spawns itself at runtime (`[RuntimeInitializeOnLoadMethod]`); owns ALL on-screen UI (`OnGUI` panel: Pendulum/Paint/Output tabs, sliders, live physics readout, CSV/JSON export). Finds `PaintPhysics`/`PendulumMotion` by type. | none in scene (created at runtime) |
| *(no script yet)* | Camera is the stock **Main Camera** GameObject — no custom controller exists. Task 7 (multi-angle camera) will add one. | **Main Camera** |

## 5. Editor-only tooling (not in builds, not part of the split)

| File | Responsibility | Attached to |
|------|----------------|-------------|
| `Assets/Editor/PaintSimCleanup.cs` | `Tools ▸ Paint Sim` menu: remove duplicate `PendulumMotion`, remove all colliders, apply good launch defaults. | none (editor menu) |
| `Assets/Editor/UnityMCPServer.cs` | Editor-side MCP/HTTP bridge for tooling. | none (editor) |

## 6. Scene object hierarchy (SampleScene.unity) — quick reference

- **Pivot** — pendulum anchor (rope top).
  - **Bucket** — has `PendulumMotion` (physics), a `LineRenderer` + dead `RopeFix`, the bucket mesh/material.
    - **inner / toppoint** (`toppoint` = rope attach point), **paintPoint** (droplet spawn origin).
- **Rope** — `LineRenderer` + `RopeFix` (the visible rope line).
- **PaintStream** — `LineRenderer` + `PaintStream` (disabled).
- **Painter** — the `PaintPhysics` component (all 4 paint files). References paintPoint, canvasRenderer, bucketMotion.
- **Canvas/floor** — the 50 m Plane the paint lands on (its `Renderer` = `canvasRenderer`).
- **Main Camera**, lights, etc.

> The **SampleScene.unity** file is shared by everyone (adding components, materials, a
> camera). Scene-YAML merges are the single biggest conflict risk — coordinate scene edits
> (see `TASK_SPLIT_PROPOSAL.md` §"Shared-file hazards").
