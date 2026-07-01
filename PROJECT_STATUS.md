# PROJECT STATUS — start here

This branch (`khaled-surface-and-restructure`) is the shared, working baseline for the paint-
bucket simulation after the **Phase-0 restructure**. It compiles cleanly and behaves exactly
like the pre-restructure project. Read this file first, then `TASK_SPLIT_PROPOSAL.md`.

---

## 1. What has been done (restructured)

The one giant paint script was split into **four files that all compile into the *same*
`PaintPhysics` component** (C# `partial class`), so we can work on different systems without
editing the same file. Behaviour, physics, visuals and output are **unchanged** — it was a
pure source-file relocation, verified by a headless compile (0 errors) and a formula/field
audit.

| File | Responsibility |
|------|----------------|
| `Assets/PaintPhysics.cs` | shared fields, `Start`/`Update` order, floor tilt, hotkeys |
| `Assets/BucketEmission.cs` | bucket reservoir, hole shape, droplet emission |
| `Assets/ParticleSimulation.cs` | in-air droplet physics + `OnDropletImpact(...)` hand-off |
| `Assets/SurfaceInteraction.cs` | splat / spread / flow / absorption / texture output |

- The old `PaintDrawer.cs` was renamed to `PaintPhysics.cs` (its script GUID was kept, so the
  scene's `Painter` object still binds and keeps all its Inspector values/references).
- **Full ownership map:** see **`MODULE_MAP.md`** — it lists every file, its one-line job, and
  which GameObject it lives on.

### One thing to verify in Unity (2 min)
Open `Assets/Scenes/SampleScene.unity`, click the **Painter** object, and confirm its component
shows **"Paint Physics (Script)"** and *not* "missing script". (This confirms the rename bound
correctly — it can't be verified outside the Editor.)

---

## 2. What remains to be done (split among the team)

Nine tasks are still open (bucket transparency, more particles, colour mixing, hole/stream
shapes, bucket-canvas collision, camera system, mouse control, UI polish, and finishing the
rope). A concrete, conflict-minimizing 3-way division — with working order, dependencies, and
risk flags — is written in **`TASK_SPLIT_PROPOSAL.md`**.

Highlights to be aware of before dividing:
- **Rope:** there is no mass-spring rope in the project. The rope is a cosmetic `RopeFix`
  line; the real pendulum physics is `PendulumMotion`. Decide whether the cosmetic rope is
  enough or a real one must be built.
- **Bucket-canvas collision** must be done *analytically* — Colliders/Rigidbodies are not
  allowed in this project without approval.
- **Everyone edits `SampleScene.unity`** — that's the biggest merge risk; coordinate scene edits.

---

## 3. Instructions for teammates

1. **Pull this branch:**
   ```
   git fetch origin
   git switch khaled-surface-and-restructure
   ```
2. **Read** this `PROJECT_STATUS.md` and then `TASK_SPLIT_PROPOSAL.md` (and `MODULE_MAP.md` for
   file ownership).
3. **Agree among yourselves** how to divide the remaining 9 tasks (the proposal is a starting
   suggestion, not a mandate).
4. **Create your own branch from here** and work on your files, e.g.:
   ```
   git switch -c <your-name>-<your-system>   # branched from khaled-surface-and-restructure
   ```
   Then commit only your system's files. Keep scene edits coordinated.

> Local-only helper branches `person1-bucket-motion`, `person2-paint-physics`,
> `person3-rope-camera-ui` exist on Khaled's machine but were intentionally **not** pushed —
> create your own branches as above.
