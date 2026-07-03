using UnityEngine;

/// <summary>
/// Documented fluid-physics relations used by the paint simulation.
/// Everything here is in SI units (metre, kilogram, second, Pa·s, N/m).
/// No cosmetic fudge factors: every quantity the surfaces use is derived from
/// one of these published relations.
/// </summary>
public static class FluidConstants
{
    // --- Generic reference fluid (water) ---
    public const float WaterDensity = 1000f;            // kg/m^3
    public const float DefaultSurfaceTension = 0.072f;  // N/m

    // --- Paint properties (SI) — single source of truth for the macroscopic model ---
    // Representative latex/acrylic paint. These are physical inputs, not tuning knobs.
    public const float PaintDensity        = 1300f;   // kg/m^3  (rho)
    public const float PaintSurfaceTension = 0.035f;  // N/m     (gamma)
    public const float PaintViscosity      = 0.1f;    // Pa·s    (eta, dynamic viscosity)

    // Steel (bucket material) — used for the DISPLACED-volume buoyancy estimate.
    public const float SteelDensity = 7850f;          // kg/m^3

    // --- Viscosity–temperature (Arrhenius / Andrade 1930) ---
    //   eta(T) = eta_ref * exp( B * (1/T - 1/T_ref) ),  T in kelvin.
    // B (activation temperature E_a/R) for waterborne paints and similar
    // structured liquids is ~2000-4000 K; 3000 K is a mid-range literature value.
    // Replaces the old ad-hoc linear lerp between two arbitrary viscosity bounds.
    public const float ViscosityArrheniusB    = 3000f;   // K
    public const float ViscosityRefTempKelvin = 298.15f; // 25 °C reference

    public static float ViscosityTemperatureFactor(float tempCelsius)
    {
        float T = Mathf.Max(233.15f, tempCelsius + 273.15f); // clamp above -40 °C for safety
        return Mathf.Exp(ViscosityArrheniusB * (1f / T - 1f / ViscosityRefTempKelvin));
    }

    // --- Torricelli efflux (Bernoulli) ---
    //   v = Cd * sqrt(2 g h) through a sharp-edged orifice under liquid head h.
    // Cd = 0.61 is the classical sharp-edged-orifice discharge coefficient
    // (already includes the vena-contracta contraction).
    public const float OrificeDischargeCoefficient = 0.61f;

    public static float TorricelliSpeed(float g, float headMeters)
        => OrificeDischargeCoefficient * Mathf.Sqrt(2f * Mathf.Max(0f, g) * Mathf.Max(0f, headMeters));

    // --- Kubelka-Munk (1931) two-flux paint colour mixing ---
    // Physical basis: a paint layer is characterised per wavelength by its absorption
    // K and scattering S; the diffuse reflectance of an opaque layer satisfies
    //   K/S = (1 - R)^2 / (2 R)                       (Kubelka & Munk 1931)
    // and mixtures follow Duncan's additivity law (Duncan 1940, Proc. Phys. Soc. 52):
    //   (K/S)_mix = sum_i c_i (K/S)_i                 (c_i = volume/mass fractions)
    // Inverting gives the mixed reflectance:
    //   R_mix = 1 + (K/S) - sqrt( (K/S)^2 + 2 (K/S) ).
    // Evaluated per RGB channel (the standard 3-band approximation used in paint
    // formulation software). This is what makes blue + yellow give GREEN — a plain
    // RGB lerp would give grey, which real paint never does.
    const float KMReflectanceMin = 0.02f; // real pigments are never perfect black/white:
    const float KMReflectanceMax = 0.98f; // keeps K/S finite (standard practice in KM software)

    static float KMKoverS(float R)
    {
        R = Mathf.Clamp(R, KMReflectanceMin, KMReflectanceMax);
        return (1f - R) * (1f - R) / (2f * R);
    }

    static float KMReflectance(float ks)
        => Mathf.Clamp01(1f + ks - Mathf.Sqrt(ks * ks + 2f * ks));

    // Mix two paints with weights wa/wb (any proportional measure: volumes, film
    // thicknesses, masses — only the ratio matters).
    public static Color KubelkaMunkMix(Color a, Color b, float wa, float wb)
    {
        float total = wa + wb;
        if (total <= 0f) return b;
        float ca = wa / total, cb = wb / total;
        return new Color(
            KMReflectance(ca * KMKoverS(a.r) + cb * KMKoverS(b.r)),
            KMReflectance(ca * KMKoverS(a.g) + cb * KMKoverS(b.g)),
            KMReflectance(ca * KMKoverS(a.b) + cb * KMKoverS(b.b)),
            1f);
    }

