using System.Collections.Generic;
using UnityEngine;

// ============================================================================
//  PaintPhysics — PARTICLE SIMULATION  (partial class; see PaintPhysics.cs)
// ----------------------------------------------------------------------------
//  Owns the life of every particle, in BOTH regimes:
//    InsideBucket : full SPH (pressure/separation + viscosity) + gravity +
//                   local-space wall containment in the moving, rotating bucket.
//    Emitted/Falling : same SPH + gravity + air damping + canvas-plane crossing,
//                   handed off to SurfaceInteraction through OnDropletImpact(...).
//
//  ONE unified pipeline for all 10,000 particles (10k redesign):
//    * one spatial hash over every active particle (world space);
//    * two-pass symmetric SPH (density/pressure first, forces second) with a
//      hard neighbour cap (brief §6: 16-64) so dense clusters stay O(k);
//    * SPH forces are computed in world space — a rigid bucket transform is an
//      isometry, so world-space distances equal bucket-local distances and the
//      forces are identical to a local-space solve; only the WALLS need the
//      local frame (ContainInBox);
//    * no GameObjects: particles are data, drawn instanced by the pool.
// ============================================================================
public partial class PaintPhysics : MonoBehaviour
{
    private SPHSolver sph;

    [SerializeField]
    float sleepVelocityThreshold = 0.05f;
    [SerializeField]
    float sleepTime = 1.5f;
    private SpatialGrid spatialGrid;

    [Header("Particle damping")]
    // Air-damping factor per 1/60 s. Applied as pow(dampingFactor, dt*60) so the decay rate is
    // frame-rate independent (the old code multiplied once per frame — faster machines dried the
    // droplets' momentum quicker than slow ones).
    public float dampingFactor = 0.96f;

    [Header("Particle interaction (SPH)")]
    public bool enableParticleInteraction = true;
    // = SPH smoothing radius h AND spatial-grid cell size. 0.12 m matches the mean particle
    // spacing of a full 10k reservoir (~0.05 m) with ~2h coverage; the old 0.25 m pulled in
    // 8x more volume per query, which the neighbour cap would truncate arbitrarily.
    public float interactionRadius = 0.12f;

    [Header("SPH tuning")]
    // EOS  p = k (rho - rho0) / rho0, clamped >= 0 (repulsion-only; avoids tensile clumping).
    // Retuned for the unified solver where the DENSE reservoir also runs SPH:
    //   * rho0 = 275 — the kernel density of particles packed at ~0.05 m spacing (a full 10k
    //     reservoir). Looser liquid (falling stream, part-filled bucket) sits BELOW rho0, feels no
    //     pressure and pools under gravity like a free-surface liquid; only real compression pushes back.
    //   * k = 150 — sized so the pressure acceleration at ~20% over-compression balances gravity
    //     (a ≈ m·n·2p/rho²·|∇W| ≈ g), i.e. the liquid stacks instead of collapsing or fizzing.
    // The old (2, 1) pair was tuned for SPARSE airborne droplets only — with the reservoir included
    // it produced pressures ~600 and a permanently boiling bucket.
    public float sphStiffness = 150f;
    public float sphRestDensity = 275f;
    [Range(8, 64)] public int maxNeighbors = 48; // brief §6: cap neighbour loops at 16-64

    [Header("Particle display (debug)")]
    public int activeParticles;
    public int insideParticles;               // live count of particles still in the bucket
    public int airborneParticles;             // Emitted/Falling count
    public float avgNeighbors;                // mean SPH neighbours (shows the grid is working)

    private static Mesh sphereMesh;
    private Material particleMatTemplate;
    private PaintParticlePool pool;

    // Cache used by BucketEmission's fill logic (refreshed every frame below).
    private int insideCountCache;

