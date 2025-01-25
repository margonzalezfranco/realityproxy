Shader "Custom/FlipY"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        LOD 100

        Pass
        {
            ZWrite Off
            Cull Off
            ZTest Always

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
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            sampler2D _MainTex;

            v2f vert (appdata v)
            {
                v2f o;
                // standard vertex transform
                o.vertex = UnityObjectToClipPos(v.vertex);

                // FLIP Y: For each input UV, we do uv.y = 1 - uv.y
                // This inverts the vertical axis
                o.uv = float2(v.uv.x, 1.0 - v.uv.y);

                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Just sample the flipped UV
                fixed4 col = tex2D(_MainTex, i.uv);
                return col;
            }
            ENDCG
        }
    }
}
