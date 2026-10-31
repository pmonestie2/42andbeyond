Shader "SpaceEnvironmentDemo/Solar Surface"
{
 Properties {_BaseMap("Solar System Scope photosphere",2D)="white"{} [HDR]_Tint("Emission",Color)=(2.8,1.6,.65,1) _FlowSpeed("Convection speed",Range(0,1))=.06}
 SubShader {Tags {"RenderPipeline"="UniversalPipeline" "RenderType"="Opaque"}
 Pass {
 HLSLPROGRAM
 #pragma vertex vert
 #pragma fragment frag
 #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
 TEXTURE2D(_BaseMap);SAMPLER(sampler_BaseMap);
 CBUFFER_START(UnityPerMaterial)
 float4 _Tint;float _FlowSpeed;
 CBUFFER_END
 struct A{float4 p:POSITION;float3 n:NORMAL;float2 uv:TEXCOORD0;};struct V{float4 p:SV_POSITION;float3 n:TEXCOORD0;float3 w:TEXCOORD1;float2 uv:TEXCOORD2;};
 V vert(A i){V o;o.w=TransformObjectToWorld(i.p.xyz);o.p=TransformWorldToHClip(o.w);o.n=TransformObjectToWorldNormal(i.n);o.uv=i.uv;return o;}
 half4 frag(V i):SV_Target {
 float t=_Time.y*_FlowSpeed;float2 uv=i.uv;
 uv.x+=.003*sin(uv.y*60+t*2);uv.y+=.002*sin(uv.x*90-t);
 float3 tex=SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,uv).rgb;
 float cells=sin(uv.x*890+sin(uv.y*630+t)*2)*sin(uv.y*620-t*1.5);
 float limb=.45+.55*pow(saturate(dot(normalize(i.n),normalize(_WorldSpaceCameraPos-i.w))),.35);
 return half4(tex*_Tint.rgb*(1+.16*cells)*limb,1);
 }
 ENDHLSL
 }}
}
