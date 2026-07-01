using System.Collections.Generic;
using UnityEngine;

// ============================================================================
//  PaintPhysics — SURFACE INTERACTION  (partial class; see PaintPhysics.cs)
// ----------------------------------------------------------------------------
//  Owns everything that happens once paint touches the floor/canvas: impact
//  splat (Madejski spread, Stow-Hadfield splash), Tanner spreading, thin-film
//  gravity flow, per-splat Lucas-Washburn absorption, all the texture Stamp*
//  writers, plus Clear / SavePainting / GetPaintAreaCoverage. Depends on
//  SurfacePreset.cs and FluidConstants.cs (both left unchanged). The entry
//  point OnDropletImpact() is the seam called by ParticleSimulation.cs.
//  Pure relocation from PaintDrawer.cs — no formula/threshold/constant changed.
// ============================================================================
public partial class PaintPhysics : MonoBehaviour
{
    [Header("Drawing / surface")]
    public int textureSize = 1024;
    public float baseSplatSize = 8f;
    public bool continuousJetMode = true;
    public float jetMaxGapUV = 0.04f;
    public bool crownSplashEnabled = false;

    // depth over which a porous surface fully absorbs paint (used by Lucas-Washburn absorption)
    public float substrateThicknessMeters = 0.001f;

    private float[,] absorbedPaint;
    [Header("Paint film (SI)")]
    public float maxFilmThicknessMeters = 2e-3f; // max wet-film thickness before it sags (~2 mm)
    private float[,] paintThickness;             // per-pixel film thickness in METRES

    [Header("Surface display (debug)")]
    public float Re, We, Ca, Oh, K;
    public float canvasTiltDeg;        // live surface tilt from horizontal (deg)

    private Texture2D texture;
    private Vector2Int lastSplatPx;
    private bool lastSplatValid;

    // (removed) surfaceWetTime / lastAbsorbFrac: the old GLOBAL absorption clock started at Start() and
    // drove every porous surface at once, so it saturated within seconds of launch regardless of when
    // paint actually landed. Absorption is now per-splat (ActiveSplat.localWetTime / localAbsorbFrac).

    // active splats keep spreading (Tanner's law) and may flow downhill (thin-film) after landing
    private class ActiveSplat
    {
        public int px, py;            // current centre in pixels (moves while flowing)
        public float cx, cy;          // sub-pixel centre, so slow flow accumulates correctly
        public float radiusMeters;    // R_final: Madejski max-spread radius (m)
        public float tv;              // Tanner viscous-relaxation time (s)
        public float volumeM3;        // paint volume in this splat (m^3)
        public Color color;           // stamp colour (already thickness-adjusted)
        public float age;
        public float life;
        // Phase 4 - surface flow state
        public float filmThickness;   // h (m)
        public float pinningForce;    // F_pin (N/m^2)
        public bool isFlowing;
        public float flowedDistanceUV; // distance travelled (pixels) since landing
        // C5 spreading regime (set at landing):
        public float thetaEqRad;      // apparent (Wenzel) equilibrium contact angle [rad]
        public bool absorptionLimited; // true when theta_eq ~ 0 (complete wetting): no finite t_v exists
        // C6 per-splat absorption clock — starts when THIS splat lands, not at game start:
        public float localWetTime;    // seconds since this splat first touched the surface
        public float localAbsorbFrac; // this splat's own Lucas-Washburn saturation progress [0..1]
        public Color currentColor;    // s.color dulled toward the substrate by localAbsorbFrac (what we stamp)
    }
    private readonly List<ActiveSplat> activeSplats = new List<ActiveSplat>();
    private const int MaxActiveSplats = 60;
    // C5, complete-wetting case (theta_eq = 0 -> de Gennes t_v -> infinity). There is no physical
    // finite capillary timescale, so this is a documented REAL-TIME design choice: the splat spreads
    // to its Madejski R_final over this visual time and is then frozen/removed once THIS splat's own
    // capillary absorption (localAbsorbFrac -> full) saturates. It is NOT a measured t_v.
    private const float AbsorptionSpreadVisualSeconds = 0.6f;
    // Saturation fraction at which an absorption-limited splat stops growing (treated as "soaked in").
    private const float AbsorptionFullFraction = 0.99f;
    // theta_eq below this (rad) is treated as complete wetting -> absorption-limited spreading.
    private const float CompleteWettingAngleRad = 0.01f;
    // Numerical guard (NOT a physical value): caps how long a single flowing splat is tracked,
    // so a near-horizontal slow rivulet can't be updated forever. Physical termination is running
    // off the floor edge (offCanvas) or drying on a level surface.
    private const float FlowSafetySeconds = 30f;

