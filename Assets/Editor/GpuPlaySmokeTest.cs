using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Editor utility: headless play-mode smoke test for the GPU liquid pipeline.
//   Unity -batchmode -projectPath . -executeMethod GpuPlaySmokeTest.Run
// (no -quit: the test exits Unity itself). Enters play mode on SampleScene and
// checks two phases:
//   Phase 1 (10 s, default 10k preset): bridge bootstrapped, GPU mode active,
//     particles alive inside the bucket, pour + GPU canvas splats happening.
//   Phase 2 (14 s, switched to the 200k preset): buffers reallocate live and
//     the reservoir refills past 100k particles without exceptions.
// Red console errors fail the test, EXCEPT Unity-editor-internal tooling noise
// (QuickSearch indexing throws on batch startup — unrelated to the scene).
// Exit code 0 = pass. Lets the pipeline be validated on machines with no display.
public static class GpuPlaySmokeTest
{
    public static void Run()
    {
        SessionState.SetBool("GpuSmoke.Active", true);
        EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        EditorApplication.EnterPlaymode();   // domain reloads; hook below resumes
    }
}

[InitializeOnLoad]
static class GpuPlaySmokeHook
{
    static double t0;
    static int redErrors;
    static int phase = 1;        // 1 = 10k default, 2 = 200k stress
    static int phaseStartFrame;
    static double phaseStartTime;
    static int fails;

    static GpuPlaySmokeHook()
    {
        if (!SessionState.GetBool("GpuSmoke.Active", false)) return;
        if (!EditorApplication.isPlayingOrWillChangePlaymode) return;
        t0 = EditorApplication.timeSinceStartup;
        Application.logMessageReceived += CountRed;
        EditorApplication.update += Tick;
    }

    static void CountRed(string msg, string stack, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        // Editor-internal tooling (QuickSearch startup indexing etc.) is not
        // part of the simulation under test.
        if (stack != null && (stack.Contains("UnityEditor.Search") || stack.Contains("UnityEditor.PackageManager")))
            return;
        redErrors++;
        Debug.LogWarning($"[GpuSmoke] RED: {type}: {msg}");
    }

    static void Tick()
    {
        if (!Application.isPlaying) return;
        double elapsed = EditorApplication.timeSinceStartup - (phase == 1 ? t0 : phaseStartTime);
        if (elapsed < (phase == 1 ? 10.0 : 14.0)) return;

        var bridge = GpuLiquidBridge.Instance;
        if (bridge == null)
        {
            Debug.LogError("[GpuSmoke] bridge did not bootstrap");
            Finish(1);
            return;
        }
        var sim = bridge.Sim;
        float fps = phase == 2
            ? (Time.frameCount - phaseStartFrame) / Mathf.Max(0.01f, (float)elapsed) : 0f;
        Debug.Log($"[GpuSmoke] phase{phase}: supported={bridge.gpuSupported} active={bridge.gpuActive} " +
                  $"preset={bridge.CurrentPresetName} alive={sim.aliveCount} inside={sim.insideCount} " +
                  $"air={sim.airborneCount} avgNb={sim.avgNeighbors:F1} h={sim.currentH * 100f:F2}cm " +
                  $"painted={sim.totalPainted}" + (phase == 2 ? $" fps~{fps:F0}" : ""));

        if (phase == 1)
        {
            if (!bridge.gpuSupported) { Debug.LogError("[GpuSmoke] compute unsupported"); fails++; }
            if (!bridge.gpuActive) { Debug.LogError("[GpuSmoke] GPU mode not active"); fails++; }
            if (sim.aliveCount <= 0) { Debug.LogError("[GpuSmoke] no particles alive after 10 s"); fails++; }
            if (sim.insideCount <= 0) { Debug.LogError("[GpuSmoke] no particles inside the bucket"); fails++; }
            if (sim.totalPainted <= 0) { Debug.LogError("[GpuSmoke] no GPU canvas impacts"); fails++; }

            phase = 2;
            phaseStartTime = EditorApplication.timeSinceStartup;
            phaseStartFrame = Time.frameCount;
            Debug.Log("[GpuSmoke] switching preset to 200k...");
            bridge.SetPreset(200000);
            return;
        }

        // phase 2 — 200k stress
        if (sim.aliveCount < 100000)
        { Debug.LogError($"[GpuSmoke] 200k preset: only {sim.aliveCount} alive after 14 s"); fails++; }
        Finish(fails);
    }

    static void Finish(int f)
    {
        EditorApplication.update -= Tick;
        Application.logMessageReceived -= CountRed;
        SessionState.SetBool("GpuSmoke.Active", false);
        if (redErrors > 0) { Debug.LogError($"[GpuSmoke] {redErrors} red console errors during play"); f++; }
        Debug.Log($"[GpuSmoke] RESULT: {(f == 0 ? "PASS" : $"FAIL ({f})")}");
        if (Application.isBatchMode) EditorApplication.Exit(f == 0 ? 0 : 1);
        else EditorApplication.ExitPlaymode();
    }
}
