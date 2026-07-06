using UnityEditor;
using UnityEngine;

// Editor utility: validates that the GPU-liquid shaders compile and that every
// kernel GpuLiquidSimulation.cs expects actually exists. Run from the CLI:
//   Unity -batchmode -quit -executeMethod GpuShaderCheck.Run
// Exits non-zero on any error, so it can gate CI / pre-hand-in checks.
public static class GpuShaderCheck
{
    static readonly string[] SimKernels =
    {
        "InitParticles", "ClearPerFrame", "Spawn", "PredictPositions",
        "ClearGrid", "BuildGrid", "ComputeLambda", "ComputeDelta",
        "ApplyDelta", "ComputeVelocity", "FinalizeFrame", "UpdateArgs"
    };
    static readonly string[] PainterKernels = { "PaintSplats", "ClearCanvas" };

    public static void Run()
    {
        int errors = 0;
        errors += CheckCompute("LiquidSPH", SimKernels);
        errors += CheckCompute("GpuCanvasPainter", PainterKernels);

        Shader sh = Shader.Find("Custom/GpuPaintParticle");
        if (sh == null) { Debug.LogError("[GpuShaderCheck] Custom/GpuPaintParticle not found"); errors++; }
        else
        {
            foreach (var m in ShaderUtil.GetShaderMessages(sh))
            {
                Debug.Log($"[GpuShaderCheck] GpuPaintParticle {m.severity}: {m.message} (line {m.line})");
                if (m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error) errors++;
            }
            if (ShaderUtil.ShaderHasError(sh)) { Debug.LogError("[GpuShaderCheck] GpuPaintParticle has errors"); errors++; }
            else Debug.Log("[GpuShaderCheck] GpuPaintParticle OK");
        }

        Debug.Log($"[GpuShaderCheck] TOTAL ERRORS: {errors}");
        if (Application.isBatchMode) EditorApplication.Exit(errors == 0 ? 0 : 1);
    }

    static int CheckCompute(string resourceName, string[] kernels)
    {
        int errors = 0;
        var cs = Resources.Load<ComputeShader>(resourceName);
        if (cs == null) { Debug.LogError($"[GpuShaderCheck] {resourceName}.compute not found"); return 1; }

        foreach (var m in ShaderUtil.GetComputeShaderMessages(cs))
        {
            Debug.Log($"[GpuShaderCheck] {resourceName} {m.severity}: {m.message} (line {m.line})");
            if (m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error) errors++;
        }
        foreach (string k in kernels)
        {
            if (!cs.HasKernel(k)) { Debug.LogError($"[GpuShaderCheck] {resourceName}: kernel '{k}' missing/failed"); errors++; }
        }
        if (errors == 0) Debug.Log($"[GpuShaderCheck] {resourceName} OK ({kernels.Length} kernels)");
        return errors;
    }
}
