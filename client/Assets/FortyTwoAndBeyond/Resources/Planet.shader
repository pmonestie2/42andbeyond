Shader "42 and Beyond/Planet"
{
    Properties {
        _MainTex ("Saturn surface map", 2D) = "white" {}
        _UseTexture ("Use surface map", Float) = 0
    }
    SubShader {
        Tags { "RenderType"="Opaque" }
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _UseTexture;
            struct Input { float4 vertex:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD0; };
            struct Output { float4 vertex:SV_POSITION; float3 normal:TEXCOORD0; float3 local:TEXCOORD1; float3 view:TEXCOORD2; float2 uv:TEXCOORD3; };
            Output vert(Input v) { Output o; o.vertex=UnityObjectToClipPos(v.vertex); o.normal=UnityObjectToWorldNormal(v.normal); o.local=v.vertex.xyz; o.view=WorldSpaceViewDir(v.vertex); o.uv=v.uv; return o; }
            float hash(float3 p) { return frac(sin(dot(p,float3(127.1,311.7,74.7)))*43758.5453); }
            float noise(float3 p) {
                float3 a=floor(p),f=frac(p); f=f*f*(3-2*f);
                return lerp(lerp(lerp(hash(a),hash(a+float3(1,0,0)),f.x),lerp(hash(a+float3(0,1,0)),hash(a+float3(1,1,0)),f.x),f.y),
                    lerp(lerp(hash(a+float3(0,0,1)),hash(a+float3(1,0,1)),f.x),lerp(hash(a+float3(0,1,1)),hash(a+float3(1,1,1)),f.x),f.y),f.z);
            }
            fixed4 frag(Output i):SV_Target {
                float3 n=normalize(i.normal);
                float3 p=normalize(i.local);
                float3 color;
                if (_UseTexture > .5) {
                    float3 mapped=tex2D(_MainTex,i.uv).rgb;
                    color=saturate((mapped-.5)*1.28+.5)*float3(.89,.83,.76);
                } else {
                    float broad=noise(p*7), folds=noise(p*22), wisps=noise(p*58);
                    float latitude=p.y+.075*(broad-.5)+.022*(folds-.5);
                    float belts=.5+.5*sin(latitude*45+folds*3);
                    float filaments=.5+.5*sin(latitude*112+wisps*3);
                    float3 deep=float3(.21,.16,.15), ochre=float3(.51,.34,.22), cream=float3(.77,.67,.48);
                    color=lerp(deep,ochre,smoothstep(.15,.85,belts));
                    color=lerp(color,cream,smoothstep(.58,.94,belts)*(.35+.55*filaments));
                    color*=.88+.22*folds;
                    float storm=1-smoothstep(.14,.28,length(float2(p.x-.28,p.y-.12)));
                    color=lerp(color,cream*1.12,storm*(.35+.65*wisps));
                }
                float facing=saturate(dot(n,normalize(i.view)));
                float light=max(saturate(dot(n,normalize(float3(-.65,.34,-.8)))),facing*.32);
                float rim=pow(1-facing,3);
                return float4(color*(.12+light*.82)+rim*float3(.12,.2,.26)*(.4+light*.6),1);
            }
            ENDCG
        }
    }
}
