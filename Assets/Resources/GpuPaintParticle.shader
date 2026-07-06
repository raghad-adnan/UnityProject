// ============================================================================
//  GpuPaintParticle — indirect-instanced sphere impostors (Built-in RP).
// ----------------------------------------------------------------------------
//  Drawn with Graphics.RenderMeshIndirect: ONE 4-vertex quad mesh, instance
//  count read by the GPU from the indirect-args buffer (which the sim's
//  UpdateArgs kernel filled from the alive-particle counter). 200,000
//  particles = ONE draw call, zero per-particle GameObjects/Transforms,
//  zero CPU matrix building (contrast: the CPU pool builds 1023 Matrix4x4
//  per DrawMeshInstanced batch every frame).
//
//  Each instance reads its own particle straight from the simulation's
//  StructuredBuffer via the alive-index list, expands the quad toward the
//  camera and shades it as a fake sphere (impostor): normal reconstructed
//  from the quad UV, pixels outside the unit disc discarded. A lit-looking
//  ball for 4 vertices instead of the built-in sphere's 515.
//
//  OPAQUE on purpose: the paint spheres render in the geometry pass and the
//  glass bucket (Standard/Fade, transparent queue) blends OVER them, so the
//  liquid is visible inside the transparent container with no transparency-
//  sorting problems (brief §E: "small opaque particles inside a transparent box").
// ============================================================================
Shader "Custom/GpuPaintParticle"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        LOD 100

        Pass
        {
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5          // StructuredBuffer access in the vertex stage
            #include "UnityCG.cginc"

            struct Particle             // must match LiquidSPH.compute (80 bytes)
            {
                float3 position;  float density;
                float3 predicted; float lambda;
                float3 velocity;  float nbCount;
                float4 color;
                uint   state;
                float  size; float lifetime; float age;
            };

            StructuredBuffer<Particle> _Particles;
            StructuredBuffer<uint>     _AliveList;
            float _VisualScale;         // rendered diameter = size * _VisualScale

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;   // quad corner in [-1,1]
                float3 col : TEXCOORD1;
            };

            v2f vert(appdata_full v, uint instanceID : SV_InstanceID)
            {
                Particle p = _Particles[_AliveList[instanceID]];

                // Camera-facing billboard: view-matrix rows are the camera's
                // world-space right/up axes.
                float3 camRight = UNITY_MATRIX_V[0].xyz;
                float3 camUp    = UNITY_MATRIX_V[1].xyz;
                float  diameter = p.size * _VisualScale;

                float2 corner = v.vertex.xy;   // quad verts at (+-0.5, +-0.5, 0)
                float3 world  = p.position + (camRight * corner.x + camUp * corner.y) * diameter;

                v2f o;
                o.pos = UnityWorldToClipPos(float4(world, 1));
                o.uv  = corner * 2.0;
                o.col = p.color.rgb;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float r2 = dot(i.uv, i.uv);
                clip(1.0 - r2);                       // circular silhouette

                // Impostor sphere normal in view space -> cheap diffuse ball.
                float3 n = float3(i.uv, sqrt(saturate(1.0 - r2)));
                float ndl = saturate(dot(n, normalize(float3(0.4, 0.75, 0.55))));
                float3 c = i.col * (0.35 + 0.65 * ndl) + pow(ndl, 24.0) * 0.15;
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
    FallBack Off
}
