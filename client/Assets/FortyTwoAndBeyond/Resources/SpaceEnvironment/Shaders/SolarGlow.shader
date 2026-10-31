Shader "SpaceEnvironmentDemo/Solar Corona"
{
 Properties {[HDR]_Tint("Corona color",Color)=(1.4,.38,.06,1) _Strength("Intensity",Float)=1}
 SubShader {Tags { "Queue"="Transparent+10" "RenderType"="Transparent"}
 Pass {Blend One One ZWrite Off Cull Back
 CGPROGRAM
 #pragma vertex vert
 #pragma fragment frag
 #include "BuiltInSpace.cginc"
 
 float4 _Tint;float _Strength;
 
 struct A{float4 p:POSITION;float3 n:NORMAL;};struct V{float4 p:SV_POSITION;float3 n:TEXCOORD0;float3 w:TEXCOORD1;float3 local:TEXCOORD2;};
 V vert(A i){V o;o.w=TransformObjectToWorld(i.p.xyz);o.p=TransformWorldToHClip(o.w);o.n=TransformObjectToWorldNormal(i.n);o.local=normalize(i.p.xyz);return o;}
 half4 frag(V i):SV_Target {
 float facing=saturate(dot(normalize(i.n),normalize(_WorldSpaceCameraPos-i.w)));
 float t=_Time.y*.45;
 float3 p=i.local;
 float noise=.5+.16*sin(dot(p,float3(23,17,9))+t*2)+.14*sin(dot(p,float3(-11,31,19))-t)+.12*sin(dot(p,float3(41,-13,27))+sin(t));
 float plumes=pow(saturate(noise),3);
 float glow=exp(-max(0,.56-facing)*11)*smoothstep(0,.16,facing)*(.08+plumes*.65);
 return half4(_Tint.rgb*glow*_Strength,0);
 }
 ENDCG
 }}
}
