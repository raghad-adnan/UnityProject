using UnityEngine;
using UnityEngine.InputSystem;

public class PendulumMotion : MonoBehaviour
{
    public Transform pivot;
    public float length = 5f;
    public float gravity = 9.81f;
    public float damping = 0.02f;
    public float mass = 1f;  // كتلة الدلو

    public float angleX = 40f;
    public float angleZ = 25f;

    private float velocityX;
    private float velocityZ;
    [Header("Energy")]
    public float kineticEnergy;
    public float potentialEnergy;
    public float totalEnergy;

    [Header("Period")]
    public float theoreticalPeriod;
    public Vector3 velocity;
    public float impulse = 2f;

    void Start()
    {
        transform.position = pivot.position + new Vector3(0, -length, 0);
        velocityX = 2f;
        velocityZ = 1.5f;
    }

    void Update()
    {
        float dt = Time.deltaTime;

        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            velocityX += impulse;
            velocityZ += impulse;
        }

        float tx = angleX * Mathf.Deg2Rad;
        float tz = angleZ * Mathf.Deg2Rad;

        float accX = -(gravity / length) * Mathf.Sin(tx) - damping * velocityX;
        float accZ = -(gravity / length) * Mathf.Sin(tz) - damping * velocityZ;

        velocityX += accX * dt;
        velocityZ += accZ * dt;

        tx += velocityX * dt;
        tz += velocityZ * dt;

        angleX = tx * Mathf.Rad2Deg;
        angleZ = tz * Mathf.Rad2Deg;

        Vector3 offset = new Vector3(
            length * Mathf.Sin(tx),
            -length * Mathf.Cos(tx),
            length * Mathf.Sin(tz)
        );

        Vector3 newPos = pivot.position + offset;
        velocity = (newPos - transform.position) / dt;
        transform.position = newPos;
        UpdateEnergies();
    }

    // حساب قوة الشد في الحبل (حسب المعادلة الفيزيائية)
    public float GetTensionForce()
    {
        float v = velocity.magnitude;
        float thetaRad = angleX * Mathf.Deg2Rad;
        float T = (mass * v * v / length) + (mass * gravity * Mathf.Cos(thetaRad));
        return T;
    }

    // حساب التسارع المماسي
    public float GetTangentialAcceleration()
    {
        float thetaRad = angleX * Mathf.Deg2Rad;
        return -gravity * Mathf.Sin(thetaRad);
    }

    // حساب التسارع الشعاعي
    public float GetRadialAcceleration()
    {
        float v = velocity.magnitude;
        return v * v / length;
    }
    // حساب الطاقة الحركية
    public float GetKineticEnergy()
    {
        float v = velocity.magnitude;
        return 0.5f * mass * v * v;
    }

    // حساب طاقة الوضع (h = L - L cosθ = L(1 - cosθ))
    public float GetPotentialEnergy()
    {
        float thetaRad = angleX * Mathf.Deg2Rad;
        float height = length * (1f - Mathf.Cos(thetaRad));
        return mass * gravity * height;
    }

    // حساب الطاقة الكلية
    public float GetTotalEnergy()
    {
        return GetKineticEnergy() + GetPotentialEnergy();
    }

    // حساب الزمن الدوري النظري
    public float GetTheoreticalPeriod()
    {
        return 2f * Mathf.PI * Mathf.Sqrt(length / gravity);
    }

    // تحديث قيم الطاقة في كل فريم (استدعيها في Update)
    void UpdateEnergies()
    {
        kineticEnergy = GetKineticEnergy();
        potentialEnergy = GetPotentialEnergy();
        totalEnergy = kineticEnergy + potentialEnergy;
        theoreticalPeriod = GetTheoreticalPeriod();
    }
}