   // Seam from ParticleSimulation.cs: a droplet has just crossed the canvas plane at hitPoint.
   void OnDropletImpact(Transform c, Vector3 hitPoint, PaintParticle p, Vector3 n)
{
    Vector3 local = c.InverseTransformPoint(hitPoint);

    Vector2 uv =
        new Vector2(
            0.5f - local.x / PlaneMeshExtent,
            0.5f - local.z / PlaneMeshExtent
        );


    if (uv.x < 0f || uv.x > 1f ||
        uv.y < 0f || uv.y > 1f)
    {
        lastSplatValid = false;
        return;
    }



    int px =
        (int)(uv.x * texture.width);

    int py =
        (int)(uv.y * texture.height);



    float speed =
        p.velocity.magnitude;



    float D =
        Mathf.Max(
            0.001f,
            p.size
        );


    // Dynamic viscosity in Pa·s: paint base viscosity modulated by the per-particle effect.
    float mu = paintViscosityPaS * Mathf.Max(0.05f, p.viscosityEffect);

    // Scale separation: visual particles are 3-10 cm (needed for visibility on a 50 m canvas),
    // but Stow & Hadfield (1981) K was validated for real spray/drip drops of 1-5 mm.
    // Using D=5 cm gives K=200-1600 at every speed -> every impact splashes, which is wrong.
    // Solution: evaluate K at D_splash = 3 mm (upper bound of real latex paint drops); this keeps
    // We/Re inside the validated regime while the visual splat size (Rmeters) still uses the full D.
    // This is an explicit scale correction, not a tuning knob.
    const float D_splash = 0.003f;   // 3 mm — physical drop scale for K / finger-count (m)
    float We_k = FluidConstants.Weber(density, speed, D_splash, surfaceTension);
    float Re_k = FluidConstants.Reynolds(density, speed, D_splash, mu);
    Ca = FluidConstants.Capillary(mu, speed, surfaceTension);
    Oh = FluidConstants.Ohnesorge(mu, density, surfaceTension, D_splash);
    K  = FluidConstants.StowHadfieldK(We_k, Re_k);
    We = We_k; Re = Re_k;   // expose physical values to SimulationManager readout

    // --- Maximum spread radius: Pasandideh-Fard / Madejski (1996) ---
    // betaMax uses the visual D so the deposited splat covers the correct canvas area.
    // Wenzel apparent contact angle accounts for surface roughness.
    float youngRad     = preset.contactAngleDeg * Mathf.Deg2Rad;
    float cosThetaStar = FluidConstants.WenzelCos(preset.wenzelRoughness, youngRad);
    float We_vis = FluidConstants.Weber(density, speed, D, surfaceTension);
    float Re_vis = FluidConstants.Reynolds(density, speed, D, mu);
    float betaMax      = FluidConstants.MadejskiBetaMax(We_vis, Re_vis, cosThetaStar);
    float Rmeters      = D * betaMax * 0.5f;                 // splat radius (m)
    int r = Mathf.Clamp(Mathf.RoundToInt(Rmeters * pixelsPerUnit), 2, textureSize / 4);
    int r0 = Mathf.Max(2, Mathf.RoundToInt(D * 0.5f * pixelsPerUnit)); // initial drop footprint (px)

    // Paint volume of the drop and the resulting film thickness (m): h = V / (pi R^2).
    float volumeM3 = p.approxMass / Mathf.Max(1f, density);          // V = m / rho
    float filmH    = volumeM3 / (Mathf.PI * Rmeters * Rmeters);

    // Deposit the film (in metres) and let porous surfaces draw it in (no adhesion fudge:
    // paint is retained/lost only by physical absorption and by gravity-driven flow below).
    AddPaintThickness(px, py, r, filmH);
    AbsorbPaint(px, py);

    // Surface colour from Beer-Lambert hiding power: opacity = 1 - exp(-h / h_hide) over the bare
    // SUBSTRATE colour (thin paint reveals the real surface: grey metal, brown wood, cream canvas).
    float thickness = paintThickness[px, py];                                   // metres
    float opacity   = FluidConstants.BeerLambertOpacity(thickness, FluidConstants.PaintHidingThicknessMeters);
    Color finalColor = Color.Lerp(preset.substrateColor, p.color, opacity);

    // --- Splash vs deposition: Stow-Hadfield K, with a roughness-lowered threshold (C2) ---
    // K_eff = 57.7*(1 - alpha*min(Ra/Ra_ref,1)); rougher surfaces splash sooner (Canvas << Metal).
    float Kthreshold = FluidConstants.SplashThresholdRough(preset.arithmeticRoughnessUm);
    if (K > Kthreshold)
    {
        // Finger count from Rayleigh-Taylor instability of the rim:  N = sqrt(beta*We/12).
        // Uses We_k (physical drop We) so the count stays in the 3-12 range of real splash literature.
        int fingers = Mathf.Clamp(Mathf.RoundToInt(FluidConstants.SplashFingerCount(We_k, betaMax)), 3, 12);
        // Secondary-droplet radius = rim/ligament thickness of the spreading lamella. Mass conservation
        // gives rim thickness h_rim ~ R_splat / beta_max (Roisman 2009, Phys. Fluids 21, 052103); the
        // Rayleigh-Plateau break-up of that thin rim sets the satellite-droplet size, which is therefore
        // ALWAYS a small fraction of the main splat. Capped at r/3 so it is visibly smaller than the mark.
        int dropletR = Mathf.Clamp(Mathf.RoundToInt(r / Mathf.Max(2f, betaMax)), 1, Mathf.Max(1, r / 3));
        ScatterDroplets(px, py, r, p.color, fingers, dropletR, Kthreshold);
        if (crownSplashEnabled) StampCrown(px, py, r, p.color, fingers, dropletR);
    }

    // --- Register an active splat: Tanner's-law growth + thin-film surface flow (C5) ---
    // theta_eq = apparent (Wenzel) equilibrium contact angle. Two physical regimes:
    //   theta_eq > 0  (Metal, Canvas): partial wetting -> de Gennes capillary t_v = eta*R/(gamma*theta^3),
    //                  the timescale that actually pairs with Tanner's (t/t_v)^(1/10) law.
    //   theta_eq = 0  (Wood, Paper, complete wetting): de Gennes t_v -> infinity, so there is NO finite
    //                  capillary timescale; we fall back to an absorption-limited visual spread that
    //                  freezes once THIS splat's own Washburn saturation completes (see UpdateSplatAbsorption).
    float thetaEqRad = Mathf.Acos(Mathf.Clamp(cosThetaStar, -1f, 1f));
    bool absorptionLimited = thetaEqRad <= CompleteWettingAngleRad;
    float tv = absorptionLimited
        ? AbsorptionSpreadVisualSeconds
        : FluidConstants.TannerRelaxTimeDeGennes(mu, surfaceTension, Rmeters, thetaEqRad);

    if (activeSplats.Count < MaxActiveSplats)
    {
        activeSplats.Add(new ActiveSplat
        {
            px = px,
            py = py,
            cx = px,
            cy = py,
            radiusMeters = Rmeters,
            tv = tv,
            volumeM3 = volumeM3,
            color = finalColor,
            age = 0f,
            // Growth window for the partial-wetting regime: Tanner growth completes at t_v, so a level
            // splat stops being updated shortly after (0.1 s floor = numerical minimum for flow-onset).
            // For the absorption-limited regime expiry is saturation-driven instead (see UpdateActiveSplats).
            // This is NOT a drying time — the texture mark is permanent.
            life = Mathf.Max(tv, 0.1f),
            isFlowing = false,
            flowedDistanceUV = 0f,
            thetaEqRad = thetaEqRad,
            absorptionLimited = absorptionLimited,
            localWetTime = 0f,        // this splat's absorption clock starts now, at landing
            localAbsorbFrac = 0f,     // nothing soaked in yet
            currentColor = finalColor // undulled at birth; UpdateSplatAbsorption dulls it over time
        });
        StampCircle(px, py, r0, finalColor); // initial contact footprint; Tanner fills it out
    }
    else
    {
        StampCircle(px, py, r, finalColor);  // fallback: stamp full splat if the list is full
    }



    textureDirty = true;



    lastSplatPx =
        new Vector2Int(px, py);


    lastSplatValid = true;
}
 void UpdateActiveSplats(float dt)
    {
        if (activeSplats.Count == 0) return;

        // Project gravity onto the (possibly tilted) canvas plane -> downhill direction.
        Transform c = (canvasRenderer != null) ? canvasRenderer.transform : transform;
        Vector3 n = c.up;
        Vector3 downProj = Vector3.down - n * Vector3.Dot(Vector3.down, n);
        float sinAlpha = Mathf.Clamp01(downProj.magnitude); // sin of tilt from horizontal
        canvasTiltDeg = Mathf.Asin(sinAlpha) * Mathf.Rad2Deg;

        Vector2 downUV = Vector2.zero;
        if (sinAlpha > 1e-4f)
        {
            Vector3 dLocal = c.InverseTransformDirection(downProj.normalized);
            // uv = (0.5 - local.x/E, 0.5 - local.z/E)  =>  d(uv) is proportional to (-dLocal.x, -dLocal.z)
            downUV = new Vector2(-dLocal.x, -dLocal.z);
            if (downUV.sqrMagnitude > 1e-10f) downUV.Normalize();
        }

        for (int i = activeSplats.Count - 1; i >= 0; i--)
        {
            ActiveSplat s = activeSplats[i];
            s.age += dt;
            s.localWetTime += dt; // per-splat contact clock: counts from THIS splat's landing, not game start

            // Advance this splat's own capillary absorption and dull its stamp colour accordingly.
            // (Done before stamping so the growth stamp below uses the up-to-date, dulled colour.)
            UpdateSplatAbsorption(s);

            if (!s.isFlowing)
            {
                // Tanner's law: radius approaches R_final as (t/t_v)^(1/10), then settles.
                float rt = FluidConstants.TannerRadius(s.radiusMeters, s.age, s.tv);
                int rtpx = Mathf.Max(2, Mathf.RoundToInt(rt * pixelsPerUnit));
                StampCircle(s.px, s.py, rtpx, s.currentColor);
                textureDirty = true;
            }

            UpdateSurfaceFlow(s, sinAlpha, downUV, dt);

            bool offCanvas = s.px < 0 || s.px >= textureSize || s.py < 0 || s.py >= textureSize;
            // Physical termination depends on the C5 spreading regime:
            //   absorption-limited (Wood/Paper, theta_eq=0): the splat keeps spreading until ITS OWN
            //       capillary absorption saturates (s.localAbsorbFrac -> full); only then is the paint
            //       "soaked in" and growth frozen. This replaces the unphysical t_v (and the old global clock).
            //   partial wetting (Metal/Canvas): a level splat stops after its de Gennes growth window.
            //   a flowing rivulet (either regime) terminates by running off the floor edge.
            // FlowSafetySeconds is only a numerical runaway guard.
            bool dried = s.absorptionLimited
                ? (!s.isFlowing && s.localAbsorbFrac >= AbsorptionFullFraction)
                : (!s.isFlowing && s.age >= s.life);
            bool expired = dried || offCanvas || s.age >= FlowSafetySeconds;
            if (expired) activeSplats.RemoveAt(i);
        }
    }

