using UnityEngine;

public static class FluidConstants
{
    public const float WaterDensity = 1000f;           
    public const float DefaultSurfaceTension = 0.072f; 

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
}
