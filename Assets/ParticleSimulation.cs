using System.Collections.Generic;
using UnityEngine;

// ============================================================================
//  PaintPhysics — PARTICLE SIMULATION  (partial class; see PaintPhysics.cs)
// ----------------------------------------------------------------------------
//  Owns the in-air life of a droplet: the SPH solver, the spatial grid, the
//  object pool, gravity/damping/sleep integration and the droplet-droplet
//  interaction (now a single, correct two-pass SPH — the old ad-hoc cohesion
//  pass was folded into it). When a particle crosses the canvas plane it hands
//  off to the surface layer through the single public seam OnDropletImpact(...)
//  (which SurfaceInteraction.cs implements).
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
    public float dampingFactor = 0.96f;

    [Header("Particle interaction (SPH)")]
    public bool enableParticleInteraction = true;
    public float interactionRadius = 0.25f;   // = SPH smoothing radius h and spatial-grid cell size

    [Header("SPH tuning")]
    public float sphStiffness = 2f;           // EOS gas constant k: higher = stronger droplet repulsion
    public float sphRestDensity = 1f;         // EOS reference density (sets interaction strength)

    [Header("Particle display (debug)")]
    public int activeParticles;

    private static Mesh sphereMesh;
    private Material particleMatTemplate;
    private PaintParticlePool pool;

    void UpdateParticles(float dt)
{
    if (pool == null || canvasRenderer == null)
        return;

    // Clamp the physics step so an occasional slow frame can't blow up the explicit SPH integration
    // (below ~30 fps we sub-cap dt rather than take one huge unstable step).
    dt = Mathf.Min(dt, 1f / 30f);

    // Keep the SPH coefficients in sync with the live Inspector/slider values.
    if (sph != null)
    {
        sph.viscosity   = viscosity;
        sph.stiffness   = sphStiffness;
        sph.restDensity = Mathf.Max(1e-6f, sphRestDensity);
    }


    // تحديث Spatial Grid
    spatialGrid.Clear();

    var list = pool.All;
    activeParticles = 0;
    for (int i = 0; i < list.Count; i++)
    {
        PaintParticle p = list[i];

        // Reservoir particles are dense and skip SPH, so excluding them from the
        // hash keeps chain lengths short and makes GetNeighbors fast for falling droplets.
        if (p.state != ParticleState.Removed && p.state != ParticleState.InsideBucket)
        {
            spatialGrid.AddParticle(p);
        }
        if(p.active)
          activeParticles++;
    }


    // SPH pass 1 — density + pressure for every grid-resident particle. This MUST run before any
    // force is computed, so each particle can read its neighbours' pressures in pass 2 (that is what
    // makes the pressure force symmetric / momentum-conserving).
    if (enableParticleInteraction)
    {
        for (int i = 0; i < list.Count; i++)
        {
            PaintParticle p = list[i];
            if (p.state == ParticleState.Removed) continue;
            // Reservoir particles are not in the spatial hash and skip SPH entirely —
            // ContainInBox already handles their shape; full SPH on 3 000 dense particles
            // was the single biggest CPU bottleneck.
            if (p.state == ParticleState.InsideBucket) continue;
            sph.ComputeDensityPressure(p, spatialGrid.GetNeighbors(p.position));
        }
    }



    Transform c = canvasRenderer.transform;

    Vector3 planePoint = c.position;
    Vector3 planeNormal = c.up;



    // تحديث حركة كل جزيء
    for (int i = 0; i < list.Count; i++)
    {
        PaintParticle p = list[i];


        if (p.state == ParticleState.Removed)
            continue;

        if (!p.active)
        continue;

        // SPH pass 2 — only for non-reservoir (Falling/Emitted) particles.
        // InsideBucket particles have no density computed and are not in the hash,
        // so skip them here (handled by the branch below: gravity + ContainInBox).
        if (enableParticleInteraction && p.state != ParticleState.InsideBucket)
        {
            List<PaintParticle> nearby = spatialGrid.GetNeighbors(p.position);
            Vector3 sphAccel = sph.PressureAcceleration(p, nearby)
                             + sph.ViscosityAcceleration(p, nearby);
            float maxA = 50f * gravity;
            if (sphAccel.sqrMagnitude > maxA * maxA) sphAccel = sphAccel.normalized * maxA;
            p.velocity += sphAccel * dt;
        }

        // Contained paint (still INSIDE the bucket): gravity + wall collision in the bucket's own frame,
        // plus the SPH above (which spreads it into a fluid). It does NOT fall to the canvas or expire —
        // it just sloshes until EmitStep pours it out the hole (state -> Emitted). Same object throughout.
        if (p.state == ParticleState.InsideBucket)
        {
            p.velocity += Vector3.down * gravity * dt;
            p.velocity *= dampingFactor;
            if (p.velocity.sqrMagnitude > 400f) p.velocity = p.velocity.normalized * 20f;
            p.position += p.velocity * dt;
            ContainInBox(p);
            p.tr.position = p.position;
            continue;
        }

        if (p.state == ParticleState.Emitted)
            p.state = ParticleState.Falling;



        Vector3 prev = p.position;



        // Gravity
        p.velocity += Vector3.down * gravity * dt;


        // Air damping
        float viscousDamping =
          Mathf.Clamp01(
           1f - p.viscosityEffect * 0.02f
          );


        p.velocity *= dampingFactor * viscousDamping;
        // Sleep optimizer

        if(p.velocity.magnitude < sleepVelocityThreshold)
        {
          p.sleepTimer += dt;
        }
        else
        {
         p.sleepTimer = 0f;
        }



        // A droplet that has slowed to a crawl in mid-air is spent: retire it to the pool
        // instead of freezing it as an invisible-but-immortal particle. (The old code set
        // active=false and continued BEFORE the lifetime/collision checks, so the sphere hung
        // in the air forever and kept occupying a maxParticles slot -> emission slowly stalled.)
        if (p.sleepTimer > sleepTime)
        {
            pool.Return(p);
            continue;
        }


        // Position integration
        p.position += p.velocity * dt;
        p.tr.position = p.position;


        p.age += dt;




        // Collision with canvas

        float sidePrev =
            Vector3.Dot(prev - planePoint, planeNormal);


        float sideNow =
            Vector3.Dot(p.position - planePoint, planeNormal);



        if (sidePrev > 0f && sideNow <= 0f)
        {

            p.state = ParticleState.Collided;


            Vector3 hit =
                Vector3.Lerp(
                    prev,
                    p.position,
                    sidePrev / (sidePrev - sideNow)
                );


            // hand off to the surface layer (SurfaceInteraction.cs) — the single seam
            // between particle physics and floor/canvas physics.
            OnDropletImpact(
                c,
                hit,
                p,
                planeNormal
            );


            p.state = ParticleState.Painted;


            pool.Return(p);
        }


        else if (p.age > p.lifetime || sideNow < -2f)
        {
            pool.Return(p);
        }

    }
}

    // Keep a contained (InsideBucket) particle inside the bucket's box, worked in the bucket's LOCAL
    // frame so it follows the swing/tilt. The unit-cube box interior is |local| ≤ 0.5; the wall margin is
    // per-axis (the box is non-uniformly scaled). Reflecting the wall-normal velocity makes the paint
    // pile up and slosh instead of leaking through the walls.
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
