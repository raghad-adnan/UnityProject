using System.Collections.Generic;
using System.Text;
using UnityEngine;

// ============================================================
//  SimulationManager - Phase 4: UI + logging + export + reset.
//  Auto-creates itself on Play (no manual Unity setup needed).
//  Binds PendulumMotion and PaintPhysics inputs to OnGUI sliders,
//  records histories, computes trajectoryLength and paintAreaCoverage,
//  exports CSV/JSON, and provides a full Reset.
// ============================================================
public class SimulationManager : MonoBehaviour
{
    public PendulumMotion pendulum;
    public PaintPhysics paint;

    [Header("Sampling")]
    public float sampleInterval = 0.1f;   // seconds between samples
    public int maxSamples = 3000;
    public float coverageInterval = 1f;   // coverage is expensive -> once per second

    // Histories (Task 4.2)
    public readonly List<float> timeHistory = new List<float>();
    public readonly List<float> thetaHistory = new List<float>();
    public readonly List<float> tensionHistory = new List<float>();
    public readonly List<float> energyHistory = new List<float>();

    // computed outputs
    public float motionTime;
    public float trajectoryLength;
    public float paintAreaCoverage;
    public int pathCount;

    // internal
    private float sampleTimer, coverageTimer;
    private Vector3 lastBucketPos;
    private float lastAngleSign;
    private bool showPanel = true;
    private Vector2 scroll;
    private int fontSize = 15;

    // auto-create the manager after the scene loads
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

        // trajectory length = sum of bucket position deltas (Task 4.2)
        Vector3 pos = pendulum.transform.position;
        trajectoryLength += Vector3.Distance(pos, lastBucketPos);
        lastBucketPos = pos;

        // path count = sign-crossings of angleX (each half swing)
        float s = Mathf.Sign(pendulum.angleX);
        if (s != 0 && s != lastAngleSign && lastAngleSign != 0) pathCount++;
        lastAngleSign = s;

        // record samples every sampleInterval
        sampleTimer += dt;
        if (sampleTimer >= sampleInterval)
        {
            sampleTimer = 0f;
            AddSample(motionTime, pendulum.angleX, pendulum.currentTension, pendulum.totalEnergy);
        }

        // color coverage (expensive) every coverageInterval
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

    // full reset (Task 4.4)
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

