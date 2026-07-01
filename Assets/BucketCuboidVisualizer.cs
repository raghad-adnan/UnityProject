using UnityEngine;

// ============================================================================
//  BucketCuboidVisualizer — transparent cuboid "validation mode" (brief §5.2).
// ----------------------------------------------------------------------------
//  Attached to the existing Bucket transform, so the glass cuboid moves and
//  rotates with the rope/pendulum automatically. It does NOT create any liquid
//  particles — the liquid seen inside is the one PaintPhysics particle system.
//
//  What it does:
//    * Enforces Standard-shader Fade (alpha-blended) mode on the bucket's own
//      renderers at runtime — belt-and-braces so the container is see-through
//      even if a material asset regresses to opaque.
//    * Draws the 12 cuboid edge lines with a single LineRenderer child in the
//      bucket's LOCAL space (one retraced strip), so the frame follows every
//      swing/tilt with zero per-frame cost.
//    * Disables shadow casting on the glass (a glass box must not cast a solid
//      shadow over the canvas).
// ============================================================================
public class BucketCuboidVisualizer : MonoBehaviour
{
    [Header("Validation mode (brief §5.2)")]
    public bool validationMode = true;          // show glass cuboid + edge frame
    public bool hideOriginalRenderer = false;   // hide the outer cube mesh entirely (edges + inner only)

    [Header("Glass")]
    public Color glassColor = new Color(0.75f, 0.85f, 0.92f, 0.25f);
    public Color innerColor = new Color(0.92f, 0.96f, 1f, 0.12f);

    [Header("Edge lines")]
    public Color edgeColor = new Color(0.15f, 0.55f, 0.95f, 1f);
    [Range(0.002f, 0.05f)] public float edgeWidth = 0.015f;

    private LineRenderer edgeLine;
    private Renderer outerRend;
    private Renderer innerRend;

    void Start()
    {
        outerRend = GetComponent<Renderer>();
        // The scene's inner container is the child cube named "inner " (trailing space in the scene).
        foreach (Transform child in transform)
        {
            if (child.name.Trim() == "inner")
            {
                innerRend = child.GetComponent<Renderer>();
                break;
            }
        }

        if (outerRend != null) MakeGlass(outerRend, glassColor);
        if (innerRend != null) MakeGlass(innerRend, innerColor);

        BuildEdgeFrame();
        Apply();
    }

    void OnValidate() { if (Application.isPlaying && edgeLine != null) Apply(); }

    // Runtime enforcement of Standard-shader Fade mode + no shadows.
    // (Material asset should already be Fade; this guarantees it even if the asset regresses.)
    void MakeGlass(Renderer r, Color c)
    {
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        Material m = r.material; // instance — do not overwrite the shared asset in play mode
        if (m == null) return;
        if (m.HasProperty("_Mode"))
        {
            m.SetFloat("_Mode", 2f);                    // Standard: Fade
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_ALPHATEST_ON");
            m.EnableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        if (m.HasProperty("_Color")) m.color = c;
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.85f); // glassy sheen
    }

    // One LineRenderer strip in bucket-LOCAL space that traces all 12 cube edges
    // (a cube has 8 odd-degree corners, so a single stroke must retrace 3 verticals —
    // retraced segments just redraw the same pixels).
    void BuildEdgeFrame()
    {
        GameObject go = new GameObject("CuboidEdges");
        go.transform.SetParent(transform, false); // local space → follows swing & tilt for free
        edgeLine = go.AddComponent<LineRenderer>();
        edgeLine.useWorldSpace = false;
        edgeLine.loop = false;
        edgeLine.sharedMaterial = new Material(Shader.Find("Sprites/Default"));

        const float h = 0.5f; // unit cube in local space; the bucket's scale shapes the cuboid
        Vector3 b0 = new Vector3(-h, -h, -h), b1 = new Vector3(h, -h, -h);
        Vector3 b2 = new Vector3(h, -h, h),  b3 = new Vector3(-h, -h, h);
        Vector3 t0 = new Vector3(-h, h, -h), t1 = new Vector3(h, h, -h);
        Vector3 t2 = new Vector3(h, h, h),  t3 = new Vector3(-h, h, h);

        Vector3[] path =
        {
            b0, b1, b2, b3, b0,    // bottom loop
            t0, t1,                // up b0->t0, top edge t0->t1
            b1, t1,                // vertical t1->b1 and back (retrace)
            t2, b2, t2,            // top t1->t2, vertical down/up (retrace)
            t3, b3, t3,            // top t2->t3, vertical down/up (retrace)
            t0                     // close the top loop
        };
        edgeLine.positionCount = path.Length;
        edgeLine.SetPositions(path);
    }

    void Apply()
    {
        if (edgeLine != null)
        {
            // Edge frame is the validation-mode cue; the glass cuboid itself stays either way.
            edgeLine.gameObject.SetActive(validationMode);
            edgeLine.startWidth = edgeWidth;
            edgeLine.endWidth = edgeWidth;
            edgeLine.startColor = edgeColor;
            edgeLine.endColor = edgeColor;
        }
        if (outerRend != null) outerRend.enabled = !hideOriginalRenderer;
    }
}
