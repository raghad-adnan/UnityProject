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
    public float crownWeberThreshold = 400f;
    public float density = FluidConstants.WaterDensity;
    public float surfaceTension = FluidConstants.DefaultSurfaceTension;
    [Header("Paint Surface Physics")]

    public float adhesionStrength = 1f;

    public float absorptionRate = 0.1f;

    public float surfaceGravity = 1f;
    
    private float[,] absorbedPaint;
    [Header("Paint thickness")]
    public float maxPaintThickness = 1f;
    public float thicknessAdd = 0.05f;
    private float[,] paintThickness;

    [Header("Display (debug)")]
    public float currentEmissionRate;
    public float paintLevel;
    public bool isHoleSubmerged;
    public int activeParticles;
    public float Re, We, Ca, Oh;

    private SurfacePreset preset;
    private Texture2D texture;
    private float emitAccumulator;
    private static Mesh sphereMesh;
    private Material particleMatTemplate;
    private PaintParticlePool pool;
    private bool textureDirty;
    private Vector2Int lastSplatPx;
    private bool lastSplatValid;

    // active splats keep spreading for a short time after landing
    private class ActiveSplat { public int px, py; public float radius; public float growRate; public Color color; public float age; public float life; }
    private readonly List<ActiveSplat> activeSplats = new List<ActiveSplat>();
    private const int MaxActiveSplats = 60;
    private float absorbTimer;

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
    absorbedPaint =
    new float[textureSize, textureSize];
    texture.wrapMode = TextureWrapMode.Clamp;


    if (canvasRenderer != null)
        canvasRenderer.material.mainTexture = texture;


    preset = SurfacePreset.From(surface);


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

        preset = SurfacePreset.From(surface);
        EmitStep(dt);
        UpdateParticles(dt);
        UpdateActiveSplats(dt);
        AbsorbStep(dt);

        activeParticles = pool != null ? pool.ActiveCount : 0;
        if (textureDirty) { texture.Apply(); textureDirty = false; }
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


    // حجم منطقي حسب اللزوجة
    p.size =
        baseSize *
        Mathf.Lerp(
            0.8f,
            1.3f,
            Mathf.Clamp01(effViscosity)
        );



    p.approxMass =
        0.001f *
        effViscosity;



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
            0.5f - local.x / 10f,
            0.5f - local.z / 10f
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


    Re =
        FluidConstants.Reynolds(
            density,
            speed,
            D,
            p.viscosityEffect
        );


    We =
        FluidConstants.Weber(
            density,
            speed,
            D,
            surfaceTension
        );



    // حجم البقعة الفيزيائي

    float spread =
        1f
        + Mathf.Clamp01(speed * 0.05f)
        + humidity * 0.001f;



    float impactEnergy =
    speed * speed * 0.02f;

    int r =
    Mathf.Clamp(
    Mathf.RoundToInt(
    p.size * spread * 40f
    + impactEnergy
    ),
    2,
    18
    );

    AddPaintThickness(
    px,
    py,
    r
    );
    ApplyAdhesion(
    px,
    py,
    r
    );
    AbsorbPaint(
    px,
    py
    );
    float thickness =paintThickness[px, py];


    Color finalColor =Color.Lerp(
        p.color * 0.35f,
        p.color,
        Mathf.Clamp01(thickness)
    );

    float stickFactor =
    adhesionStrength *
    (1f - p.velocity.magnitude * 0.02f);


    stickFactor =
    Mathf.Clamp01(stickFactor);
    StampCircle(
    px,
    py,
    r,
    finalColor
    );
    // تناثر بسيط فقط

  float splashFactor =
    We *
    Mathf.Clamp01(speed / 10f) *
    Mathf.Clamp01(1f / p.viscosityEffect);


