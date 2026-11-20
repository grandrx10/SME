// Voxel surfaces: vertex colour (the voxel's material) with the same cel lighting as SME/Toon,
// plus a tiny per-voxel brightness variation and soft cell edges so individual voxels read on big merged faces.
Shader "SME/Voxel"
{
    Properties
    {
        [MainColor] _BaseColor ("Tint", Color) = (1, 1, 1, 1)
        _VoxelSize ("Voxel Size", Float) = 0.25
        _Variation ("Per-voxel Variation", Range(0, 0.3)) = 0.06
        _EdgeDarken ("Edge Darken", Range(0, 0.5)) = 0.12
        _EdgeWidth ("Edge Width", Range(0.001, 0.3)) = 0.06

        [Header(Cel Lighting)]
        _ShadowColor ("Shadow Tint", Color) = (0.62, 0.64, 0.76, 1)
        _Bands ("Light Bands", Range(1, 5)) = 2
        _BandSoftness ("Band Edge Softness", Range(0.001, 0.25)) = 0.04
        _AmbientStrength ("Ambient Strength", Range(0, 1)) = 0.4

        [HideInInspector] _BaseMap ("Base Map", 2D) = "white" {}
        [HideInInspector] _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        [HideInInspector] _BumpMap ("Normal Map", 2D) = "bump" {}
        [HideInInspector] _BumpScale ("Normal Scale", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "VoxelForward"
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
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                float _VoxelSize;
                half _Variation;
                half _EdgeDarken;
                half _EdgeWidth;
                half4 _ShadowColor;
                half _Bands;
                half _BandSoftness;
                half _AmbientStrength;
                half _Cutoff;
                half _BumpScale;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                half4  color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float3 positionOS : TEXCOORD2;
                float3 normalOS   : TEXCOORD3;
                half4  color      : TEXCOORD4;
                half   fogFactor  : TEXCOORD5;
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
                o.positionOS = input.positionOS.xyz;
                o.normalOS = input.normalOS;
                o.color = input.color;
                o.fogFactor = ComputeFogFactor(pos.positionCS.z);
                return o;
            }

            float Hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            half Cel(half x)
            {
                half b = max(1.0h, _Bands);
                half v = saturate(x) * b;
                half f = floor(v);
                half w = max(0.001h, _BandSoftness * b);
                half step01 = smoothstep(0.5h - w, 0.5h + w, v - f);
                return saturate((f + step01) / b);
            }

            half3 ToonLight(Light light, half3 N, half3 albedo, out half amount)
            {
                half ndl = dot(N, light.direction) * 0.5h + 0.5h;
                half atten = light.shadowAttenuation * light.distanceAttenuation;
                amount = Cel(ndl * atten);
                return albedo * lerp(_ShadowColor.rgb, 1.0h, amount) * light.color * saturate(light.distanceAttenuation * 4.0h);
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // which voxel this pixel belongs to (step half a voxel back into the surface)
                float3 g = input.positionOS / _VoxelSize;
                float3 cell = floor(g - input.normalOS * 0.5 + 0.0001);
                half variation = (Hash(cell) - 0.5h) * 2.0h * _Variation;

                // soft darkening towards the voxel's edges on this face
                float3 f = frac(g);
                float3 an = abs(input.normalOS);
                float2 uv = an.x > 0.5 ? f.yz : (an.y > 0.5 ? f.xz : f.xy);
                float2 e = min(uv, 1.0 - uv);
                half edge = 1.0h - _EdgeDarken * (1.0h - smoothstep(0.0, _EdgeWidth, min(e.x, e.y)));

                half3 albedo = input.color.rgb * _BaseColor.rgb * (1.0h + variation) * edge;
                half3 N = normalize(input.normalWS);

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half amt;
                half3 color = ToonLight(mainLight, N, albedo, amt);
                color += albedo * SampleSH(N) * _AmbientStrength;

                #if defined(_ADDITIONAL_LIGHTS)
                {
                    InputData inputData = (InputData)0;
                    inputData.positionWS = input.positionWS;
                    inputData.normalWS = N;
                    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                    uint pixelLightCount = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(pixelLightCount)
                        Light light = GetAdditionalLight(lightIndex, input.positionWS, half4(1, 1, 1, 1));
                        half a2;
                        color += ToonLight(light, N, albedo, a2) * a2;
                    LIGHT_LOOP_END
                }
                #endif

                color = MixFog(color, input.fogFactor);
                return half4(color, _BaseColor.a);
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/SHADOWCASTER"
        UsePass "Universal Render Pipeline/Lit/DEPTHONLY"
        UsePass "Universal Render Pipeline/Lit/DEPTHNORMALS"
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