    // Thin-film (lubrication) flow of a settled splat under gravity on a tilted surface.
    void UpdateSurfaceFlow(ActiveSplat s, float sinAlpha, Vector2 downUV, float dt)
    {
        if (sinAlpha <= 1e-4f) { s.isFlowing = false; return; } // horizontal surface -> no flow

        float R = Mathf.Max(1e-4f, FluidConstants.TannerRadius(s.radiusMeters, s.age, s.tv));
        float h = s.volumeM3 / (Mathf.PI * R * R);   // film thickness h = V / (pi R^2)
        s.filmThickness = h;

        float rho = density, eta = paintViscosityPaS, gamma = surfaceTension;
        float cosAdv = Mathf.Cos(preset.advancingAngleDeg * Mathf.Deg2Rad);
        float cosRec = Mathf.Cos(preset.recedingAngleDeg * Mathf.Deg2Rad);

        // Contact-angle hysteresis: paint stays pinned until gravity beats the capillary pinning force.
        float Fpin = FluidConstants.PinningForce(gamma, cosRec, cosAdv, R); // N/m^2
        float Fg   = rho * gravity * sinAlpha * h;                          // N/m^2
        s.pinningForce = Fpin;
        if (Fg > Fpin) s.isFlowing = true;
        if (!s.isFlowing) return;

        // Nusselt surface velocity and downhill step (sub-pixel centre accumulates slow flow).
        float u = FluidConstants.NusseltFilmVelocity(rho, gravity, sinAlpha, h, eta); // m/s
        float stepPx = u * dt * pixelsPerUnit;
        int oldpx = s.px, oldpy = s.py;
        s.cx += downUV.x * stepPx;
        s.cy += downUV.y * stepPx;
        s.px = Mathf.RoundToInt(s.cx);
        s.py = Mathf.RoundToInt(s.cy);
        s.flowedDistanceUV += stepPx;
        if (s.px == oldpx && s.py == oldpy) return; // hasn't moved a whole pixel yet

        // Rivulet width from mass conservation: w = Q / (u*h);  Q = volume / time.
        float Q = s.volumeM3 / Mathf.Max(0.05f, s.age);       // m^3/s
        float wRiv = (u * h > 1e-9f) ? Q / (u * h) : 2f * R;  // m
        wRiv /= Mathf.Max(1f, preset.rivuletFactor);          // split among multiple channels
        int wpx = Mathf.Clamp(Mathf.RoundToInt(wRiv * pixelsPerUnit), 1,
                              Mathf.Max(2, Mathf.RoundToInt(R * pixelsPerUnit)));

        StampLine(oldpx, oldpy, s.px, s.py, wpx, s.currentColor);

        // Rayleigh-Taylor dripping: a bead forms once the film exceeds the critical thickness.
        float hcrit = FluidConstants.CriticalFilmThickness(gamma, rho, gravity, sinAlpha);
        if (h > hcrit) StampCircle(s.px, s.py, Mathf.Max(2, wpx), s.currentColor);

        textureDirty = true;
    }

