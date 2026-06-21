using System.Collections.Generic;
using UnityEngine;

public enum SurfaceType { Canvas, Wood, Metal, Paper }
public enum HoleShape { Round, Narrow, Wide, Multiple }

public class PaintPhysics : MonoBehaviour
{
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
    public float viscosity = 1f;
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
    public float baseSpeed = 1.0f;
    public float baseSize = 0.05f;
    public float baseSpread = 0.12f;
    public float particleLifetime = 5f;
    public float dampingFactor = 0.99f;

    [Header("Particle interaction")]
    public bool enableParticleInteraction = true;
    public float interactionRadius = 0.15f;
    public float cohesionStrength = 0.5f;
    public float separationStrength = 1.0f;

    [Header("Drawing / surface")]
    public float gravity = 9.81f;
    public int textureSize = 1024;
    public float baseSplatSize = 3f;
    public bool continuousJetMode = true;
    public float jetMaxGapUV = 0.15f;
    public bool crownSplashEnabled = false;
    public float crownWeberThreshold = 400f;
    public float density = FluidConstants.WaterDensity;
    public float surfaceTension = FluidConstants.DefaultSurfaceTension;

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
        texture = new Texture2D(textureSize, textureSize);
        texture.wrapMode = TextureWrapMode.Clamp;
        if (canvasRenderer != null) canvasRenderer.material.mainTexture = texture;
        preset = SurfacePreset.From(surface);
        Clear();
        particleMatTemplate = new Material(Shader.Find("Unlit/Color"));
        EnsureSphereMesh();
        pool = new PaintParticlePool(transform, sphereMesh, particleMatTemplate, maxParticles);
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

        float particleSpeed = baseSpeed / effViscosity;
        float spreadAmount = baseSpread / effViscosity;
        float flowNorm = Mathf.Clamp01(currentEmissionRate / 80f);
        spreadAmount *= Mathf.Lerp(1f, 0.25f, flowNorm); // higher flow -> tighter stream

        Vector3 spawnOffset, randomSpread;
        ComputeHolePattern(spreadAmount, out spawnOffset, out randomSpread);

        // v_total = v_bucket + exit*speed + spread
        Vector3 vel = bucketMotion.velocity + exitDirection.normalized * particleSpeed + randomSpread;

        p.position = paintPoint.position + spawnOffset;
        p.velocity = vel;
        p.color = paintColor;
        p.viscosityEffect = effViscosity;
        p.size = baseSize * effViscosity;
        p.approxMass = 0.001f * effViscosity;
        p.lifetime = particleLifetime;
        p.age = 0f;

        p.tr.position = p.position;
        p.tr.localScale = Vector3.one * p.size;
        if (p.rend != null) p.rend.material.color = p.color;
        p.go.SetActive(true);
        p.state = ParticleState.Emitted;

