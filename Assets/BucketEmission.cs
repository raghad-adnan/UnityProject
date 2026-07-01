using UnityEngine;

// ============================================================================
//  PaintPhysics — BUCKET EMISSION  (partial class; see PaintPhysics.cs header)
// ----------------------------------------------------------------------------
//  Owns everything about the bucket itself: the paint reservoir, the hole
//  shape/geometry, the physically-derived drop size (Tate + Harkins-Brown +
//  viscous Ca) and the per-frame emission that spawns particles into the pool.
//  Pure relocation from the old PaintDrawer.cs — no formula, threshold or
//  constant changed.
// ============================================================================
public partial class PaintPhysics : MonoBehaviour
{
    [Header("Paint reservoir")]
    public float maxPaintAmount = 5f;
    public float currentPaintAmount = 5f;

    [Header("Hole")]
    public HoleShape holeShape = HoleShape.Round;
    public float holeRadius = 0.06f;
    public float holeHeight = 0.1f;
    public float holeFactor = 1f;
    public Vector3 exitDirection = Vector3.down;

    [Header("Bucket geometry")]
    public float bucketRadius = 0.15f;

    [Header("Emission")]
    public float baseEmission = 40f;
    public int maxParticles = 300;
    public float baseSpeed = 0.2f;
    public float baseSize = 0.05f;
    public float baseSpread = 0.12f;
    public float particleLifetime = 5f;

    [Header("Emission display (debug)")]
    public float currentEmissionRate;
    public float paintLevel;
    public bool isHoleSubmerged;
    public float lastDropDiameter;                            // last physically-derived drop diameter (m)

    private float emitAccumulator;

    // Physically-derived diameter (m) of a drop detaching from the hole. No magic constant:
    //   Tate's law      : a pendant drop falls when its weight equals the rim surface-tension force,
    //                      m*g = 2*pi*r*gamma  ->  V = 2*pi*r*gamma / (rho*g).
    //   Harkins-Brown   : real drops are smaller (some liquid stays behind), factor Phi ~ 0.6.
    //   Viscous dynamics: faster, more viscous efflux resists pinch-off and enlarges the drop,
    //                      captured by the Capillary number Ca = mu*u/gamma  ->  V *= (1 + Ca).
    // So the drop scales with hole radius, surface tension, density, gravity AND viscosity.
    float DropDiameter(float effViscosity, float exitSpeed, out float dropMass)
    {
        float muPhys = paintViscosityPaS * Mathf.Max(0.05f, effViscosity);          // Pa·s
        float Ca = muPhys * Mathf.Max(0f, exitSpeed) / Mathf.Max(1e-6f, surfaceTension);
        const float harkinsBrown = 0.6f;
        float V = harkinsBrown * 2f * Mathf.PI * Mathf.Max(1e-4f, holeRadius) * surfaceTension
                  / Mathf.Max(1e-6f, density * gravity) * (1f + Ca);
        V = Mathf.Max(1e-12f, V);
        dropMass = density * V;
        return Mathf.Pow(6f * V / Mathf.PI, 1f / 3f); // sphere-equivalent diameter
    }

    void EmitStep(float dt)
    {
        if (currentPaintAmount <= 0f) { currentEmissionRate = 0f; return; }

        float omega = bucketMotion.velocity.magnitude / Mathf.Max(0.01f, bucketMotion.L);
        float tiltRad = Mathf.Sqrt(bucketMotion.angleX * bucketMotion.angleX
                                 + bucketMotion.angleZ * bucketMotion.angleZ) * Mathf.Deg2Rad;

        // temperature lowers the effective viscosity
        float temperatureFactor = Mathf.Clamp01(temperature / 50f);
        float adjustedViscosity = Mathf.Lerp(maxViscosity, minViscosity, temperatureFactor);
        float effViscosity = Mathf.Max(0.05f, adjustedViscosity * viscosity);

        float tiltFactor = Mathf.Abs(Mathf.Sin(tiltRad));
        float baseLevel = currentPaintAmount / Mathf.Max(0.0001f, maxPaintAmount);
        paintLevel = baseLevel + tiltFactor * 0.25f / effViscosity;

        // slosh derived from lateral acceleration, fill level, bucket size, viscosity
        float lateralAccel = bucketMotion.GetTangentialAcceleration();
        float sloshOffset = (lateralAccel * baseLevel * bucketRadius) / (effViscosity * Mathf.Max(0.1f, gravity));
        isHoleSubmerged = (paintLevel + sloshOffset) >= holeHeight;
        if (!isHoleSubmerged) { currentEmissionRate = 0f; return; }

        // flow scales with hole area (radius^2)
        float holeFlow = holeFactor * (holeRadius * holeRadius) / (0.06f * 0.06f);
        currentEmissionRate = baseEmission * baseLevel * (0.2f + tiltFactor)
                              * (1f + omega) * (1f / effViscosity) * holeFlow;

        emitAccumulator += currentEmissionRate * dt;
        while (emitAccumulator >= 1f && pool.ActiveCount < maxParticles)
        {
            emitAccumulator -= 1f;
            SpawnParticle(effViscosity);
        }
        if (pool.ActiveCount >= maxParticles) emitAccumulator = 0f;
    }

