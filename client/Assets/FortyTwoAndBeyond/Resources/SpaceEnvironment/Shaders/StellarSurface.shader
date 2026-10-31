Shader "42 and Beyond/Imported Stellar Surface"
{
 Properties {[HDR]_Tint("Star tint",Color)=(1,1,1,1) _FlowSpeed("Convection speed",Float)=.16 _Emission("Radiance",Float)=2.4}
 SubShader {
 Tags {"Queue"="Geometry" "RenderType"="Opaque"}
 Pass {
 Blend Off ZWrite On ZTest LEqual Cull Back
 CGPROGRAM
 #pragma target 3.5
 #pragma vertex vert
 #pragma fragment frag
 #include "UnityCG.cginc"
 #include "StellarSun.cginc"
 float4 _Tint;float _FlowSpeed,_Emission;
 struct A {float4 vertex:POSITION;float3 normal:NORMAL;};
 struct V {float4 pos:SV_POSITION;float3 world:TEXCOORD0;float3 normal:TEXCOORD1;};
 V vert(A i){V o;o.world=mul(unity_ObjectToWorld,i.vertex).xyz;o.pos=UnityObjectToClipPos(i.vertex);o.normal=UnityObjectToWorldNormal(i.normal);return o;}
 float4 frag(V i):SV_Target {
 float3 ray=normalize(mul((float3x3)unity_WorldToObject,i.world-_WorldSpaceCameraPos));
 float3 center=-mul(unity_WorldToObject,float4(_WorldSpaceCameraPos,1)).xyz;
 float3x3 rotation=float3x3(1,0,0,0,1,0,0,0,1);
 float time=_Time.y*_FlowSpeed;
 float s1=noiseSpere(ray,center,1,rotation,.5,float3(0,0,0),time);
 s1=pow(saturate(s1*2.4),2);
 float s2=saturate(noiseSpere(ray,center,1,rotation,4,float3(83.23,34.34,67.453),time)*2.2);
 float3 color=lerp(float3(1,1,.08),float3(1,1,1),pow(s1,60))*s1;
 color+=lerp(lerp(float3(1,.07,0),float3(1,.35,.03),s2*s2),float3(1,1,1),pow(s2,10))*s2;
 float activity=ringRayNoise(ray,center,.96,1,rotation,time);
 color+=lerp(float3(1,.48,.05),float3(1,.94,.72),saturate(pow(activity,3)))*activity;
 float limb=.55+.45*pow(saturate(dot(normalize(i.normal),normalize(_WorldSpaceCameraPos-i.world))),.35);
 return float4(color*_Tint.rgb*_Emission*limb,1);
 }
 ENDCG
 }}
}
