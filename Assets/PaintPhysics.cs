using UnityEngine;

public enum SurfaceType { Canvas, Wood, Metal, Paper }
public enum HoleShape { Round, Narrow, Wide, Multiple }

// ============================================================================
//  PaintPhysics — CORE / shared state + lifecycle coordinator
// ----------------------------------------------------------------------------
//  Phase-0 restructure: this MonoBehaviour is split across FOUR files that all
//  compile into the SAME component (C# `partial class`), so three teammates can
//  each own one system without editing the same file. Serialization, the scene
//  reference (GUID a1727060f0d1c03439c39d1fc507934b) and behaviour are byte-for-
//  byte identical to the old single-file PaintDrawer.cs — nothing was retuned.
//
//    PaintPhysics.cs        (this file) — shared fields, Start/Update, tilt, UI
//    BucketEmission.cs      — bucket reservoir + hole + droplet emission
//    ParticleSimulation.cs  — in-air particle physics + canvas-collision hand-off
//    SurfaceInteraction.cs  — splat / spread / flow / absorption / texture output
//
//  Fields live in the file that most uses them; because this is one `partial`
//  class, cross-file access (e.g. Start() below touching `pool` or `texture`)
//  is a plain member access and needs no wiring. The public field/method
//  surface that SimulationManager talks to is unchanged.
// ============================================================================
public partial class PaintPhysics : MonoBehaviour
{
    [Header("Scene refs")]
    public Transform paintPoint;
    public Renderer canvasRenderer;
    public PendulumMotion bucketMotion;
    public SurfaceType surface = SurfaceType.Canvas;
    public Color paintColor = Color.red;

    [Header("Viscosity / temperature / humidity")]
    public float viscosity = 8f;
    public float temperature = 25f;
    public float minViscosity = 0.3f;
    public float maxViscosity = 3f;
    [Range(0, 100)] public float humidity = 50f;

    [Header("Physics (shared, SI)")]
    public float gravity = 9.81f;

    [Header("Paint fluid properties (SI)")]
    // Single source of truth for the macroscopic surface model. Paint, not water.
    public float density = FluidConstants.PaintDensity;             // rho   (kg/m^3)
    public float surfaceTension = FluidConstants.PaintSurfaceTension; // gamma (N/m)
    public float paintViscosityPaS = FluidConstants.PaintViscosity;  // eta   (Pa·s) for film/Washburn/Tanner

    [Header("Unit scale (derived from canvas geometry)")]
    public float pixelsPerUnit;        // texture pixels per metre
    public float canvasMetersWidth;    // physical canvas width  (m)
    public float canvasMetersHeight;   // physical canvas height (m)

    [Header("Canvas size (editable — PDF §4 أبعاد اللوحة)")]
    // These drive the Plane's local X/Z scale each frame (Plane mesh spans 10 units), so the user can
    // set the physical canvas size at runtime. Initialised in Start() from the scene's actual size.
    public float canvasWidthMeters = 50f;
    public float canvasHeightMeters = 50f;
    // Unity's built-in Plane mesh spans 10 local units; the UV mapping below divides by this.
    private const float PlaneMeshExtent = 10f;
    private int roughnessJitterPx;     // real Ra (um) converted to pixels (microscopic -> usually 0)

    [Header("Floor tilt")]
    [Range(-80f, 80f)] public float canvasTiltControlDeg = 0f; // pitch about local X (slider or mouse)
    [Range(-80f, 80f)] public float canvasTiltRollDeg = 0f;    // roll about local Z (mouse)

    [Header("Surface vibration (environment — PDF §6 اهتزاز سطح الرسم)")]
    // Shakes the canvas in its own plane; droplets in flight see the moving surface, so a vibrating
    // floor smears the landing points (a real environmental effect, not a cosmetic one).
    public bool surfaceVibration = false;
    [Range(0f, 0.5f)] public float vibrationAmplitude = 0.05f; // metres
    [Range(0f, 20f)]  public float vibrationFrequency = 6f;    // Hz

    private Quaternion canvasBaseRotation;
    private Vector3 canvasBasePosition;
    private bool canvasBaseCaptured;

    private SurfacePreset preset;
    private bool textureDirty;

