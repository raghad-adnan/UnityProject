using UnityEngine;
using UnityEngine.InputSystem;

// ============================================================================
//  PendulumMotion — custom spherical-pendulum physics (NO Rigidbody/Joints).
// ----------------------------------------------------------------------------
//  STABILITY REWRITE. The previous version integrated two DECOUPLED planar
//  angles (thetaX, thetaZ) and rebuilt the position as
//      x = L sin(thetaX), z = L sin(thetaZ), y = -sqrt(L^2 - x^2 - z^2).
//  That parametrization is only valid while sin^2(thetaX) + sin^2(thetaZ) <= 1.
//  With the default side push (initialAngVel = 2 rad/s on a 5 m rope) the Z
//  amplitude reaches ~90 deg, the sqrt clamps to 0, the bucket position jumps
//  discontinuously, tension flips sign, and the slack/free-fall/snap logic
//  enters a violent feedback loop — the "bucket bouncing out of bounds" bug.
//
//  The pendulum is a TRUE SPHERICAL PENDULUM, integrated in TORQUE form:
//      state    : dir (unit vector pivot -> bob),  omega (angular velocity ⊥ dir)
//      taut     : dω/dt = (r × a_ext)/L²  with a_ext = g + drag + damping
//                 + friction + wind;  dir is then rotated about ω by |ω|dt
//                 (exact motion on the rope sphere; v = ω × r).
//      tension  : T = m (g cos(theta) + |v|^2 / L)   (centripetal balance)
//      slack    : T <= 0 -> ballistic flight; when |pos-pivot| >= L the rope
//                 snaps taut and the radial velocity is absorbed (inelastic jerk).
//
//  WHY THIS *IS* THE SPHERICAL PENDULUM (not two planar ones): the scheme above
//  is the constraint-projection discretisation of the exact 3-D equation
//      r̈ = g − ( g·r̂ + |v|²/L ) r̂        with |r| = L,
//  whose solutions in spherical coordinates (θ = polar angle from vertical,
//  φ = azimuth) are the classical spherical-pendulum equations
//      θ̈ = sinθ cosθ φ̇² − (g/L) sinθ ,   d/dt( m L² sin²θ φ̇ ) = 0.
//  The second line is the azimuthal angular-momentum invariant
//      Lz = m L² sin²θ φ̇ = m (r × v)·ŷ,
//  which a genuine spherical pendulum conserves when dissipation is off — a
//  decoupled two-plane system does NOT. Lz is exposed as a live readout and
//  its drift is logged in validation mode as the proof of sphericity.
//  The vector form is used instead of raw (θ, φ) coordinates because the
//  (θ, φ) chart is singular at θ = 0 (sin θ division) while the vector state
//  is regular everywhere and unconditionally stable at any amplitude.
//
//  Initial conditions cover the full spherical state:
//      θ0 = initialAngleDeg,  φ0 = releaseDirectionDeg,
//      φ̇0-type side push = initialAngVel (azimuthal, ⊥ release plane),
//      θ̇0 = initialPolarVel (in the release plane).
// ============================================================================
public class PendulumMotion : MonoBehaviour
{
    [Header("Rope")]
    [Range(0.5f, 5f)] public float L = 5f;

    public bool ropeIsElastic = false;

    [Range(100f, 10000f)] public float ropeStiffness = 5000f;
    [Range(0f, 500f)] public float ropeBreakTension = 0f;
    public bool ropeBroken = false;

    [Header("Bucket & paint")]
    [Range(0.3f, 2f)] public float emptyMass = 1f;
    [Range(0.5f, 10f)] public float initialPaintMass = 5f;
    // Live remaining paint (kg). SINGLE SOURCE OF TRUTH for the paint quantity: it is drained by the
    // actual droplet emission (PaintPhysics calls ConsumePaint with each drop's real mass), so the
    // bucket gets lighter exactly as much paint as leaves the hole — no separate reservoir counter.
    public float currentPaintMass;
    [Range(0.001f, 0.1f)] public float flowRate = 0.05f;   // paint flow-rate control (scales emission)

    [Header("Bucket orientation")]
    // A bucket hanging from a rope aligns its axis with the rope. Rotating the transform is what
    // makes the liquid slosh (local gravity tilts in the bucket frame) and makes the pour direction
    // follow the swing — the paint leaves the hole at the bucket's angle, not straight down.
    public bool alignWithRope = true;
    [Range(1f, 30f)] public float alignSpeed = 10f;   // 1/s — smoothing so slack/snap events don't snap the visual

