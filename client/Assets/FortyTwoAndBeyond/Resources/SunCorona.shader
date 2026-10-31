Shader "42 and Beyond/SunCorona"
{
    Properties {
        _Tint ("Star color", Color) = (1,.72,.36,1)
        _Strength ("Glow strength", Range(0,4)) = 1
        _HazeOnly ("Diffuse halo only", Float) = 0
    }
    SubShader {
        Tags { "Queue"="Transparent+2" "RenderType"="Transparent" }
        Pass {
            Blend One OneMinusSrcAlpha
            ZWrite Off Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Tint;
            float _Strength, _HazeOnly;
            struct Input { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
            struct Output { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; };
            Output vert(Input input) {
                Output output; output.vertex=UnityObjectToClipPos(input.vertex); output.uv=input.uv; return output;
            }
            float hash(float2 p) { return frac(sin(dot(p,float2(127.1,311.7)))*43758.5453); }
            float noise(float2 p) {
                float2 i=floor(p), f=frac(p); f=f*f*(3-2*f);
                return lerp(lerp(hash(i),hash(i+float2(1,0)),f.x),lerp(hash(i+float2(0,1)),hash(i+1),f.x),f.y);
            }
            float4 frag(Output input):SV_Target {
                float2 p=(input.uv-.5)*2;
                float r=length(p), a=atan2(p.y,p.x), t=_Time.y;
                if (_HazeOnly > .5) {
                    float haze=pow(saturate(1-r),4)*.4*_Strength;
                    return float4(_Tint.rgb*haze,haze);
                }
                float disk=1-smoothstep(.33,.345,r);
                float z=sqrt(saturate(1-pow(r/.34,2)));
                float2 sphere=float2(atan2(p.x/.34,z)+t*.055,p.y/.34);
                float granules=noise(sphere*32+float2(0,t*.08));
                float flow=noise(sphere*9+float2(t*.06,-t*.04));
                float spots=pow(saturate((noise(sphere*11)-.66)*4),2)*.16;
                float limb=.48+.52*z;
                float heat=(.76+.14*granules+.08*flow-spots)*limb;
                float3 surface=lerp(float3(.93,.24,.025),float3(1,.77,.3),saturate(heat))*_Tint.rgb*(.65+.35*heat);
                float edge=.35+.024*sin(a*11+t*.43)+.016*sin(a*19-t*.28);
                float corona=exp(-max(0,r-edge)*11)*smoothstep(.30,.36,r)*(1-smoothstep(.74,1,r));
                float rays=.45+.55*pow(saturate(.5+.5*sin(a*17+t*.19+noise(p*5)*3)),3);
                float pulse=.5+.5*sin(t*.38+a*2);
                float localAngle=atan2(sin(a-.8-t*.025),cos(a-.8-t*.025));
                float localAngle2=atan2(sin(a+2.2+t*.018),cos(a+2.2+t*.018));
                float lobe=exp(-localAngle*localAngle*8), lobe2=exp(-localAngle2*localAngle2*11);
                float archRadius=.35+.14*lobe*(.8+.2*sin(t*.31));
                float archRadius2=.35+.09*lobe2*(.8+.2*sin(t*.27+1));
                float arch=exp(-pow((r-archRadius)*68,2))*lobe+
                    exp(-pow((r-archRadius2)*75,2))*lobe2*.65;
                float knots=pow(saturate(.5+.5*sin(a*7+t*.31)),12);
                float plume=exp(-pow((r-(.39+.14*pulse))*19,2))*knots;
                float3 plasma=_Tint.rgb*float3(1,.6,.18);
                float3 color=surface*disk+plasma*(corona*rays*.4+arch*.7+plume*.35);
                float alpha=saturate(disk+corona*.8+arch+plume*.35)*_Strength*_Tint.a;
                return float4(color*alpha,alpha);
            }
            ENDCG
        }
    }
}
