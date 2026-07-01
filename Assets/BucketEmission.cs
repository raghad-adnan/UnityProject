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
    // Paint reservoir now lives on PendulumMotion (currentPaintMass, in kg) so the bucket weight and
    // the emission gate read the SAME quantity. This removes the old duplicate `currentPaintAmount`
    // counter that drained independently of the bucket's mass.

    [Header("Hole")]
    public HoleShape holeShape = HoleShape.Round;
    public float holeRadius = 0.06f;
    public float holeHeight = 0.1f;
    public float holeFactor = 1f;
    public Vector3 exitDirection = Vector3.down;

    [Header("Bucket geometry")]
    public float bucketRadius = 0.15f;

    [Header("Multi-colour (PDF §4 إمكانية استخدام أكثر من لون)")]
    public bool multiColorMode = false;
    public Color paintColor2 = new Color(0.20f, 0.45f, 1f);   // blue
    public Color paintColor3 = new Color(1f, 0.82f, 0.10f);   // yellow

    // With multi-colour on, each swing lays down a different colour from the 3-colour palette, so a
    // full run shows several coloured arcs. Off -> every drop uses the single paintColor.
    Color PickColor()
    {
        if (!multiColorMode) return paintColor;
        int idx = (bucketMotion != null ? Mathf.Abs(bucketMotion.swingCount) : 0) % 3;
        return idx == 0 ? paintColor : (idx == 1 ? paintColor2 : paintColor3);
    }

    [Header("Emission")]
    public float baseEmission = 40f;
    public int maxParticles = 2000;            // pool cap: reservoir fill + the falling stream
    public int reservoirParticles = 600;       // how many real paint particles fill the container when full
    public float baseSpeed = 0.2f;
    public float baseSize = 0.05f;
    public float baseSpread = 0.12f;
    public float particleLifetime = 5f;

    [Header("Droplet render")]
    // VISUAL-ONLY multiplier on a falling droplet's rendered size. The physical drop size (p.size,
    // ~1 cm from Tate's law) still drives ALL the impact physics; this only enlarges the rendered
    // sphere so the droplets read clearly and MATCH the container's paint blobs
    // (BucketContainer.fillParticleSize). Set to 1 for the true physical size.
    public float dropletVisualScale = 8f;

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

    // Mass of paint each contained particle represents, so releasing exactly `reservoirParticles`
    // of them empties the bucket (keeps the fill count and the bucket weight in lock-step).
    float PerParticleMass => (bucketMotion != null)
        ? bucketMotion.initialPaintMass / Mathf.Max(1, reservoirParticles) : 0.001f;

    void EmitStep(float dt)
    {
        if (bucketMotion == null) return;

        // ---- physical factors (unchanged model) ----
        float omega = bucketMotion.velocity.magnitude / Mathf.Max(0.01f, bucketMotion.L);
        float tiltRad = Mathf.Sqrt(bucketMotion.angleX * bucketMotion.angleX
                                 + bucketMotion.angleZ * bucketMotion.angleZ) * Mathf.Deg2Rad;
        float temperatureFactor = Mathf.Clamp01(temperature / 50f);
        float adjustedViscosity = Mathf.Lerp(maxViscosity, minViscosity, temperatureFactor);
        float effViscosity = Mathf.Max(0.05f, adjustedViscosity * viscosity);
        float tiltFactor = Mathf.Abs(Mathf.Sin(tiltRad));
        float baseLevel = bucketMotion.currentPaintMass / Mathf.Max(0.0001f, bucketMotion.initialPaintMass);
        paintLevel = baseLevel + tiltFactor * 0.25f / effViscosity;
        float lateralAccel = bucketMotion.GetTangentialAcceleration();
        float sloshOffset = (lateralAccel * baseLevel * bucketRadius) / (effViscosity * Mathf.Max(0.1f, gravity));
        isHoleSubmerged = (paintLevel + sloshOffset) >= holeHeight;

        // ---- (A) fill the container with REAL paint particles up to the current level ----
        // These are the same PaintParticles that later pour out of the hole and paint the canvas — one
        // particle system, not a separate decorative copy. Spawn a few per frame to avoid a start hitch.
        int fillTarget = Mathf.RoundToInt(reservoirParticles * baseLevel);
        int inside = CountInside();
        int spawnedThisFrame = 0;
        // Allow more spawns/frame so a 3000-particle reservoir fills in ~1-2 s rather than 30 s.
        while (inside < fillTarget && pool.ActiveCount < maxParticles && spawnedThisFrame < 100)
        {
            SpawnInsideParticle(effViscosity);
            inside++; spawnedThisFrame++;
        }

        // ---- (B) drain: pour contained particles out the hole at the physical flow rate ----
        if (bucketMotion.currentPaintMass <= 0f || !isHoleSubmerged) { currentEmissionRate = 0f; return; }
        float holeFlow = holeFactor * (holeRadius * holeRadius) / (0.06f * 0.06f);
        currentEmissionRate = baseEmission * baseLevel * (0.2f + tiltFactor)
                              * (1f + omega) * (1f / effViscosity) * holeFlow;
        // Paint "flow rate" input (PDF §4 سرعة تدفق اللون): scales the pour rate around its default (0.05).
        currentEmissionRate *= bucketMotion.flowRate / 0.05f;

        emitAccumulator += currentEmissionRate * dt;
        while (emitAccumulator >= 1f)
        {
            emitAccumulator -= 1f;
            if (!ReleaseLowestInside(effViscosity)) break;   // nothing left inside to pour out
            bucketMotion.ConsumePaint(PerParticleMass);      // draining lowers the reservoir AND bucket weight
        }
    }

    int CountInside()
    {
        var list = pool.All;
        int n = 0;
        for (int i = 0; i < list.Count; i++)
            if (list[i].state == ParticleState.InsideBucket) n++;
        return n;
    }

    // Spawn a REAL paint particle INSIDE the container (random spot in the bucket's box). It sloshes
    // there (ParticleSimulation handles the containment) until it drains out the hole. Sized by the same
    // Tate's-law DropDiameter used for the outgoing droplets, so inside and outgoing paint are identical.
    void SpawnInsideParticle(float effViscosity)
    {
        PaintParticle p = pool.Get();
        if (p == null) return;

        Vector3 local = new Vector3(Random.Range(-0.4f, 0.4f),
                                    Random.Range(-0.1f, 0.45f),
                                    Random.Range(-0.4f, 0.4f));
        p.position = bucketMotion.transform.TransformPoint(local);
        p.velocity = Vector3.zero;
        p.color = PickColor();
        p.viscosityEffect = effViscosity;

        float dropMass;
        p.size = DropDiameter(effViscosity, 0f, out dropMass);
        p.approxMass = dropMass;
        lastDropDiameter = p.size;

        p.age      = 0f;
        p.lifetime = particleLifetime;
        p.tr.position  = p.position;
        p.tr.localScale = Vector3.one * (p.size * dropletVisualScale);
        pool.SetParticleColor(p, p.color);
        p.go.SetActive(true);
        p.state = ParticleState.InsideBucket;
    }

    // Pour the LOWEST contained particle out through the hole: it keeps its size/colour and just switches
    // to the falling state, so the very same object that was in the container is now the droplet heading
    // to the canvas. Returns false if the container is empty.
    bool ReleaseLowestInside(float effViscosity)
    {
        var list = pool.All;
        int idx = -1;
        float minY = float.MaxValue;
        for (int i = 0; i < list.Count; i++)
        {
            PaintParticle p = list[i];
            if (p.state != ParticleState.InsideBucket) continue;
            if (p.position.y < minY) { minY = p.position.y; idx = i; }
        }
        if (idx < 0) return false;

        PaintParticle rp = list[idx];
        float particleSpeed = baseSpeed / Mathf.Max(0.05f, effViscosity) * 0.35f;
        Vector3 randomSpread;
        ComputeHolePattern(baseSpread / Mathf.Max(0.01f, effViscosity), out _, out randomSpread);
        rp.velocity = bucketMotion.velocity * 0.3f
                    + exitDirection.normalized * particleSpeed
                    + randomSpread * 0.15f;
        rp.age = 0f;
        rp.lifetime = particleLifetime;
        rp.state = ParticleState.Emitted;   // UpdateParticles: Emitted -> Falling -> canvas impact
        return true;
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

    public void RefillPaint() { if (bucketMotion != null) bucketMotion.RefillPaint(); }

    // -------------------------------------------------------------------------
    //  Hole highlights — glowing rings drawn at each hole exit point.
    //  Created once as child GameObjects of the bucket so they follow the swing
    //  automatically without any per-frame position update.
    // -------------------------------------------------------------------------
    // Called from PaintPhysics.Start() after the pool is ready.
    void SetupHoleHighlights()
    {
        if (bucketMotion == null) return;

        // Bright orange Unlit material — visible through the transparent bucket walls.
        Material glow = new Material(Shader.Find("Unlit/Color"));
        glow.color = new Color(1f, 0.55f, 0f); // vivid orange

        switch (holeShape)
        {
            case HoleShape.Round:
                SpawnHoleRing(Vector3.zero, holeRadius, false, glow);
                break;

            case HoleShape.Narrow:
                // Narrow slit: flat ellipse, major axis along Z.
                SpawnHoleRing(Vector3.zero, holeRadius, true, glow);
                break;

            case HoleShape.Wide:
                // Wide band: flat ellipse, major axis along X — use 3× radius visually.
                SpawnHoleRing(Vector3.zero, holeRadius * 3f, false, glow, scaleX: 3f);
                break;

            case HoleShape.Multiple:
                for (int k = 0; k < 3; k++)
                    SpawnHoleRing(new Vector3((k - 1) * holeRadius * 6f, 0f, 0f),
                                  holeRadius, false, glow);
                break;
        }
    }

    // Creates a LineRenderer ring child of the bucket at the given LOCAL offset on the bottom face.
    //   localOffset : x/z offset in bucket local space (y is fixed to the bottom).
    //   radius      : ring radius in local bucket units.
    //   narrow      : if true, compress X radius by 0.25 (slit shape).
    //   scaleX      : additional X multiplier for the Wide hole shape.
    void SpawnHoleRing(Vector3 localOffset, float radius, bool narrow, Material mat,
                        float scaleX = 1f)
    {
        GameObject ring = new GameObject("HoleHighlight");
        ring.transform.SetParent(bucketMotion.transform, false); // false = stay in parent's local space
        // Bottom face of the unit-cube bucket is at local y = -0.5; offset slightly upward so the ring
        // is visible and not clipped by the bucket floor geometry.
        ring.transform.localPosition = new Vector3(localOffset.x, -0.47f + holeHeight * 0.1f, localOffset.z);
        ring.transform.localRotation = Quaternion.identity;
        ring.transform.localScale    = Vector3.one;

        LineRenderer lr = ring.AddComponent<LineRenderer>();
        lr.useWorldSpace  = false; // positions in ring's local space → follows bucket swing
        lr.loop           = true;
        lr.sharedMaterial = mat;
        lr.startWidth     = 0.018f;
        lr.endWidth       = 0.018f;
        lr.startColor     = new Color(1f, 0.55f, 0f);
        lr.endColor       = new Color(1f, 0.90f, 0.1f); // yellow at the end for a glow gradient

        int segs = narrow ? 12 : 24;
        float rX = radius * (narrow ? 0.25f : 1f) * scaleX;
        float rZ = radius;
        lr.positionCount = segs;
        for (int i = 0; i < segs; i++)
        {
            float a = i / (float)segs * Mathf.PI * 2f;
            lr.SetPosition(i, new Vector3(Mathf.Cos(a) * rX, 0f, Mathf.Sin(a) * rZ));
        }
    }
}
