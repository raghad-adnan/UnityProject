# Swinging Paint Bucket — Unity/C# Physics Simulation

A physically-based simulation of a paint bucket swinging on a rope as a **true spherical
pendulum**, with the paint modelled as an **SPH particle liquid** that is visible inside a
transparent rectangular container, pours out of a hole in the bucket, free-falls under gravity
and real air drag, and paints a canvas with **angle-dependent, non-perfect splats** whose shape
and spread follow published fluid-dynamics relations.

Everything is custom math — **no Rigidbody, no Colliders, no Joints, no built-in physics**.
The same particle objects transition `InsideBucket → Emitted → Falling → Collided/Painted`
(one particle system end to end; there is no separate decorative liquid).

---

## 1. Quick start

1. Open `Assets/Scenes/SampleScene.unity` in Unity **6000.4.4f1** and press **Play**.
2. The bucket swings from the pivot; paint particles are visible sloshing inside the glass
   cuboid; they pour out of the glowing hole ring, fall, and paint the canvas.
3. Use the control panel (top-left) to change every parameter live.

**Controls**

| Input | Action |
|---|---|
| Space | Impulse kick to the pendulum |
| S | Save the painting as PNG (`Assets/Painting_*.png`) |
| R | Clear the canvas |
| Right-mouse drag | Tilt the floor (pitch/roll) — paint then flows downhill |
| Panel → Output tab | Reset / Clear / Save / Refill / Export CSV / Export JSON |

**Performance modes** (Paint tab): Safe **2k** / Strong **5k** / Stress **10k** — sets how many
drops fill the bucket. The pool and spatial hash are pre-allocated at 12,000 once, so switching
modes never reallocates; the reservoir fills gradually (staged spawning, 150/frame).

---

## 2. Scene & architecture

```
Pivot                         (fixed suspension point)
 └── Bucket                   PendulumMotion + BucketCuboidVisualizer (glass cuboid + edge lines)
      ├── inner               second glass cube = container wall thickness
      ├── PaintPoint          legacy reference point
      ├── CuboidEdges         runtime LineRenderer wireframe (12 edges)
      └── HoleHighlight(s)    runtime glowing ring(s) at the hole exit
Rope                          RopeFix — Catmull-Rom rope visual (physics live in PendulumMotion)
Canvas                        Plane (15 m × 15 m default) that receives the painting texture
Painter                       PaintPhysics — ONE component split across four partial-class files
SimulationManager             created at runtime — control panel UI + metrics + exports
```

| File | Responsibility |
|---|---|
| `Assets/PendulumMotion.cs` | Spherical-pendulum physics (torque form), rope tension/slack/break/elastic, bucket alignment |
| `Assets/PaintPhysics.cs` | Shared state, Start/Update order, canvas size/tilt/vibration, unit scale, hotkeys |
| `Assets/BucketEmission.cs` | Drop accounting (Tate), reservoir fill, hole shapes, Torricelli emission, hole rings |
| `Assets/ParticleSimulation.cs` | Unified SPH + integration for ALL particles, containment, canvas-plane crossing |
| `Assets/SurfaceInteraction.cs` | Impact splats, splash, spreading, absorption, thin-film flow, texture output |
| `Assets/SPHSolver.cs` | Müller-2003 SPH kernels, two-pass symmetric pressure + viscosity forces |
| `Assets/SpatialGrid.cs` | Zero-allocation spatial hash (linked-cell), neighbour cap |
| `Assets/PaintParticle.cs` | Pure-data particle record + state enum |
| `Assets/PaintParticlePool.cs` | O(1) free-list pool + GPU-instanced rendering (no GameObjects) |
| `Assets/BucketCuboidVisualizer.cs` | Transparent cuboid validation mode + edge wireframe |
| `Assets/Ropefix.cs` | Catmull-Rom rope spline visual (4 rope material presets) |
| `Assets/SurfacePreset.cs` | Measured material data for Canvas/Wood/Metal/Paper |
| `Assets/FluidConstants.cs` | Library of documented SI fluid relations (all formulas below) |
| `Assets/SimulationManager.cs` | Dark themed IMGUI panel, metrics, experiment compare, CSV/JSON export |
| `Assets/Resources/PaintParticleInstanced.shader` | Instanced-`_Color` sphere shader (1023 particles/draw call) |