    // Stow & Hadfield (1981) deposition/splash limit on the K parameter (smooth, dry reference surface).
    public const float SplashThresholdK = 57.7f;

    // --- Roughness correction on the splash threshold ---
    // Surface roughness lowers the splash/deposition limit (a rougher surface trips fingering sooner).
    // هذا تصحيح هندسي تقريبي وليس معادلة مستخرجة حرفياً من ورقة محكّمة — alpha معاير يدوياً.
    // (general trend only, after Mundo et al. 1995 / Rioboo et al. 2002; NO closed-form citation exists.)
    public const float SplashRoughnessRefUm = 25f;   // Ra reference (upper-ish bound of our surfaces)
    public const float SplashRoughnessAlpha = 0.4f;  // hand-tuned sensitivity (start value)

    // K_eff = K_smooth * (1 - alpha * min(Ra/Ra_ref, 1)).  Ra in micrometres.
    public static float SplashThresholdRough(float raMicrometres)
    {
        float reduction = SplashRoughnessAlpha * Mathf.Clamp01(raMicrometres / SplashRoughnessRefUm);
        return SplashThresholdK * (1f - reduction);
    }

    // Paint hiding power: the wet-film thickness at which the paint becomes opaque over the
    // substrate (a real, tabulated paint property ~ tens of microns).
    public const float PaintHidingThicknessMeters = 50e-6f; // ~50 um

    // --- Dimensionless numbers ---
    public static float Reynolds(float rho, float v, float D, float mu)
        => (mu <= 0f) ? 0f : rho * v * D / mu;

    public static float Weber(float rho, float v, float D, float sigma)
        => (sigma <= 0f) ? 0f : rho * v * v * D / sigma;

    public static float Capillary(float mu, float v, float sigma)
        => (sigma <= 0f) ? 0f : mu * v / sigma;

    public static float Ohnesorge(float mu, float rho, float sigma, float D)
    {
        float denom = rho * sigma * D;
        return (denom <= 0f) ? 0f : mu / Mathf.Sqrt(denom);
    }

    // Young's equation: cos(theta) = (gamma_SV - gamma_SL) / gamma_LV
    public static float YoungContactAngleCos(float gammaSV, float gammaSL, float gammaLV)
        => (gammaLV <= 0f) ? 0f : Mathf.Clamp((gammaSV - gammaSL) / gammaLV, -1f, 1f);

    // Wenzel (1936): roughness amplifies wetting. cos(theta*) = r * cos(theta_Young).
    // Returns the apparent (Wenzel) contact-angle cosine, clamped to a valid range.
    // NOTE on the clamp: when r*cos(theta_Young) >= 1 the surface is in the COMPLETE-WETTING /
    // superhydrophilic regime (apparent theta* = 0). This is NOT an error — it is simply OUTSIDE the
    // classic Wenzel model's validity (the film impregnates the texture; see Bico, Thiele & Quéré
    // 2002, Colloids Surf. A 206, 41-46). Callers that need a finite capillary timescale must treat
    // theta* = 0 specially (it has no finite Tanner/de Gennes relaxation time — see PaintPhysics C5).
    public static float WenzelCos(float roughnessR, float youngAngleRad)
        => Mathf.Clamp(roughnessR * Mathf.Cos(youngAngleRad), -1f, 1f);

    // Stow & Hadfield (1981), Cossali et al. (1997):
    // K = sqrt(We) * Re^0.25 ;  K > 57.7 -> splash, otherwise deposition.
    public static float StowHadfieldK(float We, float Re)
        => Mathf.Sqrt(Mathf.Max(0f, We)) * Mathf.Pow(Mathf.Max(0f, Re), 0.25f);

    // Pasandideh-Fard / Madejski (1996) maximum spread factor beta_max = D_splat / D_drop:
    // beta_max = sqrt( (We + 12) / (3*(1 - cos(theta*)) + 4*We/sqrt(Re)) ).
    public static float MadejskiBetaMax(float We, float Re, float cosThetaStar)
    {
        float denom = 3f * (1f - cosThetaStar) + 4f * We / Mathf.Sqrt(Mathf.Max(1f, Re));
        denom = Mathf.Max(denom, 1e-4f);
        return Mathf.Sqrt((We + 12f) / denom);
    }

