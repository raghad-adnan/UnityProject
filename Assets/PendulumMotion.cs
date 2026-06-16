using UnityEngine;
using UnityEngine.InputSystem;

// ============================================================
//  PendulumMotion - 3D pendulum with two swing axes (thetaX, thetaZ).
//  Matches Phase 1 / the physical study (Doc 1).
//  Pure math only (no RigidBody/Colliders), integrated in FixedUpdate.
//  Launch conditions: a release angle on X + an angular velocity on Z
//  (a real "release" -> elliptical/spiral motion emerges from physics).
// ============================================================
public class PendulumMotion : MonoBehaviour
{
    [Header("Rope")]
    [Range(0.5f, 5f)]  public float L = 5f;                 // rope length (m)
    public bool  ropeIsElastic = false;
    [Range(100f, 10000f)] public float ropeStiffness = 5000f; // k (N/m) when elastic
    [Range(0f, 500f)]  public float ropeBreakTension = 0f;  // T_break (N) - 0 = never breaks
    public bool  ropeBroken = false;

    [Header("Bucket & paint")]
    [Range(0.3f, 2f)]  public float emptyMass = 1f;         // m_b (kg)
    [Range(0.5f, 10f)] public float initialPaintMass = 5f;  // m_p(0) (kg)
    [Range(0.001f, 0.1f)] public float flowRate = 0.05f;    // mu (kg/s)

    [Header("Environment")]
    [Range(1.6f, 24f)] public float g = 9.81f;              // gravity (m/s^2)
    [Range(0.5f, 1.5f)] public float airDensity = 1.225f;   // rho_air
    [Range(0.8f, 1.2f)] public float dragCoef = 1f;         // C_d
    [Range(0.02f, 0.1f)] public float area = 0.05f;         // A (m^2)
    public Vector3 windVel = Vector3.zero;                  // wind (m/s)
    public bool  useBuoyancy = false;                       // buoyancy (optional)
    public float bucketVolume = 0.005f;                     // V for buoyancy (m^3)
    [Range(0f, 0.5f)] public float damping = 0f;            // optional linear damping (0 = spec-faithful)

    [Header("Initial conditions")]
    [Range(5f, 90f)]  public float initialAngleDeg = 30f;   // theta0 (release angle on X)
    [Range(-5f, 5f)]  public float initialAngVel = 2f;      // thetaDot0 (push on Z)
    public float impulse = 2f;                              // impulse on Space key

    [Header("Pivot")]
    public Transform pivot;

    [Header("Read-only display")]
    public float angleX, angleZ;        // degrees
    public float displayMass;
    public Vector3 velocity;            // read by PaintPhysics
    public bool ropeIsSlack;
    public float currentTension;
    public float kineticEnergy, potentialEnergy, totalEnergy;
    public float theoreticalPeriod;
    public float energyDissipationRate; // dE/dt

    [Header("Validation (Task 1.16)")]
    public bool validationMode = false;

    // ---- internal state (radians) ----
    private float thetaX, thetaZ;
    private float angVelX, angVelZ;
    private float lastAccX, lastAccZ;
    private float yOffset;
    private float thetaX0, thetaZ0;
    private float elapsed;
    private Vector3 prevPos;
    private bool impulseQueued;
    private Vector3 slackPos, slackVel;

    // ---- validation tracking ----
    private float vEnergyMin, vEnergyMax, vLogTimer, lastCrossTime, measuredPeriod, maxTensionObserved, lastThetaXSign;

    // m(t) = m_b + m_p(0) - mu*t, with a floor to avoid zero mass (Task 1.2)
    public float mass
    {
        get
        {
            float m = emptyMass + initialPaintMass - flowRate * elapsed;
            return Mathf.Max(m, Mathf.Max(emptyMass, 0.01f));
        }
    }

    void Start()
    {
        ResetSimulation();
    }

    // Full re-init - called by the UI Reset button (Task 1.15)
    public void ResetSimulation()
    {
        // Initial conditions from fields (NOT derived from scene position)
        thetaX = initialAngleDeg * Mathf.Deg2Rad; // release angle on X
        thetaZ = 0f;
        thetaX0 = thetaX;
        thetaZ0 = thetaZ;
        angVelX = 0f;                 // released from rest on X
        angVelZ = initialAngVel;      // push on Z -> elliptical/spiral motion

        elapsed = 0f;
        ropeIsSlack = false;
        ropeBroken = false;

        UpdatePositionFromAngles();
        prevPos = transform.position;
        slackPos = transform.position - pivot.position;
        slackVel = Vector3.zero;

        vEnergyMin = float.MaxValue;
        vEnergyMax = float.MinValue;
        vLogTimer = 0f; lastCrossTime = 0f; measuredPeriod = 0f; maxTensionObserved = 0f;
        lastThetaXSign = Mathf.Sign(thetaX);
    }

