using UnityEngine;

public class RopeFix : MonoBehaviour
{
    public Transform pivot;
    public Transform bucket;
    public Transform attachPoint;
    public int segments = 25;
    public float sagAmount = 0.18f;

    private LineRenderer line;

    void Start()
    {
        line = GetComponent<LineRenderer>();
        if (line == null)
        {
            line = gameObject.AddComponent<LineRenderer>();
        }

        line.positionCount = segments;
        line.startWidth = 0.09f;
        line.endWidth = 0.09f;
        line.startColor = new Color(0.45f, 0.25f, 0.12f);
        line.endColor = new Color(0.45f, 0.25f, 0.12f);

        if (line.material == null)
        {
            line.material = new Material(Shader.Find("Sprites/Default"));
        }

        if (attachPoint == null && bucket != null)
        {
            GameObject top = new GameObject("RopeAttach");
            top.transform.parent = bucket;
            top.transform.localPosition = new Vector3(0, 0.4f, 0);
            attachPoint = top.transform;
        }
    }

    void Update()
    {
        if (pivot == null) return;

        Transform end = attachPoint != null ? attachPoint : bucket;
        if (end == null) return;

        Vector3 startPoint = pivot.position;
        Vector3 endPoint = end.position;

        Vector3 midPoint = (startPoint + endPoint) / 2f;
        float distance = Vector3.Distance(startPoint, endPoint);
        midPoint.y -= sagAmount * distance;

        for (int i = 0; i < segments; i++)
        {
            float t = i / (float)(segments - 1);
            Vector3 point = GetBezierPoint(t, startPoint, midPoint, endPoint);
            line.SetPosition(i, point);
        }
    }

    private Vector3 GetBezierPoint(float t, Vector3 p0, Vector3 p1, Vector3 p2)
    {
        float u = 1 - t;
        float tt = t * t;
        float uu = u * u;
        return (uu * p0) + (2 * u * t * p1) + (tt * p2);
    }
}