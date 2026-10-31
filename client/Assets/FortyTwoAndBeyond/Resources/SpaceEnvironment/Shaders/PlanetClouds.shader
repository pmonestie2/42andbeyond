Shader "SpaceEnvironmentDemo/Cloud Layer"
{
 Properties {_BaseMap("Cloud coverage",2D)="white"{} _SunPosition("Sun position",Vector)=(-38,5,30,1) _Opacity("Opacity",Range(0,1))=.8}
 SubShader {Tags { "Queue"="Transparent" "RenderType"="Transparent"}
 Pass {Blend SrcAlpha OneMinusSrcAlpha ZWrite Off
 CGPROGRAM
 #pragma vertex vert
 #pragma fragment frag
 #include "BuiltInSpace.cginc"
 #include "SpaceMotion.cginc"
 TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
 
 float4 _SunPosition;float _Opacity;
 
 struct A{float4 p:POSITION;float3 n:NORMAL;float2 uv:TEXCOORD0;};
 struct V{float4 p:SV_POSITION;float3 n:TEXCOORD0;float3 w:TEXCOORD1;float2 uv:TEXCOORD2;};
 V vert(A i){V o;o.w=TransformObjectToWorld(i.p.xyz);o.p=TransformWorldToHClip(o.w);o.n=TransformObjectToWorldNormal(i.n);o.uv=i.uv;return o;}
 half4 frag(V i):SV_Target {i.uv=AnimateCloudUV(i.uv);float c=SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,i.uv).r;float lit=saturate(dot(normalize(i.n),normalize(_SunPosition.xyz-i.w)));return half4((.15+1.6*lit).xxx,smoothstep(.05,.9,c)*_Opacity);}
 ENDCG
 }}
}
