// The Star: a glowing outline round a lit body, seen through walls and floors. The hull of the body pushed out in
// screen space (as SME/HullOutline), drawn over everything except where SME/LitMask marked the body itself.
Shader "SME/LitOutline"
{
    Properties
    {
        [HDR] _OutlineColor ("Color", Color) = (3, 1.5, 0.6, 1)
        _Width ("Width (pixels)", Float) = 4
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+51" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "LitOutline"
            Tags { "LightMode"="SRPDefaultUnlit" }
            Cull Off
            ZWrite Off
            ZTest Always
            Stencil { Ref 64 ReadMask 64 Comp NotEqual }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _OutlineColor;
                float _Width;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert (Attributes v)
            {
                Varyings o;
                float4 cs = TransformObjectToHClip(v.positionOS.xyz);
                float3 nWS = TransformObjectToWorldNormal(v.normalOS);
                float2 nCS = mul((float3x3)UNITY_MATRIX_VP, nWS).xy;
                nCS /= max(length(nCS), 1e-5);
                cs.xy += nCS * (_Width * 2.0 / _ScreenParams.xy) * cs.w;
                o.positionCS = cs;
                return o;
            }

            half4 frag (Varyings i) : SV_Target { return half4(_OutlineColor.rgb, 1); }
            ENDHLSL
        }
    }
    Fallback Off
}
