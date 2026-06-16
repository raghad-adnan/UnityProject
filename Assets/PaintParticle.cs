using UnityEngine;

// Paint particle states (Task 2.1 / 2.16)
public enum ParticleState
{
    InsideBucket, // inside the bucket (part of the reservoir)
    Emitted,      // just left the hole
    Falling,      // falling through the air
    Collided,     // touched the canvas
    Painted,      // splat stamped
    Removed       // returned to the pool
}

// A single paint particle (Task 2.1)
// position/velocity are explicit fields; position is synced to transform for rendering.
public class PaintParticle
{
    public Vector3 position;
    public Vector3 velocity;
    public Color color = Color.red;
    public float size = 0.05f;
    public float lifetime = 5f;
    public float age = 0f;
    public float viscosityEffect = 1f;
    public float approxMass = 0.001f;
    public ParticleState state = ParticleState.Removed;

    // render refs (small sphere, no collider)
    public GameObject go;
    public Transform tr;
    public Renderer rend;
}