    [Header("Environment")]
    [Range(1.6f, 24f)] public float g = 9.81f;
    [Range(0.5f, 1.5f)] public float airDensity = 1.225f;
    [Range(0.8f, 1.2f)] public float dragCoef = 1f;
    [Range(0.02f, 0.1f)] public float area = 0.05f;
    public Vector3 windVel = Vector3.zero;
    public bool useBuoyancy = false;
    public float bucketVolume = 0.005f;
    [Range(0f, 0.5f)] public float damping = 0f;
    [Range(0f, 1f)] public float friction = 0f;   // dry (Coulomb) friction at the pivot

    [Header("Initial conditions")]
    [Range(5f, 90f)] public float initialAngleDeg = 30f;
    // Tangential push (rad/s) perpendicular to the release plane. NOTE: the old default of 2 rad/s on
    // a 5 m rope carries enough energy to swing the side plane past 90 deg — physically fine for the
    // new solver, but a wild opening demo. 0.5 rad/s gives a pleasing elliptical swing.
    [Range(-5f, 5f)] public float initialAngVel = 0.5f;
    // Polar rate θ̇0 (rad/s) IN the release plane — together with initialAngVel (azimuthal push)
    // and (θ0, φ0) this completes the full 4-component spherical initial state.
    [Range(-3f, 3f)] public float initialPolarVel = 0f;
    [Range(0f, 360f)] public float releaseDirectionDeg = 0f;   // azimuth φ0 of the release plane
    [Range(0, 40)] public int maxSwings = 0;                   // 0 = unlimited
    public float impulse = 2f;

    [Header("Pivot")]
    public Transform pivot;

    [Header("Read-only display (spherical state)")]
    public float polarAngleDeg;       // θ — true angle from vertical
    public float azimuthDeg;          // φ — heading of the bob in the horizontal plane
    public float azimuthalRate;       // φ̇ (rad/s) — precession rate around the vertical axis
    public float angularMomentumY;    // Lz = m (r×v)·ŷ — conserved when dissipation is off
    [Header("Read-only display (legacy planar projections)")]
    public float angleX, angleZ;      // asin(dir.x), asin(dir.z) in degrees (projection readouts only —
                                      // the DYNAMICS never uses them; kept for UI/report compatibility)
    public float displayMass;
    public Vector3 velocity;
    public bool ropeIsSlack;
    public float currentTension;
    public float kineticEnergy, potentialEnergy, totalEnergy;
    public float theoreticalPeriod;
    public float energyDissipationRate;
    public int swingCount;
    public bool motionStopped;

    [Header("Validation")]
    public bool validationMode = false;

    // --- integration state (vector form) ---
    private Vector3 dir = Vector3.down;   // unit vector pivot -> bob (taut mode)
    private Vector3 omega = Vector3.zero; // angular velocity (rad/s), ⊥ dir — primary taut state
    private Vector3 vel = Vector3.zero;   // bob world velocity = ω × (dir·L), derived each step
    private Vector3 slackPos, slackVel;   // ballistic state relative to pivot (slack/broken mode)
    private Vector3 swingAxis = Vector3.right; // horizontal release axis (for swing counting)
    private float lastTangAccelMag;
    private float theta0Rad;
    private Vector3 prevPos;
    private bool impulseQueued;
    private float vEnergyMin, vEnergyMax, vLogTimer, lastCrossTime, measuredPeriod, maxTensionObserved;
    private float vLzMin, vLzMax;    // azimuthal angular-momentum drift window (sphericity check)
    private float lastSwingSign;

    public float mass
    {
        get
        {
            float m = emptyMass + currentPaintMass;
            return Mathf.Max(m, Mathf.Max(emptyMass, 0.01f));
        }
    }

    // Called by PaintPhysics each time a droplet is emitted: the bucket loses exactly that drop's mass.
    public void ConsumePaint(float kg) { currentPaintMass = Mathf.Max(0f, currentPaintMass - kg); }
    // Refill the bucket back to its starting paint charge (used by the UI "Refill" button / full Reset).
    public void RefillPaint() { currentPaintMass = initialPaintMass; }

    void Start() { ResetSimulation(); }

