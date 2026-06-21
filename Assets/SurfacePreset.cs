[System.Serializable]
public struct SurfacePreset
{
    public float surfaceSpread;
    public float surfaceAbsorption;   
    public float surfaceRoughness;
    public float splashProbability;

    public SurfacePreset(float spread, float absorption, float roughness, float splash)
    {
        surfaceSpread = spread; surfaceAbsorption = absorption;
        surfaceRoughness = roughness; splashProbability = splash;
    }

    public static SurfacePreset Smooth    => new SurfacePreset(0.9f, 0.005f, 0.0f, 0.05f);
    public static SurfacePreset Rough     => new SurfacePreset(1.6f, 0.02f,  0.6f, 0.40f);
    public static SurfacePreset Absorbent => new SurfacePreset(1.3f, 0.08f,  0.2f, 0.10f);

    public static SurfacePreset From(SurfaceType t)
    {
        switch (t)
        {
            case SurfaceType.Metal: return Smooth;
            case SurfaceType.Wood:  return Rough;
            case SurfaceType.Paper: return Absorbent;
            default:                return new SurfacePreset(1.2f, 0.005f, 0.3f, 0.2f); // Canvas
        }
    }
}
