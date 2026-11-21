// A window onto somewhere else: samples _PortalTex at this pixel's screen position (the texture was rendered
// by a camera matching the main one's view through the portal, so it lines up seamlessly). _Open 0 shows an
// arcane swirl instead (portal not linked / too far to render). _Shape 1 cuts it to an oval (Warp Self's window).
//
// It also writes itself into the depth and depth-normals prepasses: the screen-space outline finds edges from
// those, and without them it would draw the edges of whatever stands *behind* the portal over the view through it.
Shader "SME/Portal"
{
    Properties
    {
        _PortalTex ("View", 2D) = "black" {}
        _Open ("Open", Range(0, 1)) = 1
        _Shape ("Shape (0 rectangle, 1 oval)", Float) = 0
        [HDR] _Edge ("Edge glow", Color) = (0.25, 0.75, 4, 1)
        _EdgeWidth ("Edge width", Range(0, 0.5)) = 0.08
        [HDR] _Swirl ("Swirl", Color) = (0.12, 0.3, 1.6, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+20" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_PortalTex);
        SAMPLER(sampler_PortalTex);

        CBUFFER_START(UnityPerMaterial)
            float4 _PortalTex_ST;
            half _Open;
            half _Shape;
            half4 _Edge;
            half _EdgeWidth;
            half4 _Swirl;
        CBUFFER_END

        /// -1..1 across the surface; clips outside the oval. Returns how far in from the edge this point is.
        float EdgeDistance(float2 uv)
        {
            float2 c = uv * 2.0 - 1.0;
            if (_Shape > 0.5)
            {
                float r = length(c);
                clip(1.0 - r);
                return 1.0 - r;
            }
            return 1.0 - max(abs(c.x), abs(c.y));
        }
        ENDHLSL

        Pass
        {
            Name "Portal"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float4 screen : TEXCOORD0; float2 uv : TEXCOORD1; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(i.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.screen = p.positionNDC;
                o.uv = i.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float edgeDist = EdgeDistance(i.uv);
                float2 suv = i.screen.xy / i.screen.w;
                half3 view = SAMPLE_TEXTURE2D(_PortalTex, sampler_PortalTex, suv).rgb;

                // closed: a slow arcane swirl
                float2 c = i.uv * 2.0 - 1.0;
                float a = atan2(c.y, c.x);
                float rr = length(c * float2(1.0, 0.67));
                half s = 0.5 + 0.5 * sin(a * 3.0 + rr * 9.0 - _Time.y * 3.5);
                half3 swirl = _Swirl.rgb * (0.45 + 0.75 * s * s);

                half3 col = lerp(swirl, view, _Open);
                // glowing rim, hard-edged like everything else
                half rim = step(edgeDist, _EdgeWidth);
                col = lerp(col, _Edge.rgb, rim);
                return half4(col, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            Cull Back
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.uv = i.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                EdgeDistance(i.uv);
                return half4(i.positionCS.z, 0, 0, 0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; float2 uv : TEXCOORD1; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.uv = i.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                EdgeDistance(i.uv);
                float3 n = normalize(i.normalWS);
                #if defined(_GBUFFER_NORMALS_OCT)
                    float2 oct = PackNormalOctQuadEncode(n);
                    return half4(PackFloat2To888(saturate(oct * 0.5 + 0.5)), 0.0);
                #else
                    return half4(n, 0.0);
                #endif
            }
            ENDHLSL
        }
    }
}
