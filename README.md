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

### 3.4 Emission through the hole

Per frame the emission gate and rate combine: paint level (mass ratio), **tilt** (true polar
angle θ), **slosh** (tangential acceleration raises the level on one side), **hole submersion**
(`level + slosh ≥ holeHeight`), **hole area** (`∝ r_hole²`), **viscosity** (temperature-adjusted),
bucket speed, and the flow-rate input. When a drop is due:

* the InsideBucket particle **nearest the hole** is selected (the liquid over the opening
  leaves first),
* it is repositioned at the hole exit `bucket.TransformPoint(holeLocal + shapeOffset)` —
  hole shapes: **Round / Narrow slit / Wide band / three Multiple streams**,
* exit velocity = **full bucket velocity** (it rides the swing at detachment)
  + **Torricelli jet** `v = C_d√(2g·h_head)` (`C_d = 0.6`, sharp-edged orifice; √viscosity loss)
  along the **tilted bucket axis** + controlled spread,
* its state flips to `Emitted` — same object, same size, same colour, same mass.

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

## 5. Control panel guide

Dark, compact IMGUI panel (procedurally themed — no asset dependencies). Header shows an FPS
pill (green/amber/red) and the live particle count on every tab.

* **Pendulum** — rope length, release angle, azimuthal + polar push, swing direction, max
  swings, gravity, masses, flow rate, air density/drag/area, pivot friction, damping, wind,
  rope stiffness/break; toggles: elastic rope, buoyancy, validation mode.
* **Paint** — performance card + 2k/5k/10k modes + SPH toggle; viscosity, temperature,
  humidity; emission rate, hole radius/height, bucket radius, hole shape; floor pitch/roll,
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
stochasticity) · Roisman 2009 (rim thickness).
