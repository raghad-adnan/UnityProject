# Physics Requirements (implemented)

Source: the rope/bucket physical study (Doc 1) and the paint particle study (Doc 2).

## Engine facts

- Unity 6000.4.4f1, Built-in Render Pipeline (NOT URP).
- Pure math, no RigidBody/Colliders.
- Physics runs in FixedUpdate.

---

## Pendulum (PendulumMotion.cs) - two angles thetaX, thetaZ

| Quantity | Formula |
|---|---|
| Variable mass | `m(t) = emptyMass + initialPaintMass - flowRate*t` (floored at >0) |
| Quadratic drag | `k_d = (airDensity*dragCoef*area*L) / (2*m)` |
| Angular accel (per axis) | `theta'' = -(g/L)*sin(theta) - k_d*thetaDot*|thetaDot|` |
| Integration | Semi-Implicit Euler (velocity first, then position) |
| 3D position | `x=L*sin(thetaX)`, `z=L*sin(thetaZ)`, `y=-sqrt(L^2-x^2-z^2)` |
| Tension (Eq.1) | `T = m*[ g*cos(theta_eff) + L*(thetaDotX^2+thetaDotZ^2) ]`, `cos(theta_eff)=-y/L` |
| Tension vs angle (Eq.3) | `T(theta) = m*g*(3*cos(theta) - 2*cos(theta0))` |
| Max tension (Eq.4) | `T_max = m*g*(3 - 2*cos(theta0))` |
| Energy | `KE = 0.5*m*(L*thetaDotX)^2 + 0.5*m*(L*thetaDotZ)^2`, `PE = m*g*y` |
| Period | `T_period = 2*pi*sqrt(L/g)` |
| Energy dissipation | `dE/dt = -0.5*rho*Cd*A*L^3*|omega|^3` |

Extras: rope slack (T<=0 -> free fall), elastic rope (Hooke), breaking
tension, wind, buoyancy, angle wraparound to [-pi, pi], ResetSimulation().

Public API used by PaintPhysics: `velocity`, `mass`, `GetTensionForce()`,
`GetTangentialAcceleration()`.

---

## Paint particles (PaintParticle/Pool + PaintDrawer.cs)

| Quantity | Formula |
|---|---|
| Tilt factor | `tiltFactor = |sin(theta)|` |
| Slosh | `sloshOffset = angularVelocity * sloshStrength / viscosity` |
| Hole submerged | `paintLevel + sloshOffset >= holeHeight` |
| Emission rate | `baseEmission * paintAmountFactor * tiltFactor * motionFactor * viscosityFactor * holeFactor` |
| Viscosity factor | `1 / viscosity` |
| Temperature -> viscosity | `Lerp(maxViscosity, minViscosity, temperatureFactor)` |
| Particle velocity | `bucketVelocity + exitDirection*particleSpeed + randomSpread` |
| Free fall | `v += g*dt; p += v*dt; v *= dampingFactor` |
| States | InsideBucket -> Emitted -> Falling -> Collided -> Painted -> Removed |

---

## Surface interaction (Phase 3)

| Quantity | Formula |
|---|---|
| Reynolds | `Re = rho*v*D/mu` |
| Weber | `We = rho*v^2*D/sigma` |
| Capillary | `Ca = mu*v/sigma` |
| Ohnesorge | `Oh = mu/sqrt(rho*sigma*D)` |
| Young | `cos(theta) = (gamma_SV - gamma_SL)/gamma_LV` |
| Splat size | `baseSplatSize + speedEffect + viscosityEffect + surfaceEffect` |
| Absorption | `C(t) = C0 * exp(-k_absorption * t)` |

Impact cases: vertical->circle, oblique->ellipse, fast oblique->streak,
very high speed->crown (optional). Surface presets: Smooth/Rough/Absorbent.
Continuous jet mode connects splats into spiral traces.