---

## 3. The physics, start → splash

### 3.1 Rope & bucket — true spherical pendulum (torque form)

State: unit direction `r̂` (pivot → bucket) and angular velocity `ω ⊥ r̂`. Each fixed step:

```
dω/dt = (r × a_ext) / L²        r = r̂·L,  a_ext = g + drag + damping + friction + wind
r̂     ← rotate r̂ about ω by |ω|·dt      (exact motion on the rope sphere)
v      = ω × r
```

This is the constraint form of `r̈ = g − (g·r̂ + |v|²/L)·r̂`, i.e. the classical spherical
pendulum `θ̈ = sinθcosθ·φ̇² − (g/L)sinθ` with the azimuthal invariant `Lz = m·L²·sin²θ·φ̇`.
Because gravity's torque `r × g` has **zero vertical component**, the integrator conserves
`Lz` *structurally* — measured drift < 0.06 % over 50 simulated minutes with dissipation off.
That invariant is what distinguishes one spherical system from two decoupled planar pendulums
(enable **Validation mode** to log it live, together with the measured vs theoretical period).

Included force/rope models (all custom):

* **Tension** `T = m(g·cosθ + v²/L)` — centripetal balance; shown live.
* **Slack rope**: when `T ≤ 0` the bob leaves the sphere and flies ballistically; when
  `|r| ≥ L` again the rope snaps taut and the radial velocity is absorbed (inelastic jerk).
* **Elastic rope** `L_eff = L + T/k` (stiffness slider), **break tension**, unlimited amplitude.
* **Quadratic air drag** `a = −(ρ·C_d·A/2m)|v|v`, **linear damping**, **Coulomb pivot
  friction** `−μg·v̂`, **wind** (drag on relative velocity), **buoyancy** option.
* **Large-amplitude period** `T ≈ 2π√(L/g)·(1 + θ₀²/16 + 11θ₀⁴/3072)` for validation.
* **Bucket alignment**: the bucket's up-axis smoothly follows the rope, so the container tilts
  with the swing — this is what makes the liquid slosh and the pour direction change.
* Initial conditions cover the full spherical state: θ₀, φ₀, azimuthal push (φ̇₀) and polar
  push (θ̇₀).
* The bucket's **mass falls live** as paint leaves (`m = m_empty + m_paint(t)`), changing the
  dynamics exactly as much as the paint that has actually poured out.

The rope you see is a **Catmull-Rom spline** through a parabolic catenary approximation whose
sag responds to live tension/slack (`RopeFix`); the physics is entirely in `PendulumMotion`.

### 3.2 Liquid inside the bucket — SPH

The container liquid is real `PaintParticle` objects (the same ones that later fall), simulated
by **Smoothed-Particle Hydrodynamics** (Müller, Charypar & Gross 2003), two-pass and symmetric
(Monaghan 1992) so pressure forces conserve momentum:

* **Pass 1 — density/pressure**: Poly6 kernel `W = 315/(64πh⁹)·(h²−r²)³`; EOS
  `p = k(ρ−ρ₀)/ρ₀`, clamped ≥ 0 (repulsion-only — avoids the SPH tensile instability).
  Tuned `(k=150, ρ₀=275)` so the reservoir pools like a free-surface liquid: below packed
  density there is no pressure, ~20 % over-compression balances gravity.
* **Pass 2 — forces**: Spiky-gradient pressure `a_i = −Σ m(p_i/ρ_i² + p_j/ρ_j²)∇W` and
  viscosity Laplacian `a_i = (μ/ρ_i)Σ m (v_j−v_i)/ρ_j ∇²W`.
* **Containment**: walls are enforced in the bucket's **local frame** (unit cube ±0.5), so the
  moving, rotating box drags and tilts the liquid — real slosh from the swing. SPH forces are
  evaluated in world space, which is exact because a rigid transform is an isometry.
* Smoothing radius `h = 0.12 m` (matched to the mean spacing of a full 10k reservoir) is also
  the spatial-hash cell size.

### 3.3 Drop accounting — the paint in = the paint out

* **Tate's law** (with Harkins–Brown correction Φ≈0.6 and a capillary-number viscosity term)
  gives the reference drop pinched off the hole:
  `V = Φ·2πr_hole·γ/(ρg)·(1+Ca)` — shown live as "Tate reference Ø".