    void Update()
    {
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
            impulseQueued = true;
    }

    // Physics in FixedUpdate (fixed timestep) - Task 1.15
    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;
        elapsed += dt;

        if (impulseQueued)
        {
            angVelX += impulse;
            angVelZ += impulse;
            impulseQueued = false;
        }

        Vector3 newPos = (ropeBroken || ropeIsSlack) ? StepFreeFall(dt) : StepPendulum(dt);

        velocity = (newPos - prevPos) / dt; // world velocity (Task 1.9)
        transform.position = newPos;
        prevPos = newPos;

        UpdateReadouts();
        if (validationMode) ValidateStep(dt);
    }

    // ---------- taut pendulum ----------
    Vector3 StepPendulum(float dt)
    {
        float m = mass;
        float gEff = useBuoyancy ? g * (1f - (airDensity * bucketVolume) / m) : g; // buoyancy (Task 1.13)

        // k_d recomputed every step because m shrinks (Task 1.3)
        float kd = (airDensity * dragCoef * area * L) / (2f * m);

        // theta'' = -(g/L)sin(theta) - k_d*thetaDot*|thetaDot|  (optional linear damping, 0 by default)
        float accX = -(gEff / L) * Mathf.Sin(thetaX)
                     - kd * angVelX * Mathf.Abs(angVelX)
                     - damping * angVelX;
        float accZ = -(gEff / L) * Mathf.Sin(thetaZ)
                     - kd * angVelZ * Mathf.Abs(angVelZ)
                     - damping * angVelZ;

        accX += WindAngularAcc(windVel.x, thetaX, angVelX, m);
        accZ += WindAngularAcc(windVel.z, thetaZ, angVelZ, m);

        lastAccX = accX;
        lastAccZ = accZ;

        // Semi-Implicit Euler: velocity first, then position (Task 1.4)
        angVelX += accX * dt;
        angVelZ += accZ * dt;
        thetaX += angVelX * dt;
        thetaZ += angVelZ * dt;

        // Wrap angles to [-pi, pi] (Task 1.14)
        thetaX = Mathf.Repeat(thetaX + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;
        thetaZ = Mathf.Repeat(thetaZ + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;

        float x = L * Mathf.Sin(thetaX);
        float z = L * Mathf.Sin(thetaZ);
        float y = -Mathf.Sqrt(Mathf.Max(0f, L * L - x * x - z * z)); // Task 1.10
        yOffset = y;

        currentTension = GetTensionForce();

        // rope break
        if (ropeBreakTension > 0f && currentTension > ropeBreakTension)
        {
            ropeBroken = true;
            slackVel = velocity; slackPos = new Vector3(x, y, z);
        }
        // rope slack when T <= 0 (Task 1.6)
        else if (currentTension <= 0f)
        {
            ropeIsSlack = true;
            slackVel = velocity; slackPos = new Vector3(x, y, z);
        }

        // elastic rope (Hooke) - quasi-static stretch
        float r = L;
        if (ropeIsElastic && ropeStiffness > 0f)
            r = L + Mathf.Max(0f, currentTension) / ropeStiffness;

        Vector3 dir = new Vector3(x, y, z) / L;
        return pivot.position + dir * r;
    }

    // ---------- free fall (slack / broken) - Task 1.6 ----------
    Vector3 StepFreeFall(float dt)
    {
        slackVel += Vector3.down * g * dt;
        slackPos += slackVel * dt;

        if (!ropeBroken && slackPos.magnitude >= L)
        {
            Vector3 dir = slackPos.normalized;
            slackPos = dir * L;
            thetaX = Mathf.Asin(Mathf.Clamp(slackPos.x / L, -1f, 1f));
            thetaZ = Mathf.Asin(Mathf.Clamp(slackPos.z / L, -1f, 1f));
            yOffset = slackPos.y;

            float cosX = Mathf.Cos(thetaX);
            float cosZ = Mathf.Cos(thetaZ);
            angVelX = (Mathf.Abs(cosX) > 0.001f) ? slackVel.x / (L * cosX) : 0f;
            angVelZ = (Mathf.Abs(cosZ) > 0.001f) ? slackVel.z / (L * cosZ) : 0f;
            ropeIsSlack = false;
        }
        return pivot.position + slackPos;
    }

    void UpdatePositionFromAngles()
    {
        float x = L * Mathf.Sin(thetaX);
        float z = L * Mathf.Sin(thetaZ);
        float y = -Mathf.Sqrt(Mathf.Max(0f, L * L - x * x - z * z));
        yOffset = y;
        transform.position = pivot.position + new Vector3(x, y, z);
    }

    float WindAngularAcc(float windComp, float theta, float angVel, float m)
    {
        if (Mathf.Abs(windComp) < 0.0001f) return 0f;
        float vBucket = L * angVel * Mathf.Cos(theta);
        float relV = windComp - vBucket;
        return 0.5f * airDensity * dragCoef * area * relV * Mathf.Abs(relV) / (m * L);
    }

    // ---------- public API (Tasks 1.5 / 1.7 / 1.8 / 1.9) ----------

    // Eq.1 - instantaneous tension
    public float GetTensionForce()
    {
        float cosEff = -yOffset / L;
        float centripetal = L * (angVelX * angVelX + angVelZ * angVelZ);
        return mass * (g * cosEff + centripetal);
    }

    // tangential acceleration = L*|theta''| combined over both axes
    public float GetTangentialAcceleration()
    {
        return L * Mathf.Sqrt(lastAccX * lastAccX + lastAccZ * lastAccZ);
    }

    // Eq.3 - tension vs angle (undamped) for validation
    public float GetTensionAtAngle(float thetaRad)
    {
        float t0 = Mathf.Max(Mathf.Abs(thetaX0), Mathf.Abs(thetaZ0));
        return mass * g * (3f * Mathf.Cos(thetaRad) - 2f * Mathf.Cos(t0));
    }

    // Eq.4 - max tension at bottom of swing
    public float GetTheoreticalMaxTension()
    {
        float t0 = Mathf.Max(Mathf.Abs(thetaX0), Mathf.Abs(thetaZ0));
        return mass * g * (3f - 2f * Mathf.Cos(t0));
    }

    // ---------- energy (Task 1.11) and display ----------
    void UpdateReadouts()
    {
        float m = mass;
        float vx = L * angVelX;
        float vz = L * angVelZ;
        kineticEnergy = 0.5f * m * (vx * vx) + 0.5f * m * (vz * vz);
        potentialEnergy = m * g * yOffset;
        totalEnergy = kineticEnergy + potentialEnergy;
        theoreticalPeriod = 2f * Mathf.PI * Mathf.Sqrt(L / g); // Task 1.12

        float wMag = Mathf.Sqrt(angVelX * angVelX + angVelZ * angVelZ);
        energyDissipationRate = -0.5f * airDensity * dragCoef * area * L * L * L * wMag * wMag * wMag;

        angleX = thetaX * Mathf.Rad2Deg;
        angleZ = thetaZ * Mathf.Rad2Deg;
        displayMass = m;
    }

    // ---------- validation tests (Task 1.16) ----------
    void ValidateStep(float dt)
    {
        if (totalEnergy < vEnergyMin) vEnergyMin = totalEnergy;
        if (totalEnergy > vEnergyMax) vEnergyMax = totalEnergy;
        if (currentTension > maxTensionObserved) maxTensionObserved = currentTension;

        // measure period via rising zero-crossing of thetaX
        float s = Mathf.Sign(thetaX);
        if (s > 0f && lastThetaXSign <= 0f)
        {
            float now = Time.time;
            if (lastCrossTime > 0f) measuredPeriod = now - lastCrossTime;
            lastCrossTime = now;
        }
        lastThetaXSign = s;

        vLogTimer += dt;
        if (vLogTimer >= 3f)
        {
            vLogTimer = 0f;
            float drift = vEnergyMax - vEnergyMin;
            Debug.Log($"[Validate] T_measured={measuredPeriod:F3}s vs 2pi*sqrt(L/g)={theoreticalPeriod:F3}s | " +
                      $"E drift={drift:F3}J | T_max_obs={maxTensionObserved:F2}N vs Eq4={GetTheoreticalMaxTension():F2}N");
        }
    }
}
