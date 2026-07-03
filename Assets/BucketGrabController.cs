using UnityEngine;
using UnityEngine.InputSystem;

// Lets the user grab the bucket with the LEFT mouse button, drag it around like pulling
// back a real pendulum, and let go to release it back into PendulumMotion's normal swing.
//
// No Physics.Raycast, no Collider, no Rigidbody is used anywhere here -- only plain
// vector/geometry math (Vector3, Plane), so this stays outside the "no built-in Unity
// physics tools" rule. It reads the mouse through the new Input System (Mouse.current),
// matching the spacebar-impulse code already in PendulumMotion.Update().
public class BucketGrabController : MonoBehaviour
{
    [Header("References")]
    public Camera cam;
    public PendulumMotion bucket;

    [Header("Grab feel")]
    [Tooltip("How close (world units) the mouse ray must pass to the bucket to start a grab.")]
    public float grabRadius = 0.5f;

    bool dragging = false;
    Vector3 lastDir;
    float lastSampleTime;
    Vector3 pendingReleaseOmega;

    public bool IsDragging => dragging;

    void Update()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null || cam == null || bucket == null || bucket.pivot == null) return;

        Vector2 mouseScreenPos = mouse.position.ReadValue();
        if (!dragging && IsOverUIPanel(mouseScreenPos)) return; // ignore clicks that land on the control panel

        Ray ray = cam.ScreenPointToRay(mouseScreenPos);

        if (!dragging && mouse.leftButton.wasPressedThisFrame)
        {
            if (DistanceRayToPoint(ray, bucket.transform.position) <= grabRadius)
                BeginDrag();
        }

        if (dragging)
        {
            UpdateDrag(ray);

            if (mouse.leftButton.wasReleasedThisFrame)
                EndDrag();
        }
    }

    void BeginDrag()
    {
        dragging = true;
        bucket.isDragging = true;
        lastDir = (bucket.transform.position - bucket.pivot.position).normalized;
        lastSampleTime = Time.time;
        pendingReleaseOmega = Vector3.zero;
    }

    void UpdateDrag(Ray ray)
    {
        // Drag plane: passes through the pivot, facing the camera. Simpler than intersecting the
        // ray with the L-radius sphere directly (which has a near/far ambiguity) -- we just find
        // where the ray crosses this plane, then re-project that point onto the sphere so the
        // rope length stays exactly L. Feels like dragging on a sheet of glass held up to you.
        Plane dragPlane = new Plane(-cam.transform.forward, bucket.pivot.position);
        if (!dragPlane.Raycast(ray, out float t)) return;

        Vector3 pointOnPlane = ray.GetPoint(t);
        bucket.DragTo(pointOnPlane);

        float dt = Time.time - lastSampleTime;
        if (dt > 1e-4f)
        {
            Vector3 newDir = (bucket.transform.position - bucket.pivot.position).normalized;
            // Small-angle estimate of angular velocity from how dir rotated this frame:
            // the same omega = (r x v)/L relation PendulumMotion itself uses internally.
            pendingReleaseOmega = Vector3.Cross(lastDir, newDir) / dt;
            lastDir = newDir;
            lastSampleTime = Time.time;
        }
    }

    void EndDrag()
    {
        dragging = false;
        bucket.ReleaseDrag(pendingReleaseOmega);
    }

    // Mouse.current.position has a bottom-left origin (Y up); IMGUI Rects use top-left (Y down).
    static bool IsOverUIPanel(Vector2 mouseScreenPos)
    {
        Vector2 guiPos = new Vector2(mouseScreenPos.x, Screen.height - mouseScreenPos.y);
        if (SimulationManager.ToggleButtonRect.Contains(guiPos)) return true;
        return SimulationManager.PanelVisible && SimulationManager.PanelRect.Contains(guiPos);
    }

    // Shortest distance between a ray and a fixed point in space -- used only to test
    // "did the click land on the bucket", no physics engine involved.
    float DistanceRayToPoint(Ray ray, Vector3 point)
    {
        Vector3 toPoint = point - ray.origin;
        float t = Mathf.Max(0f, Vector3.Dot(toPoint, ray.direction));
        Vector3 closest = ray.origin + ray.direction * t;
        return Vector3.Distance(closest, point);
    }
}
