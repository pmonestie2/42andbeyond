Shader "42 and Beyond/LegacyEffect"
{
    Properties {
        [MainTexture] _MainTex ("Original texture", 2D) = "black" {}
        _DetailTex ("Original second pass", 2D) = "black" {}
        _Tint ("Tint", Color) = (1,1,1,1)
        _UseSourceColor ("Use original RGB", Range(0,1)) = 0
    }
    SubShader {
        Tags { "Queue"="Transparent+2" "RenderType"="Transparent" }
        Pass {
            Blend SrcAlpha One
            ZWrite Off
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_ST;
            sampler2D _DetailTex;
            fixed4 _Tint;
            fixed _UseSourceColor;
            struct Input { float4 vertex:POSITION; float2 uv:TEXCOORD0; fixed4 color:COLOR; };
            struct Output { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; fixed4 color:COLOR; };
            Output vert(Input input) {
                Output output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.color = input.color;
                return output;
            }
            fixed4 frag(Output input):SV_Target {
                fixed4 baseColor = tex2D(_MainTex, input.uv);
                fixed mask = saturate(baseColor.a * 1.4);
                fixed3 color = lerp(_Tint.rgb,
                    saturate(baseColor.rgb * _Tint.rgb * 2), _UseSourceColor) * input.color.rgb;
                fixed alpha = mask * _Tint.a * input.color.a;
                return fixed4(color, alpha);
            }
            ENDCG
        }
    }
}