    // Per-splat Lucas-Washburn absorption (C6). Replaces the old global AbsorbStep that swept the whole
    // texture on a clock started at Start(). Each active splat soaks in on ITS OWN localWetTime, so a
    // mark drawn two minutes into the session absorbs exactly like one drawn at t=0 — no hidden
    // dependence on game age. As paint soaks in, the colour re-stamped each frame relaxes toward the bare
    // substrate; dulling the stamp colour (not the texture pixels, which the every-frame re-stamp would
    // overwrite) is what makes the fade actually accumulate while the splat is active and freeze once it
    // saturates. Cost is O(1) per splat — cheaper than the old full 1024x1024 sweep.
    void UpdateSplatAbsorption(ActiveSplat s)
    {
        // Non-porous surfaces (metal porosity ~ 0) never absorb -> colour stays at the deposited value.
        if (preset.porosity <= 0.001f) { s.currentColor = s.color; return; }

        // Capillary rise INSIDE the pores is governed by the intrinsic Young angle, not the Wenzel
        // apparent angle (Wenzel describes only the external footprint). Lucas-Washburn 1921; using
        // theta_Young removes the earlier absorption over-estimate (Wood ~ +74 %, Canvas ~ +40 %).
        float cosThetaYoung = Mathf.Cos(preset.contactAngleDeg * Mathf.Deg2Rad);
        float depth = FluidConstants.WashburnDepth(
            preset.poreRadiusMeters, surfaceTension, cosThetaYoung, paintViscosityPaS, s.localWetTime);
        s.localAbsorbFrac = Mathf.Clamp01(depth / Mathf.Max(1e-6f, substrateThicknessMeters));

        // Diagnostic mirror only — nothing downstream reads absorbedPaint for logic (see AbsorbPaint).
        if (absorbedPaint != null && s.px >= 0 && s.px < textureSize && s.py >= 0 && s.py < textureSize)
            absorbedPaint[s.px, s.py] = s.localAbsorbFrac;

        // Dull the stamp colour toward the substrate. Max dulling ~ porosity (a fully soaked porous
        // surface shows the mark faintly); high humidity slows absorption/drying.
        float dull = s.localAbsorbFrac * preset.porosity * (1f - (humidity / 100f) * 0.7f);
        s.currentColor = Color.Lerp(s.color, preset.substrateColor, Mathf.Clamp01(dull));
    }