if(splashFactor > 250f &&
   speed > 6f)
{
    ScatterDroplets(
        px,
        py,
        r,
        p.color
    );
}



    // Splash جانبي بسيط

    if(We > crownWeberThreshold &&
       crownSplashEnabled)
    {
        StampCrown(
            px,
            py,
            r,
            p.color
        );
    }



    // انتشار الطلاء بعد الاصطدام

    if(activeSplats.Count < MaxActiveSplats)
    {
        activeSplats.Add(
            new ActiveSplat
            {
                px = px,
                py = py,
                radius = r,
                color = p.color,
                age = 0f,
                life = 0.6f,
                growRate = r * 0.2f
            }
        );
    }



    textureDirty = true;



    lastSplatPx =
        new Vector2Int(px, py);


    lastSplatValid = true;
}
 void UpdateActiveSplats(float dt)
    {
        for (int i = activeSplats.Count - 1; i >= 0; i--)
        {
            ActiveSplat s = activeSplats[i];
            s.age += dt;
            float grow = s.growRate * dt;
            if (grow >= 0.5f)
            {
                s.radius += grow;
                StampRing(s.px, s.py, Mathf.RoundToInt(s.radius), Color.Lerp(s.color, Color.white, 0.6f));
                textureDirty = true;
            }
            if (s.age >= s.life) activeSplats.RemoveAt(i);
            else activeSplats[i] = s;
        }
    }

    void AbsorbStep(float dt)
    {
        if (preset.surfaceAbsorption <= 0.001f) return;
        absorbTimer += dt;
        if (absorbTimer < 0.5f) return;
        float elapsed = absorbTimer; absorbTimer = 0f;

        float k = preset.surfaceAbsorption * (1f - (humidity / 100f) * 0.7f);
        float fade = 1f - Mathf.Exp(-k * elapsed);
        Color32[] cols = texture.GetPixels32();
        for (int i = 0; i < cols.Length; i++)
        {
            cols[i].r = (byte)(cols[i].r + (255 - cols[i].r) * fade);
            cols[i].g = (byte)(cols[i].g + (255 - cols[i].g) * fade);
            cols[i].b = (byte)(cols[i].b + (255 - cols[i].b) * fade);
        }
        texture.SetPixels32(cols);
        texture.Apply();
    }


    void StampCircle(int cx, int cy, int r, Color col)
    {
        int rough = Mathf.RoundToInt(preset.surfaceRoughness * 3f);
        for (int x = -r; x <= r; x++)
            for (int y = -r; y <= r; y++)
                if (x * x + y * y <= r * r) PutPixel(cx + x, cy + y, col, rough);
    }

    void StampRing(int cx, int cy, int r, Color col)
    {
        int inner = Mathf.Max(0, r - 2);
        int rough = Mathf.RoundToInt(preset.surfaceRoughness * 3f);
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
        int rough = Mathf.RoundToInt(preset.surfaceRoughness * 3f);
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

    void StampCrown(int cx, int cy, int r, Color col)
    {
        int spikes = Mathf.Clamp(Mathf.RoundToInt(We / 60f), 10, 28);
        for (int i = 0; i < spikes; i++)
        {
            float a = i * Mathf.PI * 2f / spikes;
            StampCircle(cx + Mathf.RoundToInt(Mathf.Cos(a) * r * 2f), cy + Mathf.RoundToInt(Mathf.Sin(a) * r * 2f), Mathf.Max(1, r / 3), col);
        }
    }

    void ScatterDroplets(int cx, int cy, int r, Color col)
    {
        int n = Random.Range(2, 6);
        for (int i = 0; i < n; i++)
        {
            float a = Random.value * Mathf.PI * 2f, dist = Random.Range(r, r * 3f);
            StampCircle(cx + Mathf.RoundToInt(Mathf.Cos(a) * dist), cy + Mathf.RoundToInt(Mathf.Sin(a) * dist), Random.Range(1, Mathf.Max(2, r / 2)), col);
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
        float intensity = Mathf.Clamp01(1f - preset.surfaceAbsorption * 3f); // absorbent -> fainter
        texture.SetPixel(px, py, Color.Lerp(texture.GetPixel(px, py), col, intensity));
    }

    void EnsureSphereMesh()
    {
        if (sphereMesh != null) return;
        GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphereMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
        if (Application.isPlaying) Destroy(tmp); else DestroyImmediate(tmp);
    }
    void AddPaintThickness(int cx, int cy, int radius)
{
    for(int x = -radius; x <= radius; x++)
    {
        for(int y = -radius; y <= radius; y++)
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



            if(distance <= radius)
            {

                float amount =
                    1f -
                    distance / radius;



                paintThickness[px,py] =
                    Mathf.Clamp(
                        paintThickness[px,py]
                        +
                        amount * thicknessAdd,

                        0,
                        maxPaintThickness
                    );
            }
        }
    }
}
    // public utilities 
    public void Clear()
    {
        if (texture == null) return;
        Color[] cols = new Color[texture.width * texture.height];
        for (int i = 0; i < cols.Length; i++) cols[i] = Color.white;
        texture.SetPixels(cols);
        texture.Apply();
        activeSplats.Clear();
        lastSplatValid = false;
    }

    public void SavePainting()
    {
        string filename = $"Painting_{System.DateTime.Now:yyyyMMdd_HHmmss}.png";
        System.IO.File.WriteAllBytes(Application.dataPath + "/" + filename, texture.EncodeToPNG());
        Debug.Log("Saved painting: " + filename);
    }
    void AbsorbPaint(int px, int py)
    {
    if(px < 0 ||
       px >= textureSize ||
       py < 0 ||
       py >= textureSize)
        return;


    float amount =
        paintThickness[px, py]
        *
        absorptionRate
        *
        Time.deltaTime;



    paintThickness[px,py] -= amount;


    absorbedPaint[px,py] =
        Mathf.Clamp01(
            absorbedPaint[px,py]
            +
            amount
        );
    }
    public void RefillPaint() { currentPaintAmount = maxPaintAmount; }
    void ApplyAdhesion(
    int cx,
    int cy,
    int radius)
{

    for(int x=-radius;x<=radius;x++)
    {
        for(int y=-radius;y<=radius;y++)
        {

            int px=cx+x;
            int py=cy+y;


            if(px<0 ||
               px>=textureSize ||
               py<0 ||
               py>=textureSize)
                continue;



            float dist =
                Mathf.Sqrt(
                x*x+y*y);



            if(dist<=radius)
            {

                float amount =
                1f-dist/radius;



                absorbedPaint[px,py]
                += amount *
                absorptionRate *
                0.01f;



                absorbedPaint[px,py]
                =
                Mathf.Clamp01(
                absorbedPaint[px,py]);

            }

        }
    }

}
    public float GetPaintAreaCoverage()
    {
        if (texture == null) return 0f;
        Color32[] cols = texture.GetPixels32();
        int colored = 0;
        for (int i = 0; i < cols.Length; i++)
            if (cols[i].r < 245 || cols[i].g < 245 || cols[i].b < 245) colored++;
        return (float)colored / cols.Length;
    }

    void OnGUI()
    {
        if (Event.current.type == EventType.KeyDown)
        {
            if (Event.current.keyCode == KeyCode.S) SavePainting();
            if (Event.current.keyCode == KeyCode.R) Clear();
        }
    }
}
