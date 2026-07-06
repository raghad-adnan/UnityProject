using UnityEngine;

// ============================================================================
//  GpuLiquidRenderer — draws every GPU particle with ONE indirect draw call.
// ----------------------------------------------------------------------------
//  Graphics.RenderMeshIndirect + a 4-vertex quad + Custom/GpuPaintParticle:
//    * the instance count lives in the indirect-args buffer, written by the
//      sim's UpdateArgs kernel from the GPU alive-particle counter — the CPU
//      neither counts particles nor builds matrices (contrast with the CPU
//      pool's 1023-instance DrawMeshInstanced batches, which build 10,000
//      Matrix4x4 structs on the CPU every frame);
//    * the vertex shader fetches each particle's position/colour/size straight
//      from the simulation StructuredBuffer through the alive-index list, and
//      shades the quad as a sphere impostor;
//    * particles are OPAQUE so the transparent glass bucket (Fade queue)
//      blends over them — the liquid reads clearly inside the container
//      without any transparency-sorting work.
// ============================================================================
public sealed class GpuLiquidRenderer : MonoBehaviour
{
    Mesh quad;

    // Called by GpuLiquidBridge every frame while GPU mode is active (after
    // the sim step, so the draw sees this frame's state).
    public void Draw(GpuLiquidSimulation sim)
    {
        if (sim == null || !sim.LoadOk || sim.ParticleMaterial == null || sim.DrawArgsBuffer == null)
            return;
        EnsureQuad();

        var rp = new RenderParams(sim.ParticleMaterial)
        {
            // Particles roam bucket -> canvas; a generous fixed bound skips
            // per-frame CPU bounds tracking (no readback allowed).
            worldBounds = new Bounds(Vector3.zero, new Vector3(500f, 500f, 500f)),
            shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off,
            receiveShadows = false
        };
        Graphics.RenderMeshIndirect(rp, quad, sim.DrawArgsBuffer, 1);
    }

    // Unit quad in the XY plane; the vertex shader billboards and scales it.
    void EnsureQuad()
    {
        if (quad != null) return;
        quad = new Mesh { name = "GpuLiquidQuad" };
        quad.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0),
            new Vector3(-0.5f,  0.5f, 0), new Vector3(0.5f,  0.5f, 0)
        };
        quad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
        quad.bounds = new Bounds(Vector3.zero, Vector3.one);
        quad.UploadMeshData(true);
    }

    void OnDestroy() { if (quad != null) Destroy(quad); }
}
