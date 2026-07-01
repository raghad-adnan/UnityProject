# IMPLEMENTATION LOG V3 — physics audit fixes + 10k particle architecture

This pass fixes the two reported bugs (invisible container liquid, rope/bucket exploding at
start), closes the physics gaps against the Fable Implementation Brief, and redesigns the
particle architecture so SPH runs on **all** particles — inside the bucket and in the air —
up to 10,000.

---

## 1. Bug: rope/bucket "jumping out of bounds" at start — root cause + fix

**Root cause (two layers):**
1. `PendulumMotion` integrated two *decoupled* planar angles and rebuilt the position as
   `x = L·sin(θx), z = L·sin(θz), y = −√(L² − x² − z²)`. This parametrization is only valid
   while `sin²θx + sin²θz ≤ 1`. Beyond that the square root clamps to 0 and the bucket
   position teleports.
2. The scene serializes only the *old* field names (`length`, `initialAngVelX`, …), so the
   current script fell back to its defaults — including `initialAngVel = 2 rad/s`, which on a
   5 m rope carries enough energy to swing the side plane past 90°. That crossed the invalid
   region every swing → position jumps → tension flips sign → slack/free-fall/snap feedback
   loop → the violent bouncing.

**Fix (`Assets/PendulumMotion.cs`):** the pendulum is now a TRUE spherical pendulum in vector
form: state = unit direction (pivot→bob) + tangential velocity; gravity is projected onto the
tangent plane; semi-implicit Euler + position projection back onto the L-sphere; the radial
velocity component is removed each step (inextensible constraint). Tension
`T = m(g·cosθ + v²/L)`; slack (T ≤ 0) → ballistic; rope re-tightens → inelastic snap; elastic
stretch `L_eff = L + T/k`; break tension; drag/damping/Coulomb friction/wind/buoyancy all kept.
This scheme has **no invalid region** — it is robust at any amplitude. Default side push
lowered to 0.5 rad/s for a sane opening demo.

**Added:** `alignWithRope` — the bucket's up-axis now follows the rope (smoothed). This is what
makes the liquid slosh in the bucket frame and makes the paint leave the hole *at the bucket's
angle*, which then shapes the canvas splat (see §4).

Also removed: a duplicate `RopeFix` component that sat on the Bucket itself (the real one is on
the `Rope` object) — it drew a second, overlapping rope every frame.

## 2. Bug: liquid not visible inside the "see-through" container — root cause + fix

`Bucket_Outer_Mat` and `Bucket_inner_Mat` were **fully opaque** (Standard `_Mode: 0`,
`ZWrite 1`, alpha 1). The reservoir particles were spawning and simulating correctly *inside*
the bucket — they were simply hidden behind opaque walls.

**Fix:**
* Both materials converted to Standard **Fade** mode in the asset
  (`_Mode 2, SrcAlpha/OneMinusSrcAlpha, ZWrite 0, queue 3000`) with glass-like tints
  (outer α = 0.25, inner α = 0.12).
* Shadow casting disabled on both renderers (a glass box must not cast a solid shadow).
* New `Assets/BucketCuboidVisualizer.cs` on the Bucket (brief §5.2): re-enforces Fade mode at
  runtime (belt-and-braces), draws the 12 cuboid edge lines with one local-space LineRenderer
  child (follows swing/tilt for free), exposes `validationMode / hideOriginalRenderer /
  glassColor / edgeColor / edgeWidth` in the Inspector. It creates **no** particles of its own.

## 3. 10,000-particle architecture redo

