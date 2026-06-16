using System.Collections.Generic;
using UnityEngine;

public enum SurfaceType { Canvas, Wood, Metal, Paper }
public enum HoleShape { Round, Narrow, Wide, Multiple }

// ============================================================
//  PaintPhysics - paint particles (Phase 2) + surface interaction (Phase 3)
//  Uses PaintParticle + PaintParticlePool + FluidConstants + SurfacePreset.
//  Splat shape is derived from: bucket motion state + flow + surface (Task 3.11).
//  No RigidBody/Colliders; collision is a math plane intersection.
// ============================================================
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
    public float temperature = 25f;            // 0..50
    public float minViscosity = 0.3f;
    public float maxViscosity = 3f;
    [Range(0, 100)] public float humidity = 50f;

    [Header("Hole")]
    public HoleShape holeShape = HoleShape.Round;
    public float holeRadius = 0.06f;
    public float holeHeight = 0.1f;
    public float holeFactor = 1f;
    public Vector3 exitDirection = Vector3.down;

    [Header("Slosh / emission")]
    public float sloshStrength = 0.15f;
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

    [Header("Drawing / surface (Phase 3)")]
    public float gravity = 9.81f;
    public int textureSize = 1024;
    public float baseSplatSize = 3f;           // Task 3.12
    public bool continuousJetMode = true;      // Task 3.8
    public float jetMaxGapUV = 0.05f;          // max gap to connect a continuous jet
    public bool crownSplashEnabled = false;    // Task 3.5 (expensive - optional)
    public float crownWeberThreshold = 400f;
    public float density = FluidConstants.WaterDensity;
    public float surfaceTension = FluidConstants.DefaultSurfaceTension;

    [Header("Display (debug)")]
    public float currentEmissionRate;
    public float paintLevel;
    public bool isHoleSubmerged;
    public int activeParticles;
    public float Re, We, Ca, Oh;               // dimensionless numbers (Task 3.1)

    private SurfacePreset preset;

    // internal
    private Texture2D texture;
    private float emitAccumulator;
    private static Mesh sphereMesh;
    private Material particleMatTemplate;
    private PaintParticlePool pool;
    private bool textureDirty;

    // continuous jet connection (Task 3.8)
    private Vector2Int lastSplatPx;
    private bool lastSplatValid;

    // active-splat list for time-based spreading/fading (Task 3.15) - separate from pool
    private class ActiveSplat { public int px, py; public float radius; public float growRate; public Color color; public float age; public float life; }
    private readonly List<ActiveSplat> activeSplats = new List<ActiveSplat>();
    private const int MaxActiveSplats = 60;

    // periodic absorption timer (Task 3.6)
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

        preset = SurfacePreset.From(surface); // allow live surface switching from the UI

        EmitStep(dt);
        UpdateParticles(dt);
        UpdateActiveSplats(dt);   // post-landing spreading (Task 3.15)
        AbsorbStep(dt);           // time-based fade for absorbent surfaces (Task 3.6)

        activeParticles = pool != null ? pool.ActiveCount : 0;
        if (textureDirty) { texture.Apply(); textureDirty = false; }
    }

    // ---------- emission (Phase 2) ----------
    void EmitStep(float dt)
    {
        if (currentPaintAmount <= 0f) { currentEmissionRate = 0f; return; }

        float omega = bucketMotion.velocity.magnitude / Mathf.Max(0.01f, bucketMotion.L);
        float tiltRad = Mathf.Sqrt(bucketMotion.angleX * bucketMotion.angleX
                                 + bucketMotion.angleZ * bucketMotion.angleZ) * Mathf.Deg2Rad;

        float temperatureFactor = Mathf.Clamp01(temperature / 50f);
        float adjustedViscosity = Mathf.Lerp(maxViscosity, minViscosity, temperatureFactor);
        float effViscosity = Mathf.Max(0.05f, adjustedViscosity * viscosity);

        float tiltFactor = Mathf.Abs(Mathf.Sin(tiltRad));
        float baseLevel = currentPaintAmount / Mathf.Max(0.0001f, maxPaintAmount);
        paintLevel = baseLevel + tiltFactor * 0.25f / effViscosity;

        float sloshOffset = omega * sloshStrength / effViscosity;
        isHoleSubmerged = (paintLevel + sloshOffset) >= holeHeight;
        if (!isHoleSubmerged) { currentEmissionRate = 0f; return; }

        float motionFactor = 1f + omega;
        float viscosityFactor = 1f / effViscosity;
        currentEmissionRate = baseEmission * baseLevel * (0.2f + tiltFactor)
                              * motionFactor * viscosityFactor * holeFactor;

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
        spreadAmount *= Mathf.Lerp(1f, 0.25f, flowNorm); // higher flow -> tighter, more continuous stream

        Vector3 spawnOffset, randomSpread;
        ComputeHolePattern(spreadAmount, out spawnOffset, out randomSpread);

        // Task 3.8: v_total = v_flow + v_bucket
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

    void ComputeHolePattern(float spread, out Vector3 offset, out Vector3 randomSpread)
    {
        switch (holeShape)
        {
            case HoleShape.Narrow:
                offset = new Vector3(Random.Range(-holeRadius, holeRadius) * 0.3f, 0f, 0f);
                randomSpread = new Vector3(Random.Range(-spread, spread) * 0.3f, 0f, Random.Range(-spread, spread));
                break;
            case HoleShape.Wide:
                offset = new Vector3(Random.Range(-holeRadius, holeRadius) * 2f, 0f, 0f);
                randomSpread = new Vector3(Random.Range(-spread, spread) * 2f, 0f, Random.Range(-spread, spread) * 0.5f);
                break;
            case HoleShape.Multiple:
                int k = Random.Range(0, 3);
                offset = new Vector3((k - 1) * holeRadius * 2f, 0f, 0f);
                randomSpread = Random.insideUnitSphere * spread * 0.5f;
                break;
            default:
                Vector2 disc = Random.insideUnitCircle * holeRadius;
                offset = new Vector3(disc.x, 0f, disc.y);
                randomSpread = Random.insideUnitSphere * spread;
                break;
        }
    }

    // ---------- particle motion + collision ----------
    void UpdateParticles(float dt)
    {
        if (pool == null || canvasRenderer == null) return;
        if (enableParticleInteraction) ApplyInteraction(dt);

        Transform c = canvasRenderer.transform;
        Vector3 planePoint = c.position;
        Vector3 planeNormal = c.up;

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

            float sidePrev = Vector3.Dot(prev - planePoint, planeNormal);
            float sideNow = Vector3.Dot(p.position - planePoint, planeNormal);

            if (sidePrev > 0f && sideNow <= 0f)
            {
                p.state = ParticleState.Collided;
                float t = sidePrev / (sidePrev - sideNow);
                Vector3 hit = Vector3.Lerp(prev, p.position, t);
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

    // cheap cohesion/separation between particles (Task 2.14) - only a few neighbors
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

    // ---------- splat drawing by impact case (Phase 3) ----------
    void PaintSplat(Transform c, Vector3 hitPoint, PaintParticle p, Vector3 n)
    {
        Vector3 local = c.InverseTransformPoint(hitPoint);
        Vector2 uv = new Vector2(0.5f - local.x / 10f, 0.5f - local.z / 10f);
        if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f) { lastSplatValid = false; return; }

        int px = (int)(uv.x * texture.width);
        int py = (int)(uv.y * texture.height);

        // impact decomposition: normal and tangential components
        Vector3 v = p.velocity;
        float speed = v.magnitude;
        float vN = Mathf.Abs(Vector3.Dot(v, -n));
        Vector3 vTvec = v - Vector3.Dot(v, n) * n;
        float vT = vTvec.magnitude;
        float oblique = (speed > 1e-4f) ? vT / speed : 0f; // 0=vertical, 1=sliding

        // motion direction in UV space (with the axis flip)
        Vector3 localDir = c.InverseTransformDirection(vTvec);
        Vector2 uvDir = new Vector2(-localDir.x, -localDir.z);
        if (uvDir.sqrMagnitude > 1e-6f) uvDir.Normalize(); else uvDir = Vector2.right;

        // dimensionless numbers (Task 3.1)
        float D = Mathf.Max(0.001f, p.size);
        Re = FluidConstants.Reynolds(density, speed, D, p.viscosityEffect);
        We = FluidConstants.Weber(density, speed, D, surfaceTension);
        Ca = FluidConstants.Capillary(p.viscosityEffect, speed, surfaceTension);
        Oh = FluidConstants.Ohnesorge(p.viscosityEffect, density, surfaceTension, D);

        // splat size (Task 3.12): baseSplatSize + speedEffect + viscosityEffect + surfaceEffect
        float pixelsPerUnit = texture.width / Mathf.Max(0.001f, 10f * c.lossyScale.x);
        float speedEffect = speed * 0.6f;
        float viscosityEffect = p.viscosityEffect * 1.2f;
        float surfaceEffect = (preset.surfaceSpread - 1f) * 4f;
        float humidityEffect = 1f + (humidity / 100f) * 0.5f;             // higher humidity -> wider (Task 3.10)
        float tempSpread = 1f + Mathf.Clamp01(temperature / 50f) * 0.4f;  // higher temp -> more spread (Task 3.9)
        float baseR = baseSplatSize + speedEffect + viscosityEffect + surfaceEffect;
        int r = Mathf.Clamp(Mathf.RoundToInt(baseR * preset.surfaceSpread * humidityEffect * tempSpread), 1, 30);

        // continuous jet connection (Task 3.8) - produces spiral traces
        if (continuousJetMode && lastSplatValid)
        {
            float gapUV = Vector2.Distance(uv, new Vector2(lastSplatPx.x / (float)texture.width,
                                                           lastSplatPx.y / (float)texture.height));
            if (gapUV < jetMaxGapUV)
                StampLine(lastSplatPx.x, lastSplatPx.y, px, py, Mathf.Max(1, r / 2), p.color);
        }

        // choose impact case (Tasks 3.2-3.5 / 3.11)
        if (oblique < 0.3f && speed < 4f)
        {
            // Case 1: slow vertical -> symmetric circle
            StampCircle(px, py, r, p.color);
        }
        else if (oblique < 0.6f)
        {
            // Case 2: moderate oblique -> ellipse elongated along motion
            float rx = r * (1f + oblique * 1.5f);
            float ang = Mathf.Atan2(uvDir.y, uvDir.x);
            StampEllipse(px, py, rx, r, ang, p.color);
        }
        else
        {
            // Case 3: fast oblique/sliding -> streak/tail
            float len = r * (2f + oblique * 4f);
            StampStreak(px, py, uvDir, len, r, p.color);
            if (p.viscosityEffect < 0.6f) ScatterDroplets(px, py, r, p.color); // low viscosity -> secondary droplets
            // Case 4: very high speed -> crown (optional)
            if (crownSplashEnabled && We > crownWeberThreshold) StampCrown(px, py, r, p.color);
        }

        // rough surface: extra splash probability (Task 3.7)
        if (Random.value < preset.splashProbability * (0.5f + preset.surfaceRoughness))
            ScatterDroplets(px, py, r, p.color);

        // register an active splat for time-based spreading on spreading surfaces (Task 3.15)
        if (preset.surfaceSpread > 1.05f && activeSplats.Count < MaxActiveSplats)
        {
            activeSplats.Add(new ActiveSplat
            {
                px = px, py = py, radius = r, color = p.color, age = 0f,
                life = 0.5f, growRate = (preset.surfaceSpread - 1f) * r * (1f + humidity / 100f)
            });
        }

        textureDirty = true;
        lastSplatPx = new Vector2Int(px, py);
        lastSplatValid = true;
    }

    // ---------- post-landing spreading (Task 3.15) ----------
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
                Color faded = Color.Lerp(s.color, Color.white, 0.6f);
                StampRing(s.px, s.py, Mathf.RoundToInt(s.radius), faded);
                textureDirty = true;
            }
            if (s.age >= s.life) activeSplats.RemoveAt(i);
            else activeSplats[i] = s;
        }
    }

    // ---------- time-based fade for absorbent surfaces C(t)=C0*e^-kt (Task 3.6) ----------
    void AbsorbStep(float dt)
    {
        if (preset.surfaceAbsorption <= 0.001f) return;
        absorbTimer += dt;
        if (absorbTimer < 0.5f) return;
        float elapsed = absorbTimer;
        absorbTimer = 0f;

        // higher humidity slows drying -> slower fade (Task 3.10)
        float humidityFactor = 1f - (humidity / 100f) * 0.7f;
        float k = preset.surfaceAbsorption * humidityFactor;
        float retain = Mathf.Exp(-k * elapsed);   // C(t)/C0
        float fade = 1f - retain;                 // amount of fade toward white

        Color32[] cols = texture.GetPixels32();
        Color32 white = new Color32(255, 255, 255, 255);
        for (int i = 0; i < cols.Length; i++)
        {
            cols[i].r = (byte)(cols[i].r + (white.r - cols[i].r) * fade);
            cols[i].g = (byte)(cols[i].g + (white.g - cols[i].g) * fade);
            cols[i].b = (byte)(cols[i].b + (white.b - cols[i].b) * fade);
        }
        texture.SetPixels32(cols);
        texture.Apply();
    }

    // ---------- texture stamp helpers ----------
    void StampCircle(int cx, int cy, int r, Color col)
    {
        int rough = Mathf.RoundToInt(preset.surfaceRoughness * 3f);
        for (int x = -r; x <= r; x++)
            for (int y = -r; y <= r; y++)
            {
                if (x * x + y * y > r * r) continue;
                PutPixel(cx + x, cy + y, col, rough);
            }
    }

    // ring (for spreading) - only pixels near the edge
    void StampRing(int cx, int cy, int r, Color col)
    {
        int inner = Mathf.Max(0, r - 2);
        int rough = Mathf.RoundToInt(preset.surfaceRoughness * 3f);
        for (int x = -r; x <= r; x++)
            for (int y = -r; y <= r; y++)
            {
                int d2 = x * x + y * y;
                if (d2 > r * r || d2 < inner * inner) continue;
                PutPixel(cx + x, cy + y, col, rough);
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
                float lx = x * cos + y * sin;
                float ly = -x * sin + y * cos;
                if ((lx * lx) / (rx * rx) + (ly * ly) / (ry * ry) <= 1f)
                    PutPixel(cx + x, cy + y, col, rough);
            }
    }

    void StampStreak(int cx, int cy, Vector2 dir, float length, int width, Color col)
    {
        int steps = Mathf.Max(2, Mathf.CeilToInt(length));
        for (int i = -steps / 3; i <= steps; i++) // tail behind + head ahead
        {
            float t = (float)i / steps;
            int x = cx + Mathf.RoundToInt(dir.x * i);
            int y = cy + Mathf.RoundToInt(dir.y * i);
            int w = Mathf.Max(1, Mathf.RoundToInt(width * (1f - Mathf.Abs(t))));
            StampCircle(x, y, w, col);
        }
    }

    void StampCrown(int cx, int cy, int r, Color col)
    {
        int spikes = Mathf.Clamp(Mathf.RoundToInt(We / 60f), 10, 28);
        for (int i = 0; i < spikes; i++)
        {
            float a = i * Mathf.PI * 2f / spikes;
            int x = cx + Mathf.RoundToInt(Mathf.Cos(a) * r * 2f);
            int y = cy + Mathf.RoundToInt(Mathf.Sin(a) * r * 2f);
            StampCircle(x, y, Mathf.Max(1, r / 3), col);
        }
    }

    void ScatterDroplets(int cx, int cy, int r, Color col)
    {
        int n = Random.Range(2, 6);
        for (int i = 0; i < n; i++)
        {
            float a = Random.value * Mathf.PI * 2f;
            float dist = Random.Range(r, r * 3f);
            int x = cx + Mathf.RoundToInt(Mathf.Cos(a) * dist);
            int y = cy + Mathf.RoundToInt(Mathf.Sin(a) * dist);
            StampCircle(x, y, Random.Range(1, Mathf.Max(2, r / 2)), col);
        }
    }

    void StampLine(int x0, int y0, int x1, int y1, int r, Color col)
    {
        int steps = Mathf.Max(1, Mathf.RoundToInt(Vector2.Distance(new Vector2(x0, y0), new Vector2(x1, y1))));
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            int x = Mathf.RoundToInt(Mathf.Lerp(x0, x1, t));
            int y = Mathf.RoundToInt(Mathf.Lerp(y0, y1, t));
            StampCircle(x, y, r, col);
        }
    }

    void PutPixel(int px, int py, Color col, int rough)
    {
        if (rough > 0)
        {
            px += Random.Range(-rough, rough + 1);
            py += Random.Range(-rough, rough + 1);
        }
        if (px < 0 || px >= texture.width || py < 0 || py >= texture.height) return;
        Color old = texture.GetPixel(px, py);
        // absorbent surface -> lower color intensity
        float intensity = Mathf.Clamp01(1f - preset.surfaceAbsorption * 3f);
        texture.SetPixel(px, py, Color.Lerp(old, col, intensity));
    }

    void EnsureSphereMesh()
    {
        if (sphereMesh != null) return;
        GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphereMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
        if (Application.isPlaying) Destroy(tmp); else DestroyImmediate(tmp);
    }

    // ---------- public utilities ----------
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

    // colored-pixel fraction (for the Phase 4 report)
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
