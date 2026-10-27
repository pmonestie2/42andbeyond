Shader "42 and Beyond/ProceduralPlanet"
{
    Properties {
        _MainTex ("Volcanic surface", 2D) = "black" {}
        _NormalTex ("Surface normal", 2D) = "bump" {}
        _RoughnessTex ("Roughness", 2D) = "gray" {}
        _AoTex ("Ambient occlusion", 2D) = "white" {}
    }
    SubShader {
        Tags { "RenderType"="Opaque" }
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0
        sampler2D _MainTex, _NormalTex, _RoughnessTex, _AoTex;
        struct Input { float2 uv_MainTex; };
        void surf(Input i, inout SurfaceOutputStandard o) {
            half3 pigment = tex2D(_MainTex, i.uv_MainTex).rgb;
            half3 normalColor = tex2D(_NormalTex, i.uv_MainTex).rgb;
            half roughness = tex2D(_RoughnessTex, i.uv_MainTex).r;
            half ao = tex2D(_AoTex, i.uv_MainTex).r;
            half lava = saturate((pigment.r - max(pigment.g, pigment.b) * 1.15) * 1.55);
            half crust = saturate(dot(pigment, half3(.3,.59,.11)) * 2.4);
            half3 stone = lerp(half3(.045,.12,.13), half3(.31,.55,.56), crust);
            half3 molten = lerp(half3(.3,.075,.03), half3(.7,.26,.09), pigment.r);
            o.Albedo = lerp(stone, molten, lava);
            o.Normal = normalize(normalColor * 2 - 1);
            o.Metallic = .08;
            o.Smoothness = saturate(.12 + (1 - roughness) * .42);
            o.Occlusion = lerp(.65, 1, ao);
            // Slow thermal variation makes the supplied procedural map breathe
            // without changing the authoritative scene or its geometry.
            half pulse = .88 + .12 * sin(_Time.y * .8 + pigment.r * 14);
            o.Emission = lava * pulse * half3(.34,.095,.025);
            o.Alpha = 1;
        }
        ENDCG
    }
    Fallback "42 and Beyond/Planet"
}
