using UnityEngine;

// ============================================================================
//  RopeFix — visual rope rendered as a Catmull-Rom spline.
// ----------------------------------------------------------------------------
//  The pendulum PHYSICS live entirely in PendulumMotion (constraint + tension).
//  This component is purely visual: it places N control points along a
//  parabolic approximation to the catenary, then renders them with a smooth
//  Catmull-Rom cubic spline through a LineRenderer.
//
//  Four rope materials ship out of the box:
//    Hemp      — natural fibre, moderate sag, warm brown.
//    Elastic   — bungee cord, high sag that grows with tension, bright orange.
//    SteelCable— nearly inextensible, tiny sag, silver-grey.
//    SlackWorn — loose/aged rope, very high sag, dark.
//
//  All types read PendulumMotion's live state (ropeIsSlack, currentTension) to
//  dynamically scale the droop: slack rope droops maximally, elastic rope droops
//  more under load (it stretches), steel rope is nearly straight at all times.
// ============================================================================

public enum RopeType { Hemp, Elastic, SteelCable, SlackWorn }

public class RopeFix : MonoBehaviour
{
    [Header("Scene refs")]
    public Transform      pivot;
    public Transform      bucket;
    public PendulumMotion pendulum;

    [Header("Rope type")]
    public RopeType ropeType = RopeType.Hemp;

    // Rope profile: all characteristics for a given type.
    private struct Profile
    {
        internal float baseSag;        // natural sag as a fraction of chord length (0=straight)
        internal float slackSag;       // sag fraction when rope is slack (free fall)
        internal float elasticFactor;  // extra sag gained per unit tension (elastic ropes only)
        internal float startWidth;     // LineRenderer width at the pivot end
        internal float endWidth;       // LineRenderer width at the bucket end
        internal Color startColor;     // colour gradient from pivot...
        internal Color endColor;       // ...to bucket
        internal int   controlPts;     // number of intermediate Catmull-Rom control points
        internal int   renderPerSeg;   // interpolated render points per C-R segment
    }

    private static readonly Profile[] Profiles =
    {
        // Hemp: warm, textured, modest natural sag.
        new Profile
        {
            baseSag=0.12f, slackSag=0.55f, elasticFactor=0f,
            startWidth=0.09f, endWidth=0.07f,
            startColor=new Color(0.45f,0.25f,0.10f),
            endColor  =new Color(0.55f,0.35f,0.15f),
            controlPts=6, renderPerSeg=8,
        },
        // Elastic/Bungee: bright orange, sag increases under tension.
        new Profile
        {
            baseSag=0.18f, slackSag=0.70f, elasticFactor=0.0015f,
            startWidth=0.07f, endWidth=0.06f,
            startColor=new Color(0.95f,0.45f,0.05f),
            endColor  =new Color(1.00f,0.75f,0.10f),
            controlPts=8, renderPerSeg=10,
        },
        // Steel cable: rigid, almost perfectly taut, silver sheen.
        new Profile
        {
            baseSag=0.015f, slackSag=0.20f, elasticFactor=0f,
            startWidth=0.035f, endWidth=0.030f,
            startColor=new Color(0.65f,0.65f,0.70f),
            endColor  =new Color(0.80f,0.80f,0.82f),
            controlPts=4, renderPerSeg=6,
        },
        // Slack/Worn: heavy droop, dark, shows catenary clearly.
        new Profile
        {
            baseSag=0.38f, slackSag=0.85f, elasticFactor=0f,
            startWidth=0.12f, endWidth=0.10f,
            startColor=new Color(0.22f,0.13f,0.07f),
            endColor  =new Color(0.32f,0.20f,0.10f),
            controlPts=10, renderPerSeg=10,
        },
    };

    private LineRenderer line;

