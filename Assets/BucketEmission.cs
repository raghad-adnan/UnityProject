using System.Collections.Generic;
using UnityEngine;

// ============================================================================
//  PaintPhysics — BUCKET EMISSION  (partial class; see PaintPhysics.cs header)
// ----------------------------------------------------------------------------
//  Owns everything about the bucket itself: the paint reservoir, the hole
//  shape/geometry, the physically-derived drop size (Tate + Harkins-Brown +
//  viscous Ca) and the per-frame emission that releases contained particles
//  through the hole. The SAME PaintParticle objects transition
//  InsideBucket -> Emitted -> Falling -> Painted (no separate droplet system).
//
//  EMISSION IS FULLY PHYSICAL (no rate knob): the mass flow through the hole is
//  the Torricelli orifice discharge
//      m_dot = rho * (A_hole * valve) * Cd * sqrt(2 g h)
//  with A_hole the real area of the selected hole SHAPE, valve the user's
//  flow-rate control (fraction of the hole that is open), and h the liquid head
//  above the hole from the bucket's GEOMETRIC fill level. Drops per second =
//  m_dot / m_drop. Narrow/Wide/Multiple holes therefore genuinely pour at
//  different rates because their areas differ.
// ============================================================================
public partial class PaintPhysics : MonoBehaviour
{
    // Paint reservoir lives on PendulumMotion (currentPaintMass, in kg) so the bucket weight and
    // the emission gate read the SAME quantity — draining paint makes the bucket lighter.

    [Header("Hole")]
    public HoleShape holeShape = HoleShape.Round;
    // Characteristic hole radius r (m). Real paint-bucket holes are millimetres, not centimetres:
    // the old 6 cm default made the Torricelli discharge empty 5 kg of paint in a fraction of a
    // second. 8 mm drains ~5 kg in ~45 s — a realistic pour.
    public float holeRadius = 0.008f;
    // Height of the hole above the bucket floor, as a FRACTION of the bucket height (0 = the hole
    // is in the bottom face). Paint stops pouring once the fill level drops below it.
    [Range(0f, 1f)] public float holeHeight = 0f;
    // Exit direction in BUCKET-LOCAL space: transformed by the bucket's rotation at release, so a
    // tilted (swinging) bucket pours at an angle — that exit angle then shapes the canvas splat.
    public Vector3 exitDirection = Vector3.down;

    [Header("Bucket geometry (drives the transform scale)")]
    // Half-width of the bucket box (m) and its height (m). These SET the bucket transform's scale
    // each frame (ApplyBucketSize), so the container the liquid sloshes in, the capacity check and
    // the fill level all follow the same real geometry the user chose in the panel.
    // Defaults ≈ a large site bucket: 0.4 m wide, 0.45 m tall (holds ~72 L).
    public float bucketRadius = 0.2f;
    public float bucketHeightMeters = 0.45f;

    [Header("Multi-colour (PDF §4 إمكانية استخدام أكثر من لون)")]
    public bool multiColorMode = false;
    public Color paintColor2 = new Color(0.20f, 0.45f, 1f);   // blue
    public Color paintColor3 = new Color(1f, 0.82f, 0.10f);   // yellow

    // With multi-colour on, each swing lays down a different colour from the 3-colour palette, so a
    // full run shows several coloured arcs. Off -> every drop uses the single paintColor.
    Color PickColor()
    {
        if (!multiColorMode) return paintColor;
        int idx = (bucketMotion != null ? Mathf.Abs(bucketMotion.swingCount) : 0) % 3;
        return idx == 0 ? paintColor : (idx == 1 ? paintColor2 : paintColor3);
    }

    [Header("Emission")]
    // HARD capacity of the particle system. Pool + spatial hash are pre-allocated at this size once
    // at Start, so the particle-count control can move counts up and down at runtime without
    // reallocation. 12,000 = the brief's 10,000-particle stress reservoir PLUS headroom for the
    // falling stream, so choosing 10,000 really does put 10,000 particles INSIDE the bucket.
    public const int HardMaxParticles = 12000;
    // SOFT cap — live particle budget (reservoir fill + falling stream). Auto-raised by
    // UpdateDropAccounting so the full reservoir plus its falling stream always fit.
    [Range(100, HardMaxParticles)] public int maxParticles = 4000;
    // THE particle-count control: how many drops fill the container when the bucket is full.
    // The user types ANY number (panel numeric field) or uses the 2k/5k/10k shortcut buttons.
    // Mass bookkeeping stays EXACT for any N: each particle carries initialPaintMass / N kg and its
    // diameter is derived from that mass, so the paint in the bucket and the paint going out are
    // the same numbers — N drops in, N drops out, sum of drop masses == bucket paint mass.
    [Range(100, 10000)] public int reservoirParticles = 2000;
    // Staged spawning (brief §6): fill the reservoir this many particles per frame, never all at once.
    [Range(10, 500)] public int spawnPerFrame = 150;
    public float particleLifetime = 5f;

