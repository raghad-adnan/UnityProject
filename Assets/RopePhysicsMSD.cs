using System.Collections.Generic;
using UnityEngine;


public class RopePhysicsMSD : MonoBehaviour
{

    public class RopeNode
    {
        public Vector3 position;
        public Vector3 previousPosition;
        public Vector3 velocity;

        public float mass;
        public bool locked;
    }


    public class RopeConstraint
    {
        public int a;
        public int b;
        public float length;
    }



    [Header("References")]

    public Transform anchorPoint;
    public Transform endMass;


    [Header("Bucket Attachment")]

    public Vector3 bucketAttachmentOffset =
        new Vector3(0,0.5f,0);



    [Header("Rope")]

    public int nodeCount = 40;

    public float ropeLength = 3f;



    [Header("Mass")]

    public float totalRopeMass = 0.6f;

    public float bucketMass = 2f;



    [Header("Physics")]

    public float gravity = 9.81f;

    [Range(0.9f,1f)]
    public float damping = 0.995f;



    [Header("Solver")]

    public int subSteps = 20;

    public int constraintIterations = 80;



    public List<RopeNode> nodes =
        new List<RopeNode>();


    List<RopeConstraint> constraints =
        new List<RopeConstraint>();



    float segmentLength;



    void Start()
    {
        CreateRope();
    }




    Vector3 GetBucketAttachPosition()
    {

        if(endMass == null)
            return nodes[nodes.Count-1].position;


        return endMass.position +
        endMass.rotation *
        bucketAttachmentOffset;

    }





    void FixedUpdate()
    {

        if(nodes.Count < 2)
            return;



        float dt =
        Time.fixedDeltaTime /
        subSteps;



        for(int s=0;s<subSteps;s++)
        {

            ApplyGravity(dt);


            Integrate(dt);



            for(int i=0;i<constraintIterations;i++)
            {
                SolveConstraints();
            }


            AttachBucket();


            UpdateVelocity();


        }


    }







    void CreateRope()
    {

        nodes.Clear();
        constraints.Clear();



        segmentLength =
        ropeLength /
        (nodeCount-1);



        float nodeMass =
        totalRopeMass /
        nodeCount;




        for(int i=0;i<nodeCount;i++)
        {

            RopeNode n =
            new RopeNode();



            n.position =
            anchorPoint.position +
            Vector3.down *
            segmentLength *
            i;



            n.previousPosition =
            n.position;



            n.velocity =
            Vector3.zero;



            n.mass =
            nodeMass;



            n.locked =
            (i==0);



            nodes.Add(n);

        }




        for(int i=0;i<nodeCount-1;i++)
        {

            RopeConstraint c =
            new RopeConstraint();


            c.a=i;

            c.b=i+1;


            c.length =
            segmentLength;



            constraints.Add(c);

        }


    }









    void ApplyGravity(float dt)
    {

        for(int i=0;i<nodes.Count;i++)
        {

            RopeNode n =
            nodes[i];


            if(n.locked)
                continue;



            n.velocity +=
            Vector3.down *
            gravity *
            dt;


        }


    }







    void Integrate(float dt)
    {

        for(int i=0;i<nodes.Count;i++)
        {

            RopeNode n =
            nodes[i];


            if(n.locked)
                continue;



            n.previousPosition =
            n.position;



            n.position +=
            n.velocity *
            dt;



            n.velocity *= damping;


        }


    }









    void SolveConstraints()
    {


        foreach(RopeConstraint c in constraints)
        {


            RopeNode a =
            nodes[c.a];


            RopeNode b =
            nodes[c.b];



            Vector3 delta =
            b.position -
            a.position;



            float dist =
            delta.magnitude;



            if(dist < 0.0001f)
                continue;



            float error =
            dist -
            c.length;



            Vector3 correction =
            delta.normalized *
            error;



            float wA =
            a.locked ? 0 : 1;


            float wB =
            b.locked ? 0 : 1;



            float total =
            wA+wB;



            if(total==0)
                continue;



            if(!a.locked)
            {
                a.position +=
                correction *
                (wA/total);
            }



            if(!b.locked)
            {
                b.position -=
                correction *
                (wB/total);
            }



        }


    }









    void AttachBucket()
{
    if(endMass == null)
        return;


    RopeNode last =
    nodes[nodes.Count - 1];


    Vector3 target =
    GetBucketAttachPosition();


    Vector3 delta =
    target - last.position;


    // تحريك النهاية فقط مع الحفاظ على السرعة
    last.position += delta * 0.5f;


    // منع تراكم الانزياح
    last.velocity *= 0.5f;
}
    








    void UpdateVelocity()
    {

        float dt =
        Time.fixedDeltaTime;



        for(int i=0;i<nodes.Count;i++)
        {

            RopeNode n =
            nodes[i];


            n.velocity =
            (n.position -
             n.previousPosition)
             /
             dt;



            n.velocity *=0.95f;

        }


    }






#if UNITY_EDITOR

    void OnDrawGizmos()
    {

        if(nodes==null)
            return;


        Gizmos.color =
        Color.yellow;



        for(int i=0;i<nodes.Count-1;i++)
        {

            Gizmos.DrawLine(
            nodes[i].position,
            nodes[i+1].position);

        }


    }

#endif

}