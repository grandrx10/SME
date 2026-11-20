// Toon outline by inverted hull: the same mesh, pushed outward and drawn back-faces only in a dark ink colour,
// so it shows as a rim round the shape. The push is a fraction of the object's thinnest scale, so the outline
// keeps in proportion as pieces grow and shrink (and vanishes with them). Pushed radially from the object's
// centre rather than along normals, so flat-shaded shapes (prisms, cubes) don't crack open at their edges.
Shader "SME/Outline"
{
    Properties
    {
        _BaseColor ("Ink", Color) = (0.09, 0.12, 0.2, 1)
        _Width ("Width (fraction of thinnest scale)", Range(0, 1)) = 0.14
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+5" }

        Pass
        {
            Name "Outline"
            Tags { "LightMode" = "UniversalForward" }
            Cull Front
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float _Width;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };

            Varyings vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_TRANSFER_INSTANCE_ID(i, o);
                float3x3 m = (float3x3)GetObjectToWorldMatrix();
                float thinnest = min(length(m._m00_m10_m20), min(length(m._m01_m11_m21), length(m._m02_m12_m22)));
                float3 dirOS = normalize(i.positionOS.xyz + float3(1e-5, 2e-5, 3e-5));
                float3 dirWS = normalize(mul(m, dirOS));
                float3 positionWS = TransformObjectToWorld(i.positionOS.xyz) + dirWS * (_Width * thinnest);
                o.positionCS = TransformWorldToHClip(positionWS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                return half4(_BaseColor.rgb, 1);
            }
            ENDHLSL
        }
    }
}
