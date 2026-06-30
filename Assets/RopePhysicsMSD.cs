using System.Collections.Generic;
using UnityEngine;

public class RopePhysicsMSD : MonoBehaviour
{
    // ===================== Physics Node =====================
    public class RopeNode
    {
        public Vector3 position;
        public Vector3 velocity;      // متغيّر صريح (خلافاً لـ Verlet)
        public Vector3 force;         // مجموع القوى المؤثرة هذه الخطوة الفرعية

        public float mass = 0.05f;
        public bool locked;
    }

    // ===================== Spring Constraint =====================
    public class RopeSpring
    {
        public int a;
        public int b;
        public float restLength;
    }
  
    [Header("Setup")]
    public Transform anchorPoint;
    public Transform endMass;
    
    public int nodeCount = 40;
    public float ropeLength = 5f;
    [Header("End Mass")]
    public float bucketMass = 2f;
    [Header("Mass")]
    [Tooltip("الكتلة الكلية للحبل، تُوزَّع بالتساوي على كل العقد")]
    public float totalRopeMass = 0.6f;
    [Tooltip("كتلة إضافية للعقدة الأخيرة (الدلو)")]
    public float endMassExtra = 0f;

    [Header("Physics Constants - F = m*g")]
    public float gravity = 9.81f;

    [Header("Spring Constants - F = -k(x-L)")]
    [Tooltip("صلابة النابض. كبيرة = حبل أكثر صلابة لكن أقل استقراراً بدون sub-stepping كافٍ")]
    public float springStiffness = 4000f;

    [Header("Damping Constants - F = -c*v")]
    [Tooltip("معامل التخميد. كبير = حركة تخمد أسرع، يمنع الاهتزاز اللانهائي")]
    public float dampingCoefficient = 12f;

    [Header("Numerical Stability")]
    [Tooltip("عدد الخطوات الفرعية لكل FixedUpdate. كلما زاد k يجب زيادة هذا الرقم لمنع الانفجار العددي")]
    [Range(1, 64)]
    public int subSteps = 16;

    [Tooltip("حد أقصى للقوة لمنع NaN/Infinity في حالات نادرة من التمدد الشديد")]
    public float maxForceMagnitude = 5000f;

    [Header("Inextensibility (عدم التمدد غير الواقعي)")]
    [Tooltip("نسبة التمدد القصوى المسموحة قبل تفعيل تصحيح هندسي إضافي (0 = صارم جداً)")]
    [Range(0f, 0.5f)]
    public float maxStretchRatio = 0.08f;
    [Range(1, 10)]
    public int constraintIterations =5;

    [Header("Rope Type Presets")]
    public RopeType ropeType = RopeType.Standard;
    public enum RopeType { Light, Standard, Heavy }

    [HideInInspector]
    public List<RopeNode> nodes = new List<RopeNode>();
    private List<RopeSpring> springs = new List<RopeSpring>();
    private float segmentRestLength;
   


    void Start()
{

    CreateRope();


    Debug.Log(
    "Anchor = " 
    + anchorPoint.position
    );


    Debug.Log(
    "First Rope Node = "
    + nodes[0].position
    );


    Debug.Log(
    "Last Rope Node = "
    + nodes[nodes.Count-1].position
    );

}
    
    void FixedUpdate()
    {
        if (nodes.Count == 0) return;

        // dt الكلي يُقسَّم على عدد الخطوات الفرعية — هذا أساس الاستقرار
        float dtSub = Time.fixedDeltaTime / subSteps;

        for (int s = 0; s < subSteps; s++)
{
    UpdateAnchor();
    ComputeForces();
    IntegrateForces(dtSub);
    SolveLengthConstraints();
}
    }

    void ApplyPreset()
    {
        switch (ropeType)
        {
            case RopeType.Light:
                springStiffness = 2500f;
                dampingCoefficient = 5f;
                totalRopeMass = 0.3f;
                break;
            case RopeType.Standard:
                springStiffness = 4000f;
                dampingCoefficient = 8f;
                totalRopeMass = 0.6f;
                break;
            case RopeType.Heavy:
                springStiffness = 7000f;
                dampingCoefficient = 14f;
                totalRopeMass = 1.2f;
                break;
        }
    }

