using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class RopeTubeRenderer : MonoBehaviour
{
    [Header("Source")]
    public RopePhysicsMSD rope;

    [Header("Catmull-Rom Smoothing (Visual Only)")]
    [Tooltip("عدد نقاط التنعيم بين كل عقدتين فيزيائيتين - تأثير بصري فقط، لا علاقة له بالفيزياء")]
    [Range(2, 20)]
    public int subdivisionsPerSegment = 6;

    [Header("Tube Geometry")]
    [Tooltip("نصف قطر الحبل البصري")]
    public float tubeRadius = 0.03f;
    [Tooltip("عدد الأضلاع حول محيط الأسطوانة (دقة بصرية)")]
    [Range(3, 16)]
    public int radialSegments = 8;

    [Header("Material")]
    public Material ropeMaterial;

    private MeshFilter meshFilter;
    private Mesh mesh;

    // بافرز يُعاد استخدامها لتفادي GC allocation كل فريم
    private readonly List<Vector3> physicsPoints = new List<Vector3>();
    private readonly List<Vector3> smoothedPoints = new List<Vector3>();

    private Vector3[] vertices;
    private Vector3[] normals;
    private Vector2[] uvs;
    private int[] triangles;

    void Awake()
    {
        meshFilter = GetComponent<MeshFilter>();
        mesh = new Mesh();
        mesh.name = "RopeTubeMesh";
        mesh.MarkDynamic(); // المesh يتغيّر كل فريم
        meshFilter.mesh = mesh;

        var mr = GetComponent<MeshRenderer>();
        if (ropeMaterial != null)
            mr.material = ropeMaterial;
    }

   void LateUpdate()
    {
       if (rope == null)
{
    Debug.Log("NO ROPE REFERENCE");
    return;
}

if (rope.nodes.Count < 2)
{
    Debug.Log("NO NODES");
    return;
}

        // 1) سحب نقاط الفيزياء النهائية (بعد Constraint Solver) فقط للقراءة
        physicsPoints.Clear();
        for (int i = 0; i < rope.nodes.Count; i++)
            physicsPoints.Add(rope.nodes[i].position);

        CatmullRomSpline.Generate(physicsPoints, subdivisionsPerSegment, smoothedPoints);

        // 3) بناء tube mesh من المنحنى الناعم
        BuildTubeMesh(smoothedPoints);
    }
    void BuildTubeMesh(List<Vector3> path)
    {
        int pointCount = path.Count;
        if (pointCount < 2) return;

        int vertCount = pointCount * radialSegments;
        int triCount = (pointCount - 1) * radialSegments * 2;

        vertices = new Vector3[vertCount];
normals = new Vector3[vertCount];
uvs = new Vector2[vertCount];
triangles = new int[triCount * 3];

        // حساب اتجاه كل نقطة (tangent) ثم بناء إطار دائري حولها (ring)
        for (int i = 0; i < pointCount; i++)
        {
            Vector3 tangent;
            if (i == 0)
                tangent = (path[1] - path[0]).normalized;
            else if (i == pointCount - 1)
                tangent = (path[i] - path[i - 1]).normalized;
            else
                tangent = (path[i + 1] - path[i - 1]).normalized;

            if (tangent.sqrMagnitude < 0.0001f)
                tangent = Vector3.down;

            // بناء قاعدة عمودية على الـtangent (Up و Right)
            Vector3 up = Vector3.Cross(tangent, Vector3.right);
            if (up.sqrMagnitude < 0.001f)
                up = Vector3.Cross(tangent, Vector3.forward);
            up.Normalize();
            Vector3 right = Vector3.Cross(up, tangent).normalized;

            for (int j = 0; j < radialSegments; j++)
            {
                float angle = (j / (float)radialSegments) * Mathf.PI * 2f;
                Vector3 circleDir = Mathf.Cos(angle) * right + Mathf.Sin(angle) * up;

                int vertIndex = i * radialSegments + j;
                vertices[vertIndex] = path[i] + circleDir * tubeRadius;
                normals[vertIndex] = circleDir;
                uvs[vertIndex] = new Vector2(j / (float)radialSegments, i / (float)(pointCount - 1));
            }
        }

        // بناء المثلثات (triangle strip حول الأسطوانة)
        int triIdx = 0;
        for (int i = 0; i < pointCount - 1; i++)
        {
            for (int j = 0; j < radialSegments; j++)
            {
                int current = i * radialSegments + j;
                int next = i * radialSegments + (j + 1) % radialSegments;
                int currentNext = (i + 1) * radialSegments + j;
                int nextNext = (i + 1) * radialSegments + (j + 1) % radialSegments;

                triangles[triIdx++] = current;
                triangles[triIdx++] = currentNext;
                triangles[triIdx++] = next;

                triangles[triIdx++] = next;
                triangles[triIdx++] = currentNext;
                triangles[triIdx++] = nextNext;
            }
        }

        mesh.Clear();
mesh.vertices = vertices;
mesh.normals = normals;
mesh.uv = uvs;
mesh.triangles = triangles;
mesh.RecalculateBounds();
    }
}