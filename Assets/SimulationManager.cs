using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;


public class SimulationManager : MonoBehaviour
{
    public PendulumMotion pendulum;
    public PaintPhysics paint;

    [Header("Sampling")]
    public float sampleInterval = 0.1f;
    public int maxSamples = 3000;
    public float coverageInterval = 1f;

    public readonly List<float> timeHistory = new List<float>();
    public readonly List<float> thetaHistory = new List<float>();
    public readonly List<float> tensionHistory = new List<float>();
    public readonly List<float> energyHistory = new List<float>();

    public float motionTime;
    public float trajectoryLength;
    public float paintAreaCoverage;
    public int pathCount;

    private float sampleTimer, coverageTimer;
    private float smoothedFps;               // exp-smoothed frame rate for the perf readout
    private Vector3 lastBucketPos;
    private float lastAngleSign;
    private bool showPanel = true;
    private Vector2 scroll;

    // Screen-space rects of the IMGUI panel, exposed so 3D input scripts (CameraOrbit,
    // BucketGrabController) can tell "is the mouse over the UI right now?" and ignore drags
    // that land on it -- IMGUI (OnGUI) and the new Input System read the mouse independently,
    // so without this check a slider drag also spins the camera underneath it.
    public static Rect ToggleButtonRect => new Rect(10, 10, 130, 34);
    public static Rect PanelRect => new Rect(10, 50, 470, Screen.height - 64);
    public static bool PanelVisible { get; private set; } = true;
    private int currentTab = 0;

    // Captured experiments for the Compare tab (PDF §5.6 مقارنة أكثر من تجربة).
    private readonly List<ExperimentSnapshot> experiments = new List<ExperimentSnapshot>();

