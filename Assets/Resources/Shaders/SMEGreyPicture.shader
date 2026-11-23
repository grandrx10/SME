// A picture on the UI in pure black and white (The World's THE WORLD scene): its brightness only, opened up so the
// backdrop is white. Opaque.
Shader "SME/GreyPicture"
{
    Properties
    {
        [PerRendererData] _MainTex ("Picture", 2D) = "white" {}
        _Gain ("Gain", Float) = 1.75
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Gain;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float l = dot(tex2D(_MainTex, i.uv).rgb, float3(0.2126, 0.7152, 0.0722));
                return float4(saturate(l * _Gain).xxx, 1);
            }
            ENDHLSL
        }
    }
}
