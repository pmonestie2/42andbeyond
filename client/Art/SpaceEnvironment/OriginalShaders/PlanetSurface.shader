Shader "SpaceEnvironmentDemo/Planet Surface"
{
 Properties {
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
 Tags {"RenderPipeline"="UniversalPipeline" "RenderType"="Opaque"}
 Pass {
 HLSLPROGRAM
 #pragma vertex vert
 #pragma fragment frag
 #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
 TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
 TEXTURE2D(_NightMap); SAMPLER(sampler_NightMap);
 CBUFFER_START(UnityPerMaterial)
 float4 _Tint, _SunPosition, _LightColor;
 float _HasNight, _Ocean, _Ambient, _LightIntensity;
 CBUFFER_END
 struct A {float4 vertex:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD0;};
 struct V {float4 pos:SV_POSITION; float3 world:TEXCOORD0; float3 normal:TEXCOORD1; float2 uv:TEXCOORD2;};
 V vert(A i){V o;o.world=TransformObjectToWorld(i.vertex.xyz);o.pos=TransformWorldToHClip(o.world);o.normal=TransformObjectToWorldNormal(i.normal);o.uv=i.uv;return o;}
 half4 frag(V i):SV_Target {
 float3 n=normalize(i.normal), l=normalize(_SunPosition.xyz-i.world), v=normalize(_WorldSpaceCameraPos-i.world);
 float nl=dot(n,l); float3 tex=SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,i.uv).rgb;
 float3 col=tex*_Tint.rgb*(_Ambient+1.65*max(0,nl)*_LightIntensity*_LightColor.rgb);
 float water=saturate((tex.b-max(tex.r,tex.g))*9);
 col+=pow(saturate(dot(n,normalize(l+v))),240)*water*_Ocean*.45*max(0,nl);
 col+=SAMPLE_TEXTURE2D(_NightMap,sampler_NightMap,i.uv).rgb*_HasNight*(1-smoothstep(-.15,.2,nl));
 return half4(col,1);
 }
 ENDHLSL
 }
 }
}
