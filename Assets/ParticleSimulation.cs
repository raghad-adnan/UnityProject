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
    // Numerical damping for the CONTAINED liquid only (per 1/60 s, applied frame-rate independent
    // as pow(dampingFactor, dt*60)) — it stabilises the explicit SPH integration in the dense
    // reservoir. Falling droplets do NOT use it: their air resistance is the real quadratic
    // sphere drag (see the falling branch below), which is what preserves the bucket's throw
    // and produces genuinely oblique canvas impacts.
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

    // Frame-shared flat neighbour storage: each particle's neighbours are gathered from the
    // spatial hash ONCE (pass 1) into this list and referenced by (nbStart, nbCount) slices, so
    // pass 2 reuses them instead of re-walking 27 hash cells per particle a second time.
    private readonly List<PaintParticle> neighborFlat = new List<PaintParticle>(65536);

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

        // ---- SPH pass 1: gather neighbours ONCE + density/pressure for EVERY active particle ----
        // Must complete before any force is computed so pass 2 can read the neighbours' stored
        // pressures (that symmetry is what makes the pressure force momentum-conserving).
        long neighborSum = 0; int neighborSamples = 0;
        neighborFlat.Clear();
        if (enableParticleInteraction)
        {
            for (int i = 0; i < list.Count; i++)
            {
                PaintParticle p = list[i];
                if (p.state == ParticleState.Removed) continue;
                var nb = spatialGrid.GetNeighbors(p.position, maxNeighbors);
                p.nbStart = neighborFlat.Count;
                p.nbCount = nb.Count;
                for (int j = 0; j < nb.Count; j++) neighborFlat.Add(nb[j]);
                sph.ComputeDensityPressure(p, neighborFlat, p.nbStart, p.nbCount);
                neighborSum += nb.Count; neighborSamples++;
            }
        }
        avgNeighbors = neighborSamples > 0 ? (float)neighborSum / neighborSamples : 0f;

        Transform c = canvasRenderer.transform;
        Vector3 planePoint  = c.position;
        Vector3 planeNormal = c.up;

        // Frame-rate-independent SPH stabiliser for the contained liquid (see dampingFactor comment).
        float airDamp = Mathf.Pow(Mathf.Clamp01(dampingFactor), dt * 60f);

        int insideNow = 0, airborneNow = 0;

        // ---- integrate every particle ----
        for (int i = 0; i < list.Count; i++)
        {
            PaintParticle p = list[i];
            if (p.state == ParticleState.Removed) continue;
            if (!p.active) continue;

            // SPH pass 2 — symmetric pressure + viscosity accelerations for ALL states, reusing
            // the neighbour slice gathered in pass 1 (no second spatial-hash walk).
            // Inside the bucket this is what makes the reservoir behave like a liquid
            // (separation + slosh + viscous coupling) instead of independent pebbles.
            if (enableParticleInteraction)
            {
                Vector3 sphAccel = sph.PressureAcceleration(p, neighborFlat, p.nbStart, p.nbCount)
                                 + sph.ViscosityAcceleration(p, neighborFlat, p.nbStart, p.nbCount);
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

            // REAL air resistance: quadratic sphere drag on the velocity relative to the wind,
            //   a = (rho_air * Cd * A / 2m) |v_rel| v_rel,   Cd(sphere) = 0.47.
            // For a mm-cm paint drop over a ~1 m fall this is < 1-2 m/s² — nearly negligible next
            // to gravity, so the drop KEEPS the horizontal throw it inherited from the swinging
            // bucket and arrives at the canvas at its true oblique angle. (The old multiplicative
            // 0.96-per-frame damper had a ~0.3 s time constant: it erased the horizontal velocity
            // mid-fall and made every impact near-vertical.)
            {
                float radius = p.size * 0.5f;
                float crossArea = Mathf.PI * radius * radius;
                float rhoAir = (bucketMotion != null) ? bucketMotion.airDensity : 1.225f;
                Vector3 wind = (bucketMotion != null) ? bucketMotion.windVel : Vector3.zero;
                Vector3 vRel = p.velocity - wind;
                float dragK = 0.5f * rhoAir * 0.47f * crossArea / Mathf.Max(1e-6f, p.approxMass);
                // Clamp the step so explicit drag can never reverse the relative velocity.
                float dragFrac = Mathf.Min(0.9f, dragK * vRel.magnitude * dt);
                p.velocity -= vRel * dragFrac;
            }

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
        // effectiveDropletScale = dropletVisualScale, auto-reduced so the full reservoir
        // physically fits in the container (see UpdateDropAccounting).
        pool.Render(sphereMesh, particleMatTemplate, effectiveDropletScale);
    }

    // Empty the whole particle system: every non-Removed particle (inside the bucket, falling,
    // anything) goes back to the pool. Used by RefillPaint so a colour change + Refill/Reset swaps
    // the ENTIRE charge — the old paint is dumped, the staged fill then rebuilds the reservoir
    // from scratch with the newly selected colour.
    public void PurgeAllParticles()
    {
        if (pool == null) return;
        var list = pool.All;
        for (int i = 0; i < list.Count; i++)
            if (list[i].state != ParticleState.Removed) pool.Return(list[i]);
        insideCountCache = 0;
        insideParticles = 0; airborneParticles = 0; activeParticles = 0;
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
        float visualR = p.size * effectiveDropletScale * 0.5f;
        // Wall restitution 0.05: a viscous paint drop hitting a wall at low Stokes number loses its
        // normal momentum to viscous dissipation and does NOT bounce (rebound needs St > ~10;
        // paint drops in a sloshing bucket sit far below that), so the walls are near-inelastic.
        const float wallRestitution = 0.05f;
        for (int a = 0; a < 3; a++)
        {
            float rad = visualR / Mathf.Max(1e-3f, Mathf.Abs(s[a]));
            float lim = Mathf.Max(0.02f, 0.5f - rad);
            if (local[a] > lim)      { local[a] =  lim; if (lvel[a] > 0f) lvel[a] = -lvel[a] * wallRestitution; }
            else if (local[a] < -lim){ local[a] = -lim; if (lvel[a] < 0f) lvel[a] = -lvel[a] * wallRestitution; }
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
