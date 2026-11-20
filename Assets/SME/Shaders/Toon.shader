// Sudden Mage Encounter cel/toon shader for URP (Forward / Forward+).
// Same Base Map + Base Color slots as URP/Lit, so textured + tinted materials keep working.
Shader "SME/Toon"
{
    Properties
    {
        [MainTexture] _BaseMap ("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor ("Base Color", Color) = (1, 1, 1, 1)

        [Header(Cel Lighting)]
        _ShadowColor ("Shadow Tint", Color) = (0.62, 0.64, 0.76, 1)
        _Bands ("Light Bands", Range(1, 5)) = 2
        _BandSoftness ("Band Edge Softness", Range(0.001, 0.25)) = 0.04
        _AmbientStrength ("Ambient Strength", Range(0, 1)) = 0.35

        [Header(Rim)]
        _RimColor ("Rim Color", Color) = (1, 1, 1, 1)
        _RimStrength ("Rim Strength", Range(0, 1)) = 0.2
        _RimPower ("Rim Sharpness", Range(1, 12)) = 5

        [Header(Highlight)]
        _SpecStrength ("Highlight Strength", Range(0, 1)) = 0.15
        _SpecSize ("Highlight Size", Range(0.001, 0.3)) = 0.06

        // used by the borrowed URP/Lit shadow & depth passes
        [HideInInspector] _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        [HideInInspector] _BumpMap ("Normal Map", 2D) = "bump" {}
        [HideInInspector] _BumpScale ("Normal Scale", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ToonForward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half4 _ShadowColor;
                half _Bands;
                half _BandSoftness;
                half _AmbientStrength;
                half4 _RimColor;
                half _RimStrength;
                half _RimPower;
                half _SpecStrength;
                half _SpecSize;
                half _Cutoff;
                half _BumpScale;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS   : TEXCOORD2;
                half   fogFactor  : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs nrm = GetVertexNormalInputs(input.normalOS);
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.normalWS = nrm.normalWS;
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                o.fogFactor = ComputeFogFactor(pos.positionCS.z);
                return o;
            }

            // quantise 0..1 lighting into soft-edged bands
            half Cel(half x)
            {
                half b = max(1.0h, _Bands);
                half v = saturate(x) * b;
                half f = floor(v);
                half w = max(0.001h, _BandSoftness * b);
                half step01 = smoothstep(0.5h - w, 0.5h + w, v - f);
                return saturate((f + step01) / b);
            }

            half3 ToonLight(Light light, half3 N, half3 V, half3 albedo, out half amount)
            {
                half ndl = dot(N, light.direction) * 0.5h + 0.5h;      // wrapped lambert
                half atten = light.shadowAttenuation * light.distanceAttenuation;
                amount = Cel(ndl * atten);
                half3 diffuse = albedo * lerp(_ShadowColor.rgb, 1.0h, amount) * light.color * saturate(light.distanceAttenuation * 4.0h);

                half3 H = normalize(light.direction + V);
                half spec = smoothstep(1.0h - _SpecSize, 1.0h - _SpecSize * 0.5h, saturate(dot(N, H))) * amount;
                return diffuse + light.color * spec * _SpecStrength;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half3 albedo = tex.rgb * _BaseColor.rgb;

                half3 N = normalize(input.normalWS);
                half3 V = normalize(GetWorldSpaceViewDir(input.positionWS));

                // main light (+ shadows)
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half mainAmount;
                half3 color = ToonLight(mainLight, N, V, albedo, mainAmount);

                // ambient keeps the dark side readable
                color += albedo * SampleSH(N) * _AmbientStrength;

                // extra lights (muzzle flashes etc.)
                #if defined(_ADDITIONAL_LIGHTS)
                {
                    InputData inputData = (InputData)0;
                    inputData.positionWS = input.positionWS;
                    inputData.normalWS = N;
                    inputData.viewDirectionWS = V;
                    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                    uint pixelLightCount = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(pixelLightCount)
                        Light light = GetAdditionalLight(lightIndex, input.positionWS, half4(1, 1, 1, 1));
                        half amt;
                        color += ToonLight(light, N, V, albedo, amt) * amt;
                    LIGHT_LOOP_END
                }
                #endif

                // rim light on the lit side
                half rim = pow(1.0h - saturate(dot(N, V)), _RimPower);
                rim = smoothstep(0.45h, 0.55h, rim) * _RimStrength * (0.35h + 0.65h * mainAmount);
                color += _RimColor.rgb * rim;

                color = MixFog(color, input.fogFactor);
                return half4(color, 1);
            }
            ENDHLSL
        }

        // shadows / depth / normals reuse URP/Lit's passes (needed for outlines + shadows)
        UsePass "Universal Render Pipeline/Lit/SHADOWCASTER"
        UsePass "Universal Render Pipeline/Lit/DEPTHONLY"
        UsePass "Universal Render Pipeline/Lit/DEPTHNORMALS"
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
