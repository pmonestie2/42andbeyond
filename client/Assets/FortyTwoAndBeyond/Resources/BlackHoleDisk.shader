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
                float orbit = atan2(center.y,center.x);
                float shear = _Time.y * (.13 + .27 * (1-smoothstep(.23,.46,radius)));
                float sine=sin(shear), cosine=cos(shear);
                float2 flow=float2(center.x*cosine-center.y*sine,center.x*sine+center.y*cosine)+.5;
                float opacity = tex2D(_OpacityTex,flow).r * disk;
                float3 source = tex2D(_MainTex,flow).rgb;
                float stream = .82 + .18 * sin(orbit * 8 - _Time.y * 1.4 + radius*32);
                float doppler = .62 + 1.05 * saturate(cos(orbit - .72));
                float glow = pow(1 - smoothstep(.22,.43,radius),3);
                float3 color = source * _Tint.rgb * stream * doppler + glow * _Tint.rgb * .36;
                // Differential rotation drives local hot knots and occasional magnetic eruptions.
                float knot=atan2(sin(orbit-_Time.y*.48),cos(orbit-_Time.y*.48));
                float hot=exp(-knot*knot*25-pow((radius-.315)*48,2));
                float knot2=atan2(sin(orbit+_Time.y*.23-2.1),cos(orbit+_Time.y*.23-2.1));
                hot+=exp(-knot2*knot2*38-pow((radius-.38)*55,2))*.6;
                float flare=pow(saturate(.5+.5*sin(_Time.y*.62)),10);
                color+=float3(1,.65,.25)*hot*(.8+flare*2.7)*_Opacity;
                opacity=saturate(opacity+hot*.2);
                return fixed4(color,opacity * _Opacity);
            }
            ENDCG
        }
    }
}
