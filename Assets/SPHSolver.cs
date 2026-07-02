using System.Collections.Generic;
using UnityEngine;

// ============================================================================
//  SPHSolver — Smoothed-Particle Hydrodynamics for the paint droplets.
// ----------------------------------------------------------------------------
//  Kernels are the standard Müller, Charypar & Gross (2003) set (Poly6 density,
//  Spiky pressure gradient, viscosity Laplacian).
//
//  TWO-PASS design (this is what makes it physically correct):
//    1) ComputeDensityPressure() fills each particle's density + pressure.
//    2) PressureAcceleration() / ViscosityAcceleration() then read the
//       NEIGHBOURS' stored pressures, so the pressure force is symmetric
//       (Monaghan 1992) and therefore momentum-conserving — it obeys Newton's
//       third law.
//
//  PERFORMANCE (10k redesign, pass 2): the kernel normalisation factors
//  (315/64πh⁹, 45/πh⁶) are PRECOMPUTED whenever h changes instead of calling
//  Mathf.Pow(h,9)/Pow(h,6) inside every kernel evaluation — at 10,000 particles
//  with ~48 neighbours that was ~1.4 million Pow() calls per frame and the
//  single biggest cost of stress mode (measured: 6 fps). All small integer
//  powers are plain multiplications now. The MATH IS UNCHANGED — identical
//  kernels, identical forces, just not recomputing constants per pair.
//
//  All methods return ACCELERATIONS (per unit mass); the caller applies them
//  directly as  v += a * dt  with no external fudge factor.
// ============================================================================
public class SPHSolver
{
    public float restDensity  = 1f;
    public float stiffness    = 0.5f;   // gas constant k in the EOS  p = k*(ρ-ρ0)/ρ0
    public float particleMass = 0.02f;
    public float viscosity    = 10f;    // dynamic-viscosity coefficient μ

    const float PI = Mathf.PI;

    // Smoothing radius h with cached derived constants (recomputed only when h changes).
    private float h = 0.18f, h2, poly6Coef, spikyCoef, viscCoef;

    public float smoothingRadius
    {
        get => h;
        set
        {
            float v = Mathf.Max(1e-3f, value);
            if (v != h) { h = v; RecomputeKernelCoefficients(); }
        }
    }

    public SPHSolver() { RecomputeKernelCoefficients(); }

    void RecomputeKernelCoefficients()
    {
        h2 = h * h;
        float h3 = h2 * h;
        float h6 = h3 * h3;
        float h9 = h6 * h3;
        poly6Coef = 315f / (64f * PI * h9);   // Poly6:  W = c (h²-r²)³
        spikyCoef = -45f / (PI * h6);         // Spiky:  ∇W = c (h-r)² r̂
        viscCoef  =  45f / (PI * h6);         // Visc :  ∇²W = c (h-r)
    }

    // Configure the solver. `restDensity` is the reference density of the EOS  p = k*(ρ-ρ0)/ρ0.
    // Note a genuinely isolated droplet has an empty neighbour loop, so it feels NO pressure force
    // regardless of ρ0 — only droplets that actually have neighbours inside the smoothing radius get a
    // non-zero pressure GRADIENT and repel. So ρ0 here simply sets the interaction strength; it does
    // not create spurious self-repulsion. Pressure is clamped ≥ 0, so this is a repulsion-only model
    // (crowded droplets push apart), with the viscosity term matching neighbour velocities.
    public void Configure(float smoothingRadius, float viscosity, float stiffness,
                          float particleMass, float restDensity)
    {
        this.smoothingRadius = smoothingRadius;
        this.viscosity       = viscosity;
        this.stiffness       = stiffness;
        this.particleMass    = particleMass;
        this.restDensity     = Mathf.Max(1e-6f, restDensity);
    }