    class ExperimentSnapshot
    {
        public string label, surface;
        public float L, angle, viscosity, motionTime, trajectory, coverage;
        public int paths;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Object.FindFirstObjectByType<SimulationManager>() == null)
        {
            var go = new GameObject("SimulationManager");
            go.AddComponent<SimulationManager>();
        }
    }

    void Start()
    {
        if (pendulum == null) pendulum = Object.FindFirstObjectByType<PendulumMotion>();
        if (paint == null) paint = Object.FindFirstObjectByType<PaintPhysics>();
        if (pendulum != null) lastBucketPos = pendulum.transform.position;
        ResetData();
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt > 0f) smoothedFps = Mathf.Lerp(smoothedFps, 1f / dt, 0.05f);
        if (pendulum == null) return;
        motionTime += dt;

        Vector3 pos = pendulum.transform.position;
        trajectoryLength += Vector3.Distance(pos, lastBucketPos);
        lastBucketPos = pos;

        float s = Mathf.Sign(pendulum.angleX);
        if (s != 0 && s != lastAngleSign && lastAngleSign != 0) pathCount++;
        lastAngleSign = s;

        sampleTimer += dt;
        if (sampleTimer >= sampleInterval)
        {
            sampleTimer = 0f;
            AddSample(motionTime, pendulum.angleX, pendulum.currentTension, pendulum.totalEnergy);
        }

        coverageTimer += dt;
        if (coverageTimer >= coverageInterval && paint != null)
        {
            coverageTimer = 0f;
            paintAreaCoverage = paint.GetPaintAreaCoverage();
        }
    }

    void AddSample(float t, float theta, float tension, float energy)
    {
        timeHistory.Add(t); thetaHistory.Add(theta);
        tensionHistory.Add(tension); energyHistory.Add(energy);
        if (timeHistory.Count > maxSamples)
        {
            timeHistory.RemoveAt(0); thetaHistory.RemoveAt(0);
            tensionHistory.RemoveAt(0); energyHistory.RemoveAt(0);
        }
    }

    public void ResetAll()
    {
        if (pendulum != null) pendulum.ResetSimulation();
        if (paint != null) { paint.RefillPaint(); paint.Clear(); }
        ResetData();
        if (pendulum != null) lastBucketPos = pendulum.transform.position;
    }

    void ResetData()
    {
        timeHistory.Clear(); thetaHistory.Clear(); tensionHistory.Clear(); energyHistory.Clear();
        motionTime = 0f; trajectoryLength = 0f; pathCount = 0; paintAreaCoverage = 0f;
        sampleTimer = 0f; coverageTimer = 0f; lastAngleSign = 0f;
    }

    //  export
    public void ExportCSV()
    {
        var sb = new StringBuilder();
        sb.AppendLine("time_s,theta_deg,tension_N,energy_J");
        for (int i = 0; i < timeHistory.Count; i++)
            sb.AppendLine($"{timeHistory[i]:F3},{thetaHistory[i]:F4},{tensionHistory[i]:F4},{energyHistory[i]:F4}");
        sb.AppendLine();
        sb.AppendLine("summary");
        sb.AppendLine($"motionTime_s,{motionTime:F2}");
        sb.AppendLine($"trajectoryLength_m,{trajectoryLength:F3}");
        sb.AppendLine($"pathCount,{pathCount}");
        sb.AppendLine($"paintAreaCoverage,{paintAreaCoverage:F5}");
        WriteFile("SimReport", "csv", sb.ToString());
    }

    public void ExportJSON()
    {
        var r = new SimReport();
        if (pendulum != null)
        {
            r.L = pendulum.L; r.initialAngleDeg = pendulum.initialAngleDeg; r.initialAngVel = pendulum.initialAngVel;
            r.g = pendulum.g; r.emptyMass = pendulum.emptyMass; r.initialPaintMass = pendulum.initialPaintMass;
            r.flowRate = pendulum.flowRate; r.finalMass = pendulum.displayMass; r.theoreticalPeriod = pendulum.theoreticalPeriod;
            r.initialSpinRate = pendulum.initialSpinRate;
        }
        if (paint != null)
        {
            r.viscosity = paint.viscosity; r.temperature = paint.temperature; r.humidity = paint.humidity;
            r.surface = paint.surface.ToString(); r.holeShape = paint.holeShape.ToString();
        }
        r.motionTime = motionTime; r.trajectoryLength = trajectoryLength;
        r.paintAreaCoverage = paintAreaCoverage; r.pathCount = pathCount;
        WriteFile("SimReport", "json", JsonUtility.ToJson(r, true));
    }

    void WriteFile(string name, string ext, string content)
    {
        string path = $"{Application.persistentDataPath}/{name}_{System.DateTime.Now:yyyyMMdd_HHmmss}.{ext}";
        System.IO.File.WriteAllText(path, content);
        Debug.Log("Exported: " + path);
    }

    [System.Serializable]
    public class SimReport
    {
        public float L, initialAngleDeg, initialAngVel, g, emptyMass, initialPaintMass, flowRate, initialSpinRate;
        public float viscosity, temperature, humidity;
        public string surface, holeShape;
        public float motionTime, trajectoryLength, paintAreaCoverage, finalMass, theoreticalPeriod;
        public int pathCount;
    }

    // ========================================================================
    //  UI — modern dark IMGUI theme, fully procedural (no asset dependencies).
    //  Rounded backgrounds are runtime-generated 9-slice textures; all controls
    //  from the old panel are preserved (same tabs, sliders, buttons, stats),
    //  just restyled and grouped into labelled sections + cards.
    // ========================================================================

    // -- palette --
    static readonly Color ColBg      = new Color(0.078f, 0.086f, 0.106f, 0.965f); // panel background
    static readonly Color ColCard    = new Color(1f, 1f, 1f, 0.05f);              // section card
    static readonly Color ColBtn     = new Color(0.165f, 0.188f, 0.235f, 1f);
    static readonly Color ColBtnHot  = new Color(0.225f, 0.255f, 0.318f, 1f);
    static readonly Color ColAccent  = new Color(0.290f, 0.620f, 1f, 1f);         // #4A9EFF
    static readonly Color ColText    = new Color(0.910f, 0.925f, 0.950f, 1f);
    static readonly Color ColMuted   = new Color(0.580f, 0.627f, 0.702f, 1f);
    static readonly Color ColGood    = new Color(0.204f, 0.827f, 0.600f, 1f);
    static readonly Color ColWarn    = new Color(0.984f, 0.749f, 0.141f, 1f);
    static readonly Color ColBad     = new Color(0.973f, 0.443f, 0.443f, 1f);

    Texture2D texPanel, texCard, texBtn, texBtnHot, texBtnOn, texTrack, texThumb, texWhite;
    GUIStyle stPanel, stCard, stTitle, stSection, stLabel, stMuted, stValue, stBtn, stTab,
             stToggleOn, stToggleOff, stTrack, stThumb, stPill, stSwatch, stField;
    bool uiBuilt;

    // Raw text being typed into a numeric box, keyed by the control name, so partial input
    // like "0." or "-" survives until the value parses (see NumberBox).
    readonly Dictionary<string, string> fieldBuffers = new Dictionary<string, string>();

    // Rounded-rectangle RGBA texture; used as a 9-slice so any control size keeps crisp corners.
    static Texture2D Rounded(int size, int radius, Color fill)
    {
        var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float r = radius;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            // distance from the nearest corner circle centre (only corners get clipped)
            float dx = Mathf.Max(0, Mathf.Max(r - x, x - (size - 1 - r)));
            float dy = Mathf.Max(0, Mathf.Max(r - y, y - (size - 1 - r)));
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float a = Mathf.Clamp01(r - d + 1f); // 1px soft edge
            t.SetPixel(x, y, new Color(fill.r, fill.g, fill.b, fill.a * a));
        }
        t.filterMode = FilterMode.Bilinear;
        t.wrapMode = TextureWrapMode.Clamp;
        t.hideFlags = HideFlags.HideAndDontSave;
        t.Apply();
        return t;
    }

    // 16x16 texture with a thin rounded bar centred vertically — the slider TRACK. The slider
    // control itself is 14 px tall (matching the thumb) but the visible track stays a slim 6 px.
    static Texture2D TrackTex(Color fill)
    {
        const int size = 16, barTop = 5, barH = 6; // bar occupies y = 5..10
        float r = barH * 0.5f;
        var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float by = y - barTop; // position inside the bar band
            float dx = Mathf.Max(0, Mathf.Max(r - x, x - (size - 1 - r)));
            float dy = Mathf.Max(0, Mathf.Max(r - by, by - (barH - 1 - r)));
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float a = (by < -0.5f || by > barH - 0.5f) ? 0f : Mathf.Clamp01(r - d + 1f);
            t.SetPixel(x, y, new Color(fill.r, fill.g, fill.b, fill.a * a));
        }
        t.filterMode = FilterMode.Bilinear;
        t.wrapMode = TextureWrapMode.Clamp;
        t.hideFlags = HideFlags.HideAndDontSave;
        t.Apply();
        return t;
    }

    void BuildUI()
    {
        if (uiBuilt && texPanel != null) return;
        uiBuilt = true;

        texPanel  = Rounded(32, 10, ColBg);
        texCard   = Rounded(24, 7, ColCard);
        texBtn    = Rounded(24, 7, ColBtn);
        texBtnHot = Rounded(24, 7, ColBtnHot);
        texBtnOn  = Rounded(24, 7, ColAccent);
        texTrack  = TrackTex(new Color(1f, 1f, 1f, 0.16f));
        texThumb  = Rounded(16, 8, ColAccent);
        texWhite  = Rounded(12, 4, Color.white);

        var slice = new RectOffset(11, 11, 11, 11);
        var sliceS = new RectOffset(8, 8, 8, 8);

        stPanel = new GUIStyle { normal = { background = texPanel }, border = slice,
                                 padding = new RectOffset(14, 14, 12, 12) };
        stCard  = new GUIStyle { normal = { background = texCard }, border = sliceS,
                                 padding = new RectOffset(10, 10, 8, 8),
                                 margin = new RectOffset(0, 0, 2, 6) };

        // Font sizes bumped (12 -> 14/15) for comfortable reading of the panel.
        stTitle = new GUIStyle { fontSize = 17, fontStyle = FontStyle.Bold,
                                 normal = { textColor = ColText } };
        stSection = new GUIStyle { fontSize = 13, fontStyle = FontStyle.Bold,
                                   normal = { textColor = ColAccent },
                                   margin = new RectOffset(2, 0, 10, 3) };
        stLabel = new GUIStyle { fontSize = 14, normal = { textColor = ColText },
                                 alignment = TextAnchor.MiddleLeft, wordWrap = false };
        stMuted = new GUIStyle(stLabel) { fontSize = 13, normal = { textColor = ColMuted },
                                          wordWrap = true };
        stValue = new GUIStyle(stLabel) { alignment = TextAnchor.MiddleRight,
                                          normal = { textColor = ColAccent },
                                          fontStyle = FontStyle.Bold };

        stBtn = new GUIStyle { fontSize = 14, alignment = TextAnchor.MiddleCenter,
                               normal  = { background = texBtn, textColor = ColText },
                               hover   = { background = texBtnHot, textColor = Color.white },
                               active  = { background = texBtnOn, textColor = Color.white },
                               border = sliceS, padding = new RectOffset(8, 8, 6, 6),
                               margin = new RectOffset(2, 2, 2, 2) };

        // Typeable numeric value box (right-aligned, accent colour like the old value label).
        stField = new GUIStyle { fontSize = 14, alignment = TextAnchor.MiddleRight,
                                 normal  = { background = texBtn, textColor = ColAccent },
                                 hover   = { background = texBtnHot, textColor = ColAccent },
                                 focused = { background = texBtnHot, textColor = Color.white },
                                 fontStyle = FontStyle.Bold,
                                 border = sliceS, padding = new RectOffset(6, 6, 3, 3),
                                 margin = new RectOffset(2, 2, 2, 2) };

        stTab = new GUIStyle(stBtn)
        {
            onNormal = { background = texBtnOn, textColor = Color.white },
            onHover  = { background = texBtnOn, textColor = Color.white },
            fontStyle = FontStyle.Bold,
        };

        stToggleOn  = new GUIStyle(stBtn) { alignment = TextAnchor.MiddleLeft,
                                            normal = { background = texBtn, textColor = ColGood },
                                            hover  = { background = texBtnHot, textColor = ColGood } };
        stToggleOff = new GUIStyle(stBtn) { alignment = TextAnchor.MiddleLeft,
                                            normal = { background = texBtn, textColor = ColMuted },
                                            hover  = { background = texBtnHot, textColor = ColText } };

        stTrack = new GUIStyle { normal = { background = texTrack }, border = new RectOffset(6, 6, 0, 0),
                                 fixedHeight = 14, margin = new RectOffset(0, 0, 3, 0), stretchWidth = true };
        stThumb = new GUIStyle { normal = { background = texThumb }, hover = { background = texThumb },
                                 fixedWidth = 14, fixedHeight = 14 };

        stPill = new GUIStyle { fontSize = 12, fontStyle = FontStyle.Bold,
                                alignment = TextAnchor.MiddleCenter,
                                normal = { background = texBtn, textColor = ColText },
                                border = sliceS, padding = new RectOffset(8, 8, 3, 3),
                                margin = new RectOffset(2, 2, 2, 2) };
        stSwatch = new GUIStyle { normal = { background = texWhite }, border = new RectOffset(4, 4, 4, 4) };
    }

    // -- small helpers ------------------------------------------------------

    void Section(string title) => GUILayout.Label(title.ToUpper(), stSection);

    void BeginCard() => GUILayout.BeginVertical(stCard);
    void EndCard()   => GUILayout.EndVertical();

    void Line(string text)                 => GUILayout.Label(text, stMuted);
    void Row(string label, string value)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, stMuted);
        GUILayout.FlexibleSpace();
        GUILayout.Label(value, stValue);
        GUILayout.EndHorizontal();
    }

    bool Toggle(bool v, string label)
    {
        if (GUILayout.Button((v ? "●  " : "○  ") + label, v ? stToggleOn : stToggleOff)) v = !v;
        return v;
    }

    // Compact numeric formatting for the slider value column.
    static string Fmt(float v)
    {
        float a = Mathf.Abs(v);
        if (a >= 1000f) return v.ToString("F0");
        if (a >= 100f)  return v.ToString("F1");
        if (a >= 10f)   return v.ToString("F2");
        return v.ToString("F3");
    }

    // Editable numeric box shared by Slider/IntField: shows the live value, but the moment it has
    // keyboard focus the RAW typed string is kept (fieldBuffers) so partial input like "0." or "-"
    // is not reformatted away mid-keystroke. Any parseable number is applied immediately, clamped
    // to [min, max] — this is what lets the user TYPE any exact value instead of hunting a slider.
    float NumberBox(string key, float val, float min, float max, float width = 74f)
    {
        GUI.SetNextControlName(key);
        bool focused = GUI.GetNameOfFocusedControl() == key;
        string shown = (focused && fieldBuffers.TryGetValue(key, out string buf)) ? buf : Fmt(val);
        string typed = GUILayout.TextField(shown, stField, GUILayout.Width(width));
        if (focused)
        {
            fieldBuffers[key] = typed;
            string norm = typed.Replace(',', '.');
            if (float.TryParse(norm, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                val = Mathf.Clamp(parsed, min, max);
        }
        else fieldBuffers.Remove(key);
        return val;
    }

    // Slider + typeable value box: drag for coarse control, or click the number and type the exact
    // value you want.
    float Slider(string label, float val, float min, float max)
    {
        GUILayout.BeginHorizontal(GUILayout.Height(24));
        GUILayout.Label(label, stLabel, GUILayout.Width(180));
        val = GUILayout.HorizontalSlider(val, min, max, stTrack, stThumb, GUILayout.ExpandWidth(true));
        val = NumberBox(label, val, min, max);
        GUILayout.EndHorizontal();
        return val;
    }

    // Label + typeable INTEGER box (no slider) — e.g. the exact particle count.
    int IntField(string label, int val, int min, int max)
    {
        GUILayout.BeginHorizontal(GUILayout.Height(24));
        GUILayout.Label(label, stLabel);
        GUILayout.FlexibleSpace();
        GUI.SetNextControlName(label);
        bool focused = GUI.GetNameOfFocusedControl() == label;
        string shown = (focused && fieldBuffers.TryGetValue(label, out string buf)) ? buf : val.ToString();
        string typed = GUILayout.TextField(shown, stField, GUILayout.Width(90));
        if (focused)
        {
            fieldBuffers[label] = typed;
            if (int.TryParse(typed, out int parsed)) val = Mathf.Clamp(parsed, min, max);
        }
        else fieldBuffers.Remove(label);
        GUILayout.EndHorizontal();
        return val;
    }

    // Small colour preview chip.
    void Swatch(Color c)
    {
        Color prev = GUI.color;
        GUI.color = new Color(c.r, c.g, c.b, 1f);
        GUILayout.Label(GUIContent.none, stSwatch, GUILayout.Width(38), GUILayout.Height(14));
        GUI.color = prev;
    }

    // FPS pill with traffic-light colouring.
    void FpsPill()
    {
        Color prev = GUI.color;
        GUI.color = smoothedFps >= 45f ? ColGood : (smoothedFps >= 25f ? ColWarn : ColBad);
        GUILayout.Label($"{smoothedFps:F0} FPS", stPill, GUILayout.Width(64));
        GUI.color = prev;
    }

    // -- main panel ----------------------------------------------------------

    void OnGUI()
    {
        BuildUI();

        // Floating show/hide control (always visible).
        if (GUI.Button(ToggleButtonRect, showPanel ? "Hide panel" : "Show panel", stBtn))
            showPanel = !showPanel;
        PanelVisible = showPanel;
        if (!showPanel) return;

        GUILayout.BeginArea(PanelRect, stPanel);

        // Header: title + live status pills (FPS + active particle count).
        GUILayout.BeginHorizontal();
        GUILayout.Label("SWINGING PAINT BUCKET", stTitle);
        GUILayout.FlexibleSpace();
        if (paint != null) GUILayout.Label($"{paint.activeParticles} pts", stPill);
        FpsPill();
        GUILayout.EndHorizontal();
        GUILayout.Space(8);

        currentTab = GUILayout.Toolbar(currentTab, new[] { "Pendulum", "Paint", "Output", "Compare" },
                                       stTab, GUILayout.Height(30));
        GUILayout.Space(8);
        scroll = GUILayout.BeginScrollView(scroll);

        if (currentTab == 0 && pendulum != null) DrawPendulumTab();
        else if (currentTab == 1 && paint != null) DrawPaintTab();
        else if (currentTab == 2) DrawOutputTab();
        else if (currentTab == 3) DrawCompareTab();

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    void DrawPendulumTab()
    {
        Section("Rope & launch");
        pendulum.L = Slider("Rope length L (m)", pendulum.L, 0.5f, 5f);
        pendulum.initialAngleDeg = Slider("Release angle (deg)", pendulum.initialAngleDeg, 5f, 90f);
        pendulum.initialAngVel = Slider("Azimuthal push (rad/s)", pendulum.initialAngVel, -5f, 5f);
        pendulum.initialPolarVel = Slider("Polar push (rad/s)", pendulum.initialPolarVel, -3f, 3f);
        // Same initial state expressed as LINEAR speeds (v = ω·L): type the launch speed in m/s
        // directly and the matching angular rates above are set for you (applied on Reset).
        float Lsafe = Mathf.Max(0.5f, pendulum.L);
        float vPlane = Slider("Initial speed in-plane (m/s)", pendulum.initialPolarVel * Lsafe,
                              -3f * Lsafe, 3f * Lsafe);
        float vSide  = Slider("Initial speed sideways (m/s)", pendulum.initialAngVel * Lsafe,
                              -5f * Lsafe, 5f * Lsafe);
        pendulum.initialPolarVel = vPlane / Lsafe;
        pendulum.initialAngVel   = vSide  / Lsafe;
        pendulum.releaseDirectionDeg = Slider("Swing direction (Reset)", pendulum.releaseDirectionDeg, 0f, 360f);
        pendulum.maxSwings = Mathf.RoundToInt(Slider("Max swings (0=inf)", pendulum.maxSwings, 0f, 40f));

        Section("Bucket spin (about rope axis)");
        pendulum.initialSpinRate = Slider("Initial spin (rad/s, Reset)", pendulum.initialSpinRate, -15f, 15f);
        pendulum.ropeTorsionStiffness = Slider("Rope torsion k (N*m/rad)", pendulum.ropeTorsionStiffness, 0f, 0.5f);
        Row("Live spin", $"{pendulum.spinRate:F2} rad/s   twist {pendulum.spinAngleDeg:F0} deg");

        Section("Bucket & paint");
        pendulum.emptyMass = Slider("Empty mass (kg)", pendulum.emptyMass, 0.3f, 2f);
        pendulum.initialPaintMass = Slider("Paint mass (kg)", pendulum.initialPaintMass, 0.5f, 10f);
        // Physically the fraction of the hole area that is open (Torricelli discharge scales with it).
        pendulum.flowRate = Slider("Valve opening (0-1)", pendulum.flowRate, 0.05f, 1f);

        Section("Environment");
        pendulum.g = Slider("Gravity g", pendulum.g, 1.6f, 24f);
        pendulum.airDensity = Slider("Air density", pendulum.airDensity, 0.5f, 1.5f);
        pendulum.dragCoef = Slider("Drag coef", pendulum.dragCoef, 0.8f, 1.2f);
        pendulum.area = Slider("Frontal area (m2)", pendulum.area, 0.01f, 2.5f);
        pendulum.friction = Slider("Pivot friction", pendulum.friction, 0f, 1f);
        pendulum.damping = Slider("Angular damping", pendulum.damping, 0f, 0.5f);
        float windX = Slider("Wind X", pendulum.windVel.x, -5f, 5f);
        float windZ = Slider("Wind Z", pendulum.windVel.z, -5f, 5f);
        pendulum.windVel = new Vector3(windX, 0f, windZ);

        Section("Rope properties");
        pendulum.ropeStiffness = Slider("Rope stiffness", pendulum.ropeStiffness, 100f, 10000f);
        pendulum.ropeBreakTension = Slider("Break tension", pendulum.ropeBreakTension, 0f, 500f);

        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        pendulum.ropeIsElastic = Toggle(pendulum.ropeIsElastic, "Elastic rope");
        pendulum.useBuoyancy = Toggle(pendulum.useBuoyancy, "Buoyancy");
        GUILayout.EndHorizontal();
        pendulum.validationMode = Toggle(pendulum.validationMode, "Validation mode");
    }

    // Particle count (brief §6): the pool/grid are pre-allocated at the hard cap, so the count can
    // be ANY number the user types (100..10,000) — the paint mass splits exactly across the drops
    // (each = initialPaintMass/N), so bucket paint == emitted paint always. The three buttons are
    // just shortcuts to common values.
    void DrawPerformanceModes()
    {
        Section("Particles / performance");
        BeginCard();
        Row("Active (inside / air)",
            $"{paint.activeParticles}  ({paint.insideParticles} / {paint.airborneParticles})");
        Row("Budget", $"{paint.maxParticles}");
        Row("Avg SPH neighbours", $"{paint.avgNeighbors:F1}");
        Row("1 drop", $"{paint.lastDropMassKg * 1000f:F2} g,  D {paint.lastDropDiameter * 1000f:F1} mm");
        EndCard();
        paint.reservoirParticles =
            IntField("Drops in full bucket (type any number)", paint.reservoirParticles, 100, 10000);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("2000", stBtn))  SetParticleMode(2000);
        if (GUILayout.Button("5000", stBtn))  SetParticleMode(5000);
        if (GUILayout.Button("10000", stBtn)) SetParticleMode(10000);
        GUILayout.EndHorizontal();
        paint.enableParticleInteraction =
            Toggle(paint.enableParticleInteraction, "SPH particle interaction");
    }

    // Sets how many drops fill the bucket; the soft budget auto-raises to fit the reservoir
    // plus its falling stream (BucketEmission.UpdateDropAccounting).
    void SetParticleMode(int count)
    {
        paint.reservoirParticles = count;
    }

    void DrawPaintTab()
    {
        DrawPerformanceModes();

        Section("Fluid");
        paint.viscosity = Slider("Viscosity (x paint)", paint.viscosity, 0.2f, 3f);
        paint.temperature = Slider("Temperature (C)", paint.temperature, 0f, 50f);
        paint.humidity = Slider("Humidity (%)", paint.humidity, 0f, 100f);

        Section("Hole & bucket");
        // Millimetre-scale holes: the Torricelli discharge makes the flow rate follow the hole
        // AREA physically, so centimetre holes empty the bucket in a blink.
        paint.holeRadius = Slider("Hole radius (m)", paint.holeRadius, 0.002f, 0.03f);
        paint.holeHeight = Slider("Hole height (0-1 of H)", paint.holeHeight, 0f, 1f);
        paint.bucketRadius = Slider("Bucket half-width (m)", paint.bucketRadius, 0.05f, 0.75f);
        paint.bucketHeightMeters = Slider("Bucket height (m)", paint.bucketHeightMeters, 0.2f, 1.5f);
        GUILayout.Label("Hole shape (areas differ -> pour rates differ)", stMuted);
        paint.holeShape = (HoleShape)GUILayout.Toolbar((int)paint.holeShape,
            new[] { "Round", "Narrow", "Wide", "Multi" }, stTab, GUILayout.Height(28));
        BeginCard();
        Row("Pour", $"{paint.currentMassFlow * 1000f:F1} g/s   ({paint.currentEmissionRate:F0} drops/s)");
        Row("Exit speed / fill", $"{paint.currentExitSpeed:F2} m/s   /   {paint.paintLevel * 100f:F2} %");
        EndCard();

        Section("Canvas / floor");
        paint.canvasTiltControlDeg = Slider("Floor pitch", paint.canvasTiltControlDeg, -80f, 80f);
        paint.canvasTiltRollDeg = Slider("Floor roll", paint.canvasTiltRollDeg, -80f, 80f);
        Line("(or right-drag the mouse to tilt)");
        paint.canvasWidthMeters = Slider("Canvas width (m)", paint.canvasWidthMeters, 5f, 100f);
        paint.canvasHeightMeters = Slider("Canvas height (m)", paint.canvasHeightMeters, 5f, 100f);
        GUILayout.Label("Surface", stMuted);
        paint.surface = (SurfaceType)GUILayout.Toolbar((int)paint.surface,
            new[] { "Canvas", "Wood", "Metal", "Paper" }, stTab, GUILayout.Height(28));

        Section("Environment & effects");
        paint.surfaceVibration = Toggle(paint.surfaceVibration, "Surface vibration");
        paint.vibrationAmplitude = Slider("Vibration amp (m)", paint.vibrationAmplitude, 0f, 0.5f);
        paint.vibrationFrequency = Slider("Vibration freq (Hz)", paint.vibrationFrequency, 0f, 20f);
        GUILayout.BeginHorizontal();
        paint.continuousJetMode = Toggle(paint.continuousJetMode, "Continuous jet");
        paint.crownSplashEnabled = Toggle(paint.crownSplashEnabled, "Crown splash");
        GUILayout.EndHorizontal();

        DrawSurfacePhysicsReadout();

        Section("Colours");
        paint.multiColorMode = Toggle(paint.multiColorMode, "Multi-colour (cycles 3 colours per swing)");
        GUILayout.BeginHorizontal();
        GUILayout.Label("Paint colour 1 (RGB)", stMuted);
        GUILayout.FlexibleSpace();
        Swatch(paint.paintColor);
        GUILayout.EndHorizontal();
        float rr = Slider("R", paint.paintColor.r, 0f, 1f);
        float gg = Slider("G", paint.paintColor.g, 0f, 1f);
        float bb = Slider("B", paint.paintColor.b, 0f, 1f);
        paint.paintColor = new Color(rr, gg, bb);
        if (paint.multiColorMode)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Colour 2", stMuted);
            GUILayout.FlexibleSpace();
            Swatch(paint.paintColor2);
            GUILayout.EndHorizontal();
            float r2 = Slider("R2", paint.paintColor2.r, 0f, 1f);
            float g2 = Slider("G2", paint.paintColor2.g, 0f, 1f);
            float b2 = Slider("B2", paint.paintColor2.b, 0f, 1f);
            paint.paintColor2 = new Color(r2, g2, b2);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Colour 3", stMuted);
            GUILayout.FlexibleSpace();
            Swatch(paint.paintColor3);
            GUILayout.EndHorizontal();
            float r3 = Slider("R3", paint.paintColor3.r, 0f, 1f);
            float g3 = Slider("G3", paint.paintColor3.g, 0f, 1f);
            float b3 = Slider("B3", paint.paintColor3.b, 0f, 1f);
            paint.paintColor3 = new Color(r3, g3, b3);
        }
    }

    // Live, physics-derived readout for the currently selected surface (no cosmetic values).
    void DrawSurfacePhysicsReadout()
    {
        if (paint == null) return;
        var sp = SurfacePreset.From(paint.surface);
        float youngRad  = sp.contactAngleDeg * Mathf.Deg2Rad;
        float cosYoung  = Mathf.Cos(youngRad);                                  // intrinsic pore-wall angle
        float cosStar   = FluidConstants.WenzelCos(sp.wenzelRoughness, youngRad); // apparent (Wenzel) angle
        float thetaStar = Mathf.Acos(Mathf.Clamp(cosStar, -1f, 1f)) * Mathf.Rad2Deg;

        float sinA = Mathf.Sin(paint.canvasTiltControlDeg * Mathf.Deg2Rad);
        const float hRef = 1e-4f; // 0.1 mm reference film thickness for the live readout
        // Washburn uses the intrinsic Young angle (capillary rise inside the pores), matching UpdateSplatAbsorption.
        float washburn1s = FluidConstants.WashburnDepth(
            sp.poreRadiusMeters, paint.surfaceTension, cosYoung, paint.paintViscosityPaS, 1f);
        float uFilm = FluidConstants.NusseltFilmVelocity(
            paint.density, paint.gravity, sinA, hRef, paint.paintViscosityPaS);
        float hcrit = FluidConstants.CriticalFilmThickness(
            paint.surfaceTension, paint.density, paint.gravity, sinA);

        Section("Surface physics (live)");
        BeginCard();
        Row("theta_Young / theta_Wenzel", $"{sp.contactAngleDeg:F0}° / {thetaStar:F1}°");
        Row("Ra / Porosity", $"{sp.arithmeticRoughnessUm:F1} um / {sp.porosity * 100f:F0} %");
        Row("Drop Ø (Tate ref)", $"{paint.lastDropDiameter * 1000f:F2} mm ({paint.tateDropDiameter * 1000f:F2} mm)");
        float kThreshold = FluidConstants.SplashThresholdRough(sp.arithmeticRoughnessUm);
        Row("K / Kc (Stow-Hadfield)",
            $"{paint.K:F1} / {kThreshold:F1} -> {(paint.K > kThreshold ? "SPLASH" : "deposition")}");
        Row("Washburn depth (1 s)", $"{washburn1s * 1000f:F3} mm");
        Row("Film velocity", $"{uFilm * 1000f:F3} mm/s");
        string hcritTxt = float.IsInfinity(hcrit) ? "-- (horizontal)" : $"{hcrit * 1e6f:F1} um";
        Row("Drip threshold h_c", hcritTxt);
        Row("Flow", $"tilt {paint.canvasTiltControlDeg:F0}° -> {(sinA < 1e-3f ? "STATIC" : "flow-capable")}");
        Row("Scale", $"{paint.pixelsPerUnit:F0} px/m ({paint.canvasMetersWidth:F2} m wide)");
        EndCard();
    }

    void DrawOutputTab()
    {
        Section("Actions");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Reset", stBtn)) ResetAll();
        if (paint != null && GUILayout.Button("Clear", stBtn)) paint.Clear();
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (paint != null && GUILayout.Button("Save PNG", stBtn)) paint.SavePainting();
        if (paint != null && GUILayout.Button("Refill", stBtn)) paint.RefillPaint();
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Export CSV", stBtn)) ExportCSV();
        if (GUILayout.Button("Export JSON", stBtn)) ExportJSON();
        GUILayout.EndHorizontal();
        if (GUILayout.Button("Capture experiment (see Compare tab)", stBtn)) CaptureExperiment();

        Section("Report values");
        BeginCard();
        if (paint != null)
        {
            Row("FPS", $"{smoothedFps:F0}");
            Row("Active particles", $"{paint.activeParticles}");
            Row("Inside bucket / airborne", $"{paint.insideParticles} / {paint.airborneParticles}");
            Row("Avg SPH neighbours", $"{paint.avgNeighbors:F1}");
        }
        Row("Motion time", $"{motionTime:F1} s");
        Row("Paths", $"{pathCount}");
        Row("Trajectory length", $"{trajectoryLength:F2} m");
        Row("Colour coverage", $"{paintAreaCoverage * 100f:F2} %");
        EndCard();

        if (pendulum != null)
        {
            Section("Pendulum readout (spherical)");
            BeginCard();
            Row("Polar angle θ", $"{pendulum.polarAngleDeg:F1}°");
            Row("Azimuth φ", $"{pendulum.azimuthDeg:F1}°");
            Row("Precession rate φ̇", $"{pendulum.azimuthalRate:F3} rad/s");
            Row("Ang. momentum Lz", $"{pendulum.angularMomentumY:F3} kg·m²/s");
            Row("Bucket spin", $"{pendulum.spinRate:F2} rad/s ({pendulum.spinAngleDeg:F0}°)");
            Row("Mass m(t)", $"{pendulum.displayMass:F3} kg");
            Row("Tension", $"{pendulum.currentTension:F2} N");
            Row("Total energy", $"{pendulum.totalEnergy:F2} J");
            Row("Period", $"{pendulum.theoreticalPeriod:F3} s");
            EndCard();
        }
    }

    // Snapshot the current inputs + measured outputs so several runs can be compared side by side.
    void CaptureExperiment()
    {
        experiments.Add(new ExperimentSnapshot
        {
            label      = "Run " + (experiments.Count + 1),
            L          = pendulum != null ? pendulum.L : 0f,
            angle      = pendulum != null ? pendulum.initialAngleDeg : 0f,
            surface    = paint != null ? paint.surface.ToString() : "-",
            viscosity  = paint != null ? paint.viscosity : 0f,
            motionTime = motionTime,
            paths      = pathCount,
            trajectory = trajectoryLength,
            coverage   = paintAreaCoverage
        });
    }

    void DrawCompareTab()
    {
        Section("Compare experiments (PDF §5.6)");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Capture current", stBtn)) CaptureExperiment();
        if (GUILayout.Button("Clear list", stBtn)) experiments.Clear();
        GUILayout.EndHorizontal();
        GUILayout.Space(4);

        if (experiments.Count == 0)
        {
            BeginCard();
            Line("No experiments captured yet.");
            Line("Run a simulation, then press 'Capture current'.");
            EndCard();
            return;
        }

        foreach (var e in experiments)
        {
            BeginCard();
            GUILayout.Label(e.label, stTitle);
            Row("Setup", $"L={e.L:F1} m   angle={e.angle:F0}°   {e.surface}   visc={e.viscosity:F2}");
            Row("Result", $"{e.motionTime:F1} s   {e.paths} paths   {e.trajectory:F1} m   {e.coverage * 100f:F1}%");
            EndCard();
        }
    }
}
