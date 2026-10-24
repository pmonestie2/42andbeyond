Shader "42 and Beyond/BeamAdditive"
{
    Properties {
        _MainTex ("Beam", 2D) = "white" {}
        _ZTest ("Depth test", Float) = 4
    }
    SubShader {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha One
        ZTest [_ZTest]
        ZWrite Off
        Cull Off
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            struct Input { float4 vertex : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; };
            struct Output { float4 position : SV_POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; };
            Output vert(Input value) {
                Output result;
                result.position = UnityObjectToClipPos(value.vertex);
                result.color = value.color;
                result.uv = value.uv;
                return result;
            }
            fixed4 frag(Output value) : SV_Target { return tex2D(_MainTex, value.uv) * value.color; }
            ENDCG
        }
    }
    Fallback Off
}
