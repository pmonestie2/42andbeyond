Shader "42 and Beyond/EveShip"
{
    Properties {
        [HideInInspector] _TravelClip ("Travel clip", Float) = 0
        [HideInInspector] _TravelPlane ("Travel plane", Vector) = (0,0,1,0)
        _MainTex ("Baked hull", 2D) = "white" {}
        _GlowTex ("Glow mask", 2D) = "black" {}
        _NormalTex ("Packed tangent normals", 2D) = "gray" {}
        _RoughnessTex ("Roughness", 2D) = "gray" {}
        _HasDetail ("Use supplied material maps", Float) = 0
        _Tint ("Hull tint", Color) = (1,1,1,1)
        _GlowColor ("Glow color", Color) = (0.2,0.9,1,1)
        _Lift ("Dark detail lift", Range(1,3)) = 1.6
    }
    SubShader {
        Tags { "RenderType"="Opaque" }
        CGPROGRAM
        float _TravelClip; float4 _TravelPlane;
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0
        sampler2D _MainTex, _GlowTex, _NormalTex, _RoughnessTex;
        fixed4 _Tint, _GlowColor;
        half _Lift, _HasDetail;
        struct Input { float3 worldPos; float2 uv_MainTex; };
        void surf(Input i, inout SurfaceOutputStandard o) {
            if (_TravelClip > .5) clip(dot(float4(i.worldPos,1), _TravelPlane));
            half3 pigment = tex2D(_MainTex, i.uv_MainTex).rgb;
            // The supplied Blender palette is deliberately dark; reveal its
            // panel detail under the game's sparse space lighting.
            o.Albedo = saturate(pow(max(pigment, half3(.001,.001,.001)), .58) * _Lift) * _Tint.rgb;
            o.Metallic = .62;
            half2 normalXY = tex2D(_NormalTex, i.uv_MainTex).rg * 2 - 1;
            normalXY *= .68;
            half3 detailNormal = normalize(half3(normalXY,
                sqrt(saturate(1-dot(normalXY,normalXY)))));
            o.Normal = lerp(half3(0,0,1),detailNormal,_HasDetail);
            half roughness = tex2D(_RoughnessTex, i.uv_MainTex).r;
            o.Smoothness = lerp(.42,saturate(1-roughness*.76),_HasDetail);
            half glow = tex2D(_GlowTex, i.uv_MainTex).r;
            o.Emission = glow * _GlowColor.rgb * 2.8;
            o.Alpha = 1;
        }
        ENDCG
    }
    Fallback "42 and Beyond/TexturedShip"
}