    [Header("Droplet render")]
    // VISUAL-ONLY multiplier on a particle's rendered size. The physical drop size (p.size,
    // ~1 cm from the mass split) still drives ALL the impact physics; this only enlarges the
    // rendered sphere so the droplets read clearly at scene scale. Set to 1 for the true size.
    public float dropletVisualScale = 8f;
    // ACTUALLY-USED visual multiplier (read-only display): dropletVisualScale, auto-reduced when
    // the full reservoir would not physically FIT in the container at that size. Capacity check
    // uses the random-loose-packing limit of spheres (~55% volume fraction): if N drops at the
    // requested visual size exceed 55% of the container volume, the RENDERED size shrinks until
    // they fit. Purely cosmetic — p.size, masses and all formulas are untouched.
    public float effectiveDropletScale = 8f;

    [Header("Emission display (debug)")]
    public float currentEmissionRate;   // drops per second (Torricelli mass flow / drop mass)
    public float currentMassFlow;       // kg/s through the hole
    public float paintLevel;            // geometric fill fraction of the bucket volume [0..1]
    public bool isHoleSubmerged;
    public float lastDropDiameter;                            // per-drop diameter (m)
    public float lastDropMassKg;                              // per-drop mass (kg)
    public float currentExitSpeed;                            // Torricelli efflux speed (m/s) at the hole

    private float emitAccumulator;
    // Per-drop mass/diameter used this frame (mass split of the reservoir across N drops).
    private float perDropMass = 0.001f;
    private float perDropDiameter = 0.01f;

    // Hole exit point on the bucket's bottom face, in bucket-local (unit-cube) coordinates.
    private static readonly Vector3 HoleLocalPos = new Vector3(0f, -0.5f, 0f);

    // ------------------------------------------------------------------------
    //  Hole SHAPE geometry. Each shape is a defined opening with a real area;
    //  the area feeds the Torricelli discharge, so shapes genuinely pour
    //  differently (this is what makes the hole-type input a physical one):
    //    Round    : circle, radius r                       A = pi r^2
    //    Narrow   : slit along Z, length 8r, width 0.5r    A = 4 r^2
    //    Wide     : band along X, length 12r, width 1.5r   A = 18 r^2
    //    Multiple : three round holes radius r, 6r apart   A = 3 pi r^2
    //  The Tate drop size uses the shape's hydraulic rim radius r_h = 2A/P
    //  (equals r for a circle), so slits shed thinner drops than round holes.
    // ------------------------------------------------------------------------
    float HoleArea()
    {
        float r = Mathf.Max(1e-4f, holeRadius);
        switch (holeShape)
        {
            case HoleShape.Narrow:   return 4f * r * r;
            case HoleShape.Wide:     return 18f * r * r;
            case HoleShape.Multiple: return 3f * Mathf.PI * r * r;
            default:                 return Mathf.PI * r * r;
        }
    }

    float HoleHydraulicRimRadius()
    {
        float r = Mathf.Max(1e-4f, holeRadius);
        switch (holeShape)
        {
            case HoleShape.Narrow:   return 2f * (4f * r * r) / (17f * r);   // 2A/P, P = 2(8r+0.5r)
            case HoleShape.Wide:     return 2f * (18f * r * r) / (27f * r);  // 2A/P, P = 2(12r+1.5r)
            default:                 return r;                                // circle(s)
        }
    }