* The user picks how many drops N represent the bucket (2k/5k/10k modes); each particle then
  carries **exactly** `m_drop = M_paint/N` and the sphere diameter of that mass. So: N drops in
  the container, the same N drops out of the hole, each release drains its own mass from the
  bucket — `Σ drop masses ≡ bucket paint mass` at all times, and the bucket gets lighter by
  exactly what has left.
* If N drops can't visually fit the container, only the **rendered** size auto-shrinks
  (55 % random-loose-packing bound); physical sizes/masses are untouched.

### 3.4 Emission through the hole — Torricelli orifice discharge

The pour rate is **fully physical** (the old empirical rate formula is gone):

```
m_dot = ρ · (A_hole · valve) · C_d·√(2·g·h_head)        [kg/s]
drops/s = m_dot / m_drop
```

* **h_head** — the real hydrostatic head: geometric fill fraction (`V_paint/V_bucket`) times
  bucket height, projected by the bucket's tilt. As paint drains the head falls, so the pour
  weakens smoothly and stops when the level reaches the hole — like a real leaking bucket.
* **A_hole** — the true area of the selected hole **shape**: Round `πr²`, Narrow slit `4r²`
  (8r × 0.5r), Wide band `18r²` (12r × 1.5r), Multiple `3πr²` (three holes 6r apart). Shapes
  genuinely pour at different rates because their areas differ; drop pinch-off uses each
  shape's hydraulic rim radius `r_h = 2A/P` in Tate's law.
* **valve** — the flow-rate input, physically the fraction of the hole that is open.
* **Viscosity vs temperature** — Arrhenius/Andrade `η(T) = η_ref·e^{B(1/T−1/T_ref)}` (B = 3000 K),
  replacing the old linear lerp.
* **Slosh** — quasi-static free-surface tilt `tan β = a_lat/g` can wash paint over a raised hole
  (viscosity correctly does NOT enter the steady surface tilt).
* Exit velocity = **full bucket velocity** + **Torricelli jet** `C_d√(2gh)` (`C_d = 0.61`,
  sharp-edged orifice) along the **tilted bucket axis** + **bucket-spin fling** `ω_spin × r`
  (real for off-axis holes) + a **Reynolds-dependent jet spread** (laminar jets stay coherent
  ~1.5°, turbulent fan to ~10° — Lin & Reitz 1998), i.e. spread speed = `v_exit·tan σ`.
* the InsideBucket particle **nearest the hole** leaves first; its state flips to `Emitted` —
  same object, same size, same colour, same mass.

**Bucket spin (فتل الدلو):** the bucket can rotate about its own rope axis — an initial spin
input integrated as a 1-DOF rotor `I·ω̇ = −κ·θ_twist − τ_air` (rope torsional spring κ +
quadratic air drag on the rotating walls, `I = m(w²+d²)/12`). The rotating transform sweeps the
hole pattern, drags the contained liquid through the wall constraint, and flings drops
tangentially from off-axis holes.

### 3.5 Free fall

Falling drops keep SPH interaction (stream coherence) plus:

* gravity;
* **real quadratic sphere drag** `a = (ρ_air·C_d·A/2m)|v_rel|v_rel` (`C_d = 0.47`), computed
  from each drop's actual size/mass **relative to the wind** — under ~2 m/s² for these drops,
  so the horizontal throw inherited from the swing survives to the canvas (this is what makes
  impacts genuinely oblique). The Wind sliders blow the falling paint.

### 3.6 Impact — angle-dependent, non-perfect splats

Canvas crossing is detected **analytically** (plane sign change + segment interpolation — no
colliders). The impact velocity is decomposed into normal `v_n` and tangential `v_t`:

* **Splash vs deposition** — Stow & Hadfield (1981) `K = √We·Re^0.25` built from `v_n` at the
  physical 3 mm drop scale, against a **roughness-lowered threshold**
  `K_c = 57.7(1 − α·Ra/Ra_ref)` (rough canvas splashes sooner than smooth metal).
* **Spread** — Pasandideh-Fard/Madejski maximum spread
  `β_max = √((We+12)/(3(1−cosθ*) + 4We/√Re))` with the **Wenzel** apparent contact angle
  `cosθ* = r_w·cosθ_Young` (roughness amplifies wetting).