    // ---------- export (Task 4.3) ----------
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
        public float L, initialAngleDeg, initialAngVel, g, emptyMass, initialPaintMass, flowRate;
        public float viscosity, temperature, humidity;
        public string surface, holeShape;
        public float motionTime, trajectoryLength, paintAreaCoverage, finalMass, theoreticalPeriod;
        public int pathCount;
    }

    // ---------- UI (Task 4.1) ----------
    void OnGUI()
    {
        // bigger, clearer font
        GUI.skin.label.fontSize = fontSize;
        GUI.skin.button.fontSize = fontSize;
        GUI.skin.toggle.fontSize = fontSize;
        GUI.skin.box.fontSize = fontSize;

        if (GUI.Button(new Rect(10, 10, 170, 32), showPanel ? "Hide panel" : "Show panel"))
            showPanel = !showPanel;
        if (!showPanel) return;

        GUILayout.BeginArea(new Rect(10, 48, 380, Screen.height - 64), GUI.skin.box);
        scroll = GUILayout.BeginScrollView(scroll);

        if (pendulum != null)
        {
            GUILayout.Label("== PENDULUM ==");
            pendulum.L = Slider("Rope length L", pendulum.L, 0.5f, 5f);
            pendulum.initialAngleDeg = Slider("Release angle", pendulum.initialAngleDeg, 5f, 90f);
            pendulum.initialAngVel = Slider("Initial ang.vel", pendulum.initialAngVel, -5f, 5f);
            pendulum.g = Slider("Gravity g", pendulum.g, 1.6f, 24f);
            pendulum.emptyMass = Slider("Empty mass", pendulum.emptyMass, 0.3f, 2f);
            pendulum.initialPaintMass = Slider("Paint mass", pendulum.initialPaintMass, 0.5f, 10f);
            pendulum.flowRate = Slider("Flow rate", pendulum.flowRate, 0.001f, 0.1f);
            pendulum.airDensity = Slider("Air density", pendulum.airDensity, 0.5f, 1.5f);
            pendulum.dragCoef = Slider("Drag coef", pendulum.dragCoef, 0.8f, 1.2f);
            pendulum.area = Slider("Area", pendulum.area, 0.02f, 0.1f);
            pendulum.ropeStiffness = Slider("Rope stiffness", pendulum.ropeStiffness, 100f, 10000f);
            pendulum.ropeBreakTension = Slider("Break tension", pendulum.ropeBreakTension, 0f, 500f);
            pendulum.ropeIsElastic = GUILayout.Toggle(pendulum.ropeIsElastic, "Elastic rope");
            pendulum.useBuoyancy = GUILayout.Toggle(pendulum.useBuoyancy, "Buoyancy");
            pendulum.validationMode = GUILayout.Toggle(pendulum.validationMode, "Validation mode");
        }

        if (paint != null)
        {
            GUILayout.Space(8);
            GUILayout.Label("== PAINT ==");
            paint.viscosity = Slider("Viscosity", paint.viscosity, 0.2f, 3f);
            paint.temperature = Slider("Temperature", paint.temperature, 0f, 50f);
            paint.humidity = Slider("Humidity", paint.humidity, 0f, 100f);
            paint.baseEmission = Slider("Emission rate", paint.baseEmission, 0f, 120f);
            paint.holeRadius = Slider("Hole radius", paint.holeRadius, 0.01f, 0.2f);
            paint.holeHeight = Slider("Hole height", paint.holeHeight, 0f, 1f);
            paint.sloshStrength = Slider("Slosh strength", paint.sloshStrength, 0f, 0.5f);
            paint.continuousJetMode = GUILayout.Toggle(paint.continuousJetMode, "Continuous jet");
            paint.crownSplashEnabled = GUILayout.Toggle(paint.crownSplashEnabled, "Crown splash");

            GUILayout.Label("Surface:");
            paint.surface = (SurfaceType)GUILayout.Toolbar((int)paint.surface,
                new[] { "Canvas", "Wood", "Metal", "Paper" });
            GUILayout.Label("Hole shape:");
            paint.holeShape = (HoleShape)GUILayout.Toolbar((int)paint.holeShape,
                new[] { "Round", "Narrow", "Wide", "Multi" });

            GUILayout.Label("Paint color (RGB):");
            float rr = Slider("R", paint.paintColor.r, 0f, 1f);
            float gg = Slider("G", paint.paintColor.g, 0f, 1f);
            float bb = Slider("B", paint.paintColor.b, 0f, 1f);
            paint.paintColor = new Color(rr, gg, bb);
        }

        GUILayout.Space(8);
        GUILayout.Label("== ACTIONS ==");
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

        GUILayout.Space(8);
        GUILayout.Label("== LIVE READOUTS ==");
        if (pendulum != null)
        {
            GUILayout.Label($"Angle: X={pendulum.angleX:F1}  Z={pendulum.angleZ:F1}");
            GUILayout.Label($"Mass m(t)={pendulum.displayMass:F3} kg");
            GUILayout.Label($"Tension T={pendulum.currentTension:F2} N");
            GUILayout.Label($"KE={pendulum.kineticEnergy:F2}  PE={pendulum.potentialEnergy:F2}");
            GUILayout.Label($"Total E={pendulum.totalEnergy:F2} J");
            GUILayout.Label($"Period={pendulum.theoreticalPeriod:F3} s");
            GUILayout.Label($"Rope: {(pendulum.ropeBroken ? "BROKEN" : pendulum.ropeIsSlack ? "slack" : "taut")}");
        }
        if (paint != null)
        {
            GUILayout.Label($"Flow={paint.currentEmissionRate:F1}  Particles={paint.activeParticles}");
            GUILayout.Label($"Submerged: {(paint.isHoleSubmerged ? "yes" : "no")}  Level={paint.paintLevel:F2}");
            GUILayout.Label($"Re={paint.Re:F0} We={paint.We:F0} Ca={paint.Ca:F2} Oh={paint.Oh:F2}");
        }
        GUILayout.Label($"Motion time={motionTime:F1}s  Paths={pathCount}");
        GUILayout.Label($"Trajectory={trajectoryLength:F2} m");
        GUILayout.Label($"Coverage={paintAreaCoverage * 100f:F2}%");

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    float Slider(string label, float val, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label($"{label}: {val:F3}", GUILayout.Width(210));
        val = GUILayout.HorizontalSlider(val, min, max);
        GUILayout.EndHorizontal();
        return val;
    }
}
