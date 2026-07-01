// GPU-instanced particle shader (Built-in RP).
// The Standard shader does NOT declare _Color inside an instancing buffer, so giving each
// particle its own colour through a MaterialPropertyBlock silently breaks its batching.
// This shader declares _Color as a PER-INSTANCE property, so PaintParticlePool can draw
// 1023 differently-coloured spheres per DrawMeshInstanced call (~10 calls for 10,000).
// Lives in Resources/ so Shader.Find() also works in player builds.
Shader "Custom/PaintParticleInstanced"
{
    Properties
    {
        _Color ("Color", Color) = (1, 0, 0, 1)
        _Glossiness ("Smoothness", Range(0,1)) = 0.15
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 150

        CGPROGRAM
        // Lambert lighting is enough for small paint blobs and much cheaper than Standard PBR
        // at 10k instances. noshadow/noforwardadd trim passes we never need for droplets.
        #pragma surface surf Lambert noshadow noforwardadd nolightmap
        #pragma multi_compile_instancing
        #pragma target 3.0

        struct Input
        {
            float dummy; // surface shaders require a non-empty Input struct
        };

        half _Glossiness;

        UNITY_INSTANCING_BUFFER_START(Props)
            UNITY_DEFINE_INSTANCED_PROP(fixed4, _Color)
        UNITY_INSTANCING_BUFFER_END(Props)

        void surf (Input IN, inout SurfaceOutput o)
        {
            fixed4 c = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
            o.Albedo = c.rgb;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
