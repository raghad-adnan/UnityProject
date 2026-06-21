using UnityEngine;

public class PaintStream : MonoBehaviour
{
    public Transform paintPoint;
    public Transform canvas;        
    private LineRenderer line;

    void Start()
    {
        line = GetComponent<LineRenderer>();
        if (canvas == null)
        {
            GameObject g = GameObject.Find("Canvas");
            if (g != null) canvas = g.transform;
        }
    }

    void Update()
    {
        if (paintPoint == null || line == null) return;
        line.SetPosition(0, paintPoint.position);

        Vector3 endPoint;
        if (TryProjectDown(paintPoint.position, out endPoint)) line.SetPosition(1, endPoint);
        else line.SetPosition(1, paintPoint.position + Vector3.down * 5f);
    }

    //  canvas plane
    bool TryProjectDown(Vector3 origin, out Vector3 hitPoint)
    {
        hitPoint = origin;
        if (canvas == null) return false;
        Vector3 n = canvas.up, dir = Vector3.down;
        float denom = Vector3.Dot(n, dir);
        if (Mathf.Abs(denom) < 1e-6f) return false;
        float t = Vector3.Dot(canvas.position - origin, n) / denom;
        if (t < 0f) return false;
        hitPoint = origin + dir * t;
        return true;
    }
}
