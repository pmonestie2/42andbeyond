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
                float bands=.5+.22*sin(r*130)+.1*sin(r*310);
                float gap=1-smoothstep(.015,.025,abs(r-1.72));
                float fade=smoothstep(1.25,1.29,r)*(1-smoothstep(2.08,2.15,r));
                return float4(lerp(float3(.28,.24,.19),float3(.73,.63,.46),bands),
                    fade*(.32+bands*.48)*(1-gap*.9));
            }
            ENDCG
        }
    }
}
