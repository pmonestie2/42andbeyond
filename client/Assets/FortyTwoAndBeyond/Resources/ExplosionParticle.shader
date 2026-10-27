Shader "42 and Beyond/ExplosionParticle"
{
    Properties {
        _MainTex ("Smoke", 2D) = "white" {}
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
            struct Input { float4 vertex:POSITION; float2 uv:TEXCOORD0; fixed4 color:COLOR; };
            struct Output { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; fixed4 color:COLOR; };
            Output vert(Input input) {
                Output output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                output.color = input.color;
                return output;
            }
            fixed4 frag(Output input):SV_Target {
                return tex2D(_MainTex,input.uv)*input.color;
            }
            ENDCG
        }
    }
}
