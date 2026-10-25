Shader "42 and Beyond/EveShip"
{
    Properties {
        _MainTex ("Baked hull", 2D) = "white" {}
        _GlowTex ("Glow mask", 2D) = "black" {}
        _Tint ("Hull tint", Color) = (1,1,1,1)
        _GlowColor ("Glow color", Color) = (0.2,0.9,1,1)
        _Lift ("Dark detail lift", Range(1,3)) = 1.6
    }
    SubShader {
        Tags { "RenderType"="Opaque" }
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0
        sampler2D _MainTex, _GlowTex;
        fixed4 _Tint, _GlowColor;
        half _Lift;
        struct Input { float2 uv_MainTex; };
        void surf(Input i, inout SurfaceOutputStandard o) {
            half3 pigment = tex2D(_MainTex, i.uv_MainTex).rgb;
            // The supplied Blender palette is deliberately dark; reveal its
            // panel detail under the game's sparse space lighting.
            o.Albedo = saturate(pow(max(pigment, half3(.001,.001,.001)), .58) * _Lift) * _Tint.rgb;
            o.Metallic = .62;
            o.Smoothness = .42;
            half glow = tex2D(_GlowTex, i.uv_MainTex).r;
            o.Emission = glow * _GlowColor.rgb * 2.8;
            o.Alpha = 1;
        }
        ENDCG
    }
    Fallback "42 and Beyond/TexturedShip"
}
