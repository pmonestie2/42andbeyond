Shader "42 and Beyond/Imported Stellar Corona"
{
 Properties {[HDR]_Tint("Star tint",Color)=(1,1,1,1) _FlowSpeed("Plasma speed",Float)=.16 _CoreRadius("Core radius in shell",Float)=.4375 _Emission("Radiance",Float)=1.5}
 SubShader {
 Tags {"Queue"="Transparent+10" "RenderType"="Transparent"}
 Pass {
 Blend One One ZWrite Off ZTest LEqual Cull Front
 CGPROGRAM
 #pragma target 3.5
 #pragma vertex vert
 #pragma fragment frag
 #include "UnityCG.cginc"
 #include "StellarSun.cginc"
 float4 _Tint;float _FlowSpeed,_CoreRadius,_Emission;
 struct A {float4 vertex:POSITION;};struct V{float4 pos:SV_POSITION;float3 world:TEXCOORD0;};
 V vert(A i){V o;o.world=mul(unity_ObjectToWorld,i.vertex).xyz;o.pos=UnityObjectToClipPos(i.vertex);return o;}
 float4 frag(V i):SV_Target {
 float3 ray=normalize(mul((float3x3)unity_WorldToObject,i.world-_WorldSpaceCameraPos));
 float3 center=-mul(unity_WorldToObject,float4(_WorldSpaceCameraPos,1)).xyz/_CoreRadius;
 float3x3 rotation=float3x3(1,0,0,0,1,0,0,0,1);
 float b=dot(ray,center),radius=sqrt(max(0,dot(center,center)-b*b));
 float activity=ringRayNoise(ray,center,.96,1,rotation,_Time.y*_FlowSpeed);
 float3 color=lerp(float3(1,.48,.05),float3(1,.94,.72),saturate(pow(activity,3)))*activity;
 float outside=smoothstep(.96,1.035,radius);
 return float4(color*_Tint.rgb*_Emission*outside,0);
 }
 ENDCG
 }}
}
