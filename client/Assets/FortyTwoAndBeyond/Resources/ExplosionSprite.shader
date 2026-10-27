Shader "42 and Beyond/ExplosionSprite"
{
    Properties {
        _MainTex ("Explosion sprite", 2D) = "white" {}
        _Tint ("Color and opacity", Color) = (1,1,1,1)
    }
    SubShader {
        Tags { "Queue"="Transparent+3" "RenderType"="Transparent" }
        Pass {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            fixed4 _Tint;
            struct Input { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
            struct Output { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; };
            Output vert(Input input) {
                Output output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }
            fixed4 frag(Output input):SV_Target {
                return tex2D(_MainTex,input.uv)*_Tint;
            }
            ENDCG
        }
    }
}
