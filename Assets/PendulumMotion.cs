using UnityEngine;
using UnityEngine.InputSystem;

public class PendulumMotion : MonoBehaviour
{
    [Header("Rope")]
    [Range(0.5f, 3f)] public float L = 3f;  

    public bool ropeIsElastic = false;

    [Range(100f, 10000f)] public float ropeStiffness = 5000f;
    [Range(0f, 500f)] public float ropeBreakTension = 0f;  
    public bool ropeBroken = false;

    [Header("Bucket & paint")]
    [Range(0.3f, 2f)] public float emptyMass = 1f;
    [Range(0.5f, 10f)] public float initialPaintMass = 5f;
    [Range(0.001f, 0.1f)] public float flowRate = 0.05f;   

    [Header("Environment")]
    [Range(1.6f, 24f)] public float g = 9.81f;
    [Range(0.5f, 1.5f)] public float airDensity = 1.225f;
    [Range(0.8f, 1.2f)] public float dragCoef = 1f;
    [Range(0.02f, 0.1f)] public float area = 0.05f;
    public Vector3 windVel = Vector3.zero;
    public bool useBuoyancy = false;
    public float bucketVolume = 0.005f;
    [Range(0f, 0.5f)] public float damping = 0f;

    [Header("Initial conditions")]
    [Range(5f, 90f)] public float initialAngleDeg = 30f;
    [Range(-5f, 5f)] public float initialAngVel = 2f;
    public float impulse = 2f;

    [Header("Pivot")]
    public Transform pivot;

    [Header("Read-only display")]
    public float angleX, angleZ;
    public float displayMass;
    public Vector3 velocity;          
    public bool ropeIsSlack;
    public float currentTension;
    public float kineticEnergy, potentialEnergy, totalEnergy;
    public float theoreticalPeriod;
    public float energyDissipationRate;

    [Header("Validation")]
    public bool validationMode = false;

    private float thetaX, thetaZ, angVelX, angVelZ, lastAccX, lastAccZ, yOffset;
    private float thetaX0, thetaZ0, elapsed;
    private Vector3 prevPos, slackPos, slackVel;
    private bool impulseQueued;
    private float vEnergyMin, vEnergyMax, vLogTimer, lastCrossTime, measuredPeriod, maxTensionObserved, lastThetaXSign;

    
    public float mass
    {
        get
        {
            float m = emptyMass + initialPaintMass - flowRate * elapsed;
            return Mathf.Max(m, Mathf.Max(emptyMass, 0.01f));
        }
    }

    void Start() { ResetSimulation(); }
    public void ResetSimulation()
    {
        thetaX = initialAngleDeg * Mathf.Deg2Rad;
        thetaZ = 0f;
        thetaX0 = thetaX; thetaZ0 = thetaZ;
        angVelX = 0f;
        angVelZ = initialAngVel;
        elapsed = 0f;
        ropeIsSlack = false; ropeBroken = false;
        UpdatePositionFromAngles();
        prevPos = transform.position;
        slackPos = transform.position - pivot.position;
        slackVel = Vector3.zero;
        vEnergyMin = float.MaxValue; vEnergyMax = float.MinValue;
        vLogTimer = 0f; lastCrossTime = 0f; measuredPeriod = 0f; maxTensionObserved = 0f;
        lastThetaXSign = Mathf.Sign(thetaX);
    }

    void Update()
    {
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
            impulseQueued = true;
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;
        elapsed += dt;

        if (impulseQueued) { angVelX += impulse; angVelZ += impulse; impulseQueued = false; }

        Vector3 newPos = (ropeBroken || ropeIsSlack) ? StepFreeFall(dt) : StepPendulum(dt);
        velocity = (newPos - prevPos) / dt;
        transform.position = newPos;
        prevPos = newPos;

        UpdateReadouts();
        if (validationMode) ValidateStep(dt);
    }

