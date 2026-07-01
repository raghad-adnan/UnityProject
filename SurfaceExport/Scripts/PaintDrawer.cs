using System.Collections.Generic;
using UnityEngine;

public enum SurfaceType { Canvas, Wood, Metal, Paper }
public enum HoleShape { Round, Narrow, Wide, Multiple }

public class PaintPhysics : MonoBehaviour
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
    [Header("Scene refs")]
    public Transform paintPoint;
    public Renderer canvasRenderer;
    public PendulumMotion bucketMotion;
    public SurfaceType surface = SurfaceType.Canvas;
    public Color paintColor = Color.red;

    [Header("Paint reservoir")]
    public float maxPaintAmount = 5f;
    public float currentPaintAmount = 5f;

    [Header("Viscosity / temperature / humidity")]
    public float viscosity = 8f;
    public float temperature = 25f;
    public float minViscosity = 0.3f;
    public float maxViscosity = 3f;
    [Range(0, 100)] public float humidity = 50f;

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
    public float dampingFactor = 0.96f;

    [Header("Particle interaction")]
    public bool enableParticleInteraction = true;
    public float interactionRadius = 0.25f;
    public float cohesionStrength = 0.5f;
    public float separationStrength = 1.0f;

    [Header("Drawing / surface")]
    public float gravity = 9.81f;
    public int textureSize = 1024;
    public float baseSplatSize = 8f;
    public bool continuousJetMode = true;
    public float jetMaxGapUV = 0.04f;
    public bool crownSplashEnabled = false;

    [Header("Paint fluid properties (SI)")]
    // Single source of truth for the macroscopic surface model. Paint, not water.
    public float density = FluidConstants.PaintDensity;             // rho   (kg/m^3)
    public float surfaceTension = FluidConstants.PaintSurfaceTension; // gamma (N/m)
    public float paintViscosityPaS = FluidConstants.PaintViscosity;  // eta   (Pa·s) for film/Washburn/Tanner
    public float substrateThicknessMeters = 0.001f;                  // depth over which a porous surface fully absorbs paint

    private float[,] absorbedPaint;
    [Header("Paint film (SI)")]
    public float maxFilmThicknessMeters = 2e-3f; // max wet-film thickness before it sags (~2 mm)
    private float[,] paintThickness;             // per-pixel film thickness in METRES

    [Header("Unit scale (derived from canvas geometry)")]
    public float pixelsPerUnit;        // texture pixels per metre
    public float canvasMetersWidth;    // physical canvas width  (m)
    public float canvasMetersHeight;   // physical canvas height (m)
    // Unity's built-in Plane mesh spans 10 local units; the UV mapping below divides by this.
    private const float PlaneMeshExtent = 10f;
    private int roughnessJitterPx;     // real Ra (um) converted to pixels (microscopic -> usually 0)
    // (removed) surfaceWetTime / lastAbsorbFrac: the old GLOBAL absorption clock started at Start() and
    // drove every porous surface at once, so it saturated within seconds of launch regardless of when
    // paint actually landed. Absorption is now per-splat (ActiveSplat.localWetTime / localAbsorbFrac).

    [Header("Floor tilt")]
    [Range(-80f, 80f)] public float canvasTiltControlDeg = 0f; // pitch about local X (slider or mouse)
    [Range(-80f, 80f)] public float canvasTiltRollDeg = 0f;    // roll about local Z (mouse)
    private Quaternion canvasBaseRotation;
    private bool canvasBaseCaptured;
    public float lastDropDiameter;                            // last physically-derived drop diameter (m)

    [Header("Display (debug)")]
    public float currentEmissionRate;
    public float paintLevel;
    public bool isHoleSubmerged;
    public int activeParticles;
    public float Re, We, Ca, Oh, K;
    public float canvasTiltDeg;        // live surface tilt from horizontal (deg)

    private SurfacePreset preset;
    private Texture2D texture;
    private float emitAccumulator;
    private static Mesh sphereMesh;
    private Material particleMatTemplate;
    private PaintParticlePool pool;
    private bool textureDirty;
    private Vector2Int lastSplatPx;
    private bool lastSplatValid;

    // active splats keep spreading (Tanner's law) and may flow downhill (thin-film) after landing
    private class ActiveSplat
    {
        public int px, py;            // current centre in pixels (moves while flowing)
        public float cx, cy;          // sub-pixel centre, so slow flow accumulates correctly
        public float radiusMeters;    // R_final: Madejski max-spread radius (m)
        public float tv;              // Tanner viscous-relaxation time (s)
        public float volumeM3;        // paint volume in this splat (m^3)
        public Color color;           // stamp colour (already thickness-adjusted)
        public float age;
        public float life;
        // Phase 4 - surface flow state
        public float filmThickness;   // h (m)
        public float pinningForce;    // F_pin (N/m^2)
        public bool isFlowing;
        public float flowedDistanceUV; // distance travelled (pixels) since landing
        // C5 spreading regime (set at landing):
        public float thetaEqRad;      // apparent (Wenzel) equilibrium contact angle [rad]
        public bool absorptionLimited; // true when theta_eq ~ 0 (complete wetting): no finite t_v exists
        // C6 per-splat absorption clock — starts when THIS splat lands, not at game start:
        public float localWetTime;    // seconds since this splat first touched the surface
        public float localAbsorbFrac; // this splat's own Lucas-Washburn saturation progress [0..1]
        public Color currentColor;    // s.color dulled toward the substrate by localAbsorbFrac (what we stamp)
    }
    private readonly List<ActiveSplat> activeSplats = new List<ActiveSplat>();
    private const int MaxActiveSplats = 60;
    // C5, complete-wetting case (theta_eq = 0 -> de Gennes t_v -> infinity). There is no physical
    // finite capillary timescale, so this is a documented REAL-TIME design choice: the splat spreads
    // to its Madejski R_final over this visual time and is then frozen/removed once THIS splat's own
    // capillary absorption (localAbsorbFrac -> full) saturates. It is NOT a measured t_v.
    private const float AbsorptionSpreadVisualSeconds = 0.6f;
    // Saturation fraction at which an absorption-limited splat stops growing (treated as "soaked in").
    private const float AbsorptionFullFraction = 0.99f;
    // theta_eq below this (rad) is treated as complete wetting -> absorption-limited spreading.
    private const float CompleteWettingAngleRad = 0.01f;
    // Numerical guard (NOT a physical value): caps how long a single flowing splat is tracked,
    // so a near-horizontal slow rivulet can't be updated forever. Physical termination is running
    // off the floor edge (offCanvas) or drying on a level surface.
    private const float FlowSafetySeconds = 30f;

    void Start()
{
    sph = new SPHSolver();

    sph.smoothingRadius = interactionRadius;

    sph.viscosity = viscosity;

    sph.stiffness = 2f;

    sph.restDensity = 1f;


    spatialGrid = new SpatialGrid(interactionRadius);


    texture = new Texture2D(textureSize, textureSize);
    paintThickness =
    new float[textureSize, textureSize];
    absorbedPaint =
    new float[textureSize, textureSize];
    texture.wrapMode = TextureWrapMode.Clamp;


    if (canvasRenderer != null)
        canvasRenderer.material.mainTexture = texture;


    if (canvasRenderer != null)
    {
        canvasBaseRotation = canvasRenderer.transform.rotation;
        canvasBaseCaptured = true;
    }

    // This scene was saved with an older script version whose serialized density/surfaceTension
    // are stale *water* values. If we still see exactly those, switch to the documented paint
    // properties so the whole model is one self-consistent fluid. (Deliberate values are kept.)
    if (Mathf.Approximately(density, FluidConstants.WaterDensity))
        density = FluidConstants.PaintDensity;
    if (Mathf.Approximately(surfaceTension, FluidConstants.DefaultSurfaceTension))
        surfaceTension = FluidConstants.PaintSurfaceTension;

    ComputeUnitScale();
    preset = SurfacePreset.From(surface);

    // Diagnostic (once at startup, NOT per frame): flag any surface in the complete-wetting /
    // superhydrophilic regime where r*cos(theta_Young) >= 1 falls OUTSIDE the classic Wenzel model
    // (not an error; see Bico et al. 2002). Its apparent angle is clamped to 0 and C5 spreading
    // switches to the absorption-limited path.
    foreach (SurfaceType st in System.Enum.GetValues(typeof(SurfaceType)))
    {
        SurfacePreset sp = SurfacePreset.From(st);
        float rc = sp.wenzelRoughness * Mathf.Cos(sp.contactAngleDeg * Mathf.Deg2Rad);
        if (rc > 1f)
            Debug.LogWarning($"[Surface] {st}: r*cos(theta_Young) = {rc:F3} > 1 -> complete-wetting " +
                             "(superhydrophilic) regime, outside classic Wenzel validity; theta* clamped to 0.");
    }


    Clear();


    particleMatTemplate =
        new Material(Shader.Find("Unlit/Color"));


    EnsureSphereMesh();


    pool =
        new PaintParticlePool(
            transform,
            sphereMesh,
            particleMatTemplate,
            maxParticles
        );
}

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f || bucketMotion == null || paintPoint == null) return;

        ApplyCanvasTilt();
        ComputeUnitScale();
        preset = SurfacePreset.From(surface);

        // Edge jitter derived from the real surface roughness Ra (microscopic -> usually 0 px).
        roughnessJitterPx = Mathf.RoundToInt(preset.arithmeticRoughnessUm * 1e-6f * pixelsPerUnit);

        EmitStep(dt);
        UpdateParticles(dt);
        UpdateActiveSplats(dt); // per-splat spreading AND per-splat capillary absorption happen here now

        activeParticles = pool != null ? pool.ActiveCount : 0;
        if (textureDirty) { texture.Apply(); textureDirty = false; }
    }

    // Tilt the floor about its local X (pitch) and Z (roll) relative to its scene base orientation.
    // Rotating the transform changes planeNormal (= transform.up), so the thin-film flow reacts to
    // the tilt automatically — nothing else needs to know the angle.
    void ApplyCanvasTilt()
    {
        if (canvasRenderer == null) return;
        if (!canvasBaseCaptured)
        {
            canvasBaseRotation = canvasRenderer.transform.rotation;
            canvasBaseCaptured = true;
        }
        canvasRenderer.transform.rotation =
            canvasBaseRotation * Quaternion.Euler(canvasTiltControlDeg, 0f, canvasTiltRollDeg);
    }

    // Derive the metre<->pixel scale from the canvas's real world size.
    // The canvas is a Unity Plane (10 local units); InverseTransformPoint divides out scale,
    // so the full texture spans (PlaneMeshExtent * lossyScale) metres in world space.
    void ComputeUnitScale()
    {
        Vector3 s = (canvasRenderer != null) ? canvasRenderer.transform.lossyScale : Vector3.one;
        canvasMetersWidth  = PlaneMeshExtent * Mathf.Abs(s.x);
        canvasMetersHeight = PlaneMeshExtent * Mathf.Abs(s.z);
        pixelsPerUnit = (canvasMetersWidth > 1e-6f && texture != null)
            ? texture.width / canvasMetersWidth
            : 40f; // safe fallback (matches the legacy implicit scale)
    }

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


            PaintSplat(
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
   
   void PaintSplat(Transform c, Vector3 hitPoint, PaintParticle p, Vector3 n)
{
    Vector3 local = c.InverseTransformPoint(hitPoint);

    Vector2 uv =
        new Vector2(
            0.5f - local.x / PlaneMeshExtent,
            0.5f - local.z / PlaneMeshExtent
        );


    if (uv.x < 0f || uv.x > 1f ||
        uv.y < 0f || uv.y > 1f)
    {
        lastSplatValid = false;
        return;
    }



    int px =
        (int)(uv.x * texture.width);

    int py =
        (int)(uv.y * texture.height);

    

    float speed =
        p.velocity.magnitude;



    float D =
        Mathf.Max(
            0.001f,
            p.size
        );


    // Dynamic viscosity in Pa·s: paint base viscosity modulated by the per-particle effect.
    float mu = paintViscosityPaS * Mathf.Max(0.05f, p.viscosityEffect);

    Re = FluidConstants.Reynolds(density, speed, D, mu);
    We = FluidConstants.Weber(density, speed, D, surfaceTension);
    Ca = FluidConstants.Capillary(mu, speed, surfaceTension);
    Oh = FluidConstants.Ohnesorge(mu, density, surfaceTension, D);
    K  = FluidConstants.StowHadfieldK(We, Re);

    // --- Maximum spread radius: Pasandideh-Fard / Madejski (1996) ---
    // Wenzel apparent contact angle accounts for surface roughness.
    float youngRad     = preset.contactAngleDeg * Mathf.Deg2Rad;
    float cosThetaStar = FluidConstants.WenzelCos(preset.wenzelRoughness, youngRad);
    float betaMax      = FluidConstants.MadejskiBetaMax(We, Re, cosThetaStar);
    float Rmeters      = D * betaMax * 0.5f;                 // splat radius (m)
    int r = Mathf.Clamp(Mathf.RoundToInt(Rmeters * pixelsPerUnit), 2, textureSize / 4);
    int r0 = Mathf.Max(2, Mathf.RoundToInt(D * 0.5f * pixelsPerUnit)); // initial drop footprint (px)

    // Paint volume of the drop and the resulting film thickness (m): h = V / (pi R^2).
    float volumeM3 = p.approxMass / Mathf.Max(1f, density);          // V = m / rho
    float filmH    = volumeM3 / (Mathf.PI * Rmeters * Rmeters);

    // Deposit the film (in metres) and let porous surfaces draw it in (no adhesion fudge:
    // paint is retained/lost only by physical absorption and by gravity-driven flow below).
    AddPaintThickness(px, py, r, filmH);
    AbsorbPaint(px, py);

    // Surface colour from Beer-Lambert hiding power: opacity = 1 - exp(-h / h_hide) over the bare
    // SUBSTRATE colour (thin paint reveals the real surface: grey metal, brown wood, cream canvas).
    float thickness = paintThickness[px, py];                                   // metres
    float opacity   = FluidConstants.BeerLambertOpacity(thickness, FluidConstants.PaintHidingThicknessMeters);
    Color finalColor = Color.Lerp(preset.substrateColor, p.color, opacity);

    // --- Splash vs deposition: Stow-Hadfield K, with a roughness-lowered threshold (C2) ---
    // K_eff = 57.7*(1 - alpha*min(Ra/Ra_ref,1)); rougher surfaces splash sooner (Canvas << Metal).
    float Kthreshold = FluidConstants.SplashThresholdRough(preset.arithmeticRoughnessUm);
    if (K > Kthreshold)
    {
        // Finger count from Rayleigh-Taylor instability of the rim:  N = sqrt(beta*We/12).
        int fingers = Mathf.Clamp(Mathf.RoundToInt(FluidConstants.SplashFingerCount(We, betaMax)), 3, 40);
        // Ejected-droplet radius = half the rim thickness (Rayleigh break-up):  d ~ D / sqrt(beta).
        int jetR = Mathf.Max(1, Mathf.RoundToInt(0.5f * (D / Mathf.Sqrt(Mathf.Max(1f, betaMax))) * pixelsPerUnit));
        ScatterDroplets(px, py, r, p.color, fingers, jetR, speed, Kthreshold);
        if (crownSplashEnabled) StampCrown(px, py, r, p.color, fingers, jetR);
    }

    // --- Register an active splat: Tanner's-law growth + thin-film surface flow (C5) ---
    // theta_eq = apparent (Wenzel) equilibrium contact angle. Two physical regimes:
    //   theta_eq > 0  (Metal, Canvas): partial wetting -> de Gennes capillary t_v = eta*R/(gamma*theta^3),
    //                  the timescale that actually pairs with Tanner's (t/t_v)^(1/10) law.
    //   theta_eq = 0  (Wood, Paper, complete wetting): de Gennes t_v -> infinity, so there is NO finite
    //                  capillary timescale; we fall back to an absorption-limited visual spread that
    //                  freezes once THIS splat's own Washburn saturation completes (see UpdateSplatAbsorption).
    float thetaEqRad = Mathf.Acos(Mathf.Clamp(cosThetaStar, -1f, 1f));
    bool absorptionLimited = thetaEqRad <= CompleteWettingAngleRad;
    float tv = absorptionLimited
        ? AbsorptionSpreadVisualSeconds
        : FluidConstants.TannerRelaxTimeDeGennes(mu, surfaceTension, Rmeters, thetaEqRad);

    if (activeSplats.Count < MaxActiveSplats)
    {
        activeSplats.Add(new ActiveSplat
        {
            px = px,
            py = py,
            cx = px,
            cy = py,
            radiusMeters = Rmeters,
            tv = tv,
            volumeM3 = volumeM3,
            color = finalColor,
            age = 0f,
            // Growth window for the partial-wetting regime: Tanner growth completes at t_v, so a level
            // splat stops being updated shortly after (0.1 s floor = numerical minimum for flow-onset).
            // For the absorption-limited regime expiry is saturation-driven instead (see UpdateActiveSplats).
            // This is NOT a drying time — the texture mark is permanent.
            life = Mathf.Max(tv, 0.1f),
            isFlowing = false,
            flowedDistanceUV = 0f,
            thetaEqRad = thetaEqRad,
            absorptionLimited = absorptionLimited,
            localWetTime = 0f,        // this splat's absorption clock starts now, at landing
            localAbsorbFrac = 0f,     // nothing soaked in yet
            currentColor = finalColor // undulled at birth; UpdateSplatAbsorption dulls it over time
        });
        StampCircle(px, py, r0, finalColor); // initial contact footprint; Tanner fills it out
    }
    else
    {
        StampCircle(px, py, r, finalColor);  // fallback: stamp full splat if the list is full
    }



    textureDirty = true;



    lastSplatPx =
        new Vector2Int(px, py);


    lastSplatValid = true;
}
 void UpdateActiveSplats(float dt)
    {
        if (activeSplats.Count == 0) return;

        // Project gravity onto the (possibly tilted) canvas plane -> downhill direction.
        Transform c = (canvasRenderer != null) ? canvasRenderer.transform : transform;
        Vector3 n = c.up;
        Vector3 downProj = Vector3.down - n * Vector3.Dot(Vector3.down, n);
        float sinAlpha = Mathf.Clamp01(downProj.magnitude); // sin of tilt from horizontal
        canvasTiltDeg = Mathf.Asin(sinAlpha) * Mathf.Rad2Deg;

        Vector2 downUV = Vector2.zero;
        if (sinAlpha > 1e-4f)
        {
            Vector3 dLocal = c.InverseTransformDirection(downProj.normalized);
            // uv = (0.5 - local.x/E, 0.5 - local.z/E)  =>  d(uv) is proportional to (-dLocal.x, -dLocal.z)
            downUV = new Vector2(-dLocal.x, -dLocal.z);
            if (downUV.sqrMagnitude > 1e-10f) downUV.Normalize();
        }

        for (int i = activeSplats.Count - 1; i >= 0; i--)
        {
            ActiveSplat s = activeSplats[i];
            s.age += dt;
            s.localWetTime += dt; // per-splat contact clock: counts from THIS splat's landing, not game start

            // Advance this splat's own capillary absorption and dull its stamp colour accordingly.
            // (Done before stamping so the growth stamp below uses the up-to-date, dulled colour.)
            UpdateSplatAbsorption(s);

            if (!s.isFlowing)
            {
                // Tanner's law: radius approaches R_final as (t/t_v)^(1/10), then settles.
                float rt = FluidConstants.TannerRadius(s.radiusMeters, s.age, s.tv);
                int rtpx = Mathf.Max(2, Mathf.RoundToInt(rt * pixelsPerUnit));
                StampCircle(s.px, s.py, rtpx, s.currentColor);
                textureDirty = true;
            }

            UpdateSurfaceFlow(s, sinAlpha, downUV, dt);

            bool offCanvas = s.px < 0 || s.px >= textureSize || s.py < 0 || s.py >= textureSize;
            // Physical termination depends on the C5 spreading regime:
            //   absorption-limited (Wood/Paper, theta_eq=0): the splat keeps spreading until ITS OWN
            //       capillary absorption saturates (s.localAbsorbFrac -> full); only then is the paint
            //       "soaked in" and growth frozen. This replaces the unphysical t_v (and the old global clock).
            //   partial wetting (Metal/Canvas): a level splat stops after its de Gennes growth window.
            //   a flowing rivulet (either regime) terminates by running off the floor edge.
            // FlowSafetySeconds is only a numerical runaway guard.
            bool dried = s.absorptionLimited
                ? (!s.isFlowing && s.localAbsorbFrac >= AbsorptionFullFraction)
                : (!s.isFlowing && s.age >= s.life);
            bool expired = dried || offCanvas || s.age >= FlowSafetySeconds;
            if (expired) activeSplats.RemoveAt(i);
        }
    }

    // Thin-film (lubrication) flow of a settled splat under gravity on a tilted surface.
    void UpdateSurfaceFlow(ActiveSplat s, float sinAlpha, Vector2 downUV, float dt)
    {
        if (sinAlpha <= 1e-4f) { s.isFlowing = false; return; } // horizontal surface -> no flow

        float R = Mathf.Max(1e-4f, FluidConstants.TannerRadius(s.radiusMeters, s.age, s.tv));
        float h = s.volumeM3 / (Mathf.PI * R * R);   // film thickness h = V / (pi R^2)
        s.filmThickness = h;

        float rho = density, eta = paintViscosityPaS, gamma = surfaceTension;
        float cosAdv = Mathf.Cos(preset.advancingAngleDeg * Mathf.Deg2Rad);
        float cosRec = Mathf.Cos(preset.recedingAngleDeg * Mathf.Deg2Rad);

        // Contact-angle hysteresis: paint stays pinned until gravity beats the capillary pinning force.
        float Fpin = FluidConstants.PinningForce(gamma, cosRec, cosAdv, R); // N/m^2
        float Fg   = rho * gravity * sinAlpha * h;                          // N/m^2
        s.pinningForce = Fpin;
        if (Fg > Fpin) s.isFlowing = true;
        if (!s.isFlowing) return;

        // Nusselt surface velocity and downhill step (sub-pixel centre accumulates slow flow).
        float u = FluidConstants.NusseltFilmVelocity(rho, gravity, sinAlpha, h, eta); // m/s
        float stepPx = u * dt * pixelsPerUnit;
        int oldpx = s.px, oldpy = s.py;
        s.cx += downUV.x * stepPx;
        s.cy += downUV.y * stepPx;
        s.px = Mathf.RoundToInt(s.cx);
        s.py = Mathf.RoundToInt(s.cy);
        s.flowedDistanceUV += stepPx;
        if (s.px == oldpx && s.py == oldpy) return; // hasn't moved a whole pixel yet

        // Rivulet width from mass conservation: w = Q / (u*h);  Q = volume / time.
        float Q = s.volumeM3 / Mathf.Max(0.05f, s.age);       // m^3/s
        float wRiv = (u * h > 1e-9f) ? Q / (u * h) : 2f * R;  // m
        wRiv /= Mathf.Max(1f, preset.rivuletFactor);          // split among multiple channels
        int wpx = Mathf.Clamp(Mathf.RoundToInt(wRiv * pixelsPerUnit), 1,
                              Mathf.Max(2, Mathf.RoundToInt(R * pixelsPerUnit)));

        StampLine(oldpx, oldpy, s.px, s.py, wpx, s.currentColor);

        // Rayleigh-Taylor dripping: a bead forms once the film exceeds the critical thickness.
        float hcrit = FluidConstants.CriticalFilmThickness(gamma, rho, gravity, sinAlpha);
        if (h > hcrit) StampCircle(s.px, s.py, Mathf.Max(2, wpx), s.currentColor);

        textureDirty = true;
    }

    // Per-splat Lucas-Washburn absorption (C6). Replaces the old global AbsorbStep that swept the whole
    // texture on a clock started at Start(). Each active splat soaks in on ITS OWN localWetTime, so a
    // mark drawn two minutes into the session absorbs exactly like one drawn at t=0 — no hidden
    // dependence on game age. As paint soaks in, the colour re-stamped each frame relaxes toward the bare
    // substrate; dulling the stamp colour (not the texture pixels, which the every-frame re-stamp would
    // overwrite) is what makes the fade actually accumulate while the splat is active and freeze once it
    // saturates. Cost is O(1) per splat — cheaper than the old full 1024x1024 sweep.
    void UpdateSplatAbsorption(ActiveSplat s)
    {
        // Non-porous surfaces (metal porosity ~ 0) never absorb -> colour stays at the deposited value.
        if (preset.porosity <= 0.001f) { s.currentColor = s.color; return; }

        // Capillary rise INSIDE the pores is governed by the intrinsic Young angle, not the Wenzel
        // apparent angle (Wenzel describes only the external footprint). Lucas-Washburn 1921; using
        // theta_Young removes the earlier absorption over-estimate (Wood ~ +74 %, Canvas ~ +40 %).
        float cosThetaYoung = Mathf.Cos(preset.contactAngleDeg * Mathf.Deg2Rad);
        float depth = FluidConstants.WashburnDepth(
            preset.poreRadiusMeters, surfaceTension, cosThetaYoung, paintViscosityPaS, s.localWetTime);
        s.localAbsorbFrac = Mathf.Clamp01(depth / Mathf.Max(1e-6f, substrateThicknessMeters));

        // Diagnostic mirror only — nothing downstream reads absorbedPaint for logic (see AbsorbPaint).
        if (absorbedPaint != null && s.px >= 0 && s.px < textureSize && s.py >= 0 && s.py < textureSize)
            absorbedPaint[s.px, s.py] = s.localAbsorbFrac;

        // Dull the stamp colour toward the substrate. Max dulling ~ porosity (a fully soaked porous
        // surface shows the mark faintly); high humidity slows absorption/drying.
        float dull = s.localAbsorbFrac * preset.porosity * (1f - (humidity / 100f) * 0.7f);
        s.currentColor = Color.Lerp(s.color, preset.substrateColor, Mathf.Clamp01(dull));
    }


    void StampCircle(int cx,int cy,int r,Color color)
{
    for(int x=-r;x<=r;x++)
    {
        for(int y=-r;y<=r;y++)
        {

            if(x*x+y*y <= r*r)
            {

                int px=cx+x;
                int py=cy+y;


                if(px>=0 &&
                   px<texture.width &&
                   py>=0 &&
                   py<texture.height)
                {

                    texture.SetPixel(
                    px,
                    py,
                    color
                    );

                }
            }
        }
    }
}

    void StampRing(int cx, int cy, int r, Color col)
    {
        int inner = Mathf.Max(0, r - 2);
        int rough = roughnessJitterPx;
        for (int x = -r; x <= r; x++)
            for (int y = -r; y <= r; y++)
            {
                int d2 = x * x + y * y;
                if (d2 <= r * r && d2 >= inner * inner) PutPixel(cx + x, cy + y, col, rough);
            }
    }

    void StampEllipse(int cx, int cy, float rx, float ry, float angRad, Color col)
    {
        float cos = Mathf.Cos(angRad), sin = Mathf.Sin(angRad);
        int rmax = Mathf.CeilToInt(Mathf.Max(rx, ry));
        int rough = roughnessJitterPx;
        for (int x = -rmax; x <= rmax; x++)
            for (int y = -rmax; y <= rmax; y++)
            {
                float lx = x * cos + y * sin, ly = -x * sin + y * cos;
                if ((lx * lx) / (rx * rx) + (ly * ly) / (ry * ry) <= 1f) PutPixel(cx + x, cy + y, col, rough);
            }
    }

    void StampStreak(int cx, int cy, Vector2 dir, float length, int width, Color col)
    {
        int steps = Mathf.Max(2, Mathf.CeilToInt(length));
        for (int i = -steps / 3; i <= steps; i++)
        {
            float t = (float)i / steps;
            int x = cx + Mathf.RoundToInt(dir.x * i), y = cy + Mathf.RoundToInt(dir.y * i);
            StampCircle(x, y, Mathf.Max(1, Mathf.RoundToInt(width * (1f - Mathf.Abs(t)))), col);
        }
    }

    // Crown wall: `fingers` jets (Rayleigh-Taylor count) sitting on the lamella rim (radius rPx),
    // each the size of a broken-off rim droplet (jetR).
    void StampCrown(int cx, int cy, int rPx, Color col, int fingers, int jetR)
    {
        for (int i = 0; i < fingers; i++)
        {
            float a = i * Mathf.PI * 2f / fingers;
            StampCircle(cx + Mathf.RoundToInt(Mathf.Cos(a) * rPx),
                        cy + Mathf.RoundToInt(Mathf.Sin(a) * rPx), jetR, col);
        }
    }

    // Ejected satellite droplets — fully deterministic (no Random): one droplet leaves each crown
    // finger, so its azimuth equals the finger angle. Its landing radius is the projectile range of
    // a droplet thrown from the rim:
    //   u_eject = v * sqrt(1 - Kc/K)   (the kinetic energy above the splash limit drives ejection)
    //   range   = u_eject^2 * sin(2*theta) / g     (ballistic range on the surface)
    // theta is the measured crown ejection angle (~60 deg, documented splash geometry).
    // Kc is the SAME roughness-corrected threshold used to trigger the splash (C2), otherwise a rough
    // surface that splashes just above its lowered Kc would wrongly compute zero ejection range.
    void ScatterDroplets(int cx, int cy, int rPx, Color col, int count, int dropletR, float impactSpeed, float kThreshold)
    {
        float kExcess = Mathf.Clamp01(1f - kThreshold / Mathf.Max(1e-3f, K));
        float uEject  = impactSpeed * Mathf.Sqrt(kExcess);
        const float crownEjectAngleRad = 60f * Mathf.Deg2Rad;
        float rangeM  = uEject * uEject * Mathf.Sin(2f * crownEjectAngleRad) / Mathf.Max(0.01f, gravity);
        int landPx    = rPx + Mathf.RoundToInt(rangeM * pixelsPerUnit);
        for (int i = 0; i < count; i++)
        {
            float a = i * Mathf.PI * 2f / count;   // finger azimuth (same instability as the crown)
            StampCircle(cx + Mathf.RoundToInt(Mathf.Cos(a) * landPx),
                        cy + Mathf.RoundToInt(Mathf.Sin(a) * landPx), dropletR, col);
        }
    }

    void StampLine(int x0, int y0, int x1, int y1, int r, Color col)
    {
        int steps = Mathf.Max(1, Mathf.RoundToInt(Vector2.Distance(new Vector2(x0, y0), new Vector2(x1, y1))));
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            StampCircle(Mathf.RoundToInt(Mathf.Lerp(x0, x1, t)), Mathf.RoundToInt(Mathf.Lerp(y0, y1, t)), r, col);
        }
    }

    void PutPixel(int px, int py, Color col, int rough)
    {
        if (rough > 0) { px += Random.Range(-rough, rough + 1); py += Random.Range(-rough, rough + 1); }
        if (px < 0 || px >= texture.width || py < 0 || py >= texture.height) return;
        // Surface-retained fraction: porosity of the substrate goes into the pores, so a porous
        // surface shows the mark fainter.  intensity = 1 - porosity  (metal 1.0 ... paper 0.4).
        float intensity = Mathf.Clamp01(1f - preset.porosity);
        texture.SetPixel(px, py, Color.Lerp(texture.GetPixel(px, py), col, intensity));
    }

    void EnsureSphereMesh()
    {
        if (sphereMesh != null) return;
        GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphereMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
        if (Application.isPlaying) Destroy(tmp); else DestroyImmediate(tmp);
    }
    // Deposit a paint film (metres) with a triangular profile peaking at the centre (puddles are
    // thicker in the middle). filmThicknessMeters = V / (pi R^2) is the physical mean thickness.
    void AddPaintThickness(int cx, int cy, int radius, float filmThicknessMeters)
{
    int rad = Mathf.Max(1, radius);
    for(int x = -rad; x <= rad; x++)
    {
        for(int y = -rad; y <= rad; y++)
        {

            int px = cx + x;
            int py = cy + y;


            if(px < 0 ||
               px >= textureSize ||
               py < 0 ||
               py >= textureSize)
                continue;



            float distance =
                Mathf.Sqrt(
                    x*x + y*y
                );



            if(distance <= rad)
            {

                float amount =
                    filmThicknessMeters *
                    (1f - distance / rad);



                paintThickness[px,py] =
                    Mathf.Clamp(
                        paintThickness[px,py]
                        +
                        amount,

                        0f,
                        maxFilmThicknessMeters
                    );
            }
        }
    }
}
    // public utilities 
    public void Clear()
    {
        if (texture == null) return;
        // Start from the bare-surface (substrate) colour instead of a unified white, so each surface
        // reads correctly when blank (grey metal, brown wood, cream canvas/paper).
        Color baseColor = preset.substrateColor;
        if (baseColor.a <= 0f) baseColor = Color.white; // guard: preset not yet assigned
        Color[] cols = new Color[texture.width * texture.height];
        for (int i = 0; i < cols.Length; i++) cols[i] = baseColor;
        texture.SetPixels(cols);
        texture.Apply();
        activeSplats.Clear(); // also discards every splat's per-splat localWetTime/localAbsorbFrac
        lastSplatValid = false;
        if (paintThickness != null) System.Array.Clear(paintThickness, 0, paintThickness.Length);
        if (absorbedPaint != null) System.Array.Clear(absorbedPaint, 0, absorbedPaint.Length);
    }

    public void SavePainting()
    {
        string filename = $"Painting_{System.DateTime.Now:yyyyMMdd_HHmmss}.png";
        System.IO.File.WriteAllBytes(Application.dataPath + "/" + filename, texture.EncodeToPNG());
        Debug.Log("Saved painting: " + filename);
    }
   // C6: called once when a splat lands. At birth nothing has soaked in yet, so the per-pixel
   // diagnostic starts at 0. Saturation then advances per-splat in UpdateSplatAbsorption() from that
   // splat's own localWetTime — there is no global absorption clock anymore.
   void AbsorbPaint(int x,int y)
   {
       if (absorbedPaint == null) return;
       absorbedPaint[x, y] = 0f;
   }
    public void RefillPaint() { currentPaintAmount = maxPaintAmount; }
    public float GetPaintAreaCoverage()
    {
        if (texture == null) return 0f;
        // "Covered" now means "differs from the bare SUBSTRATE colour" (the substrate is no longer
        // assumed white, so a fixed near-white threshold would wrongly count blank grey metal as 100%).
        Color32 sub = (Color32)preset.substrateColor;
        const int tol = 12; // per-channel 8-bit tolerance for "still bare surface"
        Color32[] cols = texture.GetPixels32();
        int colored = 0;
        for (int i = 0; i < cols.Length; i++)
            if (Mathf.Abs(cols[i].r - sub.r) > tol ||
                Mathf.Abs(cols[i].g - sub.g) > tol ||
                Mathf.Abs(cols[i].b - sub.b) > tol) colored++;
        return (float)colored / cols.Length;
    }

    void OnGUI()
    {
        Event e = Event.current;
        if (e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.S) SavePainting();
            if (e.keyCode == KeyCode.R) Clear();
        }
        // Right-mouse drag tilts the floor: vertical drag = pitch (X), horizontal drag = roll (Z).
        if (e.type == EventType.MouseDrag && e.button == 1)
        {
            canvasTiltControlDeg = Mathf.Clamp(canvasTiltControlDeg + e.delta.y * 0.3f, -80f, 80f);
            canvasTiltRollDeg    = Mathf.Clamp(canvasTiltRollDeg    + e.delta.x * 0.3f, -80f, 80f);
            e.Use();
        }
    }
}