    void Start()
{
    sph = new SPHSolver();
    // Two-pass symmetric solver — no external fudge factor. particleMass 0.02 matches the previous
    // solver's mass; sphStiffness / sphRestDensity are exposed in the Inspector for strength tuning.
    sph.Configure(interactionRadius, viscosity, sphStiffness, 0.02f, sphRestDensity);


    // Pre-allocate the spatial hash at the HARD capacity (10k) so the safe/strong/stress particle
    // modes can be switched at runtime without reallocating.
    spatialGrid = new SpatialGrid(interactionRadius, HardMaxParticles);


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
        canvasBasePosition = canvasRenderer.transform.position;
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
    // Seed the editable canvas-size controls from the scene's actual canvas size (so the sliders
    // start at the real value instead of forcing a jump on the first frame).
    canvasWidthMeters = canvasMetersWidth;
    canvasHeightMeters = canvasMetersHeight;
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


    // Custom instanced shader: declares _Color as a PER-INSTANCE property so DrawMeshInstanced can
    // batch 1023 differently-coloured spheres per call (~10 draw calls for 10,000 particles).
    // The Standard shader has no instanced _Color, so per-particle colours would break its batching.
    Shader instShader = Shader.Find("Custom/PaintParticleInstanced");
    if (instShader == null)
    {
        Debug.LogWarning("[PaintPhysics] Custom/PaintParticleInstanced not found — falling back to Standard.");
        instShader = Shader.Find("Standard");
    }
    particleMatTemplate = new Material(instShader);
    particleMatTemplate.enableInstancing = true;


    EnsureSphereMesh();


    // Data-oriented pool sized at the HARD capacity; `maxParticles` stays the runtime soft cap.
    pool = new PaintParticlePool(HardMaxParticles);

    // Create glowing ring visuals at each hole position (child of bucket → follows swing).
    SetupHoleHighlights();
}

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f || bucketMotion == null || paintPoint == null) return;

        ApplyCanvasTilt();
        ApplyCanvasSize();
        ComputeUnitScale();
        preset = SurfacePreset.From(surface);

        // Edge jitter derived from the real surface roughness Ra (microscopic -> usually 0 px).
        roughnessJitterPx = Mathf.RoundToInt(preset.arithmeticRoughnessUm * 1e-6f * pixelsPerUnit);

        EmitStep(dt);
        UpdateParticles(dt); // also refreshes activeParticles / insideParticles / avgNeighbors
        UpdateActiveSplats(dt); // per-splat spreading AND per-splat capillary absorption happen here now

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
            canvasBasePosition = canvasRenderer.transform.position;
            canvasBaseCaptured = true;
        }
        canvasRenderer.transform.rotation =
            canvasBaseRotation * Quaternion.Euler(canvasTiltControlDeg, 0f, canvasTiltRollDeg);

        // Surface vibration: shake the canvas in its own (untilted) plane. Two slightly detuned
        // frequencies on the local X/Z axes give an irregular jitter instead of a clean straight line.
        Vector3 pos = canvasBasePosition;
        if (surfaceVibration && vibrationAmplitude > 0f)
        {
            float w = 2f * Mathf.PI * vibrationFrequency * Time.time;
            Vector3 inPlaneX = canvasBaseRotation * Vector3.right;
            Vector3 inPlaneZ = canvasBaseRotation * Vector3.forward;
            pos += inPlaneX * (Mathf.Sin(w) * vibrationAmplitude)
                 + inPlaneZ * (Mathf.Sin(w * 0.87f + 1.3f) * vibrationAmplitude);
        }
        canvasRenderer.transform.position = pos;
    }

    // Drive the Plane's local X/Z scale from the editable canvas-size controls (Plane mesh = 10 units).
    // ComputeUnitScale() reads the resulting lossyScale, so pixelsPerUnit stays consistent.
    void ApplyCanvasSize()
    {
        if (canvasRenderer == null) return;
        canvasWidthMeters  = Mathf.Max(1f, canvasWidthMeters);
        canvasHeightMeters = Mathf.Max(1f, canvasHeightMeters);
        Vector3 s = canvasRenderer.transform.localScale;
        canvasRenderer.transform.localScale = new Vector3(
            canvasWidthMeters / PlaneMeshExtent, s.y, canvasHeightMeters / PlaneMeshExtent);
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
