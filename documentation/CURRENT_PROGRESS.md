# Current Progress

## Overall: ~78% complete

The scientific core is complete and runs. What remains is mostly visual
calibration and a few required outputs/inputs.

---

## By phase

| Phase | Scope | Status |
|---|---|---|
| Phase 1 - Pendulum physics | Full Doc-1 physics | Done (100%) |
| Phase 2 - Paint particles | Particle model + pool | Structure done (~90%), visual calibration pending |
| Phase 3 - Surface / canvas | Impact cases + surfaces | Structure done (~80%), splat proportion not calibrated |
| Phase 4 - UI / report | UI + logging + export + reset | Done (~85%) |

---

## Required inputs (official assignment)

Implemented: rope length, release angle, initial velocity, gravity, air
resistance (drag/area), humidity, friction (damping), paint color, viscosity,
flow, surface type, empty mass, paint amount, hole, rope elasticity.

Missing: motion direction (free azimuth), swing count (stop after N),
multiple colors, canvas dimensions + tilt (no UI control), bucket radius,
adjustable attach point.

---

## Required outputs (official assignment)

Implemented: 3D motion view, live path drawing, final painting, save PNG,
show values used, report (CSV/JSON: inputs, motion time, path count, coverage).

Missing: compare multiple experiments side by side.

---

## Remaining work (priority order)

1. Calibrate the paint visuals (tie splat size to droplet size; realistic stream).
2. Multiple paint colors.
3. Experiment comparison.
4. Missing inputs (direction, swing count, canvas dimensions/tilt UI).
5. Run the Phase-1 validation tests and confirm they pass.

---

## Notes

- MCP (Unity <-> Claude Code) is connected on port 6400.
- All scripts and UI are in English; only chat is in Arabic.
- Validation mode (PendulumMotion.validationMode) logs period/energy/tension checks.
