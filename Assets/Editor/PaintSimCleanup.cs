using UnityEngine;
using UnityEditor;

// Helper tools under the top menu:  Tools > Paint Sim
public static class PaintSimCleanup
{
    // Remove the PendulumMotion component from any object that is not "Bucket"
    // (fixes the duplicate on the "inner" child).
    [MenuItem("Tools/Paint Sim/Remove Duplicate Pendulum")]
    public static void RemoveDuplicatePendulum()
    {
        var all = Object.FindObjectsByType<PendulumMotion>(FindObjectsSortMode.None);
        int removed = 0;

        foreach (var pm in all)
        {
            if (pm.gameObject.name.Trim() != "Bucket")
            {
                Undo.DestroyObjectImmediate(pm);
                removed++;
            }
        }

        Debug.Log($"[PaintSimCleanup] Removed {removed} duplicate PendulumMotion component(s). Kept the one on Bucket.");
    }

    // Remove all Collider components in the scene (forbidden by the project,
    // and no longer used after switching painting to a math projection).
    [MenuItem("Tools/Paint Sim/Remove All Colliders")]
    public static void RemoveAllColliders()
    {
        var all = Object.FindObjectsByType<Collider>(FindObjectsSortMode.None);
        int removed = 0;

        foreach (var col in all)
        {
            Undo.DestroyObjectImmediate(col);
            removed++;
        }

        Debug.Log($"[PaintSimCleanup] Removed {removed} Collider component(s).");
    }

    // Apply sensible launch defaults to the Bucket's PendulumMotion.
    [MenuItem("Tools/Paint Sim/Apply Good Defaults")]
    public static void ApplyGoodDefaults()
    {
        var all = Object.FindObjectsByType<PendulumMotion>(FindObjectsSortMode.None);

        foreach (var pm in all)
        {
            if (pm.gameObject.name.Trim() != "Bucket") continue;

            Undo.RecordObject(pm, "Apply Good Defaults");

            // good launch: release angle on X + push on Z -> elliptical/spiral pattern
            pm.L = 5f;
            pm.initialAngleDeg = 30f;
            pm.initialAngVel = 2f;

            // mass and paint
            pm.emptyMass = 1f;
            pm.initialPaintMass = 5f;
            pm.flowRate = 1f;  // valve fully open (flowRate = open fraction of the hole area)
            pm.damping = 0f; // linear damping off (spec-faithful)

            EditorUtility.SetDirty(pm);
            // position is computed from initialAngleDeg automatically on Play
            Debug.Log("[PaintSimCleanup] Launch defaults: L=5, angle=30, Z push=2 -> spiral pattern.");
        }
    }
}
