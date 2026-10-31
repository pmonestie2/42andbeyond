Shader "SpaceEnvironmentDemo/Cloud Layer"
{
 Properties {_BaseMap("Cloud coverage",2D)="white"{} _SunPosition("Sun position",Vector)=(-38,5,30,1) _Opacity("Opacity",Range(0,1))=.8}
 SubShader {Tags {"RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent"}
 Pass {Blend SrcAlpha OneMinusSrcAlpha ZWrite Off
 HLSLPROGRAM
 #pragma vertex vert
 #pragma fragment frag
 #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
 TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
 CBUFFER_START(UnityPerMaterial)
 float4 _SunPosition;float _Opacity;
 CBUFFER_END
 struct A{float4 p:POSITION;float3 n:NORMAL;float2 uv:TEXCOORD0;};
 struct V{float4 p:SV_POSITION;float3 n:TEXCOORD0;float3 w:TEXCOORD1;float2 uv:TEXCOORD2;};
 V vert(A i){V o;o.w=TransformObjectToWorld(i.p.xyz);o.p=TransformWorldToHClip(o.w);o.n=TransformObjectToWorldNormal(i.n);o.uv=i.uv;return o;}
 half4 frag(V i):SV_Target {float c=SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,i.uv).r;float lit=saturate(dot(normalize(i.n),normalize(_SunPosition.xyz-i.w)));return half4((.035+1.6*lit).xxx,smoothstep(.05,.9,c)*_Opacity);}
 ENDHLSL
 }}
}
