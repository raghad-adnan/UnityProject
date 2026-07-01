using UnityEngine;

public enum ParticleState { InsideBucket, Emitted, Falling, Collided, Painted, Removed }

// One paint particle.
// Rendering: all particles share ONE sharedMaterial (Standard, enableInstancing=true).
// Per-particle colour is stored in the renderer's MaterialPropertyBlock; Unity auto-batches
// objects with the same mesh+sharedMaterial+MPB into GPU-instanced draw calls (~2 calls
// for 2000 particles instead of 2000 individual draw calls).
public class PaintParticle
{
    public bool   active     = true;
    public float  sleepTimer = 0f;
    public int    bounces    = 0;

    public Vector3 position;
    public Vector3 velocity;
    public Color   color   = Color.red;
    public float   size    = 0.05f;
    public float   lifetime = 5f;
    public float   age      = 0f;
    public float   viscosityEffect = 1f;
    public float   approxMass     = 0.001f;

    // SPH scratch — filled each frame by the two-pass solver.
    public float density  = 0f;
    public float pressure = 0f;

    public ParticleState state = ParticleState.Removed;

    // Scene objects — kept for rendering (Transform.position sync) and visibility toggle.
    public GameObject go;
    public Transform  tr;
    public Renderer   rend;
    // Per-instance property block for the shared material colour.
    public MaterialPropertyBlock mpb;
}