    // --- Pass 1: density (incl. self) + pressure ---
    // Pressure is clamped ≥ 0: sparse airborne droplets must not develop negative (tensile) pressure,
    // which would make them clump unphysically (the classic SPH tensile instability).
    // Slice form: neighbours are flat[start .. start+count-1] (the caller gathers each particle's
    // neighbour list ONCE per frame and both passes reuse it — halves the spatial-hash walks).
    public void ComputeDensityPressure(PaintParticle p, List<PaintParticle> flat, int start, int count)
    {
        float density = 0f;
        Vector3 pp = p.position;
        for (int i = 0; i < count; i++)
        {
            PaintParticle o = flat[start + i];
            if (o.state == ParticleState.Removed) continue;
            float dx = pp.x - o.position.x, dy = pp.y - o.position.y, dz = pp.z - o.position.z;
            float r2 = dx * dx + dy * dy + dz * dz;
            if (r2 >= h2) continue;
            float diff = h2 - r2;
            density += particleMass * poly6Coef * diff * diff * diff;   // Poly6 kernel
        }
        p.density  = Mathf.Max(density, 1e-6f);
        p.pressure = Mathf.Max(0f, stiffness * (p.density - restDensity) / restDensity);
    }

    // Legacy list form (whole list = the neighbour set).
    public void ComputeDensityPressure(PaintParticle p, List<PaintParticle> neighbors)
        => ComputeDensityPressure(p, neighbors, 0, neighbors.Count);

    // --- Pass 2a: symmetric pressure acceleration (Monaghan 1992) ---
    //   a_i = -Σ_j m_j (p_i/ρ_i² + p_j/ρ_j²) ∇W_ij
    public Vector3 PressureAcceleration(PaintParticle p, List<PaintParticle> flat, int start, int count)
    {
        Vector3 a = Vector3.zero;
        float rhoI   = Mathf.Max(1e-6f, p.density);
        float piTerm = p.pressure / (rhoI * rhoI);
        Vector3 pp = p.position;
        for (int i = 0; i < count; i++)
        {
            PaintParticle o = flat[start + i];
            if (o == p || o.state == ParticleState.Removed) continue;
            float dx = pp.x - o.position.x, dy = pp.y - o.position.y, dz = pp.z - o.position.z;
            float r2 = dx * dx + dy * dy + dz * dz;
            if (r2 <= 1e-12f || r2 >= h2) continue;
            float dist = Mathf.Sqrt(r2);
            float t = h - dist;
            // Spiky gradient magnitude / dist  (folds the r̂ normalisation into one division)
            float gradOverDist = spikyCoef * t * t / dist;
            float rhoJ   = Mathf.Max(1e-6f, o.density);
            float pjTerm = o.pressure / (rhoJ * rhoJ);
            float s = gradOverDist * particleMass * (piTerm + pjTerm);
            a.x -= dx * s; a.y -= dy * s; a.z -= dz * s;
        }
        return a;
    }

    public Vector3 PressureAcceleration(PaintParticle p, List<PaintParticle> neighbors)
        => PressureAcceleration(p, neighbors, 0, neighbors.Count);

    // --- Pass 2b: viscosity acceleration (Müller 2003), properly normalised ---
    //   a_i = (μ/ρ_i) Σ_j m_j (v_j - v_i)/ρ_j ∇²W
    public Vector3 ViscosityAcceleration(PaintParticle p, List<PaintParticle> flat, int start, int count)
    {
        Vector3 a = Vector3.zero;
        float rhoI = Mathf.Max(1e-6f, p.density);
        Vector3 pp = p.position;
        for (int i = 0; i < count; i++)
        {
            PaintParticle o = flat[start + i];
            if (o == p || o.state == ParticleState.Removed) continue;
            float dx = pp.x - o.position.x, dy = pp.y - o.position.y, dz = pp.z - o.position.z;
            float r2 = dx * dx + dy * dy + dz * dz;
            if (r2 >= h2) continue;
            float lap  = viscCoef * (h - Mathf.Sqrt(r2));       // viscosity Laplacian
            float rhoJ = Mathf.Max(1e-6f, o.density);
            float s = particleMass * lap / rhoJ;
            a.x += (o.velocity.x - p.velocity.x) * s;
            a.y += (o.velocity.y - p.velocity.y) * s;
            a.z += (o.velocity.z - p.velocity.z) * s;
        }
        return a * (viscosity / rhoI);
    }

    public Vector3 ViscosityAcceleration(PaintParticle p, List<PaintParticle> neighbors)
        => ViscosityAcceleration(p, neighbors, 0, neighbors.Count);
}
