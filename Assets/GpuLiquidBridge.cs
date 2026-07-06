using UnityEngine;

// ============================================================================
//  GpuLiquidBridge — the ONE seam between the existing bucket/rope/canvas
//  system and the GPU particle pipeline.
// ----------------------------------------------------------------------------
//  NOTHING about the existing simulation is replaced:
//    * PendulumMotion keeps integrating the spherical pendulum and owns the
//      bucket transform and the paint mass;
//    * BucketEmission keeps computing the PHYSICS of pouring every frame
//      (geometric fill level, slosh submergence gate, Torricelli efflux
//      speed, hole-shape discharge area) — in GPU mode it just skips the
//      per-particle spawn/release loops (see the gpuMode guards there);
//    * this bridge reads those public readouts (currentExitSpeed,
//      currentMassFlow, paintLevel, ...) plus the bucket transform, converts
//      them into ~30 GPU uniforms, and drives GpuLiquidSimulation.Step().
//
//  HOW THE BUCKET MOVES THE LIQUID (report note): the bucket's
//  localToWorld / worldToLocal matrices are uploaded every frame. The compute
//  shader clamps contained particles inside the unit cube in BUCKET-LOCAL
//  space, so wall motion (swing, tilt, spin) becomes particle displacement,
//  and PBF's v = (x* - x)/dt turns that displacement into momentum — the
//  liquid sloshes with the swing without a single CPU particle update.
//
//  MASS BOOKKEEPING stays exact: the GPU counts actually-emitted drops in a
//  persistent counter; the bridge reads the delta back (async, low frequency)
//  and calls PendulumMotion.ConsumePaint(delta * perDropMass) — the bucket
//  gets lighter by exactly the paint that left the hole, same contract as
//  the CPU path.
//
//  Self-bootstraps at scene load (like SimulationManager), so NO scene-file
//  edits are needed and the existing scene keeps working untouched.
// ============================================================================
public sealed class GpuLiquidBridge : MonoBehaviour
{
    public static GpuLiquidBridge Instance { get; private set; }

    [Header("GPU mode")]
    public bool useGpuSimulation = true;    // safe: default preset is 10k
    public bool renderParticles = true;
    public bool gpuPainting = true;
    public bool performanceMode = false;    // trims iterations/neighbours for weak GPUs
    public bool showDebug = false;

    [Header("Particle preset (10k default — switch to 200k manually)")]
    public int targetParticles = 10000;

    [Header("Readouts")]
    public bool gpuSupported;
    public bool gpuActive;

    public GpuLiquidSimulation Sim => sim;

    PaintPhysics paint;
    PendulumMotion pendulum;
    GpuLiquidSimulation sim;
    GpuLiquidRenderer rend;

    // GPU drop accounting (the CPU UpdateDropAccounting caps at 10k drops;
    // GPU mode re-derives the same mass split for up to 200k drops).
    float perDropMass = 1e-4f;
    float perDropDiameter = 0.005f;
    float effVisualScale = 8f;

