// Solid-colour outline around a mesh (inverted hull, constant width in pixels).
// Added as an extra material on other players' bodies: red = enemy, blue = teammate.
// Depth tested like normal geometry, so it never shows through walls.
Shader "SME/HullOutline"
{
    Properties
    {
        _OutlineColor ("Color", Color) = (1, 0.15, 0.1, 1)
        _Width ("Width (pixels)", Float) = 2.5
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry+10" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "HullOutline"
            Tags { "LightMode"="SRPDefaultUnlit" }
            Cull Front
            ZWrite On
            ZTest LEqual

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
                // push out along the screen-space normal; * w keeps the width constant in pixels
                cs.xy += nCS * (_Width * 2.0 / _ScreenParams.xy) * cs.w;
                o.positionCS = cs;
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                return half4(_OutlineColor.rgb, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
