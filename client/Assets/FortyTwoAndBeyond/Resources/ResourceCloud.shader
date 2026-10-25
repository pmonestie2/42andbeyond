Shader "42 and Beyond/ResourceCloud"
{
    Properties {
        _Tint ("Vapor color", Color) = (0.2,0.65,0.9,1)
        _Opacity ("Density", Range(0,1)) = 0.5
    }
    SubShader {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            fixed4 _Tint;
            half _Opacity;
            struct Input { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct Output { float4 position : SV_POSITION; float3 normal : TEXCOORD0;
                float3 view : TEXCOORD1; float3 local : TEXCOORD2; };
            Output vert(Input input) {
                Output output;
                float3 world = mul(unity_ObjectToWorld, input.vertex).xyz;
                output.position = UnityObjectToClipPos(input.vertex);
                output.normal = UnityObjectToWorldNormal(input.normal);
                output.view = _WorldSpaceCameraPos.xyz - world;
                output.local = input.vertex.xyz;
                return output;
            }
            fixed4 frag(Output input) : SV_Target {
                half rim = pow(1 - saturate(abs(dot(normalize(input.normal),
                    normalize(input.view)))), 1.7);
                half swirl = .5 + .5 * sin(input.local.x * 11 + input.local.y * 7 +
                    input.local.z * 9 + _Time.y * .42);
                half bands = .5 + .5 * sin(input.local.y * 16 - input.local.z * 6 -
                    _Time.y * .27);
                half density = saturate(rim * .68 + swirl * bands * .24 - .12);
                return fixed4(_Tint.rgb * (.45 + rim * .7), density * _Opacity);
            }
            ENDCG
        }
    }
    Fallback Off
}
