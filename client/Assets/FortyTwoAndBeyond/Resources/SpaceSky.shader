Shader "42 and Beyond/SpaceSky"
{
    Properties {
        _NebulaTex ("Deep space panorama", 2D) = "black" {}
        _Exposure ("Sky exposure", Range(0,3)) = 1
    }
    SubShader {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _NebulaTex;
            float _Exposure;
            struct Input { float4 vertex:POSITION; };
            struct Output { float4 vertex:SV_POSITION; float3 direction:TEXCOORD0; };
            Output vert(Input v) { Output o; o.vertex=UnityObjectToClipPos(v.vertex); o.direction=v.vertex.xyz; return o; }
            fixed4 frag(Output i):SV_Target {
                float3 d=normalize(i.direction);
                float2 uv=float2(atan2(d.z,d.x)/6.2831853+.5,acos(clamp(d.y,-1,1))/3.1415926);
                float3 nebula=tex2D(_NebulaTex,uv).rgb;
                return float4(nebula*_Exposure,1);
            }
            ENDCG
        }
    }
}
