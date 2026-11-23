// Lava surface (KillVolume with lava on).
//
// Flat glowing orange: unlit HDR colour (above 1, so URP Bloom makes it glow), no pattern. Now and then a bubble
// swells up somewhere (a hotter disc with an ink outline), then pops by shrinking to nothing.
Shader "SME/Lava"
{
    Properties
    {
        [HDR] _Lava ("Lava", Color) = (3.2, 1.05, 0.12, 1)
        [HDR] _Bubble ("Bubble", Color) = (4.0, 2.2, 0.4, 1)
        _Ink ("Bubble Outline", Color) = (0.16, 0.04, 0.03, 1)
        _Spacing ("Bubble Spacing (m)", Float) = 2.4
        _BubbleSize ("Bubble Radius (m)", Float) = 0.32
        _OutlineWidth ("Outline Width (m)", Float) = 0.045
        _Life ("Bubble Life (s)", Float) = 1.3
        _MinWait ("Min Gap (s)", Float) = 3.0
        _MaxWait ("Max Gap (s)", Float) = 9.0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "Lava"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Lava;
                half4 _Bubble;
                half4 _Ink;
                float _Spacing;
                float _BubbleSize;
                float _OutlineWidth;
                float _Life;
                float _MinWait;
                float _MaxWait;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
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
                return o;
            }

            float2 Hash2(float2 p)
            {
                p = float2(dot(p, float2(127.1, 311.7)), dot(p, float2(269.5, 183.3)));
                return frac(sin(p) * 43758.5453);
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float t = _Time.y;
                float spacing = max(0.1, _Spacing);
                float2 p = input.positionWS.xz;
                float2 cell = floor(p / spacing);

                // Each grid cell holds at most one bubble at a time: it waits a random gap, swells for most of its
                // life, then pops by shrinking to nothing. Each new bubble picks a new spot in the cell.
                float2 h = Hash2(cell);
                float cycle = max(_Life + 0.1, lerp(_MinWait, _MaxWait, h.x));
                float tc = t / cycle + h.y;
                float n = floor(tc);
                float age = frac(tc) * cycle;
                if (age >= _Life) return half4(_Lava.rgb, 1);

                float2 spot = Hash2(cell * 1.7 + n * 3.1 + 11.0);
                float2 centre = (cell + 0.25 + 0.5 * spot) * spacing; // kept off the cell edges
                float k = age / max(0.01, _Life);
                float r = k < 0.8
                    ? _BubbleSize * (1.0 - (1.0 - k / 0.8) * (1.0 - k / 0.8))   // swell, easing out
                    : _BubbleSize * (1.0 + 0.15 * sin((k - 0.8) / 0.2 * 3.1416)) * (1.0 - (k - 0.8) / 0.2); // pop
                float d = length(p - centre);
                if (d > r) return half4(_Lava.rgb, 1);
                if (d > r - min(_OutlineWidth, r * 0.35)) return half4(_Ink.rgb, 1);

                // hotter disc with a small bright highlight toward one side
                half3 col = _Bubble.rgb;
                if (length(p - (centre + float2(-0.3, 0.3) * r)) < r * 0.28) col *= 1.4;
                return half4(col, 1);
            }
            ENDHLSL
        }

        // (no shadow caster: lava glows, it doesn't throw shadows)
        UsePass "Universal Render Pipeline/Unlit/DEPTHONLY"
    }

    FallBack Off
}
