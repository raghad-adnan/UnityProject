using UnityEngine;

public class PaintStream : MonoBehaviour
{
    public Transform paintPoint;
    public Transform canvas;

    private LineRenderer line;


    void Start()
    {
        line = GetComponent<LineRenderer>();

        // تعطيل الخط نهائياً
        if (line != null)
        {
            line.enabled = false;
        }


        if (canvas == null)
        {
            GameObject g = GameObject.Find("Canvas");

            if (g != null)
                canvas = g.transform;
        }
    }



    void Update()
    {
        // لا نرسم أي خط
        return;
    }



    // احتفظ بها إذا احتجتها لاحقاً لحساب الاصطدام
    bool TryProjectDown(Vector3 origin, out Vector3 hitPoint)
    {
        hitPoint = origin;


        if (canvas == null)
            return false;


        Vector3 n = canvas.up;
        Vector3 dir = Vector3.down;


        float denom = Vector3.Dot(n, dir);


        if (Mathf.Abs(denom) < 1e-6f)
            return false;



        float t = Vector3.Dot(canvas.position - origin, n) / denom;


        if (t < 0f)
            return false;



        hitPoint = origin + dir * t;


        return true;
    }
}