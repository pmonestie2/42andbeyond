Shader "42 and Beyond/SpaceBloom"
{
 Properties {_MainTex("Scene",2D)="black"{} _BloomTex("Bloom",2D)="black"{}}
 SubShader
 {
  Cull Off ZWrite Off ZTest Always
  CGINCLUDE
  #include "UnityCG.cginc"
  sampler2D _MainTex, _BloomTex;
  float4 _BlurDirection;
  float _Threshold, _Intensity;
  half4 bright(v2f_img i):SV_Target
  {
   float3 color=tex2D(_MainTex,i.uv).rgb;
   float peak=max(color.r,max(color.g,color.b));
   return half4(color*max(0,peak-_Threshold)/max(.0001,peak),1);
  }
  half4 blur(v2f_img i):SV_Target
  {
   float2 d=_BlurDirection.xy;
   return tex2D(_MainTex,i.uv)*.227027+
      (tex2D(_MainTex,i.uv+d*1.384615)+tex2D(_MainTex,i.uv-d*1.384615))*.316216+
      (tex2D(_MainTex,i.uv+d*3.230769)+tex2D(_MainTex,i.uv-d*3.230769))*.070270;
  }
  half4 composite(v2f_img i):SV_Target
  {
   float4 source=tex2D(_MainTex,i.uv);
   float3 color=source.rgb+tex2D(_BloomTex,i.uv).rgb*_Intensity;
   // Compress only HDR peaks, retaining the established flight and HUD palette.
   float peak=max(color.r,max(color.g,color.b));
   float mappedPeak = peak > .75 ? .75 + .25*(1-exp(-(peak-.75)*4)) : peak;
   color *= mappedPeak / max(.0001,peak);
   return half4(color,source.a);
  }
  ENDCG
  Pass {CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment bright
  ENDCG}
  Pass {CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment blur
  ENDCG}
  Pass {CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment composite
  ENDCG}
 }
}
