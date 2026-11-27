// Flat, unlit colour for spell energy. Give it HDR colours (above 1) and Bloom makes it glow.
// Nothing fades: effects shrink to nothing instead, so every pixel stays at full strength.
Shader "SME/Glow"
{
    Properties
    {
        [HDR] _BaseColor ("Colour", Color) = (0.3, 0.7, 3, 1)
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        [Enum(Off, 0, On, 1)] _ZWrite ("Depth Write", Float) = 1
        [Enum(UnityEngine.Rendering.ColorWriteMask)] _ColorMask ("Colour Write", Float) = 15
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+10" }

        Pass
        {
            Name "Glow"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]
            ZWrite [_ZWrite]
            ColorMask [_ColorMask]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float _Cull;
                float _ZWrite;
                float _ColorMask;
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
                return half4(_BaseColor.rgb, 1);
            }
            ENDHLSL
        }
    }
}
