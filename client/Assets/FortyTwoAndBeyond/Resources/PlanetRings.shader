Shader "42 and Beyond/PlanetRings"
{
    SubShader {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha Cull Off ZWrite Off
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct Input { float4 vertex:POSITION; };
            struct Output { float4 vertex:SV_POSITION; float3 local:TEXCOORD0; };
            Output vert(Input v) { Output o; o.vertex=UnityObjectToClipPos(v.vertex); o.local=v.vertex.xyz; return o; }
            fixed4 frag(Output i):SV_Target {
                float r=length(i.local.xy);
                float angle=atan2(i.local.y,i.local.x);
                float bands=saturate(.43+.2*sin(r*67)+.12*sin(r*183+angle*2)+
                    .055*sin(r*497-angle*7));
                float cassini=1-smoothstep(.012,.035,abs(r-1.72));
                float innerGap=1-smoothstep(.006,.019,abs(r-1.46));
                float fade=smoothstep(1.25,1.29,r)*(1-smoothstep(2.08,2.15,r));
                float density=(.1+.27*bands)*(1-cassini*.88)*(1-innerGap*.6);
                float3 ice=lerp(float3(.24,.22,.2),float3(.7,.62,.52),bands);
                return float4(ice,fade*density);
            }
            ENDCG
        }
    }
}