    // Physically-derived diameter (m) of a drop detaching from the hole. No magic constant:
    //   Tate's law      : a pendant drop falls when its weight equals the rim surface-tension force,
    //                      m*g = 2*pi*r*gamma  ->  V = 2*pi*r*gamma / (rho*g).
    //   Harkins-Brown   : real drops are smaller (some liquid stays behind), factor Phi ~ 0.6.
    //   Viscous dynamics: faster, more viscous efflux resists pinch-off and enlarges the drop,
    //                      captured by the Capillary number Ca = mu*u/gamma  ->  V *= (1 + Ca).
    // So the drop scales with rim radius, surface tension, density, gravity AND viscosity.
    float DropDiameter(float effViscosity, float exitSpeed, float rimRadius, out float dropMass)
    {
        float muPhys = paintViscosityPaS * Mathf.Max(0.05f, effViscosity);          // Pa·s
        float Ca = muPhys * Mathf.Max(0f, exitSpeed) / Mathf.Max(1e-6f, surfaceTension);
        const float harkinsBrown = 0.6f;
        float V = harkinsBrown * 2f * Mathf.PI * Mathf.Max(1e-4f, rimRadius) * surfaceTension
                  / Mathf.Max(1e-6f, density * gravity) * (1f + Ca);
        V = Mathf.Max(1e-12f, V);
        dropMass = density * V;
        return Mathf.Pow(6f * V / Mathf.PI, 1f / 3f); // sphere-equivalent diameter
    }

    // Reference Tate drop diameter (debug/UI): what the hole would physically pinch off.
    public float tateDropDiameter;

    // Drive the bucket transform's scale from the user's bucket geometry (called every frame from
    // PaintPhysics.Update). The contained liquid, wall containment, capacity check, fill level and
    // hole pattern all read this same transform, so one input changes the whole physical container.
    void ApplyBucketSize()
    {
        if (bucketMotion == null) return;
        bucketRadius = Mathf.Clamp(bucketRadius, 0.05f, 0.75f);
        bucketHeightMeters = Mathf.Clamp(bucketHeightMeters, 0.2f, 1.5f);
        Vector3 target = new Vector3(2f * bucketRadius, bucketHeightMeters, 2f * bucketRadius);
        if ((bucketMotion.transform.localScale - target).sqrMagnitude > 1e-10f)
            bucketMotion.transform.localScale = target;
    }

    // Recompute the drop <-> particle correspondence for this frame. ONE source of truth:
    // the user chooses HOW MANY drops represent the bucket's paint (reservoirParticles) and the
    // mass splits exactly across them:
    //   perDropMass = initialPaintMass / N,  diameter = sphere of that mass.
    // So the container fills with N particles, emptying it releases those same N, each draining
    // ITS OWN mass: count in == count out and sum(drop masses) == bucket paint mass, always.
    // Tate's law remains the physical reference (tateDropDiameter readout); picking a larger N
    // simply means finer drops.
    void UpdateDropAccounting(float effViscosity)
    {
        tateDropDiameter = DropDiameter(effViscosity, currentExitSpeed, HoleHydraulicRimRadius(), out _);

        reservoirParticles = Mathf.Clamp(reservoirParticles, 100, 10000);
        // Auto-raise the soft budget so the FULL reservoir plus a falling-stream share always fit.
        int needed = Mathf.Min(HardMaxParticles,
                               reservoirParticles + Mathf.Max(500, reservoirParticles / 5));
        if (maxParticles < needed) maxParticles = needed;

        perDropMass = bucketMotion.initialPaintMass / reservoirParticles;
        perDropDiameter = Mathf.Pow(6f * (perDropMass / Mathf.Max(1f, density)) / Mathf.PI, 1f / 3f);
        lastDropDiameter = perDropDiameter;
        lastDropMassKg   = perDropMass;

        // Visual capacity fit ("make the droplets smaller rather than lie about the count"):
        // shrink the RENDERED size until N spheres fit in ~55% of the container volume
        // (random loose packing). Rendering-only; the physical p.size is not touched.
        Vector3 s = bucketMotion.transform.lossyScale;
        float boxVol = Mathf.Abs(s.x * s.y * s.z);
        float maxSphereVol = 0.55f * boxVol / reservoirParticles;
        float maxVisualD = Mathf.Pow(6f * maxSphereVol / Mathf.PI, 1f / 3f);
        effectiveDropletScale = Mathf.Min(dropletVisualScale,
                                          maxVisualD / Mathf.Max(1e-6f, perDropDiameter));
    }

