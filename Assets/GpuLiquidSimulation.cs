using UnityEngine;
using UnityEngine.Rendering;

// ============================================================================
//  GpuLiquidSimulation — owner of the GPU particle pipeline (LiquidSPH.compute).
// ----------------------------------------------------------------------------
//  WHY GPU BUFFERS INSTEAD OF GAMEOBJECTS (the 200k argument, for the report):
//    * A GameObject per particle costs a Transform sync, renderer, culling and
//      scene-graph traversal EVERY frame. The editor dies around a few
//      thousand; 200,000 is off by two orders of magnitude.
//    * Even particles-as-C#-objects (the CPU path here, PaintParticlePool)
//      are bounded by ONE core walking 27 hash cells twice per particle per
//      frame — measured ceiling ~10k. A GPU runs the same loop on thousands
//      of cores; particles are rows in a StructuredBuffer, not objects.
//    * The CPU's only per-frame work is ~30 uniforms up (bucket matrices,
//      Torricelli emission numbers) and NOTHING down on the hot path: stats
//      come back through AsyncGPUReadback every READBACK_INTERVAL frames.
//      ComputeBuffer.GetData (a synchronous GPU->CPU stall) is never called.
//
//  This component is deliberately "dumb": it owns buffers and dispatches
//  kernels. Everything scene-related (where the bucket is, how fast paint
//  pours, what colour) is decided by GpuLiquidBridge and handed in through
//  FrameInput, so the sim stays testable and the bridge stays the single
//  seam to the existing bucket/rope/canvas system.
// ============================================================================
public sealed class GpuLiquidSimulation : MonoBehaviour
{
    // ---- solver settings (surfaced in the control panel via the bridge) ----
    [Header("Solver (PBF)")]
    [Range(1, 6)]  public int   solverIterations = 3;   // density-constraint projections per frame
    [Range(8, 64)] public int   maxNeighbors     = 48;  // hard cap per neighbour loop (O(k), never O(n))
    [Range(0f, 0.5f)] public float xsphViscosity = 0.10f;
    [Range(1.6f, 3f)] public float smoothingScale = 2.0f; // h = scale * rest particle spacing
    [Range(0.6f, 1.6f)] public float restDensityScale = 1.0f;

    [Header("Canvas painting")]
    public int canvasTextureSize = 1024;

    [Header("Debug readouts (AsyncGPUReadback, ~4x/s — never GetData)")]
    public int   aliveCount;
    public int   insideCount;
    public int   airborneCount;
    public float avgNeighbors;
    public int   totalPainted;
    public float currentH;          // auto-tuned smoothing radius (m)
    public int   capacity;

    public bool LoadOk { get; private set; }
    public int  TargetCount { get; private set; }
    public int  ReadbackVersion { get; private set; }   // bumped when fresh stats arrive
    public RenderTexture CanvasRT { get; private set; }
    public GraphicsBuffer DrawArgsBuffer => drawArgs;
    public Material ParticleMaterial { get; private set; } // buffers pre-bound for the renderer

    // Everything the bridge computes from the existing bucket/canvas each frame.
    public struct FrameInput
    {
        public Matrix4x4 worldToBucket, bucketToWorld, bucketRot, worldToCanvas;
        public Vector3 bucketScale, bucketVelocity, bucketUp, bucketPos, wallLim;
        public float   spinRate;
        public Vector3 holeLocal;
        public float   holeCaptureR, holeCaptureH;
        public int     emitBudget, holeShape;
        public float   holeRadiusM, exitSpeed, spreadSpeed;
        public Vector3 exitDirWorld;
        public int     spawnCount;
        public Color   spawnColor;
        public float   spawnMaxY, particleSize;
        public float   gravity, insideDamp, dragK, lifetime;
        public Vector3 wind, planePoint, planeNormal;
        public float   splatBasePx, bucketVolume, visualScale;
        public bool    paintingEnabled;
    }

