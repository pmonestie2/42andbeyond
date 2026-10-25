Shader "42 and Beyond/BlackHolePhoton"
{
    Properties {
        _Tint ("Photon color", Color) = (.42,.82,1,1)
        _Intensity ("Photon intensity", Range(0,4)) = 1
    }
    SubShader {
        Tags { "Queue"="Transparent+1" "RenderType"="Transparent" }
        Pass {
            Blend SrcAlpha One
            ZWrite Off
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            fixed4 _Tint;
            float _Intensity;
            struct Input { float4 vertex:POSITION; };
            struct Output { float4 vertex:SV_POSITION; float2 radial:TEXCOORD0; };
            Output vert(Input i) {
                Output o;
                o.vertex=UnityObjectToClipPos(i.vertex);
                o.radial=i.vertex.xy;
                return o;
            }
            fixed4 frag(Output i):SV_Target {
                float angle=atan2(i.radial.y,i.radial.x);
                float bright=.55+.85*saturate(cos(angle-.65));
                float flicker=.96+.04*sin(_Time.y*1.8+angle*7);
                return fixed4(_Tint.rgb*_Intensity*bright*flicker,_Tint.a);
            }
            ENDCG
        }
    }
}
