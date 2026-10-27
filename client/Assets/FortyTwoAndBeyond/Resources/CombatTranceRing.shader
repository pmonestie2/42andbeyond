Shader "42 and Beyond/CombatTranceRing"
{
    Properties {
        _Tint ("Tier tint", Color) = (1, .49, .2, 1)
        _RingPlane ("Local ring plane", Range(0,1)) = 1
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
            fixed4 _Tint;
            float _RingPlane;
            struct Input { float4 vertex:POSITION; float3 normal:NORMAL; };
            struct Output { float4 vertex:SV_POSITION; float3 local:TEXCOORD0;
                float3 normal:TEXCOORD1; };
            Output vert(Input input) {
                Output output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.local = input.vertex.xyz;
                output.normal = input.normal;
                return output;
            }
            fixed4 frag(Output input):SV_Target {
                float angle = atan2(lerp(input.local.y, input.local.z,
                    _RingPlane), input.local.x);
                float pulse = .87 + .13 * sin(_Time.y * 2.6);
                float height = lerp(input.local.z, input.local.y, _RingPlane);
                float grain = .5 + .5 * sin(angle * 89 + height * 240);
                float stripe = smoothstep(.88, .985,
                    sin(angle * 25 + _Time.y * 3.3));
                float dot = smoothstep(.963, .995,
                    sin(angle * 13 - _Time.y * 1.8));
                float top = saturate(lerp(input.normal.z, input.normal.y,
                    _RingPlane) * 2 + .12);
                dot *= top;
                float3 copper = float3(.38, .105, .033) * (.78 + grain * .22);
                float3 color = copper * _Tint.rgb +
                    stripe * float3(1.1, .42, .12) +
                    dot * float3(.9, 1.05, 1.2);
                float alpha = saturate((.57 + stripe * .24 + dot * .25) * pulse * _Tint.a);
                return fixed4(color, alpha);
            }
            ENDCG
        }
    }
}
