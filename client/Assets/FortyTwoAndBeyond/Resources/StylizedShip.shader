Shader "42 and Beyond/StylizedShip"
{
    Properties {
        _Color ("Hull color", Color) = (0.9,0.95,1,1)
        _AccentColor ("Faction accent", Color) = (0.25,0.85,1,1)
        _MainTex ("Diffuse", 2D) = "white" {}
        _BumpMap ("Normal", 2D) = "bump" {}
        _MetallicMap ("Metallic", 2D) = "black" {}
        _RoughnessMap ("Roughness", 2D) = "gray" {}
        _EmissionMap ("Emission", 2D) = "black" {}
    }
    SubShader {
        Tags { "RenderType"="Opaque" }
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0
        fixed4 _Color, _AccentColor;
        sampler2D _MainTex, _BumpMap, _MetallicMap, _RoughnessMap, _EmissionMap;
        struct Input { float2 uv_MainTex; };
        void surf(Input i, inout SurfaceOutputStandard o) {
            fixed3 pigment = tex2D(_MainTex, i.uv_MainTex).rgb;
            half accentMask = saturate((pigment.b - pigment.g - .015) * 2.8);
            half shade = dot(pigment, half3(.299,.587,.114));
            o.Albedo = lerp(pigment, shade * _AccentColor.rgb * 1.18,
                accentMask * .82) * _Color.rgb;
            o.Normal = UnpackNormal(tex2D(_BumpMap, i.uv_MainTex));
            o.Metallic = saturate(tex2D(_MetallicMap, i.uv_MainTex).r * .88);
            o.Smoothness = saturate(.12 + (1 - tex2D(_RoughnessMap, i.uv_MainTex).r) * .72);
            o.Emission = tex2D(_EmissionMap, i.uv_MainTex).rgb * _AccentColor.rgb * 1.2;
            o.Alpha = 1;
        }
        ENDCG
    }
    Fallback "42 and Beyond/Surface"
}
