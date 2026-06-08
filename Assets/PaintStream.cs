using UnityEngine;

public class PaintStream : MonoBehaviour
{
    public Transform paintPoint;
    private LineRenderer line;

    void Start()
    {
        line = GetComponent<LineRenderer>();
    }

    void Update()
    {
        Ray ray = new Ray(paintPoint.position, Vector3.down);
        RaycastHit hit;

        if (Physics.Raycast(ray, out hit))
        {
            line.SetPosition(0, paintPoint.position);
            line.SetPosition(1, hit.point);
        }
        else
        {
            line.SetPosition(0, paintPoint.position);
            line.SetPosition(1, paintPoint.position + Vector3.down * 5f);
        }
    }
}