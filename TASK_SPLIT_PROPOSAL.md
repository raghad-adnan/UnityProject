# TASK SPLIT PROPOSAL — 3 people, parallel branches

Derived from the real post–Phase-0 file boundaries (see `MODULE_MAP.md`). The goal of the
split is that **each person edits a disjoint set of files**, so branches merge cleanly.

Effort is rough (S≈small, M≈medium, L≈large). The 9 requested tasks are numbered as in the
brief. Two of them (rope count, particle count) are naturally shared and are split across
owners.

---

## Baseline that blocks everything: Phase-0 restructure (DONE)
The paint MonoBehaviour is now 4 partial files + a filename fix. This is committed as the
shared starting point for all three branches. Nothing below can start until each person
branches from that commit (they already do — the 3 branches are created from it).

---

## Person 1 — **Bucket rig, motion & interaction**
**Owns (edit freely):** `Assets/PendulumMotion.cs`, the Bucket's material asset, and any
new `BucketMouseControl.cs` / `BucketCanvasConstraint.cs` you create.
**Read-only:** all paint files, rope, camera, UI.

| # | Task | Effort | Order |
|---|------|--------|-------|
| 2 | Make the bucket material **transparent** | S | 1st |
| 8 | **Mouse control**: grab / push / move the bucket | L | 2nd |
| 6 | **Bucket-to-canvas collision** (stop it passing through the floor) | L ⚠ | 3rd |

Order rationale: task 2 is a 15-minute material change — do it first so **Person 2 can see
paint leaving the bucket** for their stream/mixing work. Tasks 8 and 6 both modify bucket
motion (`PendulumMotion`), so the same person does them back-to-back with no cross-file churn.

## Person 2 — **Paint physics (emission + particles + surface)**
**Owns (edit freely):** `Assets/BucketEmission.cs`, `Assets/ParticleSimulation.cs`,
`Assets/SurfaceInteraction.cs`, `Assets/PaintStream.cs`.
**Read-only:** `PaintPhysics.cs` core (coordinate before adding a shared field), everything else.

| # | Task | Effort | Order |
|---|------|--------|-------|
| 3a | Raise **liquid** particle count (`maxParticles`) + measure FPS | S–M | 1st |
| 5 | **Hole shapes + liquid stream** flow from holes down to the surface (trajectory realism) | L | 2nd |
| 4 | **Paint colour mixing**: wet droplet on different wet paint blends, not overwrites | L | 3rd |

Order rationale: 3a first (cheap, and more droplets make 5 & 4 easier to see). Task 5 spans
`ComputeHolePattern` (BucketEmission) + spawn trajectory (ParticleSimulation) + optionally
re-enabling `PaintStream` — all Person-2 files. Task 4 is deep inside `SurfaceInteraction`.

## Person 3 — **Rope, camera & UI**
**Owns (edit freely):** `Assets/Ropefix.cs`, `Assets/SimulationManager.cs`, and any new
`CameraController.cs`.
**Read-only:** everything else.

| # | Task | Effort | Order |
|---|------|--------|-------|
| 1 | **Fix the rope** so it actually renders in Play Mode | M | 1st |
| 3b | Raise **rope** segment count for a smoother rope | S | 2nd (after 1) |
| 7 | **Camera system** for multiple viewing angles | M | 3rd |
| 9 | **UI polish**: larger fonts, better-organized layout | S | 4th |

Order rationale: 1 must precede 3b (bumping `segments` only matters once the rope is visible).
Camera (7) and UI (9) are fully self-contained (new file / `SimulationManager` only).

**Balance:** P1 ≈ 7, P2 ≈ 7, P3 ≈ 6 effort-units — roughly even.

---

## Cross-person dependencies to coordinate directly
1. **Person 1 → Person 2 (soft):** Person 1 should land **task 2 (transparent bucket)** early
   so Person 2 can visually validate task 5 (stream leaving the hole) and task 4 (mixing).
