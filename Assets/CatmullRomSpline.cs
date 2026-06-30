using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// مرحلة التنعيم (Smoothing) فقط — Catmull-Rom Spline.
/// هذا الملف لا يحسب أي فيزياء إطلاقاً. هو يأخذ النقاط الفيزيائية
/// الناتجة من RopePhysicsMSD (بعد Constraint Solver) ويولّد منحنى
/// ناعم بينها لأغراض الرسم فقط (visual interpolation).
///
/// الترتيب: ... -> Constraint Solver -> [هذا الملف: Catmull-Rom] -> Tube Mesh
/// </summary>
public static class CatmullRomSpline
{
    /// <summary>
    /// يحوّل قائمة نقاط فيزيائية متفرقة إلى منحنى ناعم بعدد نقاط أكبر.
    /// </summary>
    /// <param name="controlPoints">نقاط العقد الفيزيائية (من RopePhysicsMSD.nodes)</param>
    /// <param name="subdivisionsPerSegment">عدد النقاط المُولَّدة بين كل عقدتين فيزيائيتين</param>
    /// <param name="output">قائمة يُكتب فيها الناتج (تُمرَّر من الخارج لتفادي GC allocation كل فريم)</param>
    public static void Generate(IReadOnlyList<Vector3> controlPoints, int subdivisionsPerSegment, List<Vector3> output)
    {
        output.Clear();

        int n = controlPoints.Count;
        if (n < 2)
        {
            for (int i = 0; i < n; i++) output.Add(controlPoints[i]);
            return;
        }

        for (int i = 0; i < n - 1; i++)
        {
            // نقاط التحكم الأربع المطلوبة لـCatmull-Rom (clamp عند الأطراف)
            Vector3 p0 = controlPoints[Mathf.Max(i - 1, 0)];
            Vector3 p1 = controlPoints[i];
            Vector3 p2 = controlPoints[i + 1];
            Vector3 p3 = controlPoints[Mathf.Min(i + 2, n - 1)];

            int steps = subdivisionsPerSegment;
            // نضيف p1 فقط في أول segment لتفادي تكرار النقاط المشتركة
            int startJ = (i == 0) ? 0 : 1;

            for (int j = startJ; j <= steps; j++)
            {
                float t = j / (float)steps;
                output.Add(EvaluatePoint(p0, p1, p2, p3, t));
            }
        }
    }

    /// <summary>
    /// معادلة Catmull-Rom القياسية (uniform, tau=0.5)
    /// P(t) = 0.5 * [ (2*P1) + (-P0+P2)*t + (2P0-5P1+4P2-P3)*t^2 + (-P0+3P1-3P2+P3)*t^3 ]
    /// </summary>
    static Vector3 EvaluatePoint(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;

        return 0.5f * (
            (2f * p1) +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3
        );
    }
}