    // ---- GPU mirrors (layout MUST match LiquidSPH.compute) ----
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct GpuParticle          // 80 bytes
    {
        public Vector3 position;  public float density;
        public Vector3 predicted; public float lambda;
        public Vector3 velocity;  public float nbCount;
        public Vector4 color;
        public uint state; public float size; public float lifetime; public float age;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct GpuSplat             // 48 bytes
    {
        public Vector2 uv; public float radiusPx; public float aspect;
        public Vector2 dirPx; public float opacity; public float pad;
        public Vector4 color;
    }

    // counter slots — must match LiquidSPH.compute
    const int CTR_DEAD = 0, CTR_ALIVE = 1, CTR_INSIDE = 2, CTR_AIR = 3,
              CTR_EMIT_REQ = 4, CTR_SPLAT = 5, CTR_TOTAL_EMITTED = 6,
              CTR_NB_SUM = 7, CTR_TOTAL_PAINTED = 8, CTR_COUNT = 12;

    const int THREADS = 128;
    const int NUM_CELLS = 1 << 17;    // hash table (power of two -> mask modulo)
    const int MAX_PER_CELL = 32;      // bounded cell capacity (documented limitation)
    const int MAX_SPLATS = 4096;      // splat events per frame (emission is far below this)
    const int READBACK_INTERVAL = 15; // frames between stats readbacks

    ComputeShader sim, painter;
    int kInit, kClearFrame, kSpawn, kPredict, kClearGrid, kBuildGrid,
        kLambda, kDelta, kApplyDelta, kVelocity, kFinalize, kArgs,
        kPaint, kClearCanvas;

    GraphicsBuffer particles, deadList, aliveList, counters, cellCount, cellEntries,
                   deltaP, splats, drawArgs, paintArgs;

    uint frameIndex;
    bool readbackPending;
    int  lastTotalEmitted;
    int  pendingEmittedDelta;
    float latticeH = -1f, latticeS = -1f, cachedRestDensity;

    // ---- shader property ids (avoids per-frame string hashing / GC) ----
    static readonly int ID_Particles = Shader.PropertyToID("_Particles");
    static readonly int ID_DeadList = Shader.PropertyToID("_DeadList");
    static readonly int ID_AliveList = Shader.PropertyToID("_AliveList");
    static readonly int ID_Counters = Shader.PropertyToID("_Counters");
    static readonly int ID_CellCount = Shader.PropertyToID("_CellCount");
    static readonly int ID_CellEntries = Shader.PropertyToID("_CellEntries");
    static readonly int ID_DeltaP = Shader.PropertyToID("_DeltaP");
    static readonly int ID_Splats = Shader.PropertyToID("_Splats");
    static readonly int ID_DrawArgs = Shader.PropertyToID("_DrawArgs");
    static readonly int ID_PaintArgs = Shader.PropertyToID("_PaintArgs");
    static readonly int ID_Canvas = Shader.PropertyToID("_Canvas");

    public void Initialize()
    {
        if (sim != null) return;
        sim     = Resources.Load<ComputeShader>("LiquidSPH");
        painter = Resources.Load<ComputeShader>("GpuCanvasPainter");
        LoadOk = SystemInfo.supportsComputeShaders && sim != null && painter != null;
        if (!LoadOk)
        {
            Debug.LogWarning("[GpuLiquid] Compute shaders unavailable " +
                             "(missing Resources/LiquidSPH.compute or unsupported GPU) — GPU mode disabled.");
            return;
        }

        kInit       = sim.FindKernel("InitParticles");
        kClearFrame = sim.FindKernel("ClearPerFrame");
        kSpawn      = sim.FindKernel("Spawn");
        kPredict    = sim.FindKernel("PredictPositions");
        kClearGrid  = sim.FindKernel("ClearGrid");
        kBuildGrid  = sim.FindKernel("BuildGrid");
        kLambda     = sim.FindKernel("ComputeLambda");
        kDelta      = sim.FindKernel("ComputeDelta");
        kApplyDelta = sim.FindKernel("ApplyDelta");
        kVelocity   = sim.FindKernel("ComputeVelocity");
        kFinalize   = sim.FindKernel("FinalizeFrame");
        kArgs       = sim.FindKernel("UpdateArgs");
        kPaint      = painter.FindKernel("PaintSplats");
        kClearCanvas = painter.FindKernel("ClearCanvas");

        EnsureCanvas();
    }

    // (Re)allocate every buffer for a particle budget. Called on preset change;
    // this is the ONLY allocation point — the per-frame path allocates nothing.
    public void Configure(int targetCount)
    {
        Initialize();
        if (!LoadOk) return;

        TargetCount = Mathf.Clamp(targetCount, 1000, 200000);
        // Capacity = reservoir target + headroom for the airborne stream, so
        // choosing 200k really keeps 200k INSIDE the bucket while paint falls.
        capacity = TargetCount + Mathf.Max(2000, TargetCount / 5);

        ReleaseBuffers();
        int particleStride = System.Runtime.InteropServices.Marshal.SizeOf<GpuParticle>(); // 80
        int splatStride    = System.Runtime.InteropServices.Marshal.SizeOf<GpuSplat>();    // 48
        particles   = NewStructured(capacity, particleStride);
        deadList    = NewStructured(capacity, 4);
        aliveList   = NewStructured(capacity, 4);
        deltaP      = NewStructured(capacity, 16);
        counters    = NewStructured(CTR_COUNT, 4);
        cellCount   = NewStructured(NUM_CELLS, 4);
        cellEntries = NewStructured(NUM_CELLS * MAX_PER_CELL, 4);
        splats      = NewStructured(MAX_SPLATS, splatStride);
        drawArgs = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured, 5, 4);
        paintArgs = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured, 3, 4);
        drawArgs.SetData(new uint[] { 6, 0, 0, 0, 0 });   // quad = 6 indices; instanceCount GPU-filled
        paintArgs.SetData(new uint[] { 0, 1, 1 });