  void SpawnParticle(float effViscosity)
{
    PaintParticle p = pool.Get();
    if (p == null) return;


    p.state = ParticleState.InsideBucket;



    // سرعة خروج الطلاء تتأثر باللزوجة
    float particleSpeed = baseSpeed / effViscosity * 0.35f;



    // انتشار الخروج
    float spreadAmount =
        baseSpread /
        Mathf.Max(0.01f, effViscosity);



    float flowNorm =
        Mathf.Clamp01(
            currentEmissionRate / 80f
        );


    // تدفق أعلى = تيار أضيق
    spreadAmount *=
        Mathf.Lerp(
            1f,
            0.25f,
            flowNorm
        );



    Vector3 spawnOffset;
    Vector3 randomSpread;


    ComputeHolePattern(
        spreadAmount,
        out spawnOffset,
        out randomSpread
    );



    // سرعة خروج الطلاء
    Vector3 exitVelocity =
        exitDirection.normalized *
        particleSpeed;



    // تقليل الانحراف العشوائي
    Vector3 controlledSpread =
        randomSpread *
        0.35f;



    // سرعة الجزيء النهائية
   Vector3 vel =
    bucketMotion.velocity * 0.3f
    + exitDirection.normalized * particleSpeed
    + randomSpread * 0.15f;


    p.position =
        paintPoint.position +
        spawnOffset;



    p.velocity = vel;


    p.color =
        paintColor;


    p.viscosityEffect =
        effViscosity;


    // Physically-derived drop size: detaches from the hole per Tate's law, sized by hole radius,
    // surface tension, density, gravity and viscosity (see DropDiameter).
    float dropMass;
    p.size = DropDiameter(effViscosity, particleSpeed, out dropMass);
    p.approxMass = dropMass;
    lastDropDiameter = p.size;



    p.lifetime =
        particleLifetime;


    p.age = 0f;



    p.tr.position =
        p.position;


    p.tr.localScale =
        Vector3.one *
        p.size;



    if(p.rend != null)
        p.rend.material.color =
            p.color;



    p.go.SetActive(true);


    p.state =
        ParticleState.Emitted;



    currentPaintAmount =
        Mathf.Max(
            0f,
            currentPaintAmount - 0.01f
        );
}
    // Hole shape sets the spawn offset and spread pattern
 void ComputeHolePattern(float spread, out Vector3 offset, out Vector3 randomSpread)
{
    float controlledSpread = spread * 0.25f; // تقليل تناثر الخروج 75%

    switch (holeShape)
    {
        case HoleShape.Narrow: // thin line along Z

            offset =
                new Vector3(
                    Random.Range(-holeRadius, holeRadius) * 0.15f,
                    0f,
                    Random.Range(-holeRadius, holeRadius) * 4f
                );


            randomSpread =
                new Vector3(
                    Random.Range(-controlledSpread, controlledSpread) * 0.15f,
                    0f,
                    Random.Range(-controlledSpread, controlledSpread)
                );

            break;



        case HoleShape.Wide: // wide band along X

            offset =
                new Vector3(
                    Random.Range(-holeRadius, holeRadius) * 6f,
                    0f,
                    Random.Range(-holeRadius, holeRadius) * 0.5f
                );


            randomSpread =
                new Vector3(
                    Random.Range(-controlledSpread, controlledSpread) * 3f,
                    0f,
                    Random.Range(-controlledSpread, controlledSpread) * 0.4f
                );

            break;



        case HoleShape.Multiple: // three separated streams

            int k = Random.Range(0, 3);


            offset =
                new Vector3(
                    (k - 1) * holeRadius * 6f,
                    0f,
                    0f
                );


            randomSpread =
                Random.insideUnitSphere *
                controlledSpread *
                0.5f;

            break;



        default: // Round

            Vector2 disc =
                Random.insideUnitCircle *
                holeRadius;


            offset =
                new Vector3(
                    disc.x,
                    0f,
                    disc.y
                );


            randomSpread =
                new Vector3(
                    Random.Range(-controlledSpread, controlledSpread),
                    Random.Range(-controlledSpread * 0.1f,
                                 controlledSpread * 0.1f),
                    Random.Range(-controlledSpread, controlledSpread)
                );


            break;
    }
}

    public void RefillPaint() { currentPaintAmount = maxPaintAmount; }
}
