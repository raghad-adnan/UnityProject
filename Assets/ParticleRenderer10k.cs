using UnityEngine;
using System.Collections.Generic;

public class ParticleRenderer10k : MonoBehaviour
{
    public Mesh particleMesh;
    public Material particleMaterial;

    public List<Vector3> positions = new List<Vector3>();

    public float particleSize = 0.05f;

    // 1023 هو الحد الأقصى لـ DrawMeshInstanced في كل batch — لا تغيير هنا
    private Matrix4x4[] matrices = new Matrix4x4[1023];

    void LateUpdate()
    {
        if (particleMesh == null || particleMaterial == null)
            return;

        int count = positions.Count;

        for (int i = 0; i < count; i += 1023)
        {
            int batch = Mathf.Min(1023, count - i);

            for (int j = 0; j < batch; j++)
            {
                matrices[j] = Matrix4x4.TRS(
                    positions[i + j],
                    Quaternion.identity,
                    Vector3.one * particleSize
                );
            }

            Graphics.DrawMeshInstanced(
                particleMesh,
                0,
                particleMaterial,
                matrices,
                batch
            );
        }
    }
}