        BindAll();
        ResetParticles();

        if (ParticleMaterial == null)
        {
            Shader s = Shader.Find("Custom/GpuPaintParticle");
            if (s != null) ParticleMaterial = new Material(s);
        }
        if (ParticleMaterial != null)
        {
            ParticleMaterial.SetBuffer(ID_Particles, particles);
            ParticleMaterial.SetBuffer(ID_AliveList, aliveList);
        }
    }

    static GraphicsBuffer NewStructured(int count, int stride)
        => new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, stride);

    // Every kernel gets its buffers bound once per Configure (bindings persist).
    void BindAll()
    {
        void B(int k, params (int id, GraphicsBuffer b)[] bs)
        { foreach (var (id, b) in bs) sim.SetBuffer(k, id, b); }

        B(kInit,       (ID_Particles, particles), (ID_DeadList, deadList));
        B(kClearFrame, (ID_Counters, counters));
        B(kSpawn,      (ID_Particles, particles), (ID_DeadList, deadList), (ID_Counters, counters));
        B(kPredict,    (ID_Particles, particles));
        B(kClearGrid,  (ID_CellCount, cellCount));
        B(kBuildGrid,  (ID_Particles, particles), (ID_CellCount, cellCount), (ID_CellEntries, cellEntries));
        B(kLambda,     (ID_Particles, particles), (ID_CellCount, cellCount), (ID_CellEntries, cellEntries));
        B(kDelta,      (ID_Particles, particles), (ID_CellCount, cellCount), (ID_CellEntries, cellEntries), (ID_DeltaP, deltaP));
        B(kApplyDelta, (ID_Particles, particles), (ID_DeltaP, deltaP));
        B(kVelocity,   (ID_Particles, particles));
        B(kFinalize,   (ID_Particles, particles), (ID_CellCount, cellCount), (ID_CellEntries, cellEntries),
                       (ID_Counters, counters), (ID_AliveList, aliveList), (ID_DeadList, deadList), (ID_Splats, splats));
        B(kArgs,       (ID_Counters, counters), (ID_DrawArgs, drawArgs), (ID_PaintArgs, paintArgs));

        painter.SetBuffer(kPaint, ID_Splats, splats);
        painter.SetTexture(kPaint, ID_Canvas, CanvasRT);
        painter.SetTexture(kClearCanvas, ID_Canvas, CanvasRT);
    }

    // Return every slot to the dead list (empty bucket). Persistent counters reset.
    public void ResetParticles()
    {
        if (!LoadOk || particles == null) return;
        var init = new int[CTR_COUNT];
        init[CTR_DEAD] = capacity;
        counters.SetData(init);
        sim.SetInt("_Capacity", capacity);
        sim.Dispatch(kInit, Groups(capacity), 1, 1);
        lastTotalEmitted = 0;
        pendingEmittedDelta = 0;
        aliveCount = insideCount = airborneCount = 0;
    }

    static int Groups(int n) => Mathf.Max(1, (n + THREADS - 1) / THREADS);

    // ------------------------------------------------------------------------
    //  One simulation frame. dt is the (clamped) frame delta; the PBF solver is
    //  unconditionally stable so no CFL substepping is needed — that is WHY a
    //  constraint solver was chosen over explicit SPH for 200k (see .compute).
    // ------------------------------------------------------------------------
    public void Step(float dt, in FrameInput fi)
    {
        if (!LoadOk || particles == null || dt <= 0f) return;

        AutoTune(fi.bucketVolume);

        // ---- per-frame uniforms (the ONLY CPU->GPU traffic on the hot path) ----
        sim.SetInt("_Capacity", capacity);
        sim.SetInt("_NumCells", NUM_CELLS);
        sim.SetInt("_MaxPerCell", MAX_PER_CELL);
        sim.SetInt("_MaxNeighbors", Mathf.Clamp(maxNeighbors, 8, 64));
        sim.SetInt("_MaxSplats", MAX_SPLATS);
        sim.SetFloat("_Dt", dt);
        sim.SetFloat("_Gravity", fi.gravity);
        sim.SetFloat("_InsideDamp", fi.insideDamp);
        sim.SetFloat("_MaxSpeedInside", 20f);
        sim.SetFloat("_DragK", fi.dragK);
        sim.SetVector("_Wind", fi.wind);
        sim.SetFloat("_Lifetime", fi.lifetime);
        sim.SetFloat("_XsphC", xsphViscosity);

        sim.SetMatrix("_WorldToBucket", fi.worldToBucket);
        sim.SetMatrix("_BucketToWorld", fi.bucketToWorld);
        sim.SetMatrix("_BucketRot", fi.bucketRot);
        sim.SetVector("_BucketScale", fi.bucketScale);
        sim.SetVector("_BucketVelocity", fi.bucketVelocity);
        sim.SetVector("_BucketUp", fi.bucketUp);
        sim.SetVector("_BucketPos", fi.bucketPos);
        sim.SetVector("_WallLim", fi.wallLim);
        sim.SetFloat("_SpinRate", fi.spinRate);

        sim.SetVector("_HoleLocal", fi.holeLocal);
        sim.SetFloat("_HoleCaptureR", fi.holeCaptureR);
        sim.SetFloat("_HoleCaptureH", fi.holeCaptureH);
        sim.SetInt("_EmitBudget", Mathf.Max(0, fi.emitBudget));
        sim.SetInt("_HoleShape", fi.holeShape);
        sim.SetFloat("_HoleRadiusM", fi.holeRadiusM);
        sim.SetVector("_ExitDirWorld", fi.exitDirWorld);
        sim.SetFloat("_ExitSpeed", fi.exitSpeed);
        sim.SetFloat("_SpreadSpeed", fi.spreadSpeed);

        sim.SetInt("_SpawnCount", Mathf.Max(0, fi.spawnCount));
        sim.SetInt("_FrameSeed", unchecked((int)(frameIndex * 2246822519u + 3266489917u)));
        sim.SetVector("_SpawnColor", fi.spawnColor);
        sim.SetFloat("_SpawnMaxY", fi.spawnMaxY);
        sim.SetFloat("_ParticleSize", fi.particleSize);

        sim.SetVector("_PlanePoint", fi.planePoint);
        sim.SetVector("_PlaneNormal", fi.planeNormal);
        sim.SetMatrix("_WorldToCanvas", fi.worldToCanvas);
        sim.SetFloat("_SplatBasePx", fi.splatBasePx);
        sim.SetFloat("_MaxSplatPx", 32f);
        sim.SetFloat("_KillBelowPlane", -2f);

        // ---- dispatch pipeline ----
        int pg = Groups(capacity);
        sim.Dispatch(kClearFrame, 1, 1, 1);
        if (fi.spawnCount > 0)
            sim.Dispatch(kSpawn, Groups(fi.spawnCount), 1, 1);
        sim.Dispatch(kPredict, pg, 1, 1);
        sim.Dispatch(kClearGrid, Groups(NUM_CELLS), 1, 1);
        sim.Dispatch(kBuildGrid, pg, 1, 1);

        int iters = Mathf.Clamp(solverIterations, 1, 6);
        for (int i = 0; i < iters; i++)
        {
            sim.Dispatch(kLambda, pg, 1, 1);
            sim.Dispatch(kDelta, pg, 1, 1);
            sim.Dispatch(kApplyDelta, pg, 1, 1);
        }

        sim.Dispatch(kVelocity, pg, 1, 1);
        sim.Dispatch(kFinalize, pg, 1, 1);
        sim.Dispatch(kArgs, 1, 1, 1);

        // GPU canvas painting: group count comes from the GPU splat counter
        // via DispatchIndirect — the CPU never learns how many splats landed.
        if (fi.paintingEnabled && CanvasRT != null)
        {
            painter.SetInt("_TexSize", CanvasRT.width);
            painter.DispatchIndirect(kPaint, paintArgs);
        }

        if (ParticleMaterial != null)
            ParticleMaterial.SetFloat("_VisualScale", fi.visualScale);

        frameIndex++;
        if (frameIndex % READBACK_INTERVAL == 0 && !readbackPending)
        {
            readbackPending = true;
            AsyncGPUReadback.Request(counters, OnStatsReadback);
        }
    }

    // Auto-tuned SPH scale: h and the rest density follow the ACTUAL particle
    // spacing (bucket volume / count), so 10k and 200k both behave like liquid.
    // rho0 is the numeric Poly6 sum over the rest lattice — no magic constant.
    void AutoTune(float bucketVolume)
    {
        float V = Mathf.Max(1e-4f, bucketVolume) * 0.55f;  // random-loose packing share
        float s = Mathf.Pow(V / Mathf.Max(1, TargetCount), 1f / 3f);
        float h = Mathf.Max(1e-3f, smoothingScale * s);
        currentH = h;

        if (Mathf.Abs(h - latticeH) > 0.01f * h || Mathf.Abs(s - latticeS) > 0.01f * s)
        {
            latticeH = h; latticeS = s;
            cachedRestDensity = LatticeDensity(h, s);
        }

        float h2 = h * h;
        float h3 = h2 * h;
        float poly6 = 315f / (64f * Mathf.PI * h3 * h3 * h3);
        float spiky = -45f / (Mathf.PI * h3 * h3);

        sim.SetFloat("_H", h);
        sim.SetFloat("_H2", h2);
        sim.SetFloat("_InvCellSize", 1f / h);
        sim.SetFloat("_Poly6", poly6);
        sim.SetFloat("_SpikyGrad", spiky);
        sim.SetFloat("_RestDensity", cachedRestDensity * Mathf.Max(0.1f, restDensityScale));
        sim.SetFloat("_Epsilon", 100f);      // CFM relaxation (Macklin & Muller Eq. 11)
        sim.SetFloat("_MaxDeltaP", 0.25f);   // per-iteration correction clamp, fraction of h
    }

    // Poly6 kernel sum over a cubic lattice with spacing s (unit particle
    // mass, self term included) = the density a particle feels at rest.
    static float LatticeDensity(float h, float s)
    {
        float h2 = h * h;
        float h3 = h2 * h;
        float poly6 = 315f / (64f * Mathf.PI * h3 * h3 * h3);
        int k = Mathf.CeilToInt(h / s);
        float sum = 0f;
        for (int x = -k; x <= k; x++)
        for (int y = -k; y <= k; y++)
        for (int z = -k; z <= k; z++)
        {
            float r2 = s * s * (x * x + y * y + z * z);
            if (r2 >= h2) continue;
            float d = h2 - r2;
            sum += poly6 * d * d * d;
        }
        return Mathf.Max(1e-6f, sum);
    }

    void OnStatsReadback(AsyncGPUReadbackRequest req)
    {
        readbackPending = false;
        if (req.hasError || this == null || counters == null) return;
        var data = req.GetData<int>();
        if (data.Length < CTR_COUNT) return;

        aliveCount    = data[CTR_ALIVE];
        insideCount   = data[CTR_INSIDE];
        airborneCount = data[CTR_AIR];
        avgNeighbors  = aliveCount > 0 ? (float)data[CTR_NB_SUM] / aliveCount : 0f;
        totalPainted  = data[CTR_TOTAL_PAINTED];

        int total = data[CTR_TOTAL_EMITTED];
        pendingEmittedDelta += Mathf.Max(0, total - lastTotalEmitted);
        lastTotalEmitted = total;
        ReadbackVersion++;
    }

    // Drops actually poured since last call — the bridge multiplies by the
    // per-drop mass so the bucket loses EXACTLY the paint that left the hole.
    public int TakeEmittedDelta()
    {
        int d = pendingEmittedDelta;
        pendingEmittedDelta = 0;
        return d;
    }

    // ---- canvas RT ----
    void EnsureCanvas()
    {
        if (CanvasRT != null) return;
        CanvasRT = new RenderTexture(canvasTextureSize, canvasTextureSize, 0,
                                     RenderTextureFormat.ARGBHalf)
        {
            enableRandomWrite = true,
            wrapMode = TextureWrapMode.Clamp,
            name = "GpuPaintCanvas"
        };
        CanvasRT.Create();
    }

    public void ClearCanvas(Color substrate)
    {
        if (!LoadOk || CanvasRT == null) return;
        painter.SetInt("_TexSize", CanvasRT.width);
        painter.SetVector("_ClearColor", substrate);
        int g = Mathf.Max(1, (CanvasRT.width + 7) / 8);
        painter.Dispatch(kClearCanvas, g, g, 1);
    }

    // One-off, user-triggered save (S key) — NOT a per-frame readback.
    public void SaveCanvasPng()
    {
        if (CanvasRT == null) return;
        var prev = RenderTexture.active;
        RenderTexture.active = CanvasRT;
        var tex = new Texture2D(CanvasRT.width, CanvasRT.height, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, CanvasRT.width, CanvasRT.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        string filename = $"Painting_GPU_{System.DateTime.Now:yyyyMMdd_HHmmss}.png";
        System.IO.File.WriteAllBytes(Application.dataPath + "/" + filename, tex.EncodeToPNG());
        Destroy(tex);
        Debug.Log("[GpuLiquid] Saved painting: " + filename);
    }

    // ---- lifetime (brief §H: release everything, no leaks) ----
    void OnDisable() { ReleaseBuffers(); }
    void OnDestroy()
    {
        ReleaseBuffers();
        if (CanvasRT != null) { CanvasRT.Release(); Destroy(CanvasRT); CanvasRT = null; }
        if (ParticleMaterial != null) { Destroy(ParticleMaterial); ParticleMaterial = null; }
    }

    void ReleaseBuffers()
    {
        Release(ref particles); Release(ref deadList); Release(ref aliveList);
        Release(ref counters); Release(ref cellCount); Release(ref cellEntries);
        Release(ref deltaP); Release(ref splats); Release(ref drawArgs); Release(ref paintArgs);
    }
    static void Release(ref GraphicsBuffer b) { b?.Release(); b = null; }
}
