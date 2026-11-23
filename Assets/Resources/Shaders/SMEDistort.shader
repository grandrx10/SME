// A nearly invisible ripple: the scene behind the mesh, bent. Seen only as a quick wobble on screen
// (Severance's cut). Needs the URP asset's Opaque Texture (on for PC).
// UV: u runs along the shape, v across it; the bend is strongest down the middle and dies off to the edges.
Shader "SME/Distort"
{
    Properties
    {
        _Strength ("Bend (screen fraction)", Range(0, 0.1)) = 0.025
        _Ripples ("Ripples along it", Range(0, 20)) = 7
        _Speed ("Ripple speed", Range(0, 40)) = 18
        _Visible ("Visibility", Range(0, 1)) = 1
        [HDR] _Tint ("Edge tint", Color) = (1, 1, 1, 0.06)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent+20" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Distort"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite Off
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half _Strength;
                half _Ripples;
                half _Speed;
                half _Visible;
                half4 _Tint;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float4 screen : TEXCOORD1; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.uv = i.uv;
                o.screen = ComputeScreenPos(o.positionCS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float2 uv = i.screen.xy / i.screen.w;
                // strongest down the middle of the band, nothing at its edges or its tips
                float across = sin(saturate(i.uv.y) * PI);
                float along = sin(saturate(i.uv.x) * PI);
                float w = across * along * _Visible;
                float t = _Time.y * _Speed;
                float2 bend = float2(sin(i.uv.x * _Ripples * 6.2832 + t), cos(i.uv.y * 9.0 - t * 0.7)) * _Strength * w;
                half3 scene = SampleSceneColor(uv + bend);
                // the faintest bright edge on the leading side, so it reads in motion
                half edge = smoothstep(0.75, 1.0, i.uv.y) * along * _Visible;
                return half4(scene + _Tint.rgb * _Tint.a * edge, 1);
            }
            ENDHLSL
        }
    }
}
