Shader "Unlit/MinimalFlip"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }

    SubShader
    {
        LOD 100

        Pass
        {
            ZWrite Off
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                // 如果你要考虑 Tiling/Offset，可以用 TRANSFORM_TEX：
                // o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                // 不需要的话就直接：
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // flip y
                float2 uvFlipped = float2(i.uv.x, 1.0 - i.uv.y);
                return tex2D(_MainTex, uvFlipped);
            }
            ENDCG
        }
    }
}