| Problem (old) | Fix (new) |
|---|---|
| One GameObject + MeshRenderer per particle (brief forbids 10k objects) | `PaintParticle` is pure data; `PaintParticlePool.Render` draws all live particles with `Graphics.DrawMeshInstanced`, 1023 per batch (~10 draw calls for 10k), shadows off |
| Standard shader can't batch per-particle colours (no instanced `_Color`) | `Assets/Resources/PaintParticleInstanced.shader` — Lambert surface shader with `_Color` in a `UNITY_INSTANCING_BUFFER` |
| `pool.Get()` scanned the whole list for a free slot — O(n) per spawn, O(n²) while filling | explicit free-index stack, O(1) Get/Return |
| Reservoir (InsideBucket) particles had **no SPH at all** (no pressure/viscosity inside the bucket — brief §5.4 unmet) | ONE unified pipeline: every active particle enters the spatial hash and the two-pass symmetric SPH. World-space forces are valid for the bucket interior because a rigid transform is an isometry; only the **walls** use the bucket-local frame (`ContainInBox`) |
| No neighbour cap → dense clusters degrade to O(local density) | `SpatialGrid.GetNeighbors(pos, maxNeighbors)` hard cap (default 48, brief range 16-64), centre cell walked first so the cap keeps true nearest neighbours |
| `h = 0.25 m` pulled 8× too much volume per query | `h = 0.12 m`, matched to full-reservoir spacing (~0.05 m) |
| EOS `(k=2, ρ0=1)` tuned for sparse droplets — with a dense reservoir it produces pressures ~600 (permanent boiling) | retuned `(k=150, ρ0=275)`: packed-at-rest density feels no pressure, ~20 % compression balances gravity — liquid pools and stacks like a free-surface fluid |
| Per-frame `*= 0.96` damping was frame-rate dependent | `pow(dampingFactor, dt·60)` — identical decay at any FPS |
| Fill hitch risk | staged spawning, `spawnPerFrame` (default 150, brief: 100-500) |
| Fixed pool size | pool + grid pre-allocated at `HardMaxParticles = 10000`; `maxParticles` is a runtime soft budget. SimulationManager buttons: **Safe 2k / Strong 5k / Stress 10k** |

New live metrics (SimulationManager → Paint & Output tabs): FPS, active particles,
inside-bucket / airborne split, average SPH neighbours, current budget.

## 4. Emission & impact physics corrections (brief §5.5–5.6)

* **Release happens AT the hole**: the particle nearest the hole (bucket-local) is chosen,
  repositioned to `bucket.TransformPoint(holeLocal + holeShapeOffset)` and released. Before,
  the "poured" particle simply switched state wherever it happened to be, and the computed
  hole-shape offset was discarded.
* **Exit velocity** = full bucket velocity (was 0.3×) + `bucket.TransformDirection(exitDirection)·v_exit`
  (was world-down regardless of tilt) + spread rotated into the bucket frame. A swinging,
  tilted bucket now throws paint at its actual angle.
* **Oblique impact** (`SurfaceInteraction.OnDropletImpact`): impact velocity is decomposed into
  normal + tangential components. Splash physics (We/Re/Stow-Hadfield K) and Madejski spread
  use the **normal** speed (standard oblique-impact treatment); the tangential component
  elongates the splat into an **area-preserving ellipse** along the sliding direction
  (aspect ≤ 3:1) and biases the satellite-droplet scatter downstream. The angle at which the
  paint left the bucket is now visible in the mark on the canvas.

## 5. Unchanged (verified correct)

* Two-pass symmetric SPH kernels (Poly6 / Spiky / viscosity Laplacian, Müller 2003;
  momentum-conserving Monaghan pressure force).
* Surface model: Tate drop size, Madejski spread, Stow-Hadfield splash + roughness-lowered
  threshold, Tanner/de Gennes spreading, Lucas-Washburn per-splat absorption, Nusselt
  thin-film downhill flow, Beer-Lambert hiding power, Wenzel roughness, 4 surface presets.
* Canvas crossing stays analytic (plane sign change + interpolation) — no colliders anywhere.

## 6. How to test

1. Open `Assets/Scenes/SampleScene.unity`, press Play. Console must stay clean.
2. Bucket swings smoothly from the pivot (no jumping), tilting with the rope; the Catmull-Rom
   rope follows it.
3. The bucket is a transparent glass cuboid with blue edge lines; coloured paint particles are
   visible pooling and sloshing inside as it swings.
4. The same particles stream out of the glowing hole ring, fall, and paint elongated,
   angle-dependent splats + scatter on the canvas.
5. Paint tab → try **Safe 2k / Strong 5k / Stress 10k**; watch FPS / active / avg-neighbour
   counters. 10k fills gradually (staged spawning).
6. Keys: Space = impulse, S = save PNG, R = clear. Right-drag tilts the floor (thin-film flow).

Files changed: `PendulumMotion.cs`, `PaintParticle.cs`, `PaintParticlePool.cs`,
`SpatialGrid.cs`, `ParticleSimulation.cs`, `BucketEmission.cs`, `SurfaceInteraction.cs`,
`PaintPhysics.cs`, `SimulationManager.cs`, `Bucket_inner_Mat.mat`, `Bucket_Outer_Mat.mat`,
`SampleScene.unity`. New: `BucketCuboidVisualizer.cs`, `Resources/PaintParticleInstanced.shader`,
this log.