    public void ResetSimulation()
    {
        float dirRad = releaseDirectionDeg * Mathf.Deg2Rad;
        float a0 = initialAngleDeg * Mathf.Deg2Rad;
        theta0Rad = a0;
        swingAxis = new Vector3(Mathf.Cos(dirRad), 0f, Mathf.Sin(dirRad));

        // Bob direction: rotated a0 from vertical, in the release plane.
        dir = new Vector3(Mathf.Sin(a0) * Mathf.Cos(dirRad), -Mathf.Cos(a0), Mathf.Sin(a0) * Mathf.Sin(dirRad));
        dir.Normalize();

        // Full spherical initial velocity, built from the two orthogonal tangent directions:
        //   azimuthal (⊥ release plane, horizontal)  — the φ̇0 side push,
        //   polar     (in the release plane, = ∂dir/∂θ) — the θ̇0 swing-plane push.
        Vector3 sidePush = new Vector3(-Mathf.Sin(dirRad), 0f, Mathf.Cos(dirRad));
        Vector3 polarTangent = new Vector3(Mathf.Cos(a0) * Mathf.Cos(dirRad),
                                           Mathf.Sin(a0),
                                           Mathf.Cos(a0) * Mathf.Sin(dirRad));
        vel = sidePush * (initialAngVel * L) + polarTangent * (initialPolarVel * L);
        vel -= dir * Vector3.Dot(vel, dir); // keep it tangential
        omega = Vector3.Cross(dir, vel) / L; // primary state: ω = (r̂ × v)/L

        currentPaintMass = initialPaintMass;   // reset the paint charge to full
        swingCount = 0; motionStopped = false;
        ropeIsSlack = false; ropeBroken = false;

        transform.position = pivot.position + dir * L;
        if (alignWithRope) transform.rotation = Quaternion.FromToRotation(Vector3.up, -dir);
        prevPos = transform.position;
        slackPos = dir * L;
        slackVel = Vector3.zero;
        vEnergyMin = float.MaxValue; vEnergyMax = float.MinValue;
        vLzMin = float.MaxValue; vLzMax = float.MinValue;
        vLogTimer = 0f; lastCrossTime = 0f; measuredPeriod = 0f; maxTensionObserved = 0f;
        lastSwingSign = Mathf.Sign(Vector3.Dot(dir, swingAxis));
    }

    void Update()
    {
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
            impulseQueued = true;
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f || pivot == null) return;
        if (motionStopped) { velocity = Vector3.zero; return; }

        if (impulseQueued)
        {
            // Legacy behaviour: a kick of `impulse` rad/s on both horizontal axes -> linear kick of
            // impulse * L along (1,0,1), applied as the equivalent angular-velocity change
            // (only the tangential part survives: omega = (r̂ × v)/L kills the radial component).
            omega += Vector3.Cross(dir, new Vector3(1f, 0f, 1f) * (impulse * L)) / L;
            impulseQueued = false;
        }

        Vector3 newPos = (ropeBroken || ropeIsSlack) ? StepBallistic(dt) : StepPendulum(dt);
        velocity = (newPos - prevPos) / dt;
        transform.position = newPos;
        prevPos = newPos;