    Vector3 StepPendulum(float dt)
    {
        float m = mass;
        float gEff = useBuoyancy ? g * (1f - (airDensity * bucketVolume) / m) : g;
        float kd = (airDensity * dragCoef * area * L) / (2f * m); 

        float accX = -(gEff / L) * Mathf.Sin(thetaX) - kd * angVelX * Mathf.Abs(angVelX) - damping * angVelX;
        float accZ = -(gEff / L) * Mathf.Sin(thetaZ) - kd * angVelZ * Mathf.Abs(angVelZ) - damping * angVelZ;
        accX += WindAngularAcc(windVel.x, thetaX, angVelX, m);
        accZ += WindAngularAcc(windVel.z, thetaZ, angVelZ, m);
        lastAccX = accX; lastAccZ = accZ;

        //  Euler
        angVelX += accX * dt; angVelZ += accZ * dt;
        thetaX += angVelX * dt; thetaZ += angVelZ * dt;
        thetaX = Mathf.Repeat(thetaX + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;
        thetaZ = Mathf.Repeat(thetaZ + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;

       
        float x = L * Mathf.Sin(thetaX);
        float z = L * Mathf.Sin(thetaZ);
        float y = -Mathf.Sqrt(Mathf.Max(0f, L * L - x * x - z * z));
        yOffset = y;

        currentTension = GetTensionForce();
        if (ropeBreakTension > 0f && currentTension > ropeBreakTension)
        {
            ropeBroken = true; slackVel = velocity; slackPos = new Vector3(x, y, z);
        }
        else if (currentTension <= 0f) 
        {
            ropeIsSlack = true; slackVel = velocity; slackPos = new Vector3(x, y, z);
        }

        float r = L;
        if (ropeIsElastic && ropeStiffness > 0f) r = L + Mathf.Max(0f, currentTension) / ropeStiffness;
        return pivot.position + (new Vector3(x, y, z) / L) * r;
    }

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
            float cosX = Mathf.Cos(thetaX), cosZ = Mathf.Cos(thetaZ);
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
        float relV = windComp - L * angVel * Mathf.Cos(theta);
        return 0.5f * airDensity * dragCoef * area * relV * Mathf.Abs(relV) / (m * L);
    }

    public float GetTensionForce()
    {
        float cosEff = -yOffset / L;
        float centripetal = L * (angVelX * angVelX + angVelZ * angVelZ);
        return mass * (g * cosEff + centripetal);
    }

    public float GetTangentialAcceleration()
    {
        return L * Mathf.Sqrt(lastAccX * lastAccX + lastAccZ * lastAccZ);
    }

    public float GetTensionAtAngle(float thetaRad)
    {
        float t0 = Mathf.Max(Mathf.Abs(thetaX0), Mathf.Abs(thetaZ0));
        return mass * g * (3f * Mathf.Cos(thetaRad) - 2f * Mathf.Cos(t0));
    }

    public float GetTheoreticalMaxTension()
    {
        float t0 = Mathf.Max(Mathf.Abs(thetaX0), Mathf.Abs(thetaZ0));
        return mass * g * (3f - 2f * Mathf.Cos(t0));
    }

    void UpdateReadouts()
    {
        float m = mass;
        float vx = L * angVelX, vz = L * angVelZ;
        kineticEnergy = 0.5f * m * (vx * vx) + 0.5f * m * (vz * vz);
        potentialEnergy = m * g * yOffset;
        totalEnergy = kineticEnergy + potentialEnergy;
        theoreticalPeriod = 2f * Mathf.PI * Mathf.Sqrt(L / g);
        float wMag = Mathf.Sqrt(angVelX * angVelX + angVelZ * angVelZ);
        energyDissipationRate = -0.5f * airDensity * dragCoef * area * L * L * L * wMag * wMag * wMag;
        angleX = thetaX * Mathf.Rad2Deg;
        angleZ = thetaZ * Mathf.Rad2Deg;
        displayMass = m;
    }

    
    void ValidateStep(float dt)
    {
        if (totalEnergy < vEnergyMin) vEnergyMin = totalEnergy;
        if (totalEnergy > vEnergyMax) vEnergyMax = totalEnergy;
        if (currentTension > maxTensionObserved) maxTensionObserved = currentTension;

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
            Debug.Log($"[Validate] T_measured={measuredPeriod:F3}s vs 2pi*sqrt(L/g)={theoreticalPeriod:F3}s | " +
                      $"E drift={(vEnergyMax - vEnergyMin):F3}J | T_max_obs={maxTensionObserved:F2}N vs Eq4={GetTheoreticalMaxTension():F2}N");
        }
    }
}
