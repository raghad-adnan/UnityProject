# Requirements Audit

Status: Done = implemented | Partial = structure done, needs work | Missing

---

## Phase 1 - Pendulum physics (PendulumMotion.cs)

| Item | Status |
|---|---|
| Eq.1 tension `T=m[g cos + L*thetaDot^2]` | Done |
| Eq.2 `theta''=-(g/L)sin - k_d*thetaDot*|thetaDot|` | Done |
| `k_d=rho*Cd*A*L/(2m)` recomputed each step | Done |
| Variable mass `m(t)` with floor | Done |
| 3D position `x=L sinX, z=L sinZ, y=-sqrt(...)` | Done |
| Semi-Implicit Euler | Done |
| Rope slack (T<=0 -> free fall) | Done |
| Energy KE/PE/E + period | Done |
| FixedUpdate, ResetSimulation, angle wrap | Done |
| Eq.3 / Eq.4 analytical tension | Done |
| Elastic rope, break tension, wind, buoyancy | Done |
| Validation tests (validationMode) | Done (run to confirm) |
| Input ranges via [Range] | Done |

## Phase 2 - Paint particles

| Item | Status |
|---|---|
| PaintParticle.cs + 6-state enum | Done |
| PaintParticlePool.cs (pool, max cap) | Done |
| Virtual paint level (tilt/motion/amount/viscosity) | Done |
| Slosh, hole submerged gate | Done |
| Emission rate (all factors) | Done |
| Viscosity + temperature coupling | Done |
| Initial velocity + random spread | Done |
| Hole shapes (Round/Narrow/Wide/Multiple) | Done |
| Cohesion / separation | Done |
| Free fall + state machine | Done |
| Pooling + bake-to-texture | Done |

## Phase 3 - Surface / canvas

| Item | Status |
|---|---|
| Re/We/Ca/Oh + Young (FluidConstants.cs) | Done |
| Impact: circle / ellipse / streak / crown | Done |
| Absorbent fade `C(t)=C0 e^-kt` | Done |
| Rough edges + splash probability | Done |
| Continuous jet mode (spiral traces) | Done |
| Temperature + humidity effects | Done |
| Motion -> shape mapping | Done |
| Splat size formula | Done (proportion not calibrated yet) |
| Surface presets struct | Done |
| Time-based spreading (active splats) | Done |

## Phase 4 - UI / report

| Item | Status |
|---|---|
| UI panel binding inputs (with ranges) | Done |
| Histories (theta/tension/energy) | Done |
| trajectoryLength + paintAreaCoverage | Done |
| CSV / JSON export | Done |
| Full Reset | Done |

---

## Official outputs still missing

- Compare multiple experiments side by side - Missing
- Multiple paint colors - Missing
- Canvas dimensions / tilt + motion direction + swing count as inputs - Missing

## Known calibration item

- Splat size is not tied to the actual droplet radius; multipliers stack and
  inflate the painted line. Needs calibration (visual only, not structural).
