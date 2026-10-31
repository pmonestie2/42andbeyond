Shader "42 and Beyond/WarpLens"
{
    Properties { _MainTex ("Flight view", 2D) = "white" {} }
    SubShader {
        Cull Off ZWrite Off ZTest Always
        Pass {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            float4 _MainTex_TexelSize, _Focus;
            float _Strength, _Aspect;

            float sceneDepth(float2 uv) {
                #if UNITY_UV_STARTS_AT_TOP
                if (_MainTex_TexelSize.y < 0) uv.y = 1-uv.y;
                #endif
                return Linear01Depth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
            }

            fixed4 frag(v2f_img i):SV_Target {
                float2 uv=i.uv;
                float4 original=tex2D(_MainTex,uv);
                float2 delta=uv-_Focus.xy;
                float2 metric=delta*float2(_Aspect,1);
                float radius=length(metric);
                float edge=smoothstep(.08,.72,radius);
                // Deform the entire rendered world, including planets, gates and ships.
                float wave=sin(radius*17-_Time.y*5.5)*.0055+sin(radius*31-_Time.y*3.1)*.0015;
                float bend=_Strength*edge*(.08+wave);
                float2 lens=_Focus.xy+delta/(1+bend);
                lens+=float2(sin(metric.y*9-_Time.y*2.2),cos(metric.x*8-_Time.y*1.7))*
                    (_Strength*edge*.0018);
                lens=clamp(lens,.001,.999);
                float depth=sceneDepth(lens);
                float sky=smoothstep(.995,.9999,depth);
                float2 direction=delta/max(.001,length(delta));
                // Geometry bends too, with shorter trails than distant starlight.
                float reach=_Strength*edge*edge*lerp(.004,.025,sky);
                float3 sum=0;float weight=0;
                [unroll] for (int tap=0;tap<5;tap++) {
                    float t=tap*.25;
                    float2 sampleUv=clamp(lens-direction*reach*t,.001,.999);
                    float difference=abs(sceneDepth(sampleUv)-depth)/max(.00001,depth);
                    float accepted=1-smoothstep(.04,.15,difference);
                    float w=(1-t*.58)*accepted;
                    sum+=tex2D(_MainTex,sampleUv).rgb*w;weight+=w;
                }
                float3 color=weight>.001 ? sum/max(.001,weight) : tex2D(_MainTex,lens).rgb;
                float2 dispersion=direction*abs(_MainTex_TexelSize.xy)*_Strength*edge*edge*1.25;
                float2 redUv=clamp(lens+dispersion,.001,.999),blueUv=clamp(lens-dispersion,.001,.999);
                float redDifference=abs(sceneDepth(redUv)-depth)/max(.00001,depth);
                float blueDifference=abs(sceneDepth(blueUv)-depth)/max(.00001,depth);
                float safeColor=1-smoothstep(.04,.15,max(redDifference,blueDifference));
                float3 dispersed=float3(tex2D(_MainTex,redUv).r,color.g,tex2D(_MainTex,blueUv).b);
                color=lerp(color,dispersed,.22*safeColor);
                color*=1-_Strength*edge*.045;
                return float4(color,original.a);
            }
            ENDCG
        }
    }
    Fallback Off
}