2. **Person 3 internal:** task 1 (rope renders) **blocks** task 3b (rope segment count).
3. **Everyone → `SampleScene.unity`:** see hazards below — this is the real integration risk.
4. **Shared `PaintPhysics.cs` core:** if you must add a *serialized* field, prefer adding it in
   *your own* partial file (it still lands on the same component) rather than editing the core
   file, so two people don't both touch `PaintPhysics.cs`.

## Shared-file hazards (flag before you start)
- **`SampleScene.unity` is edited by all three** (P1 adds mouse/collision + swaps bucket
  material; P2 may re-enable PaintStream; P3 adds a camera controller + tweaks the UI object).
  Hand-merging Unity scene YAML is painful. **Mitigation:** either (a) nominate one person to
  own scene integration and merge everyone's component additions last, or (b) make scene edits
  on a short shared `integration` branch one at a time, or (c) enable Unity's *Force Text* +
  *Smart Merge (UnityYAMLMerge)* in `.gitconfig` before you start.

## Risky / ambiguous tasks — plan around these
- **Task 6 — bucket-canvas collision (HIGH risk).** This project **forbids Colliders and
  Rigidbodies** (the `Tools ▸ Paint Sim ▸ Remove All Colliders` menu exists precisely to strip
  them, and it's a stated grading constraint). So you **cannot** solve this with a physics
  collider without instructor approval. It must be **analytic** — clamp the bucket's position
  at the canvas plane inside `PendulumMotion`. It's also ambiguous what should happen on
  contact (stop? slide along? bounce?) because the bucket is an analytic pendulum, not a
  rigid body. **Decide the desired behaviour with the team before coding.**
- **Task 8 — mouse grab/push (MEDIUM-HIGH).** The bucket is driven by an analytic pendulum
  ODE. "Push" ≈ inject angular velocity — reuse the existing space-key *impulse* path in
  `PendulumMotion`. "Grab & move" = override position while dragging, then on release
  re-derive `thetaX/thetaZ/angVel` from the drop point (the code already does exactly this in
  its free-fall re-entry — copy that pattern). Don't fight the ODE; hand control back to it.
- **Task 1 — rope (MEDIUM, scope-dependent).** Quick win first: delete the **dead duplicate
  `RopeFix` on the Bucket** (its refs are null) and confirm the **Rope** GameObject's
  `LineRenderer` has a visible material + non-zero width. That may be all it needs. BUT the
  brief assumed a real mass-spring rope (`RopePhysicsMSD` / `RopeTubeRenderer`) **that does not
  exist** — decide whether the cosmetic Bézier line (`RopeFix`) is acceptable, or whether a
  real physical rope must be built (that is a much larger task, effectively a new module).
- **Task 4 — colour mixing (MEDIUM).** `SurfaceInteraction` currently re-stamps each splat's
  colour every frame and `PutPixel` already Lerps toward the existing pixel. Real wet-on-wet
  mixing needs a per-pixel *wet colour + wetness* buffer so a new droplet blends with paint
  that hasn't dried, and it must play nicely with the per-splat absorption that already dulls
  colour over time. Self-contained but non-trivial.
- **Task 3 — particle/segment count (LOW-MEDIUM, expectations).** Bumping `maxParticles`
  (liquid) and rope `segments` is trivial to type, but "more realism" has diminishing returns
  and a real FPS cost (every droplet stamps into a 1024² texture). **Measure FPS before and
  after; don't over-promise realism gains from raw count.**

## Pre-flight note for the whole team (do this in Unity once)
Phase-0 renamed `PaintDrawer.cs` → `PaintPhysics.cs` to fix a filename/class-name mismatch
that would otherwise stop the `PaintPhysics` component from binding. **Open SampleScene, click
the *Painter* object, and confirm the component reads `Paint Physics (Script)` and NOT
"missing script".** The script GUID was preserved, so all its Inspector values and object
references (paintPoint, canvasRenderer, bucketMotion) should already be intact — but verify
once before branching work begins.