    void EmitStep(float dt)
    {
        if (bucketMotion == null) return;
        Transform bt = bucketMotion.transform;

        // ---- fluid state ----
        // Viscosity vs temperature: Arrhenius/Andrade law (see FluidConstants), replacing the old
        // ad-hoc linear lerp between two arbitrary bounds. `viscosity` is the user's multiplier on
        // the base paint (1 = standard latex paint at 25 °C).
        float effViscosity = Mathf.Max(0.05f,
            viscosity * FluidConstants.ViscosityTemperatureFactor(temperature));

        // ---- geometric fill level (real liquid volume over real container volume) ----
        Vector3 sc = bt.lossyScale;
        float bucketH = Mathf.Max(0.01f, Mathf.Abs(sc.y));
        float boxVol  = Mathf.Max(1e-6f, Mathf.Abs(sc.x * sc.y * sc.z));
        float paintVol = bucketMotion.currentPaintMass / Mathf.Max(1f, density);
        float fillFrac = Mathf.Clamp01(paintVol / boxVol);
        paintLevel = fillFrac;

        // ---- free-surface tilt (quasi-static slosh) ----
        // Under lateral acceleration a the liquid surface tilts to stay normal to the effective
        // gravity (tan(beta) = a/g); at the wall (lever arm = half-width) the level rises by
        // (w/2)*a/g. Used for the submergence gate: sloshing can wash paint over a raised hole.
        // (Viscosity does NOT enter the steady surface tilt — the old /viscosity there was wrong.)
        float aLat = bucketMotion.GetTangentialAcceleration();
        float halfWidth = 0.5f * Mathf.Max(Mathf.Abs(sc.x), Mathf.Abs(sc.z));
        float sloshRise = halfWidth * (aLat / Mathf.Max(0.1f, gravity)) / bucketH; // fraction of H
        isHoleSubmerged = (fillFrac + Mathf.Abs(sloshRise)) >= holeHeight && fillFrac > 0f;

        // ---- Torricelli head & efflux speed ----
        // Head h = depth of the hole below the free surface, measured ALONG GRAVITY: the fill
        // column above the hole, projected by the bucket's tilt (a tipped bucket holds less head
        // over a bottom hole). v = Cd*sqrt(2gh) — Bernoulli with the standard sharp-edge Cd.
        float cosTilt = Mathf.Clamp01(Vector3.Dot(bt.up, Vector3.up));
        float headMeters = Mathf.Max(0f, (fillFrac - holeHeight)) * bucketH * cosTilt;
        currentExitSpeed = FluidConstants.TorricelliSpeed(gravity, headMeters);

        UpdateDropAccounting(effViscosity);

        // ---- (A) STAGED FILL: top the container up with REAL paint particles to the current level ----
        // These are the same PaintParticles that later pour out of the hole and paint the canvas —
        // one particle system, not a decorative copy. spawnPerFrame bounds the per-frame cost so a
        // 10k reservoir fills over a couple of seconds instead of hitching one frame (brief §6).
        float massFrac = bucketMotion.currentPaintMass
                         / Mathf.Max(0.0001f, bucketMotion.initialPaintMass);
        int fillTarget = Mathf.RoundToInt(reservoirParticles * massFrac);
        // GPU mode: the reservoir is filled by the Spawn compute kernel
        // (GpuLiquidBridge sends the same staged-fill budget) — no CPU drops.
        if (!gpuMode)
        {
            int inside = insideCountCache;   // O(1): maintained by UpdateParticles each frame
            int spawnedThisFrame = 0;
            while (inside < fillTarget && pool.ActiveCount < maxParticles && spawnedThisFrame < spawnPerFrame)
            {
                SpawnInsideParticle(effViscosity);
                inside++; spawnedThisFrame++;
            }
            insideCountCache = inside;
        }

        // ---- (B) DRAIN: Torricelli orifice discharge through the selected hole shape ----
        //   m_dot = rho * (A_shape * valve) * Cd*sqrt(2gh);   drops/s = m_dot / m_drop.
        // flowRate is the valve-opening fraction (PendulumMotion.flowRate, panel input).
        if (bucketMotion.currentPaintMass <= 0f || !isHoleSubmerged || currentExitSpeed <= 0f)
        {
            currentMassFlow = 0f; currentEmissionRate = 0f; return;
        }
        float effectiveArea = HoleArea() * Mathf.Clamp01(bucketMotion.flowRate);
        currentMassFlow = density * effectiveArea * currentExitSpeed;          // kg/s
        currentEmissionRate = currentMassFlow / Mathf.Max(1e-9f, perDropMass); // drops/s

        // GPU mode: the Torricelli numbers above are the product — GpuLiquidBridge
        // turns currentMassFlow into a per-frame GPU emission budget and debits
        // the bucket by the ACTUAL emitted count (read back asynchronously).
        // The CPU must not also pour its own drops.
        if (gpuMode) return;

        emitAccumulator += currentEmissionRate * dt;
        while (emitAccumulator >= 1f)
        {
            emitAccumulator -= 1f;
            if (!ReleaseThroughHole(effViscosity)) break;    // nothing left inside to pour out
        }
    }

