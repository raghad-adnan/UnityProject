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
//       third law. The old single-pass version used only p_i (the neighbour's
//       pressure was never even computed), which violated momentum conservation
//       and had to be masked with a magic 0.02 multiplier at the call site.
//
//  All methods return ACCELERATIONS (per unit mass); the caller applies them
//  directly as  v += a * dt  with no external fudge factor.
// ============================================================================
public class SPHSolver
{
    public float smoothingRadius = 0.18f;
    public float restDensity     = 1f;
    public float stiffness       = 0.5f;   // gas constant k in the EOS  p = k*(ρ-ρ0)/ρ0
    public float particleMass    = 0.02f;
    public float viscosity       = 10f;    // dynamic-viscosity coefficient μ

    const float PI = Mathf.PI;

    // Configure the solver. `restDensity` is the reference density of the EOS  p = k*(ρ-ρ0)/ρ0.
    // Note a genuinely isolated droplet has an empty neighbour loop, so it feels NO pressure force
    // regardless of ρ0 — only droplets that actually have neighbours inside the smoothing radius get a
    // non-zero pressure GRADIENT and repel. So ρ0 here simply sets the interaction strength; it does
    // not create spurious self-repulsion. Pressure is clamped ≥ 0, so this is a repulsion-only model
    // (crowded droplets push apart), with the viscosity term matching neighbour velocities.
    public void Configure(float smoothingRadius, float viscosity, float stiffness,
                          float particleMass, float restDensity)
    {
        this.smoothingRadius = Mathf.Max(1e-3f, smoothingRadius);
        this.viscosity       = viscosity;
        this.stiffness       = stiffness;
        this.particleMass    = particleMass;
        this.restDensity     = Mathf.Max(1e-6f, restDensity);
    }

    // --- Kernels (Müller et al. 2003) ---

    // Poly6:  W(r,h) = 315/(64π h^9) (h²-r²)³   for 0 ≤ r < h
    float Poly6(float distance)
    {
        if (distance >= smoothingRadius) return 0f;
        float h2 = smoothingRadius * smoothingRadius;
        float r2 = distance * distance;
        float coefficient = 315f / (64f * PI * Mathf.Pow(smoothingRadius, 9));
        return coefficient * Mathf.Pow(h2 - r2, 3);
    }

    // Spiky gradient:  ∇W = -45/(π h^6) (h-r)² r̂
    Vector3 SpikyGradient(Vector3 direction, float distance)
    {
        if (distance <= 0f || distance >= smoothingRadius) return Vector3.zero;
        float coefficient = -45f / (PI * Mathf.Pow(smoothingRadius, 6));
        float value = coefficient * Mathf.Pow(smoothingRadius - distance, 2);
        return direction.normalized * value;
    }

    // Viscosity Laplacian:  ∇²W = 45/(π h^6) (h-r)
    float ViscosityLaplacian(float distance)
    {
        if (distance >= smoothingRadius) return 0f;
        float coefficient = 45f / (PI * Mathf.Pow(smoothingRadius, 6));
        return coefficient * (smoothingRadius - distance);
    }

    // --- Pass 1: density (incl. self) + pressure ---
    // Pressure is clamped ≥ 0: sparse airborne droplets must not develop negative (tensile) pressure,
    // which would make them clump unphysically (the classic SPH tensile instability).
    public void ComputeDensityPressure(PaintParticle p, List<PaintParticle> neighbors)
    {
        float density = 0f;
        for (int i = 0; i < neighbors.Count; i++)
        {
            PaintParticle o = neighbors[i];
            if (o.state == ParticleState.Removed) continue;
            density += particleMass * Poly6(Vector3.Distance(p.position, o.position));
        }
        p.density  = Mathf.Max(density, 1e-6f);
        p.pressure = Mathf.Max(0f, stiffness * (p.density - restDensity) / restDensity);
    }

    // --- Pass 2a: symmetric pressure acceleration (Monaghan 1992) ---
    //   a_i = -Σ_j m_j (p_i/ρ_i² + p_j/ρ_j²) ∇W_ij
    public Vector3 PressureAcceleration(PaintParticle p, List<PaintParticle> neighbors)
    {
        Vector3 a = Vector3.zero;
        float rhoI   = Mathf.Max(1e-6f, p.density);
        float piTerm = p.pressure / (rhoI * rhoI);
        for (int i = 0; i < neighbors.Count; i++)
        {
            PaintParticle o = neighbors[i];
            if (o == p || o.state == ParticleState.Removed) continue;
            Vector3 dir  = p.position - o.position;
            float   dist = dir.magnitude;
            Vector3 grad = SpikyGradient(dir, dist);
            float rhoJ   = Mathf.Max(1e-6f, o.density);
            float pjTerm = o.pressure / (rhoJ * rhoJ);
            a -= grad * (particleMass * (piTerm + pjTerm));
        }
        return a;
    }

    // --- Pass 2b: viscosity acceleration (Müller 2003), properly normalised ---
    //   a_i = (μ/ρ_i) Σ_j m_j (v_j - v_i)/ρ_j ∇²W
    // (The old version dropped the m_j/ρ_j volume weighting, so its magnitude had no physical scale.)
    public Vector3 ViscosityAcceleration(PaintParticle p, List<PaintParticle> neighbors)
    {
        Vector3 a = Vector3.zero;
        float rhoI = Mathf.Max(1e-6f, p.density);
        for (int i = 0; i < neighbors.Count; i++)
        {
            PaintParticle o = neighbors[i];
            if (o == p || o.state == ParticleState.Removed) continue;
            float dist = Vector3.Distance(p.position, o.position);
            float lap  = ViscosityLaplacian(dist);
            float rhoJ = Mathf.Max(1e-6f, o.density);
            a += (o.velocity - p.velocity) * (particleMass * lap / rhoJ);
        }
        return a * (viscosity / rhoI);
    }
}
