// Glass voxels: palette entries whose alpha is under 255 (see VoxelMesher).
//
// Same idea as The Last Chromatist's NeoNoir/Glass, adapted to flat voxel faces:
//   Ink     A solid dark line along every edge of the glass shape (where the face meets air, a corner, or
//           something that isn't glass), so a pane reads as one outlined piece rather than a grid of cubes.
//   Rim     Just inside the ink, a dithered stipple in a light tint thins out toward the middle.
//   Glint   Two thin diagonal HDR streaks in world space, so it looks like a cartoon window.
//   Face    The voxel colour as a see-through tint; its opacity is the palette alpha.
// The mesher gives each glass face uv0 = position across the cell face (0..1) and uv1 = which of its four
// sides are edges of the glass shape (1) or run on into more glass (0).
Shader "SME/VoxelGlass"
{
    Properties
    {
        _InkColor ("Ink", Color) = (0.09, 0.12, 0.2, 1)
        _InkWidth ("Ink Width (cells)", Range(0, 0.5)) = 0.09
        _RimWidth ("Rim Width (cells)", Range(0, 1)) = 0.3
        _RimFill ("Rim Fill", Range(0, 1)) = 0.6
        _RimTint ("Rim Lighten", Range(0, 1)) = 0.55
        [HDR] _GlintColor ("Glint", Color) = (2.2, 2.3, 2.4, 1)
        _GlintPeriod ("Glint Period (m)", Float) = 1.6
        _GlintWidth ("Glint Width", Range(0, 0.5)) = 0.07
        _MinAlpha ("Min Face Opacity", Range(0, 1)) = 0.12
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "VoxelGlass"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _InkColor;
                half _InkWidth;
                half _RimWidth;
                half _RimFill;
                half _RimTint;
                half4 _GlintColor;
                float _GlintPeriod;
                half _GlintWidth;
                half _MinAlpha;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                half4  color      : COLOR;
                float2 uv         : TEXCOORD0;
                float4 edges      : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                half4  color      : TEXCOORD2;
                float2 uv         : TEXCOORD3;
                nointerpolation float4 edges : TEXCOORD4;
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
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.normalWS = GetVertexNormalInputs(input.normalOS).normalWS;
                o.color = input.color;
                o.uv = input.uv;
                o.edges = input.edges;
                o.fogFactor = ComputeFogFactor(pos.positionCS.z);
                return o;
            }

            // interleaved gradient noise, as in NeoNoir/Glass and the hex barrier, so all stipples dither alike
            float Stipple(float4 positionCS)
            {
                return frac(52.9829189 * frac(dot(positionCS.xy, float2(0.06711056, 0.00583715)))) * 0.998 + 0.001;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // distance (in cells) to the nearest side of this face that is an edge of the glass shape
                float2 uv = input.uv;
                float4 sideDist = float4(uv.x, 1.0 - uv.x, uv.y, 1.0 - uv.y);
                float4 masked = lerp(9.0.xxxx, sideDist, step(0.5, input.edges));
                float dist = min(min(masked.x, masked.y), min(masked.z, masked.w));

                half3 N = normalize(input.normalWS);
                Light mainLight = GetMainLight();
                half ndl = saturate(dot(N, mainLight.direction) * 0.5h + 0.5h);
                half3 tint = input.color.rgb * lerp(0.72h, 1.0h, ndl) * mainLight.color;
                tint += input.color.rgb * SampleSH(N) * 0.25h;

                half4 col;
                if (dist < _InkWidth)
                {
                    col = half4(_InkColor.rgb, 1.0h);
                }
                else
                {
                    half rim = saturate(1.0h - (dist - _InkWidth) / max(1e-4h, _RimWidth)) * _RimFill;
                    float3 diag = normalize(float3(1.0, 1.15, 0.6));
                    float g = frac(dot(input.positionWS, diag) / max(0.01, _GlintPeriod));
                    half glint = step(abs(g - 0.5), _GlintWidth) + step(abs(g - 0.68), _GlintWidth * 0.45);

                    if (glint > 0.0h)
                        col = half4(_GlintColor.rgb, 1.0h);
                    else if (Stipple(input.positionCS) < rim)
                        col = half4(lerp(tint, 1.0h, _RimTint), 1.0h);
                    else
                        col = half4(tint, max(_MinAlpha, input.color.a));
                }

                col.rgb = MixFog(col.rgb, input.fogFactor);
                return col;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
