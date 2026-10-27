Shader "42 and Beyond/ShieldCharging"
{
    Properties {
        _Tint ("Charging field color", Color) = (0.28,0.78,1,1)
        _FlowTex ("Flowing energy lines", 2D) = "black" {}
        _HitDirection ("Contact direction", Vector) = (0,0,1,0)
        _HitAge ("Contact age", Range(0,1)) = 1
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
            float4 _HitDirection;
            float _HitAge;
            struct Input { float4 vertex:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD0; };
            struct Output { float4 vertex:SV_POSITION; float3 normal:TEXCOORD0;
                float3 view:TEXCOORD1; float2 uv:TEXCOORD2; float3 radial:TEXCOORD3; };
            Output vert(Input input) {
                Output output;
                float3 radial = normalize(input.vertex.xyz);
                float angle = acos(clamp(dot(radial, normalize(_HitDirection.xyz)), -1, 1));
                float rise = smoothstep(0, 0.12, _HitAge);
                float fall = 1 - smoothstep(0.22, 0.62, _HitAge);
                float rebound = 0.12 * exp(-pow((_HitAge - 0.7) * 14, 2));
                float waveFront = 0.35 + 2.5 * saturate((_HitAge - 0.08) / 0.62);
                float waveGap = angle - waveFront;
                float waveFade = smoothstep(0.03, 0.16, _HitAge) *
                    (1 - smoothstep(0.72, 0.92, _HitAge));
                float dent = 0.17 * exp(-0.5 * pow(angle / 0.39, 2));
                float lip = 0.03 * exp(-0.5 * pow((angle - 0.5) / 0.16, 2));
                float wave = 0.048 * cos(waveGap * 6) *
                    exp(-0.5 * pow(waveGap / 0.58, 2)) * waveFade;
                input.vertex.xyz += radial * ((lip - dent) * (rise * fall - rebound) + wave);
                float3 world = mul(unity_ObjectToWorld, input.vertex).xyz;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.normal = UnityObjectToWorldNormal(input.normal);
                output.view = normalize(_WorldSpaceCameraPos.xyz - world);
                output.uv = input.uv;
                output.radial = radial;
                return output;
            }
            fixed4 frag(Output input):SV_Target {
                float3 normal = normalize(input.normal);
                float fresnel = pow(1 - saturate(dot(normal, normalize(input.view))), 2.1);
                float hit = saturate(dot(normalize(input.radial), normalize(_HitDirection.xyz)));
                float contact = pow(hit, 19) * exp(-_HitAge * 6);
                float ripple = exp(-pow((hit - (0.94 - _HitAge * 1.1)) * 13, 2)) *
                    (1 - smoothstep(0.55, 1, _HitAge));
                float2 flowUv = input.uv + float2(_Time.y * 0.026, _Time.y * 0.009);
                float flow = tex2D(_FlowTex, flowUv).a;
                float pulse = 0.5 + 0.5 * sin(_Time.y * 2.8);
                float alpha = 0.16 + 0.11 * fresnel + 0.75 * flow +
                    0.015 * pulse + 0.45 * contact + 0.35 * ripple;
                float3 color = lerp(_Tint.rgb, float3(0.88, 0.98, 1),
                    saturate(contact * 0.75 + ripple * 0.35));
                return fixed4(color, saturate(alpha) * _Tint.a);
            }
            ENDCG
        }
    }
}