    void StampCircle(int cx,int cy,int r,Color color)
{
    for(int x=-r;x<=r;x++)
    {
        for(int y=-r;y<=r;y++)
        {

            if(x*x+y*y <= r*r)
            {

                int px=cx+x;
                int py=cy+y;


                if(px>=0 &&
                   px<texture.width &&
                   py>=0 &&
                   py<texture.height)
                {

                    texture.SetPixel(
                    px,
                    py,
                    color
                    );

                }
            }
        }
    }
}

    void StampRing(int cx, int cy, int r, Color col)
    {
        int inner = Mathf.Max(0, r - 2);
        int rough = roughnessJitterPx;
        for (int x = -r; x <= r; x++)
            for (int y = -r; y <= r; y++)
            {
                int d2 = x * x + y * y;
                if (d2 <= r * r && d2 >= inner * inner) PutPixel(cx + x, cy + y, col, rough);
            }
    }

    void StampEllipse(int cx, int cy, float rx, float ry, float angRad, Color col)
    {
        float cos = Mathf.Cos(angRad), sin = Mathf.Sin(angRad);
        int rmax = Mathf.CeilToInt(Mathf.Max(rx, ry));
        int rough = roughnessJitterPx;
        for (int x = -rmax; x <= rmax; x++)
            for (int y = -rmax; y <= rmax; y++)
            {
                float lx = x * cos + y * sin, ly = -x * sin + y * cos;
                if ((lx * lx) / (rx * rx) + (ly * ly) / (ry * ry) <= 1f) PutPixel(cx + x, cy + y, col, rough);
            }
    }