    // Spawn a REAL paint particle INSIDE the container (random spot in the bucket's box). It sloshes
    // there (SPH + ContainInBox) until it drains out the hole.
    void SpawnInsideParticle(float effViscosity)
    {
        PaintParticle p = pool.Get();
        if (p == null) return;

        Vector3 local = new Vector3(Random.Range(-0.4f, 0.4f),
                                    Random.Range(-0.1f, 0.45f),
                                    Random.Range(-0.4f, 0.4f));
        p.position = bucketMotion.transform.TransformPoint(local);
        p.velocity = Vector3.zero;
        p.color = PickColor();
        p.viscosityEffect = effViscosity;

        // The particle IS one physical drop: its size/mass come from the same accounting that
        // sized the reservoir, so what sloshes in the bucket and what falls to the canvas are
        // the same paint, drop for drop, gram for gram.
        p.size = perDropDiameter;
        p.approxMass = perDropMass;

        p.age      = 0f;
        p.lifetime = particleLifetime;
        p.state = ParticleState.InsideBucket;
    }

    // Pour one contained particle out THROUGH THE HOLE (brief §5.5):
    //   * picks the InsideBucket particle nearest the hole (in bucket-local space) — the liquid
    //     that actually sits over the opening is what leaves;
    //   * repositions it at the hole exit, offset by the hole-shape pattern;
    //   * exit velocity = FULL bucket velocity (the drop rides the swinging bucket the instant it
    //     detaches) + Torricelli jet along the tilted bucket axis + the bucket's SPIN (omega x r,
    //     real for off-axis holes) + the jet's Reynolds-dependent spread.
    // It keeps its size/colour/mass — the very object that sloshed in the container is the droplet
    // heading for the canvas. Returns false if the container is empty.
    bool ReleaseThroughHole(float effViscosity)
    {
        var list = pool.All;
        Transform bt = bucketMotion.transform;

        PaintParticle best = null;
        float bestSqr = float.MaxValue;
        for (int i = 0; i < list.Count; i++)
        {
            PaintParticle p = list[i];
            if (p.state != ParticleState.InsideBucket) continue;
            float d = (bt.InverseTransformPoint(p.position) - HoleLocalPos).sqrMagnitude;
            if (d < bestSqr) { bestSqr = d; best = p; }
        }
        if (best == null) return false;

        Vector3 offsetMeters, spreadVel;
        ComputeHolePattern(effViscosity, out offsetMeters, out spreadVel);

        // Exit AT the hole, in the bucket's frame (follows swing + tilt + spin automatically).
        best.position = bt.TransformPoint(HoleLocalPos + MetersToBucketLocal(offsetMeters, bt));
        best.velocity = bucketMotion.velocity                                     // inherited bucket motion
                      + bt.TransformDirection(exitDirection.normalized) * currentExitSpeed
                      + bt.TransformDirection(spreadVel);                          // jet spread (m/s)

        // Bucket spin: a hole at distance r from the spin (rope) axis flings the drop tangentially
        // at  v = omega x r  — this is what turns a spinning multi-hole bucket into a spiral sprayer.
        if (Mathf.Abs(bucketMotion.spinRate) > 1e-4f)
        {
            Vector3 rSpin = best.position - bt.position;
            rSpin -= bt.up * Vector3.Dot(rSpin, bt.up);       // radial part only (⊥ spin axis)
            best.velocity += Vector3.Cross(bt.up * bucketMotion.spinRate, rSpin);
        }

        best.age = 0f;
        best.lifetime = particleLifetime;
        best.state = ParticleState.Emitted;   // UpdateParticles: Emitted -> Falling -> canvas impact
        insideCountCache = Mathf.Max(0, insideCountCache - 1);

        // The bucket loses exactly THIS drop's mass — the paint going out is the paint that was in.
        bucketMotion.ConsumePaint(best.approxMass);
        return true;
    }

