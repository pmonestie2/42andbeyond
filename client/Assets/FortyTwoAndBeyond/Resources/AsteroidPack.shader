Shader "42 and Beyond/AsteroidPack"
{
    Properties {
        _MainTex ("Rock color", 2D) = "gray" {}
        _NormalTex ("DirectX normal", 2D) = "bump" {}
        _MetalTex ("Metallic", 2D) = "black" {}
        _RoughnessTex ("Roughness", 2D) = "white" {}
        _AoTex ("Ambient occlusion", 2D) = "white" {}
        _Tint ("Rock tint", Color) = (1,1,1,1)
    }
    SubShader {
        Tags { "RenderType"="Opaque" }
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0
        sampler2D _MainTex, _NormalTex, _MetalTex, _RoughnessTex, _AoTex;
        fixed4 _Tint;
        struct Input { float2 uv_MainTex; };
        void surf(Input i, inout SurfaceOutputStandard o) {
            half3 pigment = tex2D(_MainTex, i.uv_MainTex).rgb;
            half3 normalMap = tex2D(_NormalTex, i.uv_MainTex).rgb * 2 - 1;
            o.Albedo = pow(max(pigment, half3(.002,.002,.002)), .78) * _Tint.rgb;
            o.Normal = normalize(half3(normalMap.x, -normalMap.y, normalMap.z));
            o.Metallic = tex2D(_MetalTex, i.uv_MainTex).r * .72;
            o.Smoothness = saturate(.06 + (1 - tex2D(_RoughnessTex, i.uv_MainTex).r) * .48);
            o.Occlusion = lerp(.62, 1, tex2D(_AoTex, i.uv_MainTex).r);
            o.Alpha = 1;
        }
        ENDCG
    }
    Fallback "Standard"
}