    void StampStreak(int cx, int cy, Vector2 dir, float length, int width, Color col)
    {
        int steps = Mathf.Max(2, Mathf.CeilToInt(length));
        for (int i = -steps / 3; i <= steps; i++)
        {
            float t = (float)i / steps;
            int x = cx + Mathf.RoundToInt(dir.x * i), y = cy + Mathf.RoundToInt(dir.y * i);
            StampCircle(x, y, Mathf.Max(1, Mathf.RoundToInt(width * (1f - Mathf.Abs(t)))), col);
        }
    }

    // Crown wall: `fingers` jets (Rayleigh-Taylor count) sitting on the lamella rim (radius rPx),
    // each the size of a broken-off rim droplet (jetR).
    void StampCrown(int cx, int cy, int rPx, Color col, int fingers, int jetR)
    {
        for (int i = 0; i < fingers; i++)
        {
            float a = i * Mathf.PI * 2f / fingers;
            StampCircle(cx + Mathf.RoundToInt(Mathf.Cos(a) * rPx),
                        cy + Mathf.RoundToInt(Mathf.Sin(a) * rPx), jetR, col);
        }
    }

    // Ejected satellite droplets. The MEANS here are all physical; only the per-droplet FLUCTUATIONS
    // are random, and that randomness is physically irreducible: the Rayleigh-Plateau break-up of the
    // corona fingers is a stochastic instability, so finger spacing, ejection speed and droplet size
    // scatter around their mean values instead of being identical (Yarin 2006, Ann. Rev. Fluid Mech.
    // 38, 159; Villermaux 2007, Ann. Rev. Fluid Mech. 39, 419). A perfectly uniform ring is the ONE
    // thing real splash never produces. Called once per impact (mark is permanent), so no per-frame flicker.
    //
    // RANGE MEAN: the vacuum ballistic range (u^2*sin2θ/g) over-predicts on mm drops where air drag
    // dominates; real scatter lands within 3-5× the crater radius (Rioboo et al. 2002, Exp. Fluids 33,
    // 112). Mean reach = rPx*(1 + kExcess*ScatterReachFactor); kExcess = 1 - Kc/K is the fraction of
    // impact energy above the splash limit (physical origin: u_eject = v*sqrt(kExcess)).
    void ScatterDroplets(int cx, int cy, int rPx, Color col, int count, int dropletR, float kThreshold)
    {
        float kExcess  = Mathf.Clamp01(1f - kThreshold / Mathf.Max(1e-3f, K));
        const float ScatterReachFactor = 4f;   // mean reach up to 5× rPx when K >> threshold
        float meanReach = Mathf.Max(rPx + 1, rPx * (1f + kExcess * ScatterReachFactor));

        // Ejection probability: only fingers whose ligament exceeds the Rayleigh break-up length shed a
        // droplet; the excess energy (kExcess) raises the fraction that do. So not every finger ejects.
        float ejectProb = Mathf.Clamp01(0.35f + 0.65f * kExcess);

        for (int i = 0; i < count; i++)
        {
            if (Random.value > ejectProb) continue;   // this finger's ligament didn't break off

            // Azimuth: mean spacing 2π/count, jittered by up to ±half a spacing (fingers are not equidistant).
            float a = (i + Random.Range(-0.5f, 0.5f)) * Mathf.PI * 2f / count;
            // Reach: ejection-speed spread scatters landing distance around the mean (±~40%).
            float reach = meanReach * Random.Range(0.6f, 1.15f);
            // Size: satellite-droplet size distribution around the mean rim scale; still << main splat.
            int dR = Mathf.Max(1, Mathf.RoundToInt(dropletR * Random.Range(0.55f, 1.2f)));

            StampCircle(cx + Mathf.RoundToInt(Mathf.Cos(a) * reach),
                        cy + Mathf.RoundToInt(Mathf.Sin(a) * reach), dR, col);
        }
    }

