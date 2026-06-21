using System.Collections.Generic;
using UnityEngine;


public class PaintParticlePool
{
    private readonly List<PaintParticle> all = new List<PaintParticle>();
    private readonly int maxCount;
    private readonly Transform parent;
    private readonly Mesh mesh;
    private readonly Material matTemplate;

    public int ActiveCount { get; private set; }
    public IReadOnlyList<PaintParticle> All => all;

    public PaintParticlePool(Transform parent, Mesh mesh, Material matTemplate, int maxCount)
    {
        this.parent = parent; this.mesh = mesh; this.matTemplate = matTemplate; this.maxCount = maxCount;
    }

    public PaintParticle Get()
    {
        for (int i = 0; i < all.Count; i++)
            if (all[i].state == ParticleState.Removed) { ActiveCount++; return all[i]; }

        if (all.Count >= maxCount) return null;
        PaintParticle p = Create();
        all.Add(p);
        ActiveCount++;
        return p;
    }

    public void Return(PaintParticle p)
    {
        p.state = ParticleState.Removed;
        if (p.go != null) p.go.SetActive(false);
        ActiveCount = Mathf.Max(0, ActiveCount - 1);
    }

    private PaintParticle Create()
    {
        GameObject go = new GameObject("PaintParticle");
        go.transform.SetParent(parent);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.material = new Material(matTemplate);
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        go.SetActive(false);
        return new PaintParticle { go = go, tr = go.transform, rend = mr, state = ParticleState.Removed };
    }
}
