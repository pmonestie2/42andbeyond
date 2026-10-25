Shader "42 and Beyond/StylizedRock"
{
    Properties {
        _Tint ("Stone tint", Color) = (0.7,0.75,0.85,1)
        _AccentColor ("Mineral accent", Color) = (0.2,0.9,1,1)
        _AccentStrength ("Mineral strength", Range(0,1)) = 0
        _MainTex ("Diffuse", 2D) = "white" {}
        _BumpMap ("Normal", 2D) = "bump" {}
        _ArmMap ("AO roughness metal", 2D) = "white" {}
        _VeinTex ("Mineral veins", 2D) = "black" {}
    }
    SubShader {
        Tags { "RenderType"="Opaque" }
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0
        fixed4 _Tint, _AccentColor;
        half _AccentStrength;
        sampler2D _MainTex, _BumpMap, _ArmMap, _VeinTex;
        struct Input { float2 uv_MainTex; };
        void surf(Input i, inout SurfaceOutputStandard o) {
            fixed3 stone = tex2D(_MainTex, i.uv_MainTex).rgb;
            fixed3 vein = tex2D(_VeinTex, i.uv_MainTex * 1.45).rgb;
            half mineral = saturate((vein.b - vein.r) * 3.5) * _AccentStrength;
            fixed3 arm = tex2D(_ArmMap, i.uv_MainTex).rgb;
            o.Albedo = lerp(stone * _Tint.rgb, _AccentColor.rgb * .42, mineral * .52);
            o.Normal = UnpackNormal(tex2D(_BumpMap, i.uv_MainTex));
            o.Occlusion = .55 + arm.r * .45;
            o.Metallic = arm.b * .18;
            o.Smoothness = saturate(.11 + (1 - arm.g) * .27);
            o.Emission = _AccentColor.rgb * mineral * .2;
            o.Alpha = 1;
        }
        ENDCG
    }
    Fallback "42 and Beyond/Surface"
}
