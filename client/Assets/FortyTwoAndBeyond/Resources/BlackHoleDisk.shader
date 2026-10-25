Shader "42 and Beyond/BlackHoleDisk"
{
    Properties {
        _MainTex ("Accretion disk", 2D) = "black" {}
        _OpacityTex ("Disk opacity", 2D) = "white" {}
        _Tint ("Disk color", Color) = (1.5,1,.7,1)
        _Opacity ("Disk brightness", Range(0,2)) = 1
    }
    SubShader {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Pass {
            Blend SrcAlpha One
            ZWrite Off
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex, _OpacityTex;
            fixed4 _Tint;
            float _Opacity;
            struct Input { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
            struct Output { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; };
            Output vert(Input i) {
                Output o;
                o.vertex = UnityObjectToClipPos(i.vertex);
                o.uv = i.uv;
                return o;
            }
            fixed4 frag(Output i):SV_Target {
                float2 center = i.uv - .5;
                float radius = length(center);
                float disk = smoothstep(.19,.28,radius) * (1 - smoothstep(.43,.5,radius));
                float opacity = tex2D(_OpacityTex,i.uv).r * disk;
                float3 source = tex2D(_MainTex,i.uv).rgb;
                float orbit = atan2(center.y,center.x);
                float stream = .92 + .08 * sin(orbit * 8 - _Time.y * .35);
                float doppler = .62 + 1.05 * saturate(cos(orbit - .72));
                float glow = pow(1 - smoothstep(.22,.43,radius),3);
                float3 color = source * _Tint.rgb * stream * doppler +
                    glow * _Tint.rgb * .36;
                return fixed4(color,opacity * _Opacity);
            }
            ENDCG
        }
    }
}
