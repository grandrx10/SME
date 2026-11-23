// SME/Toon with a _Fade that thins the surface out in an ordered-dither stipple (1 = solid, 0 = gone), the same
// way The Last Chromatist faded things. Every pass dithers (colour, depth, normals), so the screen outline thins
// out together with the body instead of leaving a ghost silhouette. Opaque, so it never needs sorting.
// Used for things that manifest out of thin air and vanish again, like the Blade cards' sword.
Shader "SME/ToonFade"
{
    Properties
    {
        [MainTexture] _BaseMap ("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _Fade ("Visibility", Range(0, 1)) = 1
        [HDR] _FlashColor ("Flash Colour", Color) = (0, 0, 0, 0)

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
    }

    HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _Fade;
            half4 _FlashColor;
            half4 _ShadowColor;
            half _Bands;
            half _BandSoftness;
            half _AmbientStrength;
            half4 _RimColor;
            half _RimStrength;
            half _RimPower;
            half _SpecStrength;
            half _SpecSize;
        CBUFFER_END

        // 4x4 Bayer stipple: below _Fade the pixel stays, above it the pixel is dropped
        void DitherClip(float4 positionCS)
        {
            uint2 p = (uint2)positionCS.xy & 3;
            const float bayer[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
            clip(_Fade - (bayer[p.y * 4 + p.x] + 0.5) / 16.0);
        }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ToonFadeForward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                half fogFactor : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.normalWS = GetVertexNormalInputs(input.normalOS).normalWS;
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                o.fogFactor = ComputeFogFactor(pos.positionCS.z);
                return o;
            }

            half Cel(half x)
            {
                half b = max(1.0h, _Bands);
                half v = saturate(x) * b;
                half f = floor(v);
                half w = max(0.001h, _BandSoftness * b);
                return saturate((f + smoothstep(0.5h - w, 0.5h + w, v - f)) / b);
            }

            half3 ToonLight(Light light, half3 N, half3 V, half3 albedo, out half amount)
            {
                half ndl = dot(N, light.direction) * 0.5h + 0.5h;
                amount = Cel(ndl * light.shadowAttenuation * light.distanceAttenuation);
                half3 diffuse = albedo * lerp(_ShadowColor.rgb, 1.0h, amount) * light.color * saturate(light.distanceAttenuation * 4.0h);
                half3 H = normalize(light.direction + V);
                half spec = smoothstep(1.0h - _SpecSize, 1.0h - _SpecSize * 0.5h, saturate(dot(N, H))) * amount;
                return diffuse + light.color * spec * _SpecStrength;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                DitherClip(input.positionCS);
                half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb * _BaseColor.rgb;
                half3 N = normalize(input.normalWS);
                half3 V = normalize(GetWorldSpaceViewDir(input.positionWS));
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half mainAmount;
                half3 color = ToonLight(mainLight, N, V, albedo, mainAmount);
                color += albedo * SampleSH(N) * _AmbientStrength;
                half rim = pow(1.0h - saturate(dot(N, V)), _RimPower);
                color += _RimColor.rgb * smoothstep(0.45h, 0.55h, rim) * _RimStrength * (0.35h + 0.65h * mainAmount);
                // a charge flash: the whole blade goes hot white (HDR, so it blooms)
                color = lerp(color, _FlashColor.rgb, saturate(_FlashColor.a));
                color = MixFog(color, input.fogFactor);
                return half4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            Varyings vert(Attributes i)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_TRANSFER_INSTANCE_ID(i, o);
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                return o;
            }
            half frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                DitherClip(i.positionCS);
                return i.positionCS.z;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            Varyings vert(Attributes i)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_TRANSFER_INSTANCE_ID(i, o);
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                return o;
            }
            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                DitherClip(i.positionCS);
                #if defined(_GBUFFER_NORMALS_OCT)
                    float2 oct = PackNormalOctQuadEncode(normalize(i.normalWS));
                    return half4(PackFloat2To888(saturate(oct * 0.5 + 0.5)), 0.0);
                #else
                    return half4(NormalizeNormalPerPixel(i.normalWS), 0.0);
                #endif
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
