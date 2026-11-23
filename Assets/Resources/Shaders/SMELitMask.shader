// The Star: marks (in the stencil, drawing nothing) every pixel of a lit body, through whatever is in front of it, so
// SME/LitOutline can draw just the rim round it. Goes on the body as an extra material.
Shader "SME/LitMask"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+50" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "LitMask"
            Tags { "LightMode"="SRPDefaultUnlit" }
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0
            Stencil { Ref 64 ReadMask 64 WriteMask 64 Comp Always Pass Replace }

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

            half4 frag (Varyings i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
    Fallback Off
}