    // Convert a world-metre offset into the bucket's local (unit-cube) coordinates.
    static Vector3 MetersToBucketLocal(Vector3 meters, Transform bt)
    {
        Vector3 s = bt.lossyScale;
        return new Vector3(meters.x / Mathf.Max(1e-4f, Mathf.Abs(s.x)),
                           meters.y / Mathf.Max(1e-4f, Mathf.Abs(s.y)),
                           meters.z / Mathf.Max(1e-4f, Mathf.Abs(s.z)));
    }

    // Hole shape -> exit-point offset (METRES, bucket-local axes) + jet spread velocity (m/s).
    //
    // Jet spread: a liquid jet leaving an orifice fans out by a small angle that grows with the
    // Reynolds number of the efflux (laminar jets stay coherent ~1-2 deg; turbulent jets fan to
    // ~10 deg — Lin & Reitz 1998, Ann. Rev. Fluid Mech. 30). The spread VELOCITY is therefore
    // v_exit * tan(sigma) — tied to the real exit speed, not an arbitrary constant.
    void ComputeHolePattern(float effViscosity, out Vector3 offsetMeters, out Vector3 spreadVel)
    {
        float r = Mathf.Max(1e-4f, holeRadius);
        float muPhys = paintViscosityPaS * Mathf.Max(0.05f, effViscosity);
        float ReJet = density * currentExitSpeed * (2f * HoleHydraulicRimRadius()) / Mathf.Max(1e-6f, muPhys);
        float sigmaRad = Mathf.Lerp(1.5f, 10f, Mathf.InverseLerp(2000f, 10000f, ReJet)) * Mathf.Deg2Rad;
        float vSpread = currentExitSpeed * Mathf.Tan(sigmaRad);

        switch (holeShape)
        {
            case HoleShape.Narrow: // slit along Z: length 8r, width 0.5r
                offsetMeters = new Vector3(Random.Range(-0.25f, 0.25f) * r, 0f,
                                           Random.Range(-4f, 4f) * r);
                spreadVel = new Vector3(Random.Range(-vSpread, vSpread) * 0.25f, 0f,
                                        Random.Range(-vSpread, vSpread));
                break;

            case HoleShape.Wide: // band along X: length 12r, width 1.5r
                offsetMeters = new Vector3(Random.Range(-6f, 6f) * r, 0f,
                                           Random.Range(-0.75f, 0.75f) * r);
                spreadVel = new Vector3(Random.Range(-vSpread, vSpread), 0f,
                                        Random.Range(-vSpread, vSpread) * 0.5f);
                break;

            case HoleShape.Multiple: // three round holes, 6r apart along X
                int k = Random.Range(0, 3);
                Vector2 disc3 = Random.insideUnitCircle * r;
                offsetMeters = new Vector3((k - 1) * 6f * r + disc3.x, 0f, disc3.y);
                spreadVel = Random.insideUnitSphere * vSpread;
                spreadVel.y = 0f;
                break;

            default: // Round
                Vector2 disc = Random.insideUnitCircle * r;
                offsetMeters = new Vector3(disc.x, 0f, disc.y);
                spreadVel = new Vector3(Random.Range(-vSpread, vSpread), 0f,
                                        Random.Range(-vSpread, vSpread));
                break;
        }
    }

    // Refill = dump whatever is left and pour in a FRESH charge: every existing particle (old
    // colour, old state) is returned to the pool and the reservoir refills from scratch with the
    // currently selected colour. This is what makes "change colour then Refill/Reset" behave like
    // a real bucket swap instead of new paint appearing on top of the old.
    public void RefillPaint()
    {
        if (bucketMotion != null) bucketMotion.RefillPaint();
        PurgeAllParticles();
    }

    // -------------------------------------------------------------------------
    //  Hole highlights — glowing rings drawn at each hole exit point (visual
    //  INDICATOR of where/what the hole is; ring size has a readability floor
    //  because millimetre holes would be invisible at scene scale).
    //  Rebuilt automatically whenever the hole shape/size or bucket size changes
    //  at runtime (the old build-once-at-Start version is why switching the hole
    //  type in the panel appeared to do nothing).
    // -------------------------------------------------------------------------
    private readonly List<GameObject> holeRings = new List<GameObject>();
    private HoleShape ringsShape = (HoleShape)(-1);
    private float ringsRadius = -1f, ringsHeight = -1f, ringsBucketW = -1f;
    private Material holeGlowMat;

