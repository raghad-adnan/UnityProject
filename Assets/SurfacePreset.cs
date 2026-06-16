// Surface preset as data: spread / absorption / roughness / splash (Task 3.13)
// Three base archetypes: Smooth / Rough / Absorbent - mapped to material types.
[System.Serializable]
public struct SurfacePreset
{
    public float surfaceSpread;       // spreading amount
    public float surfaceAbsorption;   // absorption rate (k in C(t)=C0*e^-kt)
    public float surfaceRoughness;    // roughness (irregular edges)
    public float splashProbability;   // chance of splash/scatter

    public SurfacePreset(float spread, float absorption, float roughness, float splash)
    {
        surfaceSpread = spread;
        surfaceAbsorption = absorption;
        surfaceRoughness = roughness;
        splashProbability = splash;
    }

    // The three base archetypes
    public static SurfacePreset Smooth    => new SurfacePreset(0.9f, 0.005f, 0.0f, 0.05f);
    public static SurfacePreset Rough     => new SurfacePreset(1.6f, 0.02f,  0.6f, 0.40f);
    public static SurfacePreset Absorbent => new SurfacePreset(1.3f, 0.08f,  0.2f, 0.10f);

    // Map material type to the closest archetype
    public static SurfacePreset From(SurfaceType t)
    {
        switch (t)
        {
            case SurfaceType.Metal: return Smooth;                       // metal -> smooth
            case SurfaceType.Wood:  return Rough;                        // wood -> rough
            case SurfaceType.Paper: return Absorbent;                    // paper -> absorbent
            default:                return new SurfacePreset(1.2f, 0.005f, 0.3f, 0.2f); // canvas (light absorption so paint builds up)
        }
    }
}
