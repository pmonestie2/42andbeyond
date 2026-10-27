Shader "42 and Beyond/ShieldReflection"
{
    Properties { _Tint ("Energy color", Color) = (1,1,1,1) }
    SubShader {
        Tags { "Queue"="Transparent+4" "RenderType"="Transparent" }
        Pass {
            Blend SrcAlpha One
            ZWrite Off
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            fixed4 _Tint;
            struct Input { float4 vertex:POSITION; fixed4 color:COLOR; };
            struct Output { float4 vertex:SV_POSITION; fixed4 color:COLOR; };
            Output vert(Input input) {
                Output output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.color = input.color;
                return output;
            }
            fixed4 frag(Output input):SV_Target {
                return input.color * _Tint;
            }
            ENDCG
        }
    }
}