* **Stain shape** — the classic impact-stain relation `width/length = sin(impact angle)`:
  a vertical drop leaves a circle; oblique drops leave a **comet/teardrop** (round head at
  first contact, tail tapering downstream), area-preserving, capped 4:1.
* **Satellite droplets** — Rayleigh–Taylor finger count `N = √(β·We/12)`, rim-scale sizes,
  stochastic ejection. Ejection is **directional** (Bird, Tsai & Stone 2009): a full ring for
  normal impact narrowing to a downstream fan as the impact grazes; downstream satellites land
  farther; at grazing angles each satellite smears into a streak. Optional **crown splash**
  ring (suppressed on oblique hits, where the crown physically tears open).
* **Hiding power** — Beer–Lambert opacity `1 − e^(−h/h_hide)` over the true substrate colour:
  thin paint reveals grey metal / brown wood / cream canvas.
* **Kubelka–Munk colour mixing** — a drop landing on paint that is still wet merges with it:
  per RGB band `K/S = (1−R)²/2R` (Kubelka & Munk 1931), mixtures follow Duncan's additivity
  `K/S_mix = Σ cᵢ(K/S)ᵢ` weighted by the two merged **volumes** (existing film thickness vs new
  deposit), then `R_mix = 1 + K/S − √((K/S)² + 2K/S)`. This is real subtractive paint mixing:
  blue on yellow gives **green**, red on blue gives purple — an RGB average would give grey.
  Below ~5 µm of existing film the drop keeps its own colour (nothing to mix with). Splash
  satellites and the crown carry the mixed colour (they are ejected from the merged lamella).
