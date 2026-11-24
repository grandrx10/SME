// A body of water (RisingWater): flat cartoon water in the Tsunami's colours. Unlit deep blue, with patches of aqua and
// inked foam on a 0.25 m grid that drift about, and an inked foam line wherever it meets something solid.
Shader "SME/Water"
{
    Properties
    {
        [HDR] _Deep ("Water", Color) = (0.06, 0.5, 1.1, 1)
        [HDR] _Aqua ("Crests", Color) = (0.12, 0.85, 1.5, 1)
        [HDR] _Foam ("Foam", Color) = (2.1, 2.6, 2.8, 1)
        _Ink ("Ink", Color) = (0.09, 0.12, 0.2, 1)
        _Alpha ("Opacity", Range(0, 1)) = 0.86
        _Cell ("Voxel Size (m)", Float) = 0.25
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent-100" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Water"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Deep;
                half4 _Aqua;
                half4 _Foam;
                half4 _Ink;
                float _Alpha;
                float _Cell;
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

            // how high the water stands in a cell, -1 .. 1
            float Wave(float2 c)
            {
                float t = _Time.y;
                float a = sin(c.x * 0.55 + t * 0.9 + 1.6 * sin(c.y * 0.31 + t * 0.35));
                float b = sin(c.y * 0.62 - t * 0.7 + 1.4 * sin(c.x * 0.27 - t * 0.3));
                return a * b;
            }

            static const float FoamAt = 0.9, AquaAt = 0.5;

            half4 frag(Varyings i) : SV_Target
            {
                // the sides: a band of foam along the top
                if (i.normalWS.y < 0.5)
                {
                    float down = UNITY_MATRIX_M._m13 - i.positionWS.y;
                    if (down < 0.16) return half4(_Foam.rgb, 1);
                    if (down < 0.22) return half4(_Ink.rgb, 1);
                    return half4(_Deep.rgb, _Alpha);
                }

                // where it meets something: ink, then foam
                float scene = LinearEyeDepth(SampleSceneDepth(GetNormalizedScreenSpaceUV(i.positionCS)), _ZBufferParams);
                float3 ray = i.positionWS - _WorldSpaceCameraPos;
                float under = (scene - i.eye) / max(i.eye, 0.01) * abs(ray.y); // how deep it is right there
                if (under >= 0)
                {
                    if (under < 0.07) return half4(_Foam.rgb, 1);
                    if (under < 0.11) return half4(_Ink.rgb, 1);
                }

                float2 p = i.positionWS.xz / _Cell;
                float2 fw = fwidth(p);
                if (max(fw.x, fw.y) > 0.7) return half4(_Deep.rgb, _Alpha); // too far off to make out

                float2 c = floor(p), f = p - c;
                float w = Wave(c);
                if (w > FoamAt) return half4(_Foam.rgb, 1);
                // the ink round the foam
                const float edge = 0.2;
                if ((f.x < edge && Wave(c + float2(-1, 0)) > FoamAt) || (f.x > 1 - edge && Wave(c + float2(1, 0)) > FoamAt) ||
                    (f.y < edge && Wave(c + float2(0, -1)) > FoamAt) || (f.y > 1 - edge && Wave(c + float2(0, 1)) > FoamAt))
                    return half4(_Ink.rgb, 1);
                return half4(w > AquaAt ? _Aqua.rgb : _Deep.rgb, _Alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