        currentPaintAmount = Mathf.Max(0f, currentPaintAmount - 0.01f);
    }

    // Hole shape sets the spawn offset and spread pattern
    void ComputeHolePattern(float spread, out Vector3 offset, out Vector3 randomSpread)
    {
        switch (holeShape)
        {
            case HoleShape.Narrow: // thin line along Z
                offset = new Vector3(Random.Range(-holeRadius, holeRadius) * 0.15f, 0f, Random.Range(-holeRadius, holeRadius) * 4f);
                randomSpread = new Vector3(Random.Range(-spread, spread) * 0.15f, 0f, Random.Range(-spread, spread));
                break;
            case HoleShape.Wide: // wide band along X
                offset = new Vector3(Random.Range(-holeRadius, holeRadius) * 6f, 0f, Random.Range(-holeRadius, holeRadius) * 0.5f);
                randomSpread = new Vector3(Random.Range(-spread, spread) * 3f, 0f, Random.Range(-spread, spread) * 0.4f);
                break;
            case HoleShape.Multiple: // three separated streams
                int k = Random.Range(0, 3);
                offset = new Vector3((k - 1) * holeRadius * 6f, 0f, 0f);
                randomSpread = Random.insideUnitSphere * spread * 0.5f;
                break;
            default: // Round
                Vector2 disc = Random.insideUnitCircle * holeRadius;
                offset = new Vector3(disc.x, 0f, disc.y);
                randomSpread = Random.insideUnitSphere * spread;
                break;
        }
    }

    void UpdateParticles(float dt)
    {
        if (pool == null || canvasRenderer == null) return;
        if (enableParticleInteraction) ApplyInteraction(dt);

        Transform c = canvasRenderer.transform;
        Vector3 planePoint = c.position, planeNormal = c.up;

        var list = pool.All;
        for (int i = 0; i < list.Count; i++)
        {
            PaintParticle p = list[i];
            if (p.state == ParticleState.Removed) continue;
            if (p.state == ParticleState.Emitted) p.state = ParticleState.Falling;

            Vector3 prev = p.position;
            p.velocity += Vector3.down * gravity * dt;
            p.velocity *= dampingFactor;
            p.position += p.velocity * dt;
            p.tr.position = p.position;
            p.age += dt;

            // collision = sign of distance to plane flips
            float sidePrev = Vector3.Dot(prev - planePoint, planeNormal);
            float sideNow = Vector3.Dot(p.position - planePoint, planeNormal);
            if (sidePrev > 0f && sideNow <= 0f)
            {
                p.state = ParticleState.Collided;
                Vector3 hit = Vector3.Lerp(prev, p.position, sidePrev / (sidePrev - sideNow));
                PaintSplat(c, hit, p, planeNormal);
                p.state = ParticleState.Painted;
                pool.Return(p);
            }
            else if (p.age > p.lifetime || sideNow < -2f)
            {
                pool.Return(p);
            }
        }
    }

    // cheap cohesion/separation against a few neighbors
    void ApplyInteraction(float dt)
    {
        var list = pool.All;
        float r2 = interactionRadius * interactionRadius;
        for (int i = 0; i < list.Count; i++)
        {
            PaintParticle a = list[i];
            if (a.state != ParticleState.Falling && a.state != ParticleState.Emitted) continue;
            int checkedCount = 0;
            for (int j = i + 1; j < list.Count && checkedCount < 3; j++)
            {
                PaintParticle b = list[j];
                if (b.state != ParticleState.Falling && b.state != ParticleState.Emitted) continue;
                checkedCount++;
                Vector3 d = b.position - a.position;
                float dist2 = d.sqrMagnitude;
                if (dist2 > r2 || dist2 < 1e-6f) continue;
                float dist = Mathf.Sqrt(dist2);
                Vector3 dir = d / dist;
                float sep = (interactionRadius - dist) / interactionRadius;
                a.velocity -= dir * sep * separationStrength * dt;
                b.velocity += dir * sep * separationStrength * dt;
                float coh = cohesionStrength * a.viscosityEffect * dt;
                a.velocity += dir * coh;
                b.velocity -= dir * coh;
            }
        }
    }

   
    void PaintSplat(Transform c, Vector3 hitPoint, PaintParticle p, Vector3 n)
    {
        Vector3 local = c.InverseTransformPoint(hitPoint);
        Vector2 uv = new Vector2(0.5f - local.x / 10f, 0.5f - local.z / 10f);
        if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f) { lastSplatValid = false; return; }

        int px = (int)(uv.x * texture.width);
        int py = (int)(uv.y * texture.height);

        Vector3 v = p.velocity;
        float speed = v.magnitude;
        Vector3 vTvec = v - Vector3.Dot(v, n) * n;
        float oblique = (speed > 1e-4f) ? vTvec.magnitude / speed : 0f; // 0=vertical, 1=sliding

        Vector3 localDir = c.InverseTransformDirection(vTvec);
        Vector2 uvDir = new Vector2(-localDir.x, -localDir.z);
        if (uvDir.sqrMagnitude > 1e-6f) uvDir.Normalize(); else uvDir = Vector2.right;

        float D = Mathf.Max(0.001f, p.size);
        Re = FluidConstants.Reynolds(density, speed, D, p.viscosityEffect);
        We = FluidConstants.Weber(density, speed, D, surfaceTension);
        Ca = FluidConstants.Capillary(p.viscosityEffect, speed, surfaceTension);
        Oh = FluidConstants.Ohnesorge(p.viscosityEffect, density, surfaceTension, D);

        // splat size
        float pixelsPerUnit = texture.width / Mathf.Max(0.001f, 10f * c.lossyScale.x);
        float spreadFromEnv = 1f + (humidity / 100f) * 0.15f + Mathf.Clamp01(temperature / 50f) * 0.15f;
        float radiusWorld = p.size * (0.8f + p.viscosityEffect * 0.2f) * preset.surfaceSpread + speed * 0.005f;
        int r = Mathf.Clamp(Mathf.RoundToInt(radiusWorld * spreadFromEnv * pixelsPerUnit), 1, 10);

        // continuous jet 
        if (continuousJetMode && lastSplatValid)
        {
            float gapUV = Vector2.Distance(uv, new Vector2(lastSplatPx.x / (float)texture.width, lastSplatPx.y / (float)texture.height));
            if (gapUV < jetMaxGapUV) StampLine(lastSplatPx.x, lastSplatPx.y, px, py, Mathf.Max(1, r / 2), p.color);
        }

        if (oblique < 0.3f && speed < 4f) StampCircle(px, py, r, p.color);              // vertical slow
        else if (oblique < 0.6f) StampEllipse(px, py, r * (1f + oblique * 1.5f), r, Mathf.Atan2(uvDir.y, uvDir.x), p.color); // oblique
        else
        {
            StampStreak(px, py, uvDir, r * (2f + oblique * 4f), r, p.color);            // fast sliding
            if (p.viscosityEffect < 0.6f) ScatterDroplets(px, py, r, p.color);
            if (crownSplashEnabled && We > crownWeberThreshold) StampCrown(px, py, r, p.color);
        }

        if (Random.value < preset.splashProbability * (0.5f + preset.surfaceRoughness))
            ScatterDroplets(px, py, r, p.color);

        if (preset.surfaceSpread > 1.05f && activeSplats.Count < MaxActiveSplats)
            activeSplats.Add(new ActiveSplat { px = px, py = py, radius = r, color = p.color, age = 0f, life = 0.5f, growRate = (preset.surfaceSpread - 1f) * r * (1f + humidity / 100f) });

        textureDirty = true;
        lastSplatPx = new Vector2Int(px, py);
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

    public void RefillPaint() { currentPaintAmount = maxPaintAmount; }

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
