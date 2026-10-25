Shader "42 and Beyond/SpaceSky"
{
    Properties { _NebulaTex ("Deep space panorama", 2D) = "black" {} }
    SubShader {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _NebulaTex;
            struct Input { float4 vertex:POSITION; };
            struct Output { float4 vertex:SV_POSITION; float3 direction:TEXCOORD0; };
            Output vert(Input v) { Output o; o.vertex=UnityObjectToClipPos(v.vertex); o.direction=v.vertex.xyz; return o; }
            float hash(float3 p) { return frac(sin(dot(p,float3(127.1,311.7,74.7)))*43758.5453); }
            float noise(float3 p) {
                float3 a=floor(p),f=frac(p); f=f*f*(3-2*f);
                return lerp(lerp(lerp(hash(a),hash(a+float3(1,0,0)),f.x),lerp(hash(a+float3(0,1,0)),hash(a+float3(1,1,0)),f.x),f.y),lerp(lerp(hash(a+float3(0,0,1)),hash(a+float3(1,0,1)),f.x),lerp(hash(a+float3(0,1,1)),hash(a+float3(1,1,1)),f.x),f.y),f.z);
            }
            fixed4 frag(Output i):SV_Target {
                float3 d=normalize(i.direction);
                float band=exp(-abs(dot(d,normalize(float3(.25,1,.45))))*5);
                float cloud=noise(d*4)*.55+noise(d*11)*.3+noise(d*27)*.15;
                float2 uv=float2(atan2(d.z,d.x)/6.2831853+.5,acos(clamp(d.y,-1,1))/3.1415926);
                float3 nebula=tex2D(_NebulaTex,uv).rgb;
                float seam=1-smoothstep(0,.05,min(uv.x,1-uv.x));
                float3 opposite=tex2D(_NebulaTex,float2(1-uv.x,uv.y)).rgb;
                nebula=lerp(nebula,(nebula+opposite)*.5,seam);
                float3 col=nebula*.16+float3(.002,.005,.011)+band*cloud*cloud*float3(.012,.02,.035);
                float2 grid=uv*float2(1600,800),cell=floor(grid),local=frac(grid)-.5;
                float h=hash(float3(cell,1));
                float star=step(.997,h)*(1-smoothstep(.03,.21,length(local)));
                col+=star*lerp(float3(.4,.65,1),float3(1,.86,.68),hash(float3(cell,2)))*.7;
                return float4(col,1);
            }
            ENDCG
        }
    }
}