    void Start()
    {
        // Attach or find the LineRenderer.
        line = GetComponent<LineRenderer>();
        if (line == null) line = gameObject.AddComponent<LineRenderer>();

        // Use a simple unlit shader so the rope colour is not affected by scene lighting.
        if (line.sharedMaterial == null)
            line.sharedMaterial = new Material(Shader.Find("Sprites/Default"));

        // Auto-find references if not wired in Inspector.
        if (pivot    == null) pivot    = GameObject.Find("Pivot")?.transform;
        if (pendulum == null) pendulum = Object.FindFirstObjectByType<PendulumMotion>();
        if (bucket   == null && pendulum != null) bucket = pendulum.transform;
    }

    void Update()
    {
        if (pivot == null || bucket == null) return;
        Profile p = Profiles[(int)ropeType];

        // Live sag: blend between base and slack sag depending on rope state.
        float slack   = (pendulum != null && pendulum.ropeIsSlack) ? 1f : 0f;
        float tension = (pendulum != null) ? pendulum.currentTension : 0f;

        // Elastic ropes sag MORE under tension (the cord is stretched, pulling the midpoint down).
        float elasticExtra = p.elasticFactor * Mathf.Max(0f, tension);

        float sag = Mathf.Lerp(p.baseSag + elasticExtra, p.slackSag, slack);

        // Attach point: top of the bucket (offset 0.4 in local Y).
        Vector3 start = pivot.position;
        Vector3 end   = bucket.position + bucket.up * 0.4f;
        float   chord = Vector3.Distance(start, end);
        float   sagAmt= sag * chord;

        // Build N+2 control points (first and last are phantom endpoints for the C-R tangent).
        int nCP = Mathf.Max(4, p.controlPts);
        Vector3[] cp = new Vector3[nCP];

        for (int i = 0; i < nCP; i++)
        {
            float t  = i / (float)(nCP - 1);
            cp[i]    = Vector3.Lerp(start, end, t);
            // Parabolic sag (catenary approximation, valid for sag/span ≤ ~0.3).
            // Profile: 4 t (1-t) peaks at t=0.5. A slight forward bias (0.55) mimics the
            // natural asymmetry introduced by the swinging bucket's inertia.
            float profile = 4f * t * (1f - t) * (1f + 0.2f * (t - 0.55f));
            cp[i].y -= sagAmt * profile;
        }

        // Phantom endpoints extend the tangent at each end (mirror extrapolation).
        Vector3 pPre  = 2f * cp[0]       - cp[1];           // phantom before first point
        Vector3 pPost = 2f * cp[nCP - 1] - cp[nCP - 2];     // phantom after last point

        // Output positions: (nCP-1) segments × renderPerSeg points + 1 closing point.
        int totalPts = (nCP - 1) * p.renderPerSeg + 1;
        line.positionCount = totalPts;
        line.startWidth    = p.startWidth;
        line.endWidth      = p.endWidth;
        line.startColor    = p.startColor;
        line.endColor      = p.endColor;

        int idx = 0;
        for (int seg = 0; seg < nCP - 1; seg++)
        {
            // The four Catmull-Rom control points for this segment.
            Vector3 p0 = (seg == 0)        ? pPre        : cp[seg - 1];
            Vector3 p1 = cp[seg];
            Vector3 p2 = cp[seg + 1];
            Vector3 p3 = (seg == nCP - 2) ? pPost       : cp[seg + 2];

            for (int r = 0; r < p.renderPerSeg; r++)
            {
                float t = r / (float)p.renderPerSeg;
                line.SetPosition(idx++, CatmullRom(p0, p1, p2, p3, t));
            }
        }
        // Close the last point exactly on the bucket attach.
        line.SetPosition(idx, end);
    }

    // Standard uniform Catmull-Rom interpolation between p1 and p2,
    // with p0 and p3 as the outer context points.
    //   q(t) = 0.5 * [ 2p1
    //                + (-p0 + p2) t
    //                + (2p0 - 5p1 + 4p2 - p3) t²
    //                + (-p0 + 3p1 - 3p2 + p3) t³ ]
    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (
              2f * p1
            + (-p0 + p2)                    * t
            + (2f*p0 - 5f*p1 + 4f*p2 - p3) * t2
            + (-p0 + 3f*p1 - 3f*p2 + p3)   * t3
        );
    }
}
