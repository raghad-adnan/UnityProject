using System.Collections.Generic;
using UnityEngine;

// ============================================================================
//  PaintParticlePool
// ----------------------------------------------------------------------------
//  GPU-instancing approach (Built-in RP):
//    All particles share ONE sharedMaterial (Standard, enableInstancing=true).
//    Per-particle colour is stored in each renderer's MaterialPropertyBlock.
//    Unity automatically batches same-mesh / same-sharedMaterial objects with
//    MPBs into GPU-instanced draw calls — typically 2-3 calls for 2000 particles
//    instead of 2000 separate draw calls.
// ============================================================================
public class PaintParticlePool
{
    private readonly List<PaintParticle> all = new List<PaintParticle>();
    private readonly int      maxCount;
    private readonly Transform parent;
    private readonly Mesh      mesh;
    private readonly Material  matTemplate;   // SHARED material (enableInstancing=true)

    // Single cached shader property ID to avoid per-frame string lookup.
    private static readonly int ColorPropID = Shader.PropertyToID("_Color");

    public int ActiveCount { get; private set; }
    public IReadOnlyList<PaintParticle> All => all;

    public PaintParticlePool(Transform parent, Mesh mesh, Material matTemplate, int maxCount)
    {
        this.parent = parent; this.mesh = mesh; this.matTemplate = matTemplate; this.maxCount = maxCount;
    }

    public PaintParticle Get()
    {
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].state == ParticleState.Removed)
            {
                PaintParticle p = all[i];
                p.active     = true;
                p.sleepTimer = 0f;
                ActiveCount++;
                return p;
            }
        }
        if (all.Count >= maxCount) return null;

        PaintParticle np = Create();
        np.active     = true;
        np.sleepTimer = 0f;
        all.Add(np);
        ActiveCount++;
        return np;
    }

    public void Return(PaintParticle p)
    {
        p.state      = ParticleState.Removed;
        p.active     = false;
        p.sleepTimer = 0f;
        p.velocity   = Vector3.zero;
        p.go.SetActive(false);
        ActiveCount--;
    }

    // Set the particle's colour via its MaterialPropertyBlock.
    // This keeps the sharedMaterial intact so all particles stay in the same
    // GPU-instancing batch while each showing its own colour.
    public void SetParticleColor(PaintParticle p, Color c)
    {
        if (p.rend == null || p.mpb == null) return;
        p.mpb.SetColor(ColorPropID, c);
        p.rend.SetPropertyBlock(p.mpb);
    }

    private PaintParticle Create()
    {
        GameObject go = new GameObject("PaintParticle");
        go.transform.SetParent(parent);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        // sharedMaterial (NOT new Material) — this is the key: all particles reference the
        // same material so Unity's GPU-instancing system can batch them automatically.
        mr.sharedMaterial        = matTemplate;
        mr.shadowCastingMode     = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows        = false;
        go.SetActive(false);

        var mpb = new MaterialPropertyBlock();
        return new PaintParticle { go = go, tr = go.transform, rend = mr, mpb = mpb,
                                   state = ParticleState.Removed };
    }
}
