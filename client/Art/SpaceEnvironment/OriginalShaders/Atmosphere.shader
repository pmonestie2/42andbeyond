Shader "SpaceEnvironmentDemo/Atmosphere"
{
 Properties {[HDR]_Tint("Scattering tint",Color)=(.08,.35,1,1) _SunPosition("Sun position",Vector)=(-38,5,30,1) _Strength("Scattering",Range(0,4))=1}
 SubShader {Tags {"RenderPipeline"="UniversalPipeline" "Queue"="Transparent+5" "RenderType"="Transparent"}
 Pass {Blend One One ZWrite Off Cull Back
 HLSLPROGRAM
 #pragma vertex vert
 #pragma fragment frag
 #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
 CBUFFER_START(UnityPerMaterial)
 float4 _Tint,_SunPosition;float _Strength;
 CBUFFER_END
 struct A{float4 p:POSITION;float3 n:NORMAL;};struct V{float4 p:SV_POSITION;float3 n:TEXCOORD0;float3 w:TEXCOORD1;};
 V vert(A i){V o;o.w=TransformObjectToWorld(i.p.xyz);o.p=TransformWorldToHClip(o.w);o.n=TransformObjectToWorldNormal(i.n);return o;}
 half4 frag(V i):SV_Target {float3 n=normalize(i.n),v=normalize(_WorldSpaceCameraPos-i.w);float rim=pow(1-saturate(dot(n,v)),4.5);float sun=saturate(dot(n,normalize(_SunPosition.xyz-i.w))+.18);return half4(_Tint.rgb*rim*sun*_Strength,0);}
 ENDHLSL
 }}
}
