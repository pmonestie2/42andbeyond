Shader "42 and Beyond/GateField"
{
    SubShader {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha One Cull Off ZWrite Off
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct Input { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
            struct Output { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; };
            Output vert(Input v) { Output o; o.vertex=UnityObjectToClipPos(v.vertex); o.uv=v.uv; return o; }
            fixed4 frag(Output i):SV_Target {
                float2 centered=(i.uv-.5)*2;
                float radius=length(centered);
                if (radius>1) discard;
                float angle=atan2(centered.y,centered.x);
                float ripple=.5+.5*sin(radius*34-angle*3-_Time.y*2.2);
                float current=pow(saturate(cos(angle*12+radius*22-_Time.y*1.3)),12);
                float rim=smoothstep(.71,.88,radius)*(1-smoothstep(.94,1,radius));
                float centerGlow=(1-smoothstep(0,.8,radius))*(.045+.035*ripple);
                float spiral=(.5+.5*sin(angle*5-radius*17+_Time.y*.9));
                float alpha=centerGlow+spiral*(1-smoothstep(.3,.86,radius))*.035+
                    rim*(.1+.1*ripple)+current*rim*.07;
                float3 color=float3(.04,.37,.61)+current*float3(.035,.1,.16);
                return float4(color,alpha);
            }
            ENDCG
        }
    }
}
