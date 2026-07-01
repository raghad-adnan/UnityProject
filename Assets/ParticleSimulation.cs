using System.Collections.Generic;
using UnityEngine;

// ============================================================================
//  PaintPhysics — PARTICLE SIMULATION  (partial class; see PaintPhysics.cs)
// ----------------------------------------------------------------------------
//  Owns the in-air life of a droplet: the SPH solver, the spatial grid, the
//  object pool, gravity/damping/sleep integration and the cohesion/separation
//  interaction. When a particle crosses the canvas plane it hands off to the
//  surface layer through the single public seam OnDropletImpact(...) (which
//  SurfaceInteraction.cs implements). Pure relocation — no formula changed.
// ============================================================================
public partial class PaintPhysics : MonoBehaviour
{
    private SPHSolver sph;

    [SerializeField]
    float sleepVelocityThreshold = 0.05f;
    [SerializeField]
    float sleepTime = 1.5f;
    [SerializeField]
    float particleRepulsion = 2f;
    [SerializeField]
    float viscosityStrength = 0.5f;
    private SpatialGrid spatialGrid;

    [Header("Particle damping")]
    public float dampingFactor = 0.96f;

    [Header("Particle interaction")]
    public bool enableParticleInteraction = true;
    public float interactionRadius = 0.25f;
    public float cohesionStrength = 0.5f;
    public float separationStrength = 1.0f;

    [Header("Particle display (debug)")]
    public int activeParticles;

    private static Mesh sphereMesh;
    private Material particleMatTemplate;
    private PaintParticlePool pool;

    void UpdateParticles(float dt)
{
    if (pool == null || canvasRenderer == null)
        return;


    // تحديث Spatial Grid
    spatialGrid.Clear();

    var list = pool.All;
    activeParticles = 0;
    for (int i = 0; i < list.Count; i++)
    {
        PaintParticle p = list[i];

        if (p.state != ParticleState.Removed)
        {
            spatialGrid.AddParticle(p);
        }
        if(p.active)
          activeParticles++;
    }


    // تفاعل الجزيئات (لاحقاً سيستخدم SPH)
    if (enableParticleInteraction)
        ApplyInteraction(dt);



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

        // جزيئات قريبة (جاهزة لـ SPH)
        List<PaintParticle> nearby =
            spatialGrid.GetNeighbors(p.position);

        Vector3 pressureForce =
        sph.CalculatePressureForce(
        p,
        nearby
        );


        Vector3 viscosityForce =
           sph.CalculateViscosityForce(
           p,
           nearby
        ) ;



        p.velocity +=
    (pressureForce + viscosityForce)
    * dt
    * 0.02f;

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



        if(p.sleepTimer > sleepTime)
        {
         p.active = false;
         p.velocity = Vector3.zero;
         continue;
        }


        // Position integration
        p.position += p.velocity * dt;


        // تحديث الشكل المرئي
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
    if(Time.frameCount % 60 == 0)
{
    Debug.Log("Active particles: " + activeParticles);
}
}
    // cheap cohesion/separation against a few neighbors
    void ApplyInteraction(float dt)
{
    var list = pool.All;


    for (int i = 0; i < list.Count; i++)
    {
        PaintParticle p = list[i];


        if (p.state == ParticleState.Removed)
            continue;



        // جلب الجزيئات القريبة فقط
        List<PaintParticle> neighbors =
            spatialGrid.GetNeighbors(p.position);



        Vector3 force = Vector3.zero;



        for (int j = 0; j < neighbors.Count; j++)
        {
            PaintParticle other = neighbors[j];


            if (other == p)
                continue;


            if (other.state == ParticleState.Removed)
                continue;



            Vector3 dir =
                p.position - other.position;


            float distance =
                dir.magnitude;



            if (distance <= 0.0001f)
                continue;



            float interactionRadius = 0.15f;



            if (distance < interactionRadius)
            {

                float overlap =
                    interactionRadius - distance;



                // قوة تنافر بسيطة لمنع تداخل القطرات
                Vector3 repulsion =
                    dir.normalized *
                    overlap *
                    particleRepulsion;



                force += repulsion;



                // لزوجة تقريبية
                Vector3 viscosity =
                    (other.velocity - p.velocity)
                    * viscosityStrength;



                force += viscosity;
            }
        }



        // تحويل القوة إلى تسارع
        p.velocity += force * dt;
    }
}

    void EnsureSphereMesh()
    {
        if (sphereMesh != null) return;
        GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphereMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
        if (Application.isPlaying) Destroy(tmp); else DestroyImmediate(tmp);
    }
}
