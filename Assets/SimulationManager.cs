using System.Collections.Generic;
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
    private Vector3 lastBucketPos;
    private float lastAngleSign;
    private bool showPanel = true;
    private Vector2 scroll;
    private int fontSize = 17;
    private int currentTab = 0;
    private int selectedPaletteSlot = 0;
    private readonly List<ExperimentRecord> experiments = new List<ExperimentRecord>();
    private int experimentCounter = 0;

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
        if (pendulum != null)
        {
            sb.AppendLine($"releaseDirection_deg,{pendulum.releaseDirectionDeg:F1}");
            sb.AppendLine($"maxSwings,{pendulum.maxSwings}");
            sb.AppendLine($"friction,{pendulum.friction:F2}");
        }
        if (paint != null)
        {
            sb.AppendLine($"canvas_w,{paint.canvasSize.x:F2}");
            sb.AppendLine($"canvas_h,{paint.canvasSize.y:F2}");
            sb.AppendLine($"canvasTilt_deg,{paint.canvasTiltDeg:F1}");
            sb.AppendLine($"colors,{(paint.multiColor ? paint.palette.Count : 1)}");
        }
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
            r.releaseDirectionDeg = pendulum.releaseDirectionDeg; r.maxSwings = pendulum.maxSwings;
            r.friction = pendulum.friction; r.airDensity = pendulum.airDensity;
            r.dragCoef = pendulum.dragCoef; r.area = pendulum.area;
        }
        if (paint != null)
        {
            r.viscosity = paint.viscosity; r.temperature = paint.temperature; r.humidity = paint.humidity;
            r.surface = paint.surface.ToString(); r.holeShape = paint.holeShape.ToString();
            r.canvasWidth = paint.canvasSize.x; r.canvasHeight = paint.canvasSize.y; r.canvasTiltDeg = paint.canvasTiltDeg;
            r.multiColor = paint.multiColor; r.colorCount = paint.multiColor ? paint.palette.Count : 1;
            r.paintRebounds = paint.paintRebounds; r.restitution = paint.restitution; r.surfaceFriction = paint.surfaceFriction;
            r.surfaceVibration = paint.surfaceVibration; r.vibrationFrequency = paint.vibrationFrequency; r.vibrationAmplitude = paint.vibrationAmplitude;
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

    //  experiment comparison
    public void CaptureExperiment()
    {
        if (paint != null) paintAreaCoverage = paint.GetPaintAreaCoverage();

        experimentCounter++;
        var rec = new ExperimentRecord { label = $"Exp {experimentCounter}" };

        var sb = new StringBuilder();
        if (pendulum != null)
        {
            sb.AppendLine($"L={pendulum.L:F2}  angle={pendulum.initialAngleDeg:F0}  dir={pendulum.releaseDirectionDeg:F0}");
            sb.AppendLine($"angVel={pendulum.initialAngVel:F2}  maxSwings={pendulum.maxSwings}");
            sb.AppendLine($"g={pendulum.g:F2}  friction={pendulum.friction:F2}  drag={pendulum.dragCoef:F2}");
            sb.AppendLine($"emptyMass={pendulum.emptyMass:F2}  paint={pendulum.initialPaintMass:F2}  flow={pendulum.flowRate:F3}");
        }
        if (paint != null)
        {
            sb.AppendLine($"visc={paint.viscosity:F2}  temp={paint.temperature:F0}  humid={paint.humidity:F0}");
            sb.AppendLine($"surface={paint.surface}  hole={paint.holeShape}");
            sb.AppendLine($"canvas={paint.canvasSize.x:F1}x{paint.canvasSize.y:F1}  tilt={paint.canvasTiltDeg:F0}");
            sb.AppendLine($"colors={(paint.multiColor ? paint.palette.Count : 1)}");
        }
        rec.inputs = sb.ToString();

        rec.motionTime = motionTime;
        rec.trajectoryLength = trajectoryLength;
        rec.paintAreaCoverage = paintAreaCoverage;
        rec.pathCount = pathCount;
        if (paint != null) rec.thumbnail = MakeThumbnail(paint.GetPaintTexture(), 110);

        experiments.Add(rec);
        currentTab = 3;
    }

    Texture2D MakeThumbnail(Texture2D src, int size)
    {
        if (src == null) return null;
        var t = new Texture2D(size, size, TextureFormat.RGB24, false);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                t.SetPixel(x, y, src.GetPixelBilinear((x + 0.5f) / size, (y + 0.5f) / size));
        t.Apply();
        return t;
    }

    void RemoveExperiment(int i)
    {
        if (i < 0 || i >= experiments.Count) return;
        if (experiments[i].thumbnail != null) Destroy(experiments[i].thumbnail);
        experiments.RemoveAt(i);
    }

    void ClearExperiments()
    {
        for (int i = 0; i < experiments.Count; i++)
            if (experiments[i].thumbnail != null) Destroy(experiments[i].thumbnail);
        experiments.Clear();
    }

    public void ExportComparisonCSV()
    {
        if (experiments.Count == 0) { Debug.Log("No experiments to export."); return; }
        var sb = new StringBuilder();
        sb.AppendLine("label,motionTime_s,pathCount,trajectoryLength_m,paintAreaCoverage");
        foreach (var e in experiments)
            sb.AppendLine($"{e.label},{e.motionTime:F2},{e.pathCount},{e.trajectoryLength:F3},{e.paintAreaCoverage:F5}");
        WriteFile("Comparison", "csv", sb.ToString());
    }

    void DrawCompareTab()
    {
        GUILayout.Label($"Captured experiments: {experiments.Count}");

        bool doCapture = false, doExport = false, doClear = false;
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Capture current")) doCapture = true;
        if (GUILayout.Button("Export CSV")) doExport = true;
        if (GUILayout.Button("Clear all")) doClear = true;
        GUILayout.EndHorizontal();
        GUILayout.Space(8);

        if (experiments.Count == 0)
            GUILayout.Label("No experiments yet.\nSet inputs, let it run, then 'Capture current'.");

        int removeIndex = -1;
        for (int i = 0; i < experiments.Count; i++)
        {
            ExperimentRecord e = experiments[i];
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            if (e.thumbnail != null)
                GUILayout.Box(e.thumbnail, GUILayout.Width(110), GUILayout.Height(110));
            GUILayout.BeginVertical();
            GUILayout.Label(e.label);
            GUILayout.Label($"time = {e.motionTime:F1} s");
            GUILayout.Label($"paths = {e.pathCount}");
            GUILayout.Label($"trajectory = {e.trajectoryLength:F2} m");
            GUILayout.Label($"coverage = {e.paintAreaCoverage * 100f:F2} %");
            if (GUILayout.Button("Remove", GUILayout.Width(120))) removeIndex = i;
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.Label(e.inputs);
            GUILayout.EndVertical();
            GUILayout.Space(6);
        }

        // apply mutations after layout so IMGUI groups stay balanced
        if (doCapture) CaptureExperiment();
        if (doExport) ExportComparisonCSV();
        if (doClear) ClearExperiments();
        if (removeIndex >= 0) RemoveExperiment(removeIndex);
    }

    [System.Serializable]
    public class SimReport
    {
        public float L, initialAngleDeg, initialAngVel, g, emptyMass, initialPaintMass, flowRate;
        public float viscosity, temperature, humidity;
        public string surface, holeShape;
        public float motionTime, trajectoryLength, paintAreaCoverage, finalMass, theoreticalPeriod;
        public int pathCount;
        public float releaseDirectionDeg, friction, airDensity, dragCoef, area;
        public int maxSwings;
        public float canvasWidth, canvasHeight, canvasTiltDeg;
        public bool multiColor;
        public int colorCount;
        public bool paintRebounds, surfaceVibration;
        public float restitution, surfaceFriction, vibrationFrequency, vibrationAmplitude;
    }

    public class ExperimentRecord
    {
        public string label;
        public string inputs;
        public float motionTime, trajectoryLength, paintAreaCoverage;
        public int pathCount;
        public Texture2D thumbnail;
    }

    //  UI 
    void OnGUI()
    {
        GUI.skin.label.fontSize = fontSize;
        GUI.skin.button.fontSize = fontSize;
        GUI.skin.toggle.fontSize = fontSize;
        GUI.skin.box.fontSize = fontSize;

        if (GUI.Button(new Rect(10, 10, 180, 36), showPanel ? "Hide panel" : "Show panel"))
            showPanel = !showPanel;
        if (!showPanel) return;

        GUILayout.BeginArea(new Rect(10, 54, 440, Screen.height - 72), GUI.skin.box);
        currentTab = GUILayout.Toolbar(currentTab, new[] { "Pendulum", "Paint", "Output", "Compare" });
        GUILayout.Space(6);
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
        pendulum.L = Slider("Rope length L", pendulum.L, 0.5f, 5f);
        pendulum.initialAngleDeg = Slider("Release angle", pendulum.initialAngleDeg, 5f, 90f);
        pendulum.initialAngVel = Slider("Initial ang.vel", pendulum.initialAngVel, -5f, 5f);
        pendulum.releaseDirectionDeg = Slider("Release direction", pendulum.releaseDirectionDeg, 0f, 360f);
        pendulum.maxSwings = Mathf.RoundToInt(Slider("Max swings (0=inf)", pendulum.maxSwings, 0f, 40f));
        pendulum.g = Slider("Gravity g", pendulum.g, 1.6f, 24f);
        pendulum.emptyMass = Slider("Empty mass", pendulum.emptyMass, 0.3f, 2f);
        pendulum.initialPaintMass = Slider("Paint mass", pendulum.initialPaintMass, 0.5f, 10f);
        pendulum.flowRate = Slider("Flow rate", pendulum.flowRate, 0.001f, 0.1f);
        pendulum.airDensity = Slider("Air density", pendulum.airDensity, 0.5f, 1.5f);
        pendulum.dragCoef = Slider("Drag coef", pendulum.dragCoef, 0.8f, 1.2f);
        pendulum.area = Slider("Area", pendulum.area, 0.02f, 0.1f);
        pendulum.friction = Slider("Friction", pendulum.friction, 0f, 1f);
        pendulum.ropeStiffness = Slider("Rope stiffness", pendulum.ropeStiffness, 100f, 10000f);
        pendulum.ropeBreakTension = Slider("Break tension", pendulum.ropeBreakTension, 0f, 500f);
        GUILayout.Space(4);
        pendulum.ropeIsElastic = GUILayout.Toggle(pendulum.ropeIsElastic, "Elastic rope");
        pendulum.useBuoyancy = GUILayout.Toggle(pendulum.useBuoyancy, "Buoyancy");
        pendulum.validationMode = GUILayout.Toggle(pendulum.validationMode, "Validation mode");
        GUILayout.Space(4);
        GUILayout.Label($"Swings: {pendulum.swingCount}" + (pendulum.motionStopped ? "  (stopped)" : ""));
        GUILayout.Label("Note: angle/direction/vel/max-swings apply after Reset.");
    }

    void DrawPaintTab()
    {
        paint.viscosity = Slider("Viscosity", paint.viscosity, 0.2f, 3f);
        paint.temperature = Slider("Temperature", paint.temperature, 0f, 50f);
        paint.humidity = Slider("Humidity", paint.humidity, 0f, 100f);
        paint.baseEmission = Slider("Emission rate", paint.baseEmission, 0f, 120f);
        paint.holeRadius = Slider("Hole radius", paint.holeRadius, 0.01f, 0.2f);
        paint.holeHeight = Slider("Hole height", paint.holeHeight, 0f, 1f);
        paint.bucketRadius = Slider("Bucket radius", paint.bucketRadius, 0.05f, 0.5f);

        GUILayout.Space(4);
        GUILayout.Label("Canvas:");
        paint.canvasSize.x = Slider("Canvas width", paint.canvasSize.x, 2f, 30f);
        paint.canvasSize.y = Slider("Canvas height", paint.canvasSize.y, 2f, 30f);
        paint.canvasTiltDeg = Slider("Canvas tilt", paint.canvasTiltDeg, 0f, 90f);

        GUILayout.Space(4);
        paint.surfaceVibration = GUILayout.Toggle(paint.surfaceVibration, "Surface vibration");
        if (paint.surfaceVibration)
        {
            paint.vibrationAmplitude = Slider("Vib amplitude", paint.vibrationAmplitude, 0f, 0.4f);
            paint.vibrationFrequency = Slider("Vib frequency", paint.vibrationFrequency, 0f, 10f);
        }

        GUILayout.Space(4);
        paint.paintRebounds = GUILayout.Toggle(paint.paintRebounds, "Paint rebound (restitution)");
        if (paint.paintRebounds)
        {
            paint.restitution = Slider("Restitution e", paint.restitution, 0f, 1f);
            paint.surfaceFriction = Slider("Surface friction", paint.surfaceFriction, 0f, 1f);
            paint.maxBounces = Mathf.RoundToInt(Slider("Max bounces", paint.maxBounces, 0f, 5f));
        }

        GUILayout.Space(4);
        paint.continuousJetMode = GUILayout.Toggle(paint.continuousJetMode, "Continuous jet");
        paint.crownSplashEnabled = GUILayout.Toggle(paint.crownSplashEnabled, "Crown splash");

        GUILayout.Space(4);
        GUILayout.Label("Surface:");
        paint.surface = (SurfaceType)GUILayout.Toolbar((int)paint.surface,
            new[] { "Canvas", "Wood", "Metal", "Paper" });
        GUILayout.Label("Hole shape:");
        paint.holeShape = (HoleShape)GUILayout.Toolbar((int)paint.holeShape,
            new[] { "Round", "Narrow", "Wide", "Multi" });

        GUILayout.Space(4);
        GUILayout.Label("Paint color (RGB):");
        float rr = Slider("R", paint.paintColor.r, 0f, 1f);
        float gg = Slider("G", paint.paintColor.g, 0f, 1f);
        float bb = Slider("B", paint.paintColor.b, 0f, 1f);
        paint.paintColor = new Color(rr, gg, bb);

        GUILayout.Space(6);
        paint.multiColor = GUILayout.Toggle(paint.multiColor, "Use multiple colors");
        if (paint.multiColor)
        {
            GUILayout.Label("Color mode:");
            paint.colorMode = (PaintColorMode)GUILayout.Toolbar((int)paint.colorMode, new[] { "Random", "Cycle" });

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("- color") && paint.palette.Count > 1)
                paint.palette.RemoveAt(paint.palette.Count - 1);
            if (GUILayout.Button("+ color") && paint.palette.Count < 8)
                paint.palette.Add(Color.white);
            GUILayout.EndHorizontal();

            if (paint.palette.Count > 0)
            {
                string[] labels = new string[paint.palette.Count];
                for (int i = 0; i < labels.Length; i++) labels[i] = (i + 1).ToString();
                selectedPaletteSlot = Mathf.Clamp(selectedPaletteSlot, 0, paint.palette.Count - 1);
                selectedPaletteSlot = GUILayout.Toolbar(selectedPaletteSlot, labels);

                Color sc = paint.palette[selectedPaletteSlot];
                float pr = Slider("Slot R", sc.r, 0f, 1f);
                float pg = Slider("Slot G", sc.g, 0f, 1f);
                float pb = Slider("Slot B", sc.b, 0f, 1f);
                paint.palette[selectedPaletteSlot] = new Color(pr, pg, pb);
            }
        }
    }

    void DrawOutputTab()
    {
        GUILayout.Label("Actions:");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Reset")) ResetAll();
        if (paint != null && GUILayout.Button("Clear")) paint.Clear();
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (paint != null && GUILayout.Button("Save PNG")) paint.SavePainting();
        if (paint != null && GUILayout.Button("Refill")) paint.RefillPaint();
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Export CSV")) ExportCSV();
        if (GUILayout.Button("Export JSON")) ExportJSON();
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Capture experiment")) CaptureExperiment();
        if (GUILayout.Button("Open Compare")) currentTab = 3;
        GUILayout.EndHorizontal();

        GUILayout.Space(10);
        GUILayout.Label("Report values:");
        GUILayout.Label($"Motion time = {motionTime:F1} s");
        GUILayout.Label($"Paths = {pathCount}");
        GUILayout.Label($"Trajectory length = {trajectoryLength:F2} m");
        GUILayout.Label($"Color coverage = {paintAreaCoverage * 100f:F2} %");
        if (pendulum != null)
        {
            GUILayout.Space(6);
            GUILayout.Label($"Mass m(t) = {pendulum.displayMass:F3} kg");
            GUILayout.Label($"Tension = {pendulum.currentTension:F2} N");
            GUILayout.Label($"Total energy = {pendulum.totalEnergy:F2} J");
            GUILayout.Label($"Period = {pendulum.theoreticalPeriod:F3} s");
        }
    }

    float Slider(string label, float val, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label($"{label}: {val:F3}", GUILayout.Width(215));
        val = GUILayout.HorizontalSlider(val, min, max);
        GUILayout.EndHorizontal();
        return val;
    }
}