    void StampLine(int x0, int y0, int x1, int y1, int r, Color col)
    {
        int steps = Mathf.Max(1, Mathf.RoundToInt(Vector2.Distance(new Vector2(x0, y0), new Vector2(x1, y1))));
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            StampCircle(Mathf.RoundToInt(Mathf.Lerp(x0, x1, t)), Mathf.RoundToInt(Mathf.Lerp(y0, y1, t)), r, col);
        }
    }

    void PutPixel(int px, int py, Color col, int rough)
    {
        if (rough > 0) { px += Random.Range(-rough, rough + 1); py += Random.Range(-rough, rough + 1); }
        if (px < 0 || px >= texture.width || py < 0 || py >= texture.height) return;
        // Surface-retained fraction: porosity of the substrate goes into the pores, so a porous
        // surface shows the mark fainter.  intensity = 1 - porosity  (metal 1.0 ... paper 0.4).
        float intensity = Mathf.Clamp01(1f - preset.porosity);
        texture.SetPixel(px, py, Color.Lerp(texture.GetPixel(px, py), col, intensity));
    }

    // Deposit a paint film (metres) with a triangular profile peaking at the centre (puddles are
    // thicker in the middle). filmThicknessMeters = V / (pi R^2) is the physical mean thickness.
    void AddPaintThickness(int cx, int cy, int radius, float filmThicknessMeters)
{
    int rad = Mathf.Max(1, radius);
    for(int x = -rad; x <= rad; x++)
    {
        for(int y = -rad; y <= rad; y++)
        {

            int px = cx + x;
            int py = cy + y;


            if(px < 0 ||
               px >= textureSize ||
               py < 0 ||
               py >= textureSize)
                continue;



            float distance =
                Mathf.Sqrt(
                    x*x + y*y
                );



            if(distance <= rad)
            {

                float amount =
                    filmThicknessMeters *
                    (1f - distance / rad);



                paintThickness[px,py] =
                    Mathf.Clamp(
                        paintThickness[px,py]
                        +
                        amount,

                        0f,
                        maxFilmThicknessMeters
                    );
            }
        }
    }
}
    // public utilities
    public void Clear()
    {
        if (texture == null) return;
        // Start from the bare-surface (substrate) colour instead of a unified white, so each surface
        // reads correctly when blank (grey metal, brown wood, cream canvas/paper).
        Color baseColor = preset.substrateColor;
        if (baseColor.a <= 0f) baseColor = Color.white; // guard: preset not yet assigned
        Color[] cols = new Color[texture.width * texture.height];
        for (int i = 0; i < cols.Length; i++) cols[i] = baseColor;
        texture.SetPixels(cols);
        texture.Apply();
        activeSplats.Clear(); // also discards every splat's per-splat localWetTime/localAbsorbFrac
        lastSplatValid = false;
        if (paintThickness != null) System.Array.Clear(paintThickness, 0, paintThickness.Length);
        if (absorbedPaint != null) System.Array.Clear(absorbedPaint, 0, absorbedPaint.Length);
    }

    public void SavePainting()
    {
        string filename = $"Painting_{System.DateTime.Now:yyyyMMdd_HHmmss}.png";
        System.IO.File.WriteAllBytes(Application.dataPath + "/" + filename, texture.EncodeToPNG());
        Debug.Log("Saved painting: " + filename);
    }
   // C6: called once when a splat lands. At birth nothing has soaked in yet, so the per-pixel
   // diagnostic starts at 0. Saturation then advances per-splat in UpdateSplatAbsorption() from that
   // splat's own localWetTime — there is no global absorption clock anymore.
   void AbsorbPaint(int x,int y)
   {
       if (absorbedPaint == null) return;
       absorbedPaint[x, y] = 0f;
   }
    public float GetPaintAreaCoverage()
    {
        if (texture == null) return 0f;
        // "Covered" now means "differs from the bare SUBSTRATE colour" (the substrate is no longer
        // assumed white, so a fixed near-white threshold would wrongly count blank grey metal as 100%).
        Color32 sub = (Color32)preset.substrateColor;
        const int tol = 12; // per-channel 8-bit tolerance for "still bare surface"
        Color32[] cols = texture.GetPixels32();
        int colored = 0;
        for (int i = 0; i < cols.Length; i++)
            if (Mathf.Abs(cols[i].r - sub.r) > tol ||
                Mathf.Abs(cols[i].g - sub.g) > tol ||
                Mathf.Abs(cols[i].b - sub.b) > tol) colored++;
        return (float)colored / cols.Length;
    }
}