* **Continuous jet** — at pouring rates the stream is an unbroken liquid column, so successive
  impact points are bridged into one connected trace (band of the jet's footprint width)
  whenever they land within the coherence gap.

### 3.7 After landing — surface behaviour per material

Each splat stays "active" and keeps evolving:

* **Tanner/de Gennes spreading** — `R(t) = R_final(t/t_v)^{1/10}` with capillary relaxation
  `t_v = ηR/(γθ_eq³)`; complete-wetting surfaces (θ*→0) switch to an absorption-limited spread.
* **Lucas–Washburn absorption** — per-splat clock, `depth = √(r_pore·γ·cosθ_Young/(2η)·t)`;
  porous surfaces dull the mark toward the substrate as paint soaks in; humidity slows it.
* **Thin-film gravity flow** — on a tilted floor, once gravity beats the contact-angle
  **pinning force** `γ(cosθ_rec − cosθ_adv)/R`, the splat runs downhill at the **Nusselt** film
  velocity `u = ρg·sinα·h²/(3η)`, drawing mass-conserving rivulets, with **Rayleigh–Taylor
  dripping** past the critical thickness `h_c = √(γ/(ρg·sinα))`.
* **Surface presets** (measured data): Canvas, Wood, Metal, Paper — contact angle, porosity,
  pore radius, Wenzel roughness, Ra, hysteresis angles, substrate colour. Switching surface
  visibly changes splash threshold, spread, absorption and colour.
* Canvas extras: size sliders (physical metres), tilt (slider or right-drag), **surface
  vibration** (in-plane shake that smears landings).

---

## 4. Performance architecture (10,000 particles)

The brief forbids 10k GameObjects, O(n²) loops, per-particle colliders and single-frame spawns.

| Technique | Implementation |
|---|---|
| Data-oriented particles | `PaintParticle` is plain data — zero GameObjects/Transforms/Renderers |
| O(1) pooling | free-index stack; pool + hash pre-allocated once at 12,000 |
| GPU instancing | `Graphics.DrawMeshInstanced`, 1023/batch, custom shader with per-instance `_Color` in an instancing buffer (~10 draw calls for 10k), shadows off |
| Spatial hash | linked-cell hash, O(1) insert, 27-cell queries, zero per-frame allocation |
| Neighbour cap | 48 per particle (brief range 16-64), centre cell walked first |
| Single gather | each particle's neighbour slice is collected once per frame and reused by both SPH passes |
| Kernel caching | Poly6/Spiky/viscosity normalisations precomputed when `h` changes — removed ~1.4 M `Mathf.Pow` calls/frame at 10k (measured 6 fps → interactive) |
| Staged spawning | 150 particles/frame; a 10k reservoir fills in ~2 s with no hitch |
| State separation | painted/expired particles return to the pool; texture `Apply()` only when dirty |
| Live metrics | FPS pill, active/inside/airborne counts, average SPH neighbours, budget |

---

## 4b. GPU simulation mode (up to 200,000 particles)

The CPU path above tops out around 10k: one core walking 2×27 hash cells per particle per
frame is the hard ceiling, and GameObjects were never an option (a Transform + renderer +
culling per particle freezes the editor at a few thousand). GPU mode moves the **same
liquid** — same bucket, same Torricelli pour, same canvas — onto compute shaders, where
particles are rows in a `StructuredBuffer` updated by thousands of GPU cores.

**Files**

| File | Role |
|---|---|
| `Resources/LiquidSPH.compute` | 12-kernel pipeline: dead-list spawn → predict → hash grid → PBF constraint iterations → velocity/XSPH → state transitions (hole emission, canvas crossing) → alive list + indirect args |
| `Resources/GpuCanvasPainter.compute` | splat events → `RenderTexture` (indirect dispatch, one group per splat) |
| `Resources/GpuPaintParticle.shader` | sphere-impostor billboards for `RenderMeshIndirect` |
| `GpuLiquidSimulation.cs` | buffer owner + dispatcher; auto-tunes `h`/rest density from bucket volume ÷ count |
| `GpuLiquidRenderer.cs` | ONE indirect draw call for all particles; instance count written by the GPU |
| `GpuLiquidBridge.cs` | the seam to the existing system — bootstraps itself at Play, no scene edits |

**How it connects to the existing bucket (nothing was replaced)**

* `PendulumMotion` still owns the swing and the paint mass; `BucketEmission.EmitStep`
  still computes fill level, slosh submergence, Torricelli efflux and hole-shape areas
  every frame. In GPU mode it skips only the per-particle spawn/release loops
  (`gpuMode` guards); the bridge converts those same readouts into GPU uniforms.
* The bucket's `localToWorld`/`worldToLocal` matrices are uploaded each frame. Contained
  particles are clamped inside the unit cube in **bucket-local space** every PBF
  iteration, so wall motion (swing/tilt/spin) becomes particle displacement, and
  `v = (x* − x)/dt` turns that into momentum — the slosh follows the swing for free.
* The same GPU particle transitions `InsideBucket → Falling → splat`: emission
  repositions it at the hole with bucket velocity + Torricelli jet + Re-dependent spread
  + spin fling (the CPU formulas, verbatim), and the canvas crossing appends a splat
  event that `GpuCanvasPainter` rasterises into a RenderTexture. Mass stays exact: a
  persistent GPU counter of actually-emitted drops is read back asynchronously and
  debited via `ConsumePaint(count × perDropMass)`.

**Why PBF instead of explicit SPH at 200k** — at 200k the rest spacing is ~6 mm and
h ≈ 1.2 cm; explicit (state-equation) SPH is then CFL-limited to sub-millisecond steps
(≈15 substeps/frame). Position-Based Fluids (Macklin & Müller 2013) projects the density
constraint 2–4 times per frame and is unconditionally stable, with λ clamped to
compression only (the PBF analogue of the CPU's "pressure ≥ 0" rule).

**Scaling rules kept** — no `GetData` (stats via `AsyncGPUReadback` every 15 frames);
no O(n²) (bounded-capacity hash grid, 2¹⁷ cells × 32 slots, 27-cell queries, neighbour
cap); no GameObjects (indirect draw, GPU-side instance count); buffers allocated once
per preset and released in `OnDisable`/`OnDestroy`.

**Presets** (Paint tab → "GPU simulation"): 10k (safe default, 4 iterations / 48
neighbours) · 50k (3/40) · 100k (3/32) · 200k (2/28) — plus a Performance-mode toggle
that trims further. Starts at 10k; 200k is a manual, warned choice.

**Known limitations (GPU mode)** — GPU painting stamps oblique elliptical splats but not
the CPU path's film flow / Lucas-Washburn absorption / Kubelka-Munk mixing (CPU mode
keeps all of that); a hash cell overflows past 32 entries under extreme transient
compression (skipped for that frame's neighbour list only); paint-coverage % reads the
CPU texture, so it reports 0 in GPU mode; per-cell capacity and splat events cap at
4096/frame.

---

## 5. Control panel guide

Dark IMGUI panel (procedurally themed — no asset dependencies). Header shows an FPS
pill (green/amber/red) and the live particle count on every tab.

**Every numeric input is typeable**: each slider has a value box — click it and type the exact
number you want (partial input like `0.` is preserved while typing; values clamp to the valid
range). Font sizes are enlarged for comfortable reading.

* **Pendulum** — rope length, release angle, azimuthal + polar push **and the same launch state
  as linear speeds in m/s** (v = ω·L, applied on Reset), swing direction, max swings; **bucket
  spin** (initial spin + rope torsion κ + live spin readout); masses, **valve opening** (the
  physical flow-rate input), gravity, air density/drag/**frontal area**, pivot friction,
  damping, wind, rope stiffness/break; toggles: elastic rope, buoyancy, validation mode.
* **Paint** — performance card + **exact particle-count field (type any number 100–10,000)**
  with 2k/5k/10k shortcut buttons + SPH toggle; viscosity, temperature, humidity; hole radius
  (millimetre scale)/height, **bucket half-width & height (drive the real container size)**,
  hole shape with a live pour card (g/s, drops/s, exit speed, fill level); floor pitch/roll,
  canvas size, surface selector; vibration, continuous jet, crown splash; a live
  **surface-physics card** (θ_Young/θ_Wenzel, Ra, porosity, drop Ø vs Tate, K vs K_c verdict,
  Washburn depth, film velocity, drip threshold, px/m scale); paint colours with swatches +
  3-colour multi-swing mode.
* **Output** — Reset/Clear/Save PNG/Refill/Export CSV/Export JSON/Capture experiment; report
  card (FPS, counts, motion time, paths, trajectory length, coverage) and the **spherical
  pendulum card** (θ, φ, precession rate φ̇, angular momentum Lz, mass, tension, energy, period).
* **Compare** — capture any number of runs and compare inputs/outputs side by side.

---

## 6. What was implemented / fixed in this pass (change log)

1. **Pendulum rewritten** as a true spherical pendulum: first from two decoupled planar angles
   (which exploded past 90° and produced NaN positions) to a vector constraint form, then to
   the **torque/angular-momentum form** that conserves Lz structurally. Added bucket-rope
   alignment, full spherical initial conditions and spherical readouts + validation logging.
2. **Container made a real transparent rectangular cuboid** — both meshes were Unity
   *cylinders* with opaque materials; now Cube meshes with Standard-Fade glass materials,
   no shadow casting, runtime edge wireframe and validation-mode parameters
   (`BucketCuboidVisualizer`). Visual volume now exactly equals the containment volume.
3. **Particle architecture redone for 10k** (table above) — data-oriented pool, instanced
   rendering + custom shader, neighbour caps, kernel caching, single gather, staged spawning,
   SPH extended to the in-bucket reservoir (it previously had no particle interaction at all).
4. **Exact drop accounting** — reservoir count ↔ paint mass ↔ per-drop mass/diameter are one
   consistent bookkeeping (Tate reference shown); the 2k/5k/10k buttons set the true in-bucket
   count (the old budget split silently capped "10k" at 8000).
5. **Emission corrected** — release at the hole with the hole-shape offset (previously drops
   just switched state wherever they were), full bucket-velocity inheritance, Torricelli exit
   jet along the tilted bucket axis (was a ~0.04 m/s leak straight down).
6. **Free fall corrected** — replaced a frame-rate-dependent 0.96×/frame damper (which erased
   the horizontal throw mid-fall) with real quadratic sphere drag + wind coupling.
7. **Impact made angle-dependent** — normal/tangential decomposition, comet stains
   (width/length = sinθ), directional satellite fans with streaks, crown suppression on
   oblique hits; splat geometry persists through capillary regrowth.
8. **Visibility scale fixed** — marks are built from the visually-scaled drop and the canvas
   default is 15 m (~68 px/m); previously every stain collapsed to the 2 px minimum.
9. **Panel redesigned** — compact modern dark theme, sections/cards, value column, FPS pill,
   toggle chips, colour swatches; every original control and stat kept.
10. **Cleanup** — duplicate `RopeFix` removed from the Bucket, dead knobs removed, and all
    scattered docs consolidated into this single README.
11. **Torricelli emission** — the empirical pour-rate formula (`baseEmission × (0.2+tilt) ×
    (1+ω) × 1/η …`) replaced by the physical orifice discharge `ṁ = ρ·A_shape·valve·C_d√(2gh)`
    with a real geometric fill level; hole shapes now have true areas (their pour rates differ)
    and millimetre-scale radii; bucket resized to a realistic geometry (0.4 × 0.45 m, drives
    the transform from the panel) with 5 kg of paint → ~70 s pour.
12. **Kubelka–Munk wet-on-wet colour mixing** (per-band K/S with Duncan additivity, volume
    weighted) — drops landing on wet paint of another colour form the real mixed pigment
    colour; satellites/crown inherit it. Continuous-jet toggle now actually bridges coherent
    stream traces (it previously did nothing).
13. **Refill/Reset = real bucket swap** — every existing particle (old colour, in bucket or in
    flight) is purged and the reservoir refills with the newly selected colour.
14. **Bucket spin** — 1-DOF rotor about the rope axis (initial spin, rope torsion spring,
    quadratic wall air-drag); spins the hole pattern, sloshes the liquid, flings drops from
    off-axis holes (`ω×r`).
15. **More physical replacements** — Arrhenius viscosity–temperature (was linear lerp),
    buoyancy from the true displaced material volume (was a fixed 0.005 m³ knob), free-surface
    slosh tilt `tan β = a/g` (viscosity removed from the static tilt), Reynolds-dependent jet
    spread (was an arbitrary constant), near-inelastic viscous wall restitution, drag frontal
    area defaulting to the real bucket cross-section.
16. **Panel input overhaul** — every slider gained a typeable exact-value box, the particle
    count is a free numeric field (100–10,000), launch speed can be typed directly in m/s,
    and all fonts were enlarged for readability.
17. **Dead code removed** — `PaintStream` (self-disabling stub) deleted from code + scene;
    unused `StampRing`/`StampEllipse`/`PutPixel`, legacy SPH overloads, superseded
    `TannerRelaxTime`, and the `holeFactor`/`baseSize`/`baseSpread`/`minViscosity`/
    `maxViscosity`/`bucketVolume` knobs; leftover `Assets/XRI` settings (no XR package is
    installed) and the `_Recovery` scene.

---

## 7. Validation & acceptance checks

* **Period**: validation mode logs measured vs `2π√(L/g)(1+θ₀²/16+…)`.
* **Energy**: drift window logged; conservative settings stay bounded.
* **Sphericity**: `Lz` drift logged — < 0.06 % over 50 sim-minutes with dissipation off
  (verified in a standalone reimplementation of the exact integrator).
* **Mass**: `reservoirParticles × perDropMass ≡ initialPaintMass`; the bucket weighs exactly
  its empty mass + remaining paint at all times.
* **Zero console errors**; safe demo (2k) runs far above 30 fps; 10k stress mode is reachable
  gradually via staged spawning with live FPS/particle counters on screen.
* Demo path: bucket swings → liquid visible inside the transparent cuboid → the same
  particles exit the hole → fall → angle-dependent splats/trails on the canvas → PNG/CSV/JSON
  export, experiment compare, reset/refill.

## 8. References

Müller, Charypar & Gross 2003 (SPH kernels) · Monaghan 1992 (symmetric SPH) · Tate 1864 /
Harkins & Brown 1919 (drop mass) · Torricelli/Bernoulli (orifice efflux) · Stow & Hadfield
1981, Cossali 1997 (splash threshold) · Mundo 1995 / Rioboo 2002 (roughness effect) ·
Pasandideh-Fard/Madejski 1996 (max spread) · Wenzel 1936 (roughness wetting) · Tanner 1979 /
de Gennes 1985 (spreading) · Lucas 1918/Washburn 1921 (absorption) · Nusselt (film flow) ·
Bird, Tsai & Stone 2009 (oblique splash asymmetry) · Yarin 2006 / Villermaux 2007 (splash
stochasticity) · Roisman 2009 (rim thickness) · Kubelka & Munk 1931 / Duncan 1940 (paint
colour mixing) · Andrade 1930 (viscosity–temperature) · Lin & Reitz 1998 (jet breakup/spread).
