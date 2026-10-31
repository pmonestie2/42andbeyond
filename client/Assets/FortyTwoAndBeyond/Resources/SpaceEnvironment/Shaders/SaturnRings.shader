Shader "SpaceEnvironmentDemo/Saturn Rings"
{
 Properties {_BaseMap("Solar System Scope rings RGBA",2D)="white"{} _SunPosition("Sun position",Vector)=(-38,5,30,1) _PlanetCenter("Planet center",Vector)=(40,2,0,1) _PlanetRadius("Planet radius",Float)=6}
 SubShader {Tags { "Queue"="Transparent" "RenderType"="Transparent"}
 Pass {Blend SrcAlpha OneMinusSrcAlpha ZWrite Off Cull Off
 CGPROGRAM
 #pragma vertex vert
 #pragma fragment frag
 #include "BuiltInSpace.cginc"
 TEXTURE2D(_BaseMap);SAMPLER(sampler_BaseMap);
 
 float4 _SunPosition,_PlanetCenter;float _PlanetRadius;
 
 struct A{float4 p:POSITION;float2 uv:TEXCOORD0;};struct V{float4 p:SV_POSITION;float2 uv:TEXCOORD0;float3 w:TEXCOORD1;};
 V vert(A i){V o;o.w=TransformObjectToWorld(i.p.xyz);o.p=TransformWorldToHClip(o.w);o.uv=i.uv;return o;}
 half4 frag(V i):SV_Target {float3 towardCamera=normalize(_WorldSpaceCameraPos-i.w);
 float3 planetOffset=_PlanetCenter.xyz-i.w;
 float front=dot(planetOffset,towardCamera);
 if(front>0 && dot(planetOffset,planetOffset)-front*front<_PlanetRadius*_PlanetRadius)discard;
 float4 c=SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,i.uv);float3 toSun=normalize(_SunPosition.xyz-i.w), delta=_PlanetCenter.xyz-i.w;float d=dot(delta,toSun);float miss=length(delta-toSun*max(0,d));float shadow=d>0?smoothstep(_PlanetRadius*.97,_PlanetRadius*1.04,miss):1;return half4(c.rgb*(.08+shadow*1.4),c.a*.86);}
 ENDCG
 }}
}