    void UpdateParticles(float dt)
    {
        if (pool == null || canvasRenderer == null)
            return;

        // Clamp the physics step so an occasional slow frame can't blow up the explicit SPH
        // integration (below ~30 fps we sub-cap dt rather than take one huge unstable step).
        dt = Mathf.Min(dt, 1f / 30f);

        // Keep the SPH coefficients in sync with the live Inspector/slider values.
        if (sph != null)
        {
            sph.smoothingRadius = Mathf.Max(1e-3f, interactionRadius);
            sph.viscosity   = viscosity;
            sph.stiffness   = sphStiffness;
            sph.restDensity = Mathf.Max(1e-6f, sphRestDensity);
        }

        // ---- rebuild the spatial hash over ALL active particles (inside + airborne) ----
        spatialGrid.Clear();
        var list = pool.All;
        activeParticles = 0;
        for (int i = 0; i < list.Count; i++)
        {
            PaintParticle p = list[i];
            if (p.state == ParticleState.Removed) continue;
            spatialGrid.AddParticle(p);
            if (p.active) activeParticles++;
        }

        // ---- SPH pass 1: density + pressure for EVERY active particle ----
        // Must complete before any force is computed so pass 2 can read the neighbours' stored
        // pressures (that symmetry is what makes the pressure force momentum-conserving).
        long neighborSum = 0; int neighborSamples = 0;
        if (enableParticleInteraction)
        {
            for (int i = 0; i < list.Count; i++)
            {
                PaintParticle p = list[i];
                if (p.state == ParticleState.Removed) continue;
                var nb = spatialGrid.GetNeighbors(p.position, maxNeighbors);
                sph.ComputeDensityPressure(p, nb);
                neighborSum += nb.Count; neighborSamples++;
            }
        }
        avgNeighbors = neighborSamples > 0 ? (float)neighborSum / neighborSamples : 0f;

        Transform c = canvasRenderer.transform;
        Vector3 planePoint  = c.position;
        Vector3 planeNormal = c.up;

        // Frame-rate-independent air damping (see dampingFactor comment).
        float airDamp = Mathf.Pow(Mathf.Clamp01(dampingFactor), dt * 60f);

        int insideNow = 0, airborneNow = 0;

        // ---- integrate every particle ----
        for (int i = 0; i < list.Count; i++)
        {
            PaintParticle p = list[i];
            if (p.state == ParticleState.Removed) continue;
            if (!p.active) continue;

            // SPH pass 2 — symmetric pressure + viscosity accelerations for ALL states.
            // Inside the bucket this is what makes the reservoir behave like a liquid
            // (separation + slosh + viscous coupling) instead of independent pebbles.
            if (enableParticleInteraction)
            {
                List<PaintParticle> nearby = spatialGrid.GetNeighbors(p.position, maxNeighbors);
                Vector3 sphAccel = sph.PressureAcceleration(p, nearby)
                                 + sph.ViscosityAcceleration(p, nearby);
                float maxA = 50f * gravity;
                if (sphAccel.sqrMagnitude > maxA * maxA) sphAccel = sphAccel.normalized * maxA;
                p.velocity += sphAccel * dt;
            }

            // Contained paint (still INSIDE the bucket): world-space gravity + SPH above, walls
            // enforced in the bucket's LOCAL frame so the box drags/tilts the liquid as it swings
            // and rotates. It does NOT fall to the canvas or expire — it sloshes until EmitStep
            // pours it out the hole (state -> Emitted). Same data object throughout its life.
            if (p.state == ParticleState.InsideBucket)
            {
                p.velocity += Vector3.down * gravity * dt;
                p.velocity *= airDamp;
                if (p.velocity.sqrMagnitude > 400f) p.velocity = p.velocity.normalized * 20f;
                p.position += p.velocity * dt;
                ContainInBox(p);
                insideNow++;
                continue;
            }

            if (p.state == ParticleState.Emitted)
                p.state = ParticleState.Falling;
            airborneNow++;

            Vector3 prev = p.position;

            // Gravity
            p.velocity += Vector3.down * gravity * dt;

            // Air damping: base factor modulated by the droplet's viscosity effect.
            float viscousDamping = Mathf.Clamp01(1f - p.viscosityEffect * 0.02f);
            p.velocity *= airDamp * Mathf.Pow(viscousDamping, dt * 60f);

            // Sleep optimizer: a droplet that has slowed to a crawl in mid-air is spent —
            // retire it to the pool instead of keeping an immortal invisible particle.
            if (p.velocity.magnitude < sleepVelocityThreshold)
                p.sleepTimer += dt;
            else
                p.sleepTimer = 0f;
            if (p.sleepTimer > sleepTime)
            {
                pool.Return(p);
                continue;
            }

            // Position integration
            p.position += p.velocity * dt;
            p.age += dt;

            // Canvas crossing: analytic plane sign change (no colliders), impact point
            // interpolated on the segment for sub-step accuracy.
            float sidePrev = Vector3.Dot(prev - planePoint, planeNormal);
            float sideNow  = Vector3.Dot(p.position - planePoint, planeNormal);

            if (sidePrev > 0f && sideNow <= 0f)
            {
                p.state = ParticleState.Collided;
                Vector3 hit = Vector3.Lerp(prev, p.position, sidePrev / (sidePrev - sideNow));

                // hand off to the surface layer (SurfaceInteraction.cs) — the single seam
                // between particle physics and floor/canvas physics.
                OnDropletImpact(c, hit, p, planeNormal);

                p.state = ParticleState.Painted;
                pool.Return(p);
            }
            else if (p.age > p.lifetime || sideNow < -2f)
            {
                pool.Return(p);
            }
        }

        insideParticles   = insideNow;
        airborneParticles = airborneNow;
        insideCountCache  = insideNow;

        // ---- draw all live particles GPU-instanced (no GameObjects) ----
        pool.Render(sphereMesh, particleMatTemplate, dropletVisualScale);
    }

    // Keep a contained (InsideBucket) particle inside the bucket's box, worked in the bucket's LOCAL
    // frame so it follows the swing/tilt/rotation. The unit-cube box interior is |local| ≤ 0.5; the
    // wall margin is per-axis (the box is non-uniformly scaled). Reflecting the wall-normal velocity
    // with a small restitution makes the paint pile up and slosh instead of leaking through the walls.
    void ContainInBox(PaintParticle p)
    {
        if (bucketMotion == null) return;
        Transform bt = bucketMotion.transform;
        Vector3 s = bt.lossyScale;
        Vector3 local = bt.InverseTransformPoint(p.position);
        Vector3 lvel  = bt.InverseTransformVector(p.velocity);
        float visualR = p.size * dropletVisualScale * 0.5f;
        for (int a = 0; a < 3; a++)
        {
            float rad = visualR / Mathf.Max(1e-3f, Mathf.Abs(s[a]));
            float lim = Mathf.Max(0.02f, 0.5f - rad);
            if (local[a] > lim)      { local[a] =  lim; if (lvel[a] > 0f) lvel[a] = -lvel[a] * 0.2f; }
            else if (local[a] < -lim){ local[a] = -lim; if (lvel[a] < 0f) lvel[a] = -lvel[a] * 0.2f; }
        }
        p.position = bt.TransformPoint(local);
        p.velocity = bt.TransformVector(lvel);
    }

    void EnsureSphereMesh()
    {
        if (sphereMesh != null) return;
        GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphereMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
        if (Application.isPlaying) Destroy(tmp); else DestroyImmediate(tmp);
    }
}
