using UnityEngine;

public enum ParticleState { InsideBucket, Emitted, Falling, Collided, Painted, Removed }

// One paint particle
public class PaintParticle
{   
    public bool active = true;

    public float sleepTimer = 0f;
    public Vector3 position;
    public Vector3 velocity;
    public Color color = Color.red;
    public float size = 0.05f;
    public float lifetime = 5f;
    public float age = 0f;
    public float viscosityEffect = 1f;
    public float approxMass = 0.001f;
    public ParticleState state = ParticleState.Removed;

    public GameObject go;
    public Transform tr;
    public Renderer rend;
}
