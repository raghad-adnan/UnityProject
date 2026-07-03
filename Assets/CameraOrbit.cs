using UnityEngine;
using UnityEngine.InputSystem;

// Simple orbit camera: LEFT mouse drag rotates around a target point, scroll wheel zooms.
// Pure Transform math (position/rotation only) -- no physics components involved, so it
// doesn't touch the "no built-in Unity physics tools" rule at all.
//
// It steps aside while the bucket is being grabbed (see BucketGrabController) so the left
// mouse button isn't fought over: click on the bucket -> grab it, click anywhere else -> orbit.
public class CameraOrbit : MonoBehaviour
{
    [Header("References")]
    public Transform target;                 // point to orbit around (e.g. Pivot)
    public BucketGrabController grabController; // optional; if set, orbiting pauses during a grab

    [Header("Orbit")]
    public Vector3 targetOffset = new Vector3(0f, -1.5f, 0f);
    public float distance = 8f;
    public float minDistance = 2f;
    public float maxDistance = 20f;
    [Range(0.1f, 10f)] public float rotateSpeed = 3f;
    [Range(0.1f, 10f)] public float zoomSpeed = 2f;
    public float minPitchDeg = -80f;
    public float maxPitchDeg = 80f;

    float yaw;
    float pitch;

    void Start()
    {
        // Read the starting yaw/pitch/distance from wherever the camera already is in the
        // scene, so it doesn't jump to a different spot the first time you touch the mouse.
        Vector3 offset = transform.position - GetTargetPosition();
        distance = Mathf.Max(offset.magnitude, minDistance);
        pitch = Mathf.Asin(Mathf.Clamp(offset.y / distance, -1f, 1f)) * Mathf.Rad2Deg;
        yaw = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
    }

    Vector3 GetTargetPosition() => (target != null ? target.position : Vector3.zero) + targetOffset;

    // Mouse.current.position has a bottom-left origin (Y up), same as Camera.ScreenPointToRay.
    // IMGUI's Rects (SimulationManager.ToggleButtonRect/PanelRect) use a top-left origin (Y down),
    // so we flip Y before testing containment.
    static bool IsOverUIPanel(Vector2 mouseScreenPos)
    {
        Vector2 guiPos = new Vector2(mouseScreenPos.x, Screen.height - mouseScreenPos.y);
        if (SimulationManager.ToggleButtonRect.Contains(guiPos)) return true;
        return SimulationManager.PanelVisible && SimulationManager.PanelRect.Contains(guiPos);
    }

    void Update()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null) return;
        if (grabController != null && grabController.IsDragging) return; // bucket owns the mouse right now
        if (IsOverUIPanel(mouse.position.ReadValue())) return; // don't spin the camera while dragging a slider

        if (mouse.leftButton.isPressed)
        {
            Vector2 delta = mouse.delta.ReadValue();
            yaw += delta.x * rotateSpeed * 0.1f;
            pitch -= delta.y * rotateSpeed * 0.1f;
            pitch = Mathf.Clamp(pitch, minPitchDeg, maxPitchDeg);
        }

        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) > 0.01f)
            distance = Mathf.Clamp(distance - scroll * zoomSpeed * 0.01f, minDistance, maxDistance);

        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
        transform.SetPositionAndRotation(GetTargetPosition() + rotation * new Vector3(0f, 0f, -distance), rotation);
    }
}
