Shader "42 and Beyond/StationHull"
{
    Properties {
        _Color ("Metal tint", Color) = (0.35,0.45,0.55,1)
        _DetailTex ("Metal detail", 2D) = "white" {}
        _Metallic ("Metallic", Range(0,1)) = 0.65
        _Glossiness ("Smoothness", Range(0,1)) = 0.38
        _PlateScale ("Plate scale", Range(0.05,2)) = 0.32
        _PatternStrength ("Panel variation", Range(0,1)) = 0.65
    }
    SubShader {
        Tags { "RenderType"="Opaque" }
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0
        fixed4 _Color;
        sampler2D _DetailTex;
        half _Metallic, _Glossiness, _PlateScale, _PatternStrength;
        struct Input { float3 worldPos; float3 worldNormal; };
        half PanelHash(float2 cell) {
            return frac(sin(dot(cell, float2(127.1, 311.7))) * 43758.5453);
        }
        void surf(Input i, inout SurfaceOutputStandard o) {
            float3 normalWeight = pow(abs(normalize(i.worldNormal)), 6);
            normalWeight /= max(.001, normalWeight.x + normalWeight.y + normalWeight.z);
            float2 wallX = i.worldPos.yz * _PlateScale;
            float2 floorY = i.worldPos.xz * _PlateScale;
            float2 wallZ = i.worldPos.xy * _PlateScale;
            float2 uv = wallX * normalWeight.x + floorY * normalWeight.y +
                wallZ * normalWeight.z;
            float2 edge = min(frac(uv), 1 - frac(uv));
            half seam = 1 - smoothstep(.018, .045, min(edge.x, edge.y));
            half panel = PanelHash(floor(uv));
            half micro = PanelHash(floor(uv * 7.3));
            half3 detail = tex2D(_DetailTex, uv * 2.1).rgb;
            half shade = lerp(1, .81 + panel * .27, _PatternStrength) *
                (1 - seam * .18 * _PatternStrength);
            o.Albedo = _Color.rgb * detail * shade * (.95 + micro * .07);
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness * (.88 + panel * .2);
            o.Alpha = 1;
        }
        ENDCG
    }
    Fallback "42 and Beyond/Surface"
}
