Shader "SpaceEnvironmentDemo/Planet Surface"
{
 Properties {
  _TerrainMap("Terrain detail",2D)="black"{}
  _TerrainBlend("Terrain blend",Float)=0
  _TerrainSeed("Terrain seed",Float)=0
  _BodyFlow("Atmospheric flow", Float)=0
  _BaseMap("Planet albedo",2D)="white"{}
  _NightMap("City lights",2D)="black"{}
  _HasNight("City lights intensity",Float)=0
  _Ocean("Ocean specularity",Float)=0
  _SunPosition("Sun position",Vector)=(-38,5,30,1)
  _Tint("Surface tint",Color)=(1,1,1,1)
  _LightIntensity("Light intensity",Float)=1
  _LightColor("Light color",Color)=(1,.88,.72,1)
  _Ambient("Night-side fill",Range(0,0.2))=.015
 }
 SubShader {
 Tags { "RenderType"="Opaque"}
 Pass {
 Blend Off ZWrite On ZTest LEqual Cull Back
 CGPROGRAM
 #pragma vertex vert
 #pragma fragment frag
 #include "BuiltInSpace.cginc"
 #include "SpaceMotion.cginc"
 TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
 TEXTURE2D(_NightMap); SAMPLER(sampler_NightMap);
 
 float4 _Tint, _SunPosition, _LightColor;
 float _HasNight, _Ocean, _Ambient, _LightIntensity;
 
 struct A {float4 vertex:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD0;};
 struct V {float4 pos:SV_POSITION; float3 world:TEXCOORD0; float3 normal:TEXCOORD1; float2 uv:TEXCOORD2;};
 V vert(A i){V o;o.world=TransformObjectToWorld(i.vertex.xyz);o.pos=TransformWorldToHClip(o.world);o.normal=TransformObjectToWorldNormal(i.normal);o.uv=i.uv;return o;}
 half4 frag(V i):SV_Target {
 float3 n=normalize(i.normal), l=normalize(_SunPosition.xyz-i.world), v=normalize(_WorldSpaceCameraPos-i.world);
 i.uv=AnimatePlanetUV(i.uv);
 float nl=dot(n,l); float3 tex=SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,i.uv).rgb;
 tex=ComposePlanetSurface(i.uv,tex);
 float3 col=tex*_Tint.rgb*(_Ambient+1.65*max(0,nl)*_LightIntensity*_LightColor.rgb);
 float water=saturate((tex.b-max(tex.r,tex.g))*9);
 col+=pow(saturate(dot(n,normalize(l+v))),240)*water*_Ocean*.45*max(0,nl);
 col+=SAMPLE_TEXTURE2D(_NightMap,sampler_NightMap,i.uv).rgb*_HasNight*(1-smoothstep(-.15,.2,nl));
 return half4(col,1);
 }
 ENDCG
 }
 }
}
