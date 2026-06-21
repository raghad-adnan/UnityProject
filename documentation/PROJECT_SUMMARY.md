# Project Summary - Swinging Paint Bucket Simulation

## What is this project?

A Unity 3D physics simulation of a paint bucket hanging on a rope from a fixed
pivot. The bucket swings as a 3D pendulum and releases paint (as particles)
from a hole in its base onto a canvas below, producing spiral / geometric
paint patterns driven by real physics equations.

It is an academic project (Damascus University - "Virtual Reality" course).
It is a **3D desktop simulation**, not a headset-VR app.

---

## Hard constraints (from the assignment)

- **No Unity physics tools** (RigidBody, Colliders, ready-made libraries) without
  instructor approval. The whole simulation is hand-written math.
- Unity Engine + C# only.
- Built-in Render Pipeline (not URP). Unity 6 (6000.4.4f1).

---

## Scene structure

```
Main Camera
Directional Light
Pivot (0, 3, 0)
  └── Bucket            (PendulumMotion, RopeFix, LineRenderer)
        ├── inner       (visual)
        │     ├── toppoint     (rope attach reference)
        │     └── PaintPoint   (paint drip origin)
        └── PaintPoint
Canvas (0, -3, 0)       (receives paint; Texture2D)
Painter                 (PaintPhysics - the particle/paint system)
PaintStream             (PaintStream - visual stream line)
Rope                    (RopeFix - rope LineRenderer)
SimulationManager       (auto-created at runtime - UI + reporting)
```

---

## Scripts (current)

| File | Class | Role |
|---|---|---|
| `PendulumMotion.cs` | PendulumMotion | 3D two-angle pendulum physics |
| `PaintParticle.cs` | PaintParticle | One paint particle (data + state) |
| `PaintParticlePool.cs` | PaintParticlePool | Object pool for particles |
| `PaintDrawer.cs` | PaintPhysics | Emission + particle motion + canvas painting |
| `FluidConstants.cs` | FluidConstants | Re/We/Ca/Oh + Young's equation |
| `SurfacePreset.cs` | SurfacePreset | Surface data (spread/absorption/roughness) |
| `PaintStream.cs` | PaintStream | Visual stream line bucket -> canvas |
| `RopeFix.cs` | RopeFix | Rope LineRenderer (Bezier sag) |
| `SimulationManager.cs` | SimulationManager | Runtime UI, logging, export, reset |
| `Editor/PaintSimCleanup.cs` | PaintSimCleanup | Editor helper menu |

---

## Technology

- Unity 6 (6000.4.4f1), Built-in Render Pipeline
- C#, Input System package
- LineRenderer (rope + stream), Texture2D (canvas)
- Object pooling for paint particles
- MCP server on port 6400 (Claude Code bridge)

See `PROJECT_DOCUMENTATION.docx` for a full code walkthrough,
`REQUIREMENTS_AUDIT.md` for task coverage, and `CURRENT_PROGRESS.md` for status.
