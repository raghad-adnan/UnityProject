using UnityEngine;

public enum ParticleState { InsideBucket, Emitted, Falling, Collided, Painted, Removed }

// One paint particle — PURE DATA, no GameObject/Transform/Renderer.
//
// 10,000-particle rule (project brief §6): particles are a data/algorithm problem, not
// 10,000 Unity objects. A GameObject per particle costs a Transform sync, a renderer,
// culling bookkeeping and scene-graph overhead each — at 10k that freezes the editor.
// Instead the pool stores plain C# objects and draws every active particle with
// Graphics.DrawMeshInstanced in 1023-instance batches (see PaintParticlePool.Render).
public class PaintParticle
{
    public bool   active     = true;
    public float  sleepTimer = 0f;
    public int    bounces    = 0;

    public Vector3 position;        // world position (all states; kept in sync for rendering)
    public Vector3 velocity;        // world velocity
    public Color   color   = Color.red;
    public float   size    = 0.05f; // physical drop diameter (m) — drives all impact physics
    public float   lifetime = 5f;
    public float   age      = 0f;
    public float   viscosityEffect = 1f;
    public float   approxMass     = 0.001f;

    // SPH scratch — filled each frame by the two-pass solver.
    public float density  = 0f;
    public float pressure = 0f;
    // Slice of this particle's neighbours in the frame's shared flat list (gathered once,
    // reused by both SPH passes — see ParticleSimulation.UpdateParticles).
    public int nbStart = 0;
    public int nbCount = 0;

    public ParticleState state = ParticleState.Removed;

    // Pool bookkeeping: index into the pool's master list (free-list recycling is O(1)).
    public int poolIndex = -1;
}
