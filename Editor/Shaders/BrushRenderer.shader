Shader "Hidden/BrushRenderer"
{
    Properties {}
    SubShader
    {
        Tags
        {
            "RenderType"="Opaque+10000"
        }
        LOD 100
        ZTest On
        Offset -1, 1
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
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
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            float4 _BrushSettings;


            v2f vert(appdata v)
            {
                v2f o;

                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;

                return o;
            }

            half Mask(half2 position, half2 center, float size, float hardness)
            {
                float dist = distance(center, position);
                return 1 - smoothstep(size * hardness, size, dist);
            }

            float4 frag(v2f IN) : SV_Target
            {
                float2 brushPosition = _BrushSettings.xy;
                half brushSize = _BrushSettings.z;
                half brushHardness = _BrushSettings.w;
                half brushMask = Mask(IN.uv, brushPosition, brushSize, brushHardness);

                return half4(1, 1, 1, brushMask);
            }
            ENDCG
        }
    }
}