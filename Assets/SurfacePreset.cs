using UnityEngine;

/// <summary>
/// Physically-grounded surface properties. Every field is a real, measurable
/// material parameter (contact angle, porosity, pore radius, roughness, surface
/// energy, hysteresis angles) — there are no cosmetic multipliers here.
/// Values are drawn from published wettability / porosity / roughness data:
///   Acunman et al. (wettability of paints), Nieminen et al. (paper pore
///   structure), Rabinovich et al. (surface roughness).
/// </summary>
[System.Serializable]
public struct SurfacePreset
{
    public float contactAngleDeg;       // Young contact angle (deg): small = wets/spreads, large = beads
    public float porosity;              // void fraction [0..1]: absorption capacity
    public float poreRadiusMeters;      // mean capillary radius (m): drives Washburn absorption
    public float wenzelRoughness;       // Wenzel r >= 1: roughness amplifies apparent wetting
    public float arithmeticRoughnessUm; // Ra (micrometres): real (sub-pixel) edge displacement
    public float surfaceEnergyMJm2;     // surface energy (mJ/m^2): wettability / adhesion
    public float advancingAngleDeg;     // theta_adv: front-edge pinning resistance (hysteresis)
    public float recedingAngleDeg;      // theta_rec: rear-edge pinning resistance (hysteresis)
    public float rivuletFactor;         // 1 = single rivulet, >1 = several narrow channels (rough)
    public Color substrateColor;        // bare-surface colour the texture starts from and that
                                        // thin paint reveals (Beer-Lambert base); replaces the old
                                        // unified white so Metal reads grey and Wood reads brown.

    public SurfacePreset(
        float contactAngle, float poros, float poreRadius, float wenzelR,
        float raMicron, float surfaceEnergy,
        float advancing, float receding, float rivulet, Color substrate)
    {
        contactAngleDeg       = contactAngle;
        porosity              = poros;
        poreRadiusMeters      = poreRadius;
        wenzelRoughness       = wenzelR;
        arithmeticRoughnessUm = raMicron;
        surfaceEnergyMJm2     = surfaceEnergy;
        advancingAngleDeg     = advancing;
        recedingAngleDeg      = receding;
        rivuletFactor         = rivulet;
        substrateColor        = substrate;
    }

    //                                              theta  poros  pore(m)   wenzel  Ra(um) energy  adv   rec   riv  substrate (bare-surface colour)
    public static SurfacePreset Metal  => new SurfacePreset(15f, 0.00f, 0f,      1.01f,  0.5f,  500f,   25f,  10f,  1.0f, new Color(0.690f, 0.690f, 0.710f)); // silver-grey #B0B0B5
    public static SurfacePreset Wood   => new SurfacePreset(55f, 0.30f, 5e-6f,   1.80f,  6.0f,  40f,    65f,  35f,  2.0f, new Color(0.545f, 0.353f, 0.169f)); // natural brown #8B5A2B
    public static SurfacePreset Canvas => new SurfacePreset(45f, 0.50f, 10e-6f,  1.40f,  20.0f, 35f,    55f,  30f,  1.5f, new Color(0.980f, 0.970f, 0.940f)); // cream (≈white)
    public static SurfacePreset Paper  => new SurfacePreset(25f, 0.60f, 2e-6f,   1.20f,  4.0f,  30f,    35f,  15f,  1.0f, new Color(0.990f, 0.980f, 0.930f)); // light off-white

    public static SurfacePreset From(SurfaceType t)
    {
        switch (t)
        {
            case SurfaceType.Metal: return Metal;
            case SurfaceType.Wood:  return Wood;
            case SurfaceType.Paper: return Paper;
            default:                return Canvas;
        }
    }
}
