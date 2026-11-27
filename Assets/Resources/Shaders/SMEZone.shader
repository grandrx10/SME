// The closing zone's border: a see-through red wall with hot, inked rings climbing it, and an inked line of light
// wherever it meets something solid. Unlit; both faces.
Shader "SME/Zone"
{
    Properties
    {
        [HDR] _Wall ("Wall", Color) = (1.3, 0.06, 0.08, 1)
        [HDR] _Hot ("Rings", Color) = (4, 0.35, 0.3, 1)
        _Ink ("Ink", Color) = (0.09, 0.12, 0.2, 1)
        _Alpha ("Opacity", Range(0, 1)) = 0.3
        _Gap ("Between rings (m)", Float) = 4
        _Climb ("Climb (m/s)", Float) = 1.5
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent-90" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Zone"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Wall;
                half4 _Hot;
                half4 _Ink;
                float _Alpha;
                float _Gap;
                float _Climb;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float eye : TEXCOORD2;
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(i.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.eye = -TransformWorldToView(o.positionWS).z;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                // where it meets something: a line of light, then ink
                float scene = LinearEyeDepth(SampleSceneDepth(GetNormalizedScreenSpaceUV(i.positionCS)), _ZBufferParams);
                float3 ray = i.positionWS - _WorldSpaceCameraPos;
                float past = (scene - i.eye) / max(i.eye, 0.01) * abs(dot(ray, normalize(i.normalWS))); // how far past the wall that is
                if (past >= 0)
                {
                    if (past < 0.12) return half4(_Hot.rgb, 1);
                    if (past < 0.17) return half4(_Ink.rgb, 1);
                }

                // the rings (too far off to make out: just the wall)
                if (fwidth(i.positionWS.y) < 0.15)
                {
                    float d = abs(frac((i.positionWS.y - _Time.y * _Climb) / _Gap) - 0.5) * _Gap;
                    if (d < 0.07) return half4(_Hot.rgb, 1);
                    if (d < 0.11) return half4(_Ink.rgb, 1);
                }
                return half4(_Wall.rgb, _Alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