    // de Gennes (1985, Rev. Mod. Phys. 57, 827) capillary-spreading relaxation time for a
    // PARTIALLY-wetting drop:  t_v = eta * R / (gamma * theta_eq^3),  theta_eq = apparent (Wenzel)
    // equilibrium contact angle [rad]. This is the timescale that actually pairs with Tanner's
    // R(t) = R_final*(t/t_v)^(1/10) capillary law. Valid only for theta_eq > 0; for complete wetting
    // (theta_eq -> 0) t_v -> infinity, so the caller must switch to an absorption/geometry-limited model.
    public static float TannerRelaxTimeDeGennes(float eta, float gamma, float Rfinal, float thetaEqRad)
    {
        float th = Mathf.Max(0.01f, thetaEqRad);          // floor avoids divide-by-~0 near complete wetting
        float denom = gamma * th * th * th;
        return (denom <= 0f) ? 0f : eta * Mathf.Max(1e-6f, Rfinal) / denom;
    }

    // Tanner's law spreading radius: R(t) = R_final * (t/t_v)^(1/10).
    public static float TannerRadius(float Rfinal, float age, float tv)
    {
        if (tv <= 0f) return Rfinal;
        return Rfinal * Mathf.Pow(Mathf.Clamp01(age / tv), 0.1f);
    }

    // Washburn (1921) capillary penetration depth:
    // depth(t) = sqrt( r_pore*sigma*cos(theta*) / (2*eta) * t ).  depth grows as sqrt(t).
    public static float WashburnDepth(float poreRadius, float sigma, float cosThetaStar, float eta, float t)
    {
        if (cosThetaStar <= 0f || eta <= 0f || poreRadius <= 0f || t <= 0f) return 0f;
        float rate = poreRadius * sigma * cosThetaStar / (2f * eta);
        return Mathf.Sqrt(Mathf.Max(0f, rate * t));
    }

    // Nusselt thin-film (lubrication) surface velocity: u = rho*g*sin(alpha)*h^2 / (3*eta).
    public static float NusseltFilmVelocity(float rho, float g, float sinAlpha, float h, float eta)
        => (eta <= 0f) ? 0f : rho * g * sinAlpha * h * h / (3f * eta);

    // Critical film thickness for Rayleigh-Taylor dripping: h_crit = sqrt( sigma / (rho*g*sin(alpha)) ).
    // (No cos(alpha): at alpha = 90 deg the vertical film is still unstable.)
    public static float CriticalFilmThickness(float sigma, float rho, float g, float sinAlpha)
    {
        float denom = rho * g * sinAlpha;
        return (denom <= 0f) ? float.PositiveInfinity : Mathf.Sqrt(sigma / denom);
    }

    // Contact-angle-hysteresis pinning force per unit length:
    // F_pin = sigma * (cos(theta_rec) - cos(theta_adv)) / R   [N/m^2 when compared to rho*g*sin(a)*h].
    public static float PinningForce(float sigma, float cosRec, float cosAdv, float R)
        => (R <= 0f) ? 0f : sigma * (cosRec - cosAdv) / R;

    // Beer-Lambert hiding power of a paint film of thickness h over a substrate:
    //   opacity = 1 - exp(-h / h_hide).  Thin film -> translucent, thick film -> opaque.
    public static float BeerLambertOpacity(float thickness, float hidingThickness)
        => (hidingThickness <= 0f) ? 1f : 1f - Mathf.Exp(-Mathf.Max(0f, thickness) / hidingThickness);

    // Number of splash fingers from the Rayleigh-Taylor instability of the decelerating rim.
    // The rim decelerates at a ~ v^2/(2 R_max); with the RT most-unstable wavelength
    // lambda = 2*pi*sqrt(3 sigma/(rho a)) and N = pi*D_max/lambda this reduces to:
    //   N = sqrt( beta_max * We / 12 ).   (The 12 comes from the RT wavelength + rim geometry.)
    public static float SplashFingerCount(float We, float betaMax)
        => Mathf.Sqrt(Mathf.Max(0f, betaMax) * Mathf.Max(0f, We) / 12f);
}
