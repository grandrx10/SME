// An afterimage: flat colour that thins out in an ordered-dither stipple as _Fade goes 1 -> 0 (opaque, so it
// never needs sorting). Same idea as The Last Chromatist's dithered _Fade.
Shader "SME/Afterimage"
{
    Properties
    {
        [HDR] _BaseColor ("Colour", Color) = (0.25, 0.75, 4, 1)
        _Fade ("Visibility", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+10" }

        Pass
        {
            Name "Afterimage"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half _Fade;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };

            Varyings vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_TRANSFER_INSTANCE_ID(i, o);
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                // 4x4 Bayer threshold for this pixel
                uint2 p = (uint2)i.positionCS.xy & 3;
                const float bayer[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
                float threshold = (bayer[p.y * 4 + p.x] + 0.5) / 16.0;
                clip(_Fade - threshold);
                return half4(_BaseColor.rgb, 1);
            }
            ENDHLSL
        }
    }
}
