Shader "42 and Beyond/WormholePlasma"
{
    Properties {
        _Mode ("Rim or throat", Float) = 0
        _Open ("Aperture", Range(0,1)) = 1
    }
    SubShader {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off Cull Off
        Pass {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float _Mode, _Open;
            struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
            v2f vert(appdata_base v) {
                v2f o;float a=v.texcoord.x*6.2831853;
                float breathing=sin(a*7-_Time.y*1.6)*.018+sin(a*11+_Time.y*.9)*.012;
                v.vertex.xy+=normalize(v.vertex.xy+float2(.0001,0))*breathing*(1-_Mode);
                o.pos=UnityObjectToClipPos(v.vertex);o.uv=v.texcoord.xy;return o;
            }
            float hash(float2 p) {
                float3 h=frac(float3(p.xyx)*.1031);h+=dot(h,h.yzx+33.33);return frac((h.x+h.y)*h.z);
            }
            float noise(float2 p) {
                float2 i=floor(p),f=frac(p);f=f*f*(3-2*f);
                return lerp(lerp(hash(i),hash(i+float2(1,0)),f.x),lerp(hash(i+float2(0,1)),hash(i+1),f.x),f.y);
            }
            float flow(float2 p) {
                return noise(p)*.57+noise(p*2.03+11.7)*.29+noise(p*4.1+23.9)*.14;
            }
            float filament(float phase,float sharpness) {
                float resolved=1-smoothstep(.5,2.2,fwidth(phase));
                return lerp(.12,pow(saturate(.5+.5*sin(phase)),sharpness),resolved);
            }
            float4 frag(v2f i):SV_Target {
                float t=_Time.y,a=i.uv.x*6.2831853,d=i.uv.y;
                float2 orbit=float2(cos(a),sin(a));
                float n=flow(orbit*(3+d*2)+float2(t*.28,-t*.17));
                float turbulence=flow(orbit*9+float2(d*5-t*.23,t*.31));
                if (_Mode < .5) {
                    float strands=filament(d*24+n*8-a*3-t*2.5,5);
                    float heat=saturate(n*.55+strands*.5+turbulence*.2);
                    float3 color=lerp(float3(.008,.035,.10),float3(.18,.9,1.5),heat);
                    color+=float3(.68,.86,1.0)*pow(heat,5)*2.2;
                    float band=.55+.45*pow(saturate(.5+.5*cos(d*6.2831853)),2);
                    return float4(color*(.8+strands*1.4),(.25+heat*.55)*band*_Open);
                }
                float spiral=filament(a*4+d*24-t*3.4+n*3.5,5);
                float secondary=filament(a*3-d*17+t*2.1+turbulence*3,7);
                float fade=pow(saturate(1-d),1.2);
                float alpha=fade*(.12+spiral*.58+secondary*.18+turbulence*.1)*_Open;
                float3 color=float3(.012,.025,.08)+float3(.08,.46,.78)*spiral+float3(.20,.08,.38)*secondary;
                return float4(color*(.8+turbulence*.5),alpha);
            }
            ENDCG
        }
    }
}