   void CreateRope()
{
    nodes.Clear();
    springs.Clear();

    segmentRestLength = ropeLength / (nodeCount - 1);

    float perNodeMass = totalRopeMass / nodeCount;


    for(int i = 0; i < nodeCount; i++)
    {

        RopeNode node = new RopeNode();


        node.position =
            anchorPoint.position +
            Vector3.down *
            segmentRestLength *
            i;


        node.velocity = Vector3.zero;

        node.force = Vector3.zero;


        node.mass = perNodeMass;


        node.locked = (i == 0);


        nodes.Add(node);

    }



    for(int i = 0; i < nodeCount - 1; i++)
    {

        RopeSpring spring = new RopeSpring();


        spring.a = i;
        spring.b = i + 1;


        spring.restLength =
            segmentRestLength;


        springs.Add(spring);

    }


}
    /// <summary>
    /// حساب القوى: F = m*g (جاذبية) + F = -k(x-L) (نابض) + F = -c*v (تخميد)
    /// </summary>
    void ComputeForces()
    {
        // تصفير القوى
        for (int i = 0; i < nodes.Count; i++)
            nodes[i].force = Vector3.zero;

        // 1) الجاذبية: F = m * g
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].locked) continue;
            nodes[i].force += Vector3.down * gravity * nodes[i].mass;
        }
        // وزن الدلو على العقدة الأخيرة
        RopeNode endNode = nodes[nodes.Count - 1];

        if(!endNode.locked)
          {
           endNode.force += 
           Vector3.down *
           gravity *
           bucketMass;
           }
        // 2) قوى النابض والتخميد بين كل عقدتين متجاورتين
        for (int i = 0; i < springs.Count; i++)
        {
            RopeSpring sp = springs[i];
            RopeNode a = nodes[sp.a];
            RopeNode b = nodes[sp.b];

            Vector3 delta = b.position - a.position;
            float currentLength = delta.magnitude;
            if (currentLength < 0.0001f) continue;

            Vector3 dir = delta / currentLength;

            // Spring: F = -k * (x - L)
            float stretch = currentLength - sp.restLength;
            Vector3 springForce = -springStiffness * stretch * dir;

            // Damping: F = -c * v (نستخدم السرعة النسبية على طول محور النابض)
            Vector3 relativeVelocity = b.velocity - a.velocity;
            float velAlongSpring = Vector3.Dot(relativeVelocity, dir);
            Vector3 dampingForce = -dampingCoefficient * velAlongSpring * dir;

            Vector3 totalSpringForce = springForce + dampingForce;

            // حماية ضد التضخم العددي
            if (totalSpringForce.magnitude > maxForceMagnitude)
                totalSpringForce = totalSpringForce.normalized * maxForceMagnitude;

            // قانون نيوتن الثالث: قوة متساوية ومعاكسة
            if (!a.locked) a.force -= totalSpringForce;
            if (!b.locked) b.force += totalSpringForce;
        }
    }

    /// <summary>
    /// تكامل نيوتن: a = F/m ، v += a*dt ، x += v*dt (Semi-implicit Euler)
    /// </summary>
    void IntegrateForces(float dt)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            RopeNode n = nodes[i];
            if (n.locked) continue;

            Vector3 acceleration = n.force / n.mass; // F = m*a  ->  a = F/m
            n.velocity += acceleration * dt;
            n.position += n.velocity * dt;
        }
    }

    /// <summary>
    /// تصحيح هندسي إضافي خفيف يمنع التمدد غير الواقعي (over-stretch)
    /// الذي قد يحدث رغم القوى لو كانت السرعة عالية جداً للحظة واحدة.
    /// هذا ليس بديلاً عن النابض، بل صمام أمان إضافي محدود بـmaxStretchRatio.
    /// </summary>
    void SolveLengthConstraints()
    {
        if (maxStretchRatio <= 0f) return;

        for (int iter = 0; iter < constraintIterations; iter++)
        {
            for (int i = 0; i < springs.Count; i++)
            {
                RopeSpring sp = springs[i];
                RopeNode a = nodes[sp.a];
                RopeNode b = nodes[sp.b];

                Vector3 delta = b.position - a.position;
                float distance = delta.magnitude;
                if (distance < 0.0001f) continue;

                float maxAllowed = sp.restLength * (1f + maxStretchRatio);
                if (distance <= maxAllowed) continue; // ضمن الحد المسموح، لا تدخّل

                float excess = distance - maxAllowed;
                Vector3 correction = (delta / distance) * excess;

                float wa = a.locked ? 0f : 0.5f;
                float wb = b.locked ? 0f : 0.5f;
                float wSum = wa + wb;
                if (wSum <= 0f) continue;

                if (!a.locked) a.position += correction * (wa / wSum);
                if (!b.locked) b.position -= correction * (wb / wSum);
            }
        }
    }
    void ApplyRopePreset()
{
    switch(ropeType)
    {

        case RopeType.Light:

            springStiffness = 1800f;
            dampingCoefficient = 5f;
            totalRopeMass = 0.25f;

            break;



        case RopeType.Standard:

            springStiffness = 4000f;
            dampingCoefficient = 12f;
            totalRopeMass = 0.6f;

            break;



        case RopeType.Heavy:

            springStiffness = 8000f;
            dampingCoefficient = 25f;
            totalRopeMass = 1.5f;

            break;

    }
}
void UpdateAnchor()
{
    if (nodes.Count == 0) return;

    nodes[0].position = anchorPoint.position;
    nodes[0].velocity = Vector3.zero;
    nodes[0].force = Vector3.zero;
}

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (nodes == null) return;
        Gizmos.color = Color.yellow;
        for (int i = 0; i < nodes.Count - 1; i++)
            Gizmos.DrawLine(nodes[i].position, nodes[i + 1].position);
    }
#endif
}
