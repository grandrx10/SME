// A cartoon soap bubble: mostly see-through, a tinted rim, a thin ink edge round the silhouette and a hard
// white highlight. Hard steps, no gradients, so it reads as toon rather than glassy.
Shader "SME/Bubble"
{
    Properties
    {
        [HDR] _BaseColor ("Colour", Color) = (0.9, 1.4, 1.7, 1)
        _InkColor ("Ink", Color) = (0.09, 0.12, 0.2, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Bubble"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _InkColor;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; float3 viewWS : TEXCOORD1; UNITY_VERTEX_INPUT_INSTANCE_ID };

            Varyings vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_TRANSFER_INSTANCE_ID(i, o);
                float3 p = TransformObjectToWorld(i.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(p);
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.viewWS = GetWorldSpaceViewDir(p);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float3 n = normalize(i.normalWS);
                float3 v = normalize(i.viewWS);
                float rim = 1 - saturate(dot(n, v));

                // hard highlight up and to the left of the bubble, as seen from the camera
                float3 camRight = UNITY_MATRIX_V[0].xyz, camUp = UNITY_MATRIX_V[1].xyz;
                float3 h = normalize(v + camUp * 0.9 - camRight * 0.6);
                if (dot(n, h) > 0.94) return half4(1.6, 1.8, 2.0, 0.85);

                if (rim > 0.86) return half4(_InkColor.rgb, 0.6);      // ink edge
                if (rim > 0.55) return half4(_BaseColor.rgb, 0.4);     // tinted rim
                return half4(_BaseColor.rgb * 0.7, 0.1);               // clear middle
            }
            ENDHLSL
        }
    }
}