    // Called from PaintPhysics.Update: rebuild the rings only when a relevant input changed.
    void RefreshHoleHighlights()
    {
        if (bucketMotion == null) return;
        float bw = bucketMotion.transform.lossyScale.x;
        if (holeShape == ringsShape && Mathf.Approximately(holeRadius, ringsRadius)
            && Mathf.Approximately(holeHeight, ringsHeight) && Mathf.Approximately(bw, ringsBucketW))
            return;

        ringsShape = holeShape; ringsRadius = holeRadius; ringsHeight = holeHeight; ringsBucketW = bw;
        SetupHoleHighlights();
    }

    // Called from PaintPhysics.Start() and RefreshHoleHighlights().
    void SetupHoleHighlights()
    {
        if (bucketMotion == null) return;

        for (int i = 0; i < holeRings.Count; i++)
            if (holeRings[i] != null) Destroy(holeRings[i]);
        holeRings.Clear();

        if (holeGlowMat == null)
        {
            // Bright orange Unlit material — visible through the transparent bucket walls.
            holeGlowMat = new Material(Shader.Find("Unlit/Color"));
            holeGlowMat.color = new Color(1f, 0.55f, 0f); // vivid orange
        }

        Vector3 sc = bucketMotion.transform.lossyScale;
        float sx = Mathf.Max(1e-4f, Mathf.Abs(sc.x)), sz = Mathf.Max(1e-4f, Mathf.Abs(sc.z));
        // Local-space ring radius with a readability floor (indicator, not physics).
        float rLocX = Mathf.Max(0.04f, holeRadius / sx);
        float rLocZ = Mathf.Max(0.04f, holeRadius / sz);

        switch (holeShape)
        {
            case HoleShape.Round:
                SpawnHoleRing(Vector3.zero, rLocX, rLocZ, holeGlowMat);
                break;

            case HoleShape.Narrow: // slit along Z: long in Z, thin in X
                SpawnHoleRing(Vector3.zero, rLocX * 0.5f, Mathf.Min(0.45f, rLocZ * 4f), holeGlowMat);
                break;

            case HoleShape.Wide:   // band along X: long in X, thin-ish in Z
                SpawnHoleRing(Vector3.zero, Mathf.Min(0.45f, rLocX * 6f), rLocZ * 0.75f, holeGlowMat);
                break;

            case HoleShape.Multiple:
                for (int k = 0; k < 3; k++)
                {
                    float xLoc = Mathf.Clamp((k - 1) * 6f * holeRadius / sx, -0.4f, 0.4f);
                    SpawnHoleRing(new Vector3(xLoc, 0f, 0f), rLocX, rLocZ, holeGlowMat);
                }
                break;
        }
    }

    // Creates a LineRenderer ellipse child of the bucket at the given LOCAL offset on the bottom face.
    void SpawnHoleRing(Vector3 localOffset, float radiusX, float radiusZ, Material mat)
    {
        GameObject ring = new GameObject("HoleHighlight");
        ring.transform.SetParent(bucketMotion.transform, false); // stays in bucket-local space
        // Bottom face of the unit-cube bucket is at local y = -0.5; offset slightly upward so the
        // ring is visible and not clipped by the bucket floor geometry; holeHeight lifts it.
        ring.transform.localPosition = new Vector3(localOffset.x, -0.47f + holeHeight * 0.94f, localOffset.z);
        ring.transform.localRotation = Quaternion.identity;
        ring.transform.localScale    = Vector3.one;
        holeRings.Add(ring);

        LineRenderer lr = ring.AddComponent<LineRenderer>();
        lr.useWorldSpace  = false; // positions in ring's local space → follows bucket swing
        lr.loop           = true;
        lr.sharedMaterial = mat;
        lr.startWidth     = 0.018f;
        lr.endWidth       = 0.018f;
        lr.startColor     = new Color(1f, 0.55f, 0f);
        lr.endColor       = new Color(1f, 0.90f, 0.1f); // yellow at the end for a glow gradient

        const int segs = 24;
        lr.positionCount = segs;
        for (int i = 0; i < segs; i++)
        {
            float a = i / (float)segs * Mathf.PI * 2f;
            lr.SetPosition(i, new Vector3(Mathf.Cos(a) * radiusX, 0f, Mathf.Sin(a) * radiusZ));
        }
    }
}
