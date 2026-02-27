Shader "Custom/StereoBlend"
{
    Properties
    {
        _LeftTex ("Left Passthrough", 2D) = "black" {}
        _RightTex ("Right Passthrough", 2D) = "black" {}
        _LeftAssets ("Left Assets", 2D) = "clear" {}
        _RightAssets ("Right Assets", 2D) = "clear" {}
        _AssetOffsetX ("Asset Horizontal Offset", Float) = -0.05
        _AssetOffsetY ("Asset Vertical Offset", Float) = 0.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _LeftTex;
            sampler2D _RightTex;
            sampler2D _LeftAssets;
            sampler2D _RightAssets;
            float _AssetOffsetX;
            float _AssetOffsetY;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 assetUV = i.uv + float2(_AssetOffsetX, _AssetOffsetY);
                // Passthrough from left eye only — no doubling
                fixed4 passthrough = tex2D(_LeftTex, i.uv);

                // Average Unity assets from both eyes (reduces aliasing on edges)
                fixed4 leftAsset  = tex2D(_LeftAssets,  assetUV);
                fixed4 rightAsset = tex2D(_RightAssets, assetUV);
                fixed4 assets = (leftAsset + rightAsset) * 0.5;

                // Overlay averaged assets on top of single-eye passthrough
                return lerp(passthrough, assets, assets.a);
            }
            ENDCG
        }
    }
}
