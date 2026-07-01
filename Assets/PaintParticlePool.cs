using System.Collections.Generic;
using UnityEngine;

// ============================================================================
//  PaintParticlePool — data-oriented pool + GPU-instanced renderer.
// ----------------------------------------------------------------------------
//  ARCHITECTURE (10k-particle redesign):
//    * Particles are plain C# data objects (PaintParticle) — ZERO GameObjects,
//      Transforms or MeshRenderers per particle. The project brief forbids
//      spawning 10,000 Unity objects; at that count the scene graph alone
//      dominates the frame time.
//    * O(1) Get()/Return() via an explicit free-index stack (the old version
//      linearly scanned the whole list for a Removed slot — O(n) per spawn,
//      i.e. O(n^2) behaviour while filling a 10k reservoir).
//    * Rendering: Graphics.DrawMeshInstanced in batches of 1023 (the API cap)
//      with one shared material (Custom/PaintParticleInstanced) and per-instance
//      colours through a MaterialPropertyBlock vector array. 10,000 spheres
//      -> ~10 draw calls, no per-object culling, no shadow casters.
// ============================================================================
public class PaintParticlePool
{
    private readonly List<PaintParticle> all = new List<PaintParticle>();
    private readonly Stack<int> freeIndices = new Stack<int>();
    private readonly int maxCount;

    public int ActiveCount { get; private set; }
    public IReadOnlyList<PaintParticle> All => all;

    // --- instanced-rendering scratch (persistent, zero per-frame allocation) ---
    private const int BatchSize = 1023;                       // DrawMeshInstanced hard limit
    private readonly Matrix4x4[] batchMatrices = new Matrix4x4[BatchSize];
    private readonly Vector4[]   batchColors   = new Vector4[BatchSize];
    private readonly MaterialPropertyBlock mpb = new MaterialPropertyBlock();
    private static readonly int ColorPropID = Shader.PropertyToID("_Color");

    public PaintParticlePool(int maxCount)
    {
        this.maxCount = maxCount;
        // Pre-size the master list capacity so filling to max never reallocates.
        all.Capacity = maxCount;
    }

    // O(1): pop a recycled slot, or append a new data object (no Instantiate — it's plain data).
    public PaintParticle Get()
    {
        PaintParticle p;
        if (freeIndices.Count > 0)
        {
            p = all[freeIndices.Pop()];
        }
        else
        {
            if (all.Count >= maxCount) return null;
            p = new PaintParticle { poolIndex = all.Count };
            all.Add(p);
        }
        p.active = true;
        p.sleepTimer = 0f;
        p.bounces = 0;
        ActiveCount++;
        return p;
    }

    // O(1): mark Removed and push the slot on the free stack.
    public void Return(PaintParticle p)
    {
        if (p.state == ParticleState.Removed) return; // already returned — guard against double-free
        p.state      = ParticleState.Removed;
        p.active     = false;
        p.sleepTimer = 0f;
        p.velocity   = Vector3.zero;
        freeIndices.Push(p.poolIndex);
        ActiveCount--;
    }

    // Draw every non-Removed particle as a GPU-instanced sphere.
    //   visualScale — multiplier from physical drop diameter to rendered diameter
    //                 (physical drops are ~1 cm; scaled up so they read at scene scale).
    public void Render(Mesh mesh, Material material, float visualScale)
    {
        if (mesh == null || material == null) return;

        int n = 0;
        for (int i = 0; i < all.Count; i++)
        {
            PaintParticle p = all[i];
            if (p.state == ParticleState.Removed) continue;

            float s = p.size * visualScale;
            // TRS without rotation: spheres are rotation-invariant, so build the matrix directly
            // (cheaper than Matrix4x4.TRS with a quaternion).
            Matrix4x4 m = default;
            m.m00 = s; m.m11 = s; m.m22 = s; m.m33 = 1f;
            m.m03 = p.position.x; m.m13 = p.position.y; m.m23 = p.position.z;
            batchMatrices[n] = m;
            Color c = p.color;
            batchColors[n] = new Vector4(c.r, c.g, c.b, 1f);
            n++;

            if (n == BatchSize) { Flush(mesh, material, n); n = 0; }
        }
        if (n > 0) Flush(mesh, material, n);
    }

    private void Flush(Mesh mesh, Material material, int count)
    {
        mpb.SetVectorArray(ColorPropID, batchColors);
        Graphics.DrawMeshInstanced(
            mesh, 0, material, batchMatrices, count, mpb,
            UnityEngine.Rendering.ShadowCastingMode.Off, false);
    }
}