    float emitAccum;          // fractional drops owed to the Torricelli discharge
    int   insideEst;          // estimated contained count (corrected by readbacks)
    int   lastReadbackVersion;
    Texture cpuCanvasTex;     // CPU Texture2D to restore when GPU mode turns off

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Object.FindFirstObjectByType<GpuLiquidBridge>() != null) return;
        if (Object.FindFirstObjectByType<PaintPhysics>() == null) return;  // not our scene
        var go = new GameObject("GpuLiquid");
        go.AddComponent<GpuLiquidSimulation>();
        go.AddComponent<GpuLiquidRenderer>();
        go.AddComponent<GpuLiquidBridge>();
    }

    void Awake()
    {
        Instance = this;
        sim  = GetComponent<GpuLiquidSimulation>();
        rend = GetComponent<GpuLiquidRenderer>();
        if (sim == null)  sim  = gameObject.AddComponent<GpuLiquidSimulation>();
        if (rend == null) rend = gameObject.AddComponent<GpuLiquidRenderer>();
    }

    void OnEnable()
    {
        PaintPhysics.OnCanvasCleared    += HandleCanvasCleared;
        PaintPhysics.OnParticlesPurged  += HandleParticlesPurged;
    }
    void OnDisable()
    {
        PaintPhysics.OnCanvasCleared    -= HandleCanvasCleared;
        PaintPhysics.OnParticlesPurged  -= HandleParticlesPurged;
        if (gpuActive) SetGpuActive(false);
    }

    void Start()
    {
        paint    = Object.FindFirstObjectByType<PaintPhysics>();
        pendulum = paint != null ? paint.bucketMotion : Object.FindFirstObjectByType<PendulumMotion>();

        if (paint != null) sim.canvasTextureSize = Mathf.Max(256, paint.textureSize);
        sim.Initialize();
        gpuSupported = sim.LoadOk;
        if (!gpuSupported)
        {
            useGpuSimulation = false;
            Debug.LogWarning("[GpuLiquidBridge] GPU simulation unavailable on this device — " +
                             "staying on the CPU particle path.");
            return;
        }
        SetPreset(targetParticles);
    }

    // ------------------------------------------------------------------------
    //  Presets (brief §G): 10k / 50k / 100k / 200k. Solver quality steps down
    //  as counts step up (brief: "200k visual/optimized with reduced neighbour
    //  count") — the ARCHITECTURE is identical at every preset, only the
    //  iteration/neighbour budgets change.
    // ------------------------------------------------------------------------
    public void SetPreset(int count)
    {
        targetParticles = Mathf.Clamp(count, 1000, 200000);
        if (!gpuSupported) return;

        if      (targetParticles <= 10000)  { sim.solverIterations = 4; sim.maxNeighbors = 48; }
        else if (targetParticles <= 50000)  { sim.solverIterations = 3; sim.maxNeighbors = 40; }
        else if (targetParticles <= 100000) { sim.solverIterations = 3; sim.maxNeighbors = 32; }
        else                                { sim.solverIterations = 2; sim.maxNeighbors = 28; }
        ApplyPerformanceMode();

        sim.Configure(targetParticles);
        insideEst = 0; emitAccum = 0f;
        lastReadbackVersion = sim.ReadbackVersion;
        if (gpuActive) ClearGpuCanvas();
    }

    public void ApplyPerformanceMode()
    {
        if (!performanceMode || sim == null) return;
        sim.solverIterations = Mathf.Min(sim.solverIterations, 2);
        sim.maxNeighbors     = Mathf.Min(sim.maxNeighbors, 24);
    }

    public string CurrentPresetName =>
        targetParticles >= 200000 ? "200k (target)" :
        targetParticles >= 100000 ? "100k (high)"   :
        targetParticles >= 50000  ? "50k (medium)"  : "10k (safe)";

    // ------------------------------------------------------------------------
    //  Main loop. LateUpdate so PaintPhysics.Update has already refreshed this
    //  frame's Torricelli numbers (fill level, exit speed, mass flow).
    // ------------------------------------------------------------------------
    void LateUpdate()
    {
        if (paint == null || pendulum == null) return;

        bool want = useGpuSimulation && gpuSupported;
        if (want != gpuActive) SetGpuActive(want);
        paint.gpuMode = gpuActive;              // keeps the CPU guards in sync
        if (!gpuActive) return;

        float dt = Mathf.Min(Time.deltaTime, 1f / 30f);   // same clamp as the CPU solver
        if (dt <= 0f) return;

        UpdateDropAccountingGpu();

        // Keep the displayed canvas in sync with the painting-path toggle.
        var cr = paint.canvasRenderer;
        if (cr != null)
        {
            Texture wantTex = gpuPainting ? (Texture)sim.CanvasRT : cpuCanvasTex;
            if (wantTex != null && cr.material.mainTexture != wantTex)
                cr.material.mainTexture = wantTex;
        }

        var fi = BuildFrameInput(dt);
        sim.Step(dt, fi);

        // Fresh stats arrived? Re-anchor the contained-count estimate and pay
        // the emitted paint out of the bucket (exact, just ~0.25 s delayed).
        if (sim.ReadbackVersion != lastReadbackVersion)
        {
            lastReadbackVersion = sim.ReadbackVersion;
            insideEst = sim.insideCount;
        }
        int emitted = sim.TakeEmittedDelta();
        if (emitted > 0) pendulum.ConsumePaint(emitted * perDropMass);

        if (renderParticles) rend.Draw(sim);
    }

    // Same accounting rules as BucketEmission.UpdateDropAccounting, extended
    // past its 10k cap: the paint mass splits EXACTLY across N drops, and the
    // rendered size shrinks until N spheres fit ~55% of the container volume.
    void UpdateDropAccountingGpu()
    {
        perDropMass = pendulum.initialPaintMass / Mathf.Max(1, targetParticles);
        perDropDiameter = Mathf.Pow(
            6f * (perDropMass / Mathf.Max(1f, paint.density)) / Mathf.PI, 1f / 3f);

        Vector3 s = pendulum.transform.lossyScale;
        float boxVol = Mathf.Max(1e-6f, Mathf.Abs(s.x * s.y * s.z));
        float maxSphereVol = 0.55f * boxVol / Mathf.Max(1, targetParticles);
        float maxVisualD = Mathf.Pow(6f * maxSphereVol / Mathf.PI, 1f / 3f);
        effVisualScale = Mathf.Clamp(
            Mathf.Min(paint.dropletVisualScale, maxVisualD / Mathf.Max(1e-6f, perDropDiameter)),
            1f, 40f);
    }

    GpuLiquidSimulation.FrameInput BuildFrameInput(float dt)
    {
        Transform bt = pendulum.transform;
        Vector3 scale = bt.lossyScale;

        var fi = new GpuLiquidSimulation.FrameInput();

        // ---- bucket coupling (matrices = how the swing reaches the liquid) ----
        fi.worldToBucket  = bt.worldToLocalMatrix;
        fi.bucketToWorld  = bt.localToWorldMatrix;
        fi.bucketRot      = Matrix4x4.Rotate(bt.rotation);
        fi.bucketScale    = scale;
        fi.bucketVelocity = pendulum.velocity;
        fi.bucketUp       = bt.up;
        fi.bucketPos      = bt.position;
        fi.spinRate       = pendulum.spinRate;
        fi.bucketVolume   = Mathf.Abs(scale.x * scale.y * scale.z);

        // Wall clamp limits: half-cube minus the rendered particle radius per
        // axis (the box is non-uniformly scaled) — mirrors ContainInBox.
        float visualR = perDropDiameter * effVisualScale * 0.5f;
        fi.wallLim = new Vector3(
            Mathf.Max(0.02f, 0.5f - visualR / Mathf.Max(1e-3f, Mathf.Abs(scale.x))),
            Mathf.Max(0.02f, 0.5f - visualR / Mathf.Max(1e-3f, Mathf.Abs(scale.y))),
            Mathf.Max(0.02f, 0.5f - visualR / Mathf.Max(1e-3f, Mathf.Abs(scale.z))));

        // ---- hole & emission: driven by the SAME Torricelli numbers the CPU
        // path uses (BucketEmission.EmitStep keeps computing them in GPU mode).
        fi.holeLocal    = new Vector3(0f, -0.5f + paint.holeHeight * 0.9f, 0f);
        fi.holeCaptureR = Mathf.Max(4f * paint.holeRadius / Mathf.Max(1e-3f, Mathf.Abs(scale.x)), 0.09f);
        fi.holeCaptureH = 0.15f;
        fi.holeShape    = (int)paint.holeShape;
        fi.holeRadiusM  = paint.holeRadius;
        fi.exitDirWorld = bt.TransformDirection(paint.exitDirection.normalized);
        fi.exitSpeed    = paint.currentExitSpeed;

        // Jet fan-out angle grows with the efflux Reynolds number
        // (Lin & Reitz 1998) — same relation as BucketEmission.ComputeHolePattern.
        float effVisc = Mathf.Max(0.05f,
            paint.viscosity * FluidConstants.ViscosityTemperatureFactor(paint.temperature));
        float mu = paint.paintViscosityPaS * effVisc;
        float reJet = paint.density * paint.currentExitSpeed * (2f * paint.holeRadius) / Mathf.Max(1e-6f, mu);
        float sigma = Mathf.Lerp(1.5f, 10f, Mathf.InverseLerp(2000f, 10000f, reJet)) * Mathf.Deg2Rad;
        fi.spreadSpeed = paint.currentExitSpeed * Mathf.Tan(sigma);

        // Emission budget: drops/frame = Torricelli mass flow / per-drop mass.
        // The GPU takes AT MOST this many contained particles out through the
        // hole this frame; the bucket is debited by the actual count later.
        float dropsPerSec = paint.currentMassFlow / Mathf.Max(1e-9f, perDropMass);
        emitAccum += dropsPerSec * dt;
        int budget = Mathf.Min(Mathf.FloorToInt(emitAccum), 2048);
        emitAccum -= budget;
        fi.emitBudget = budget;

        // ---- staged fill (CPU twin: BucketEmission section A) ----
        float massFrac = pendulum.currentPaintMass / Mathf.Max(1e-4f, pendulum.initialPaintMass);
        int fillTarget = Mathf.RoundToInt(targetParticles * massFrac);
        int spawnPerFrame = Mathf.Max(256, targetParticles / 100);
        int spawn = Mathf.Clamp(fillTarget - insideEst, 0, spawnPerFrame);
        fi.spawnCount = spawn;
        insideEst = Mathf.Max(0, insideEst + spawn - budget);   // corrected at each readback
        fi.spawnColor   = PickSpawnColor();
        fi.spawnMaxY    = -0.46f + 0.92f * Mathf.Clamp01(Mathf.Max(0.05f, paint.paintLevel));
        fi.particleSize = perDropDiameter;

        // ---- environment / integration ----
        fi.gravity    = paint.gravity;
        fi.insideDamp = Mathf.Pow(Mathf.Clamp01(paint.dampingFactor), dt * 60f);
        float r = perDropDiameter * 0.5f;
        fi.dragK = 0.5f * pendulum.airDensity * 0.47f * Mathf.PI * r * r
                   / Mathf.Max(1e-9f, perDropMass);
        fi.wind     = pendulum.windVel;
        fi.lifetime = paint.particleLifetime;

        // ---- canvas ----
        if (paint.canvasRenderer != null)
        {
            Transform c = paint.canvasRenderer.transform;
            fi.planePoint    = c.position;
            fi.planeNormal   = c.up;
            fi.worldToCanvas = c.worldToLocalMatrix;
        }
        else fi.planeNormal = Vector3.up;

        // Splat radius: Madejski-style spread of the VISUAL-scale drop
        // (beta ~ 3 at paint-drop impact speeds), in texels.
        float dVis = perDropDiameter * effVisualScale;
        fi.splatBasePx = 0.5f * 3f * dVis * Mathf.Max(1f, paint.pixelsPerUnit);

        fi.visualScale     = effVisualScale;
        fi.paintingEnabled = gpuPainting;
        return fi;
    }

    Color PickSpawnColor()
    {
        if (!paint.multiColorMode) return paint.paintColor;
        int idx = Mathf.Abs(pendulum.swingCount) % 3;
        return idx == 0 ? paint.paintColor : (idx == 1 ? paint.paintColor2 : paint.paintColor3);
    }

    // ------------------------------------------------------------------------
    //  Mode switching: CPU particles are purged when GPU takes over (ONE
    //  particle system at a time), and the canvas material shows the GPU
    //  RenderTexture instead of the CPU Texture2D. Both layers persist, so
    //  toggling back restores the CPU painting untouched.
    // ------------------------------------------------------------------------
    void SetGpuActive(bool on)
    {
        gpuActive = on;
        if (paint == null) return;
        paint.gpuMode = on;

        var cr = paint.canvasRenderer;
        if (on)
        {
            paint.PurgeAllParticles();
            if (sim.capacity == 0) sim.Configure(targetParticles);
            sim.ResetParticles();
            ClearGpuCanvas();
            if (cr != null)
            {
                cpuCanvasTex = cr.material.mainTexture;
                if (gpuPainting) cr.material.mainTexture = sim.CanvasRT;
            }
            insideEst = 0; emitAccum = 0f;
        }
        else if (cr != null && cpuCanvasTex != null)
        {
            cr.material.mainTexture = cpuCanvasTex;
        }
    }

    public void ClearGpuCanvas()
    {
        if (sim == null || paint == null) return;
        sim.ClearCanvas(SurfacePreset.From(paint.surface).substrateColor);
    }

    public void SaveGpuPainting() { if (sim != null) sim.SaveCanvasPng(); }

    // R key / panel Clear / ResetAll all funnel through PaintPhysics.Clear().
    void HandleCanvasCleared() { if (gpuActive) ClearGpuCanvas(); }

    // RefillPaint purges the CPU particles ("bucket swap"); mirror it on GPU.
    void HandleParticlesPurged()
    {
        if (!gpuActive || sim == null) return;
        sim.ResetParticles();
        insideEst = 0; emitAccum = 0f;
    }
}
