Shader "42 and Beyond/SunCorona"
{
    Properties {
        _Tint ("Star color", Color) = (1,.72,.36,1)
        _Strength ("Glow strength", Range(0,4)) = 1
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
            float _Strength;
            struct Input { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
            struct Output { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; };
            Output vert(Input input) {
                Output output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }
            fixed4 frag(Output input):SV_Target {
                float2 radial = (input.uv-.5)*2;
                float radius = length(radial);
                float angle = atan2(radial.y,radial.x);
                float rays = sin(angle*13+_Time.y*.11)*sin(angle*23-_Time.y*.08);
                float edge = .34 + .055*rays;
                float core = 1-smoothstep(.15,.34,radius);
                float corona = (1-smoothstep(edge,.83,radius)) * smoothstep(.1,.31,radius);
                float haze = pow(saturate(1-radius),3)*.22;
                float alpha = saturate(core*.9+corona*.42+haze)*_Strength*_Tint.a;
                float3 color = lerp(_Tint.rgb,float3(1,.96,.79),core*.85);
                return fixed4(color,alpha);
            }
            ENDCG
        }
    }
}
