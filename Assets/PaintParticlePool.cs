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
    for(int i = 0; i < all.Count; i++)
    {
        if(all[i].state == ParticleState.Removed)
        {

            PaintParticle p = all[i];


            // إيقاظ الجزيء
            p.active = true;

            // تصفير عداد النوم
            p.sleepTimer = 0f;


            ActiveCount++;

            return p;
        }
    }



    if(all.Count >= maxCount)
        return null;



    PaintParticle newParticle = Create();


    newParticle.active = true;
    newParticle.sleepTimer = 0f;


    all.Add(newParticle);


    ActiveCount++;


    return newParticle;
}

    public void Return(PaintParticle p)
{
    p.state = ParticleState.Removed;


    p.active = false;


    p.sleepTimer = 0f;


    p.velocity = Vector3.zero;


    p.go.SetActive(false);


    ActiveCount--;
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
