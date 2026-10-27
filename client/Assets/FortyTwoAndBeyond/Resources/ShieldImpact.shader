Shader "42 and Beyond/ShieldImpact"
{
    Properties {
        _Tint ("Shield color", Color) = (0.28,0.75,1,1)
        _FlowTex ("Flowing energy lines", 2D) = "black" {}
        _Age ("Impact age", Range(0,1)) = 0
        _HitDirection ("Hit direction", Vector) = (0,0,1,0)
    }
    SubShader {
        Tags { "Queue"="Transparent+3" "RenderType"="Transparent" }
        Pass {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            fixed4 _Tint;
            sampler2D _FlowTex;
            float _Age;
            float4 _HitDirection;
            struct Input { float4 vertex:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD0; };
            struct Output { float4 vertex:SV_POSITION; float3 normal:TEXCOORD0;
                float3 view:TEXCOORD1; float2 uv:TEXCOORD2; };
            Output vert(Input input) {
                Output output;
                float3 radial = normalize(input.vertex.xyz);
                float angle = acos(clamp(dot(radial,
                    normalize(_HitDirection.xyz)), -1, 1));
                float rise = smoothstep(0, 0.12, _Age);
                float fall = 1 - smoothstep(0.22, 0.62, _Age);
                float rebound = 0.12 * exp(-pow((_Age - 0.7) * 14, 2));
                float compression = rise * fall - rebound;
                float dent = 0.17 * exp(-0.5 * pow(angle / 0.39, 2));
                float lip = 0.03 * exp(-0.5 * pow((angle - 0.5) / 0.16, 2));
                float waveFront = 0.35 + 2.5 * saturate((_Age - 0.08) / 0.62);
                float waveGap = angle - waveFront;
                float waveFade = smoothstep(0.03, 0.16, _Age) *
                    (1 - smoothstep(0.72, 0.92, _Age));
                float membraneWave = 0.048 * cos(waveGap * 6) *
                    exp(-0.5 * pow(waveGap / 0.58, 2)) * waveFade;
                input.vertex.xyz += radial * ((lip - dent) * compression + membraneWave);
                float3 world = mul(unity_ObjectToWorld, input.vertex).xyz;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.normal = UnityObjectToWorldNormal(input.normal);
                output.view = normalize(_WorldSpaceCameraPos.xyz - world);
                output.uv = input.uv;
                return output;
            }
            fixed4 frag(Output input):SV_Target {
                float3 normal = normalize(input.normal);
                float hit = saturate(dot(normal, normalize(_HitDirection.xyz)));
                float fresnel = pow(1 - saturate(dot(normal, normalize(input.view))), 3);
                float wave = exp(-pow((hit - (0.94 - _Age * 1.1)) * 13, 2));
                float core = pow(hit, 18) * exp(-_Age * 7);
                float visible = 1 - smoothstep(0.55, 1, _Age);
                float2 flowUv = input.uv + float2(_Age * 0.17, _Age * 0.06);
                float flow = tex2D(_FlowTex, flowUv).a;
                float alpha = visible * (0.14 + 0.08 * fresnel +
                    0.28 * wave + 0.22 * core + 0.7 * flow);
                float3 color = lerp(_Tint.rgb, float3(0.82, 0.97, 1),
                    saturate(core * 0.75 + wave * 0.35));
                color = lerp(color, float3(0.62, 0.92, 1), flow);
                return fixed4(color, saturate(alpha) * _Tint.a);
            }
            ENDCG
        }
    }
}
