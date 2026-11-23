// The Sun: turns the colours of whatever it's drawn over inside out (1 - colour). Goes on the body as an extra
// material, so the whole body (face and all) shows in inverted colours.
Shader "SME/Invert"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent-100" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "Invert"
            Tags { "LightMode"="SRPDefaultUnlit" }
            Cull Back
            ZWrite Off
            ZTest LEqual
            Offset -1, -1
            Blend OneMinusDstColor Zero, Zero One

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert (Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                return o;
            }

            half4 frag (Varyings i) : SV_Target { return half4(1, 1, 1, 1); }
            ENDHLSL
        }
    }
    Fallback Off
}
