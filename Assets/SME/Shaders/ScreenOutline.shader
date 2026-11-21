// Full-screen outline (used by a URP "Full Screen Pass Renderer Feature").
// Finds edges from depth + normal discontinuities and draws them in a flat colour.
Shader "SME/ScreenOutline"
{
    Properties
    {
        _OutlineColor ("Outline Color", Color) = (0.32, 0.33, 0.36, 1)
        _Thickness ("Thickness (pixels)", Range(0.5, 4)) = 1.25
        _DepthThreshold ("Depth Threshold", Range(0.001, 0.5)) = 0.06
        _NormalThreshold ("Normal Threshold", Range(0.01, 1.5)) = 0.45
        _FadeStart ("Fade Start (m)", Float) = 60
        _FadeEnd ("Fade End (m)", Float) = 160
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        Pass
        {
            Name "Outline"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

            float4 _OutlineColor;
            float _Thickness;
            float _DepthThreshold;
            float _NormalThreshold;
            float _FadeStart;
            float _FadeEnd;

            // Distance along the view, rebuilt from the camera's real (inverse) view-projection rather than
            // _ZBufferParams: that formula assumes an ordinary projection, and the portal cameras use an oblique
            // one (near plane bent onto the portal), where it gives nonsense and draws false edges.
            float EyeDepth(float2 uv)
            {
                float3 world = ComputeWorldSpacePosition(uv, SampleSceneDepth(uv), UNITY_MATRIX_I_VP);
                return dot(world - _WorldSpaceCameraPos, -UNITY_MATRIX_V[2].xyz);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                half4 col = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);

                float2 px = _Thickness / _ScreenParams.xy;
                float2 o1 = float2(px.x, 0);
                float2 o2 = float2(0, px.y);

                float d  = EyeDepth(uv);
                float dl = EyeDepth(uv - o1), dr = EyeDepth(uv + o1);
                float dd = EyeDepth(uv - o2), du = EyeDepth(uv + o2);

                // depth edges: relative difference so it works near and far
                float depthDiff = (abs(dl - d) + abs(dr - d) + abs(dd - d) + abs(du - d)) / max(d, 0.0001);
                float depthEdge = smoothstep(_DepthThreshold, _DepthThreshold * 2.0, depthDiff);

                // normal edges (creases)
                float3 n  = SampleSceneNormals(uv);
                float3 nl = SampleSceneNormals(uv - o1), nr = SampleSceneNormals(uv + o1);
                float3 nd = SampleSceneNormals(uv - o2), nu = SampleSceneNormals(uv + o2);
                float normalDiff = (1 - dot(n, nl)) + (1 - dot(n, nr)) + (1 - dot(n, nd)) + (1 - dot(n, nu));
                float normalEdge = smoothstep(_NormalThreshold, _NormalThreshold * 1.6, normalDiff);

                float edge = saturate(max(depthEdge, normalEdge));

                // fade out in the distance so far geometry doesn't turn to mush
                float nearest = min(d, min(min(dl, dr), min(dd, du)));
                edge *= 1.0 - saturate((nearest - _FadeStart) / max(0.001, _FadeEnd - _FadeStart));

                col.rgb = lerp(col.rgb, _OutlineColor.rgb, edge * _OutlineColor.a);
                return col;
            }
            ENDHLSL
        }
    }
}