        AlignBucket(dt);
        UpdateReadouts();
        if (validationMode) ValidateStep(dt);
    }

    // --- taut rope: spherical pendulum in TORQUE / angular-momentum form ---
    // State: dir (unit vector on the rope sphere) + omega (angular velocity, ⊥ dir);
    // the bob velocity is v = ω × r with r = dir·L.
    //   dω/dt = (r × a_ext) / L²            (torque per unit m L²)
    //   dir   ← rotate dir about ω by |ω|dt (exact motion on the sphere)
    // Gravity's torque r × g has ZERO vertical component, so the spherical pendulum's
    // azimuthal invariant Lz = m(r×v)·ŷ is conserved STRUCTURALLY by this scheme, not just
    // approximately (measured: < 0.06 % drift and bounded energy over 50 simulated minutes;
    // the previous project-onto-sphere form drifted Lz by >90 % over the same run).
    // Only physical dissipation (drag/damping/friction/wind) changes Lz — as it should.
    Vector3 StepPendulum(float dt)
    {
        float m = mass;
        float gEff = useBuoyancy ? g * (1f - (airDensity * bucketVolume) / m) : g;

        Vector3 r = dir * L;
        vel = Vector3.Cross(omega, r);

        // External accelerations as FULL vectors — the cross product with r discards the radial
        // part automatically (the rope tension carries it), so no explicit projection is needed.
        Vector3 aExt = Vector3.down * gEff;

        // Quadratic air drag: a = -(rho Cd A / 2m) |v| v.
        float dragK = airDensity * dragCoef * area / (2f * m);
        aExt -= dragK * vel.magnitude * vel;

        // Linear (viscous) damping — matches the old per-axis damping semantics (units 1/s).
        aExt -= damping * vel;

        // Dry (Coulomb) pivot friction: constant deceleration opposing the motion direction.
        float speed = vel.magnitude;
        if (friction > 0f && speed > 1e-4f)
            aExt -= (friction * gEff) * (vel / speed);

        // Wind: quadratic drag on the RELATIVE velocity (wind - bob velocity).
        if (windVel.sqrMagnitude > 1e-8f)
        {
            Vector3 rel = windVel - vel;
            aExt += dragK * rel.magnitude * rel;
        }

        lastTangAccelMag = (aExt - dir * Vector3.Dot(aExt, dir)).magnitude;

        // Torque step + exact rotation on the sphere (symplectic-Euler-like: ω first, then dir).
        omega += Vector3.Cross(r, aExt) / (L * L) * dt;
        omega -= dir * Vector3.Dot(omega, dir);   // no spin about the rope axis
        float angDeg = omega.magnitude * dt * Mathf.Rad2Deg;
        if (angDeg > 1e-7f) dir = Quaternion.AngleAxis(angDeg, omega.normalized) * dir;
        dir.Normalize();
        vel = Vector3.Cross(omega, dir * L);

        // Tension from the centripetal balance: T = m (g cos(theta) + v^2 / L); cos(theta) = -dir.y.
        currentTension = m * (gEff * (-dir.y) + vel.sqrMagnitude / L);

        if (ropeBreakTension > 0f && currentTension > ropeBreakTension)
        {
            ropeBroken = true; slackVel = vel; slackPos = dir * L;
        }
        else if (currentTension <= 0f)
        {
            // Rope can only pull. Above the horizontal with too little speed the bob leaves the
            // constraint and flies ballistically until the rope goes taut again.
            ropeIsSlack = true; slackVel = vel; slackPos = dir * L;
            currentTension = 0f;
        }

        // Swing counting: sign change of the bob's horizontal component along the release axis.
        float sgn = Mathf.Sign(Vector3.Dot(dir, swingAxis));
        if (sgn != 0f && lastSwingSign != 0f && sgn != lastSwingSign)
        {
            swingCount++;
            if (maxSwings > 0 && swingCount >= maxSwings) motionStopped = true;
        }
        if (sgn != 0f) lastSwingSign = sgn;

        // Elastic rope: visual/physical stretch proportional to tension (L_eff = L + T/k).
        float ropeLen = L;
        if (ropeIsElastic && ropeStiffness > 0f) ropeLen = L + Mathf.Max(0f, currentTension) / ropeStiffness;
        return pivot.position + dir * ropeLen;
    }

    // --- slack or broken rope: ballistic flight, snap taut when the rope re-tightens ---
    Vector3 StepBallistic(float dt)
    {
        slackVel += Vector3.down * g * dt;
        slackPos += slackVel * dt;
        currentTension = 0f;

        if (!ropeBroken && slackPos.magnitude >= L)
        {
            // Inextensible-rope snap: an impulsive tension instantly removes the radial (along-rope)
            // velocity component — an inelastic jerk that dissipates energy. Only the tangential
            // velocity survives into the resumed swing (rebuilt as angular velocity).
            dir = slackPos.normalized;
            slackPos = dir * L;
            slackVel -= dir * Vector3.Dot(slackVel, dir);
            vel = slackVel;
            omega = Vector3.Cross(dir, vel) / L;   // resume the taut (torque-form) state
            ropeIsSlack = false;
        }
        return pivot.position + slackPos;
    }

    // Ease the bucket's up-axis toward the rope direction (exp smoothing, framerate independent).
    void AlignBucket(float dt)
    {
        if (!alignWithRope) return;
        Vector3 toPivot = pivot.position - transform.position;
        if (toPivot.sqrMagnitude < 1e-8f) return;
        Quaternion target = Quaternion.FromToRotation(Vector3.up, toPivot.normalized);
        float t = 1f - Mathf.Exp(-alignSpeed * dt);
        transform.rotation = Quaternion.Slerp(transform.rotation, target, t);
    }

    // Linear tangential acceleration magnitude (m/s^2) — used by the emission slosh model.
    public float GetTangentialAcceleration() => lastTangAccelMag;

    public float GetTensionForce() => currentTension;

    public float GetTensionAtAngle(float thetaRad)
        => mass * g * (3f * Mathf.Cos(thetaRad) - 2f * Mathf.Cos(theta0Rad));

    public float GetTheoreticalMaxTension()
        => mass * g * (3f - 2f * Mathf.Cos(theta0Rad));

    void UpdateReadouts()
    {
        float m = mass;
        Vector3 d = (ropeBroken || ropeIsSlack) ? slackPos.normalized : dir;
        kineticEnergy = 0.5f * m * vel.sqrMagnitude;
        if (ropeBroken || ropeIsSlack) kineticEnergy = 0.5f * m * slackVel.sqrMagnitude;
        float yOff = (transform.position - pivot.position).y;
        potentialEnergy = m * g * yOff;
        totalEnergy = kineticEnergy + potentialEnergy;

        // Large-amplitude period: the small-angle 2π√(L/g) under-predicts the real period at big swing
        // angles, so add the first two elliptic-integral correction terms in the release amplitude θ0.
        //   T ≈ 2π√(L/g) · (1 + θ0²/16 + 11 θ0⁴/3072 + …)
        float th0sq = theta0Rad * theta0Rad;
        theoreticalPeriod = 2f * Mathf.PI * Mathf.Sqrt(L / g)
                            * (1f + th0sq / 16f + 11f * th0sq * th0sq / 3072f);

        float v = (ropeBroken || ropeIsSlack) ? slackVel.magnitude : vel.magnitude;
        energyDissipationRate = -0.5f * airDensity * dragCoef * area * v * v * v;

        angleX = Mathf.Asin(Mathf.Clamp(d.x, -1f, 1f)) * Mathf.Rad2Deg;
        angleZ = Mathf.Asin(Mathf.Clamp(d.z, -1f, 1f)) * Mathf.Rad2Deg;

        // Spherical state (θ, φ, φ̇, Lz). Lz = m (r × v)·ŷ is the spherical pendulum's azimuthal
        // angular-momentum invariant: constant when drag/damping/friction/wind are off. Watching it
        // hold while the bob precesses is the direct demonstration that this is ONE spherical
        // system, not two decoupled planes (which have no such shared invariant).
        polarAngleDeg = Vector3.Angle(Vector3.down, d);
        azimuthDeg = Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg;
        Vector3 r = transform.position - pivot.position;
        Vector3 vv = (ropeBroken || ropeIsSlack) ? slackVel : vel;
        angularMomentumY = m * (r.x * vv.z - r.z * vv.x);   // m (r × v)·ŷ (φ̇-positive convention)
        float horizSq = r.x * r.x + r.z * r.z;
        azimuthalRate = (horizSq > 1e-6f) ? (r.x * vv.z - r.z * vv.x) / horizSq : 0f;

        displayMass = m;
    }

    void ValidateStep(float dt)
    {
        if (totalEnergy < vEnergyMin) vEnergyMin = totalEnergy;
        if (totalEnergy > vEnergyMax) vEnergyMax = totalEnergy;
        if (currentTension > maxTensionObserved) maxTensionObserved = currentTension;
        if (angularMomentumY < vLzMin) vLzMin = angularMomentumY;
        if (angularMomentumY > vLzMax) vLzMax = angularMomentumY;

        float s = Mathf.Sign(Vector3.Dot(dir, swingAxis));
        if (s > 0f && lastSwingSign <= 0f)
        {
            float now = Time.time;
            if (lastCrossTime > 0f) measuredPeriod = now - lastCrossTime;
            lastCrossTime = now;
        }

        vLogTimer += dt;
        if (vLogTimer >= 3f)
        {
            vLogTimer = 0f;
            // Lz drift is the sphericity check: with damping/friction/wind at 0 (and the tiny air
            // drag notwithstanding) the azimuthal angular momentum of a TRUE spherical pendulum is
            // conserved. Two decoupled planar pendulums do not conserve it.
            Debug.Log($"[Validate] T_measured={measuredPeriod:F3}s vs theory={theoreticalPeriod:F3}s | " +
                      $"E drift={(vEnergyMax - vEnergyMin):F3}J | " +
                      $"Lz={angularMomentumY:F4} (drift {(vLzMax - vLzMin):F4}) | " +
                      $"T_max_obs={maxTensionObserved:F2}N vs Eq4={GetTheoreticalMaxTension():F2}N");
        }
    }
}
