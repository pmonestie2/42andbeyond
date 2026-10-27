Shader "42 and Beyond/LegacyShip"
{
    Properties {
        _MainTex ("Original hull texture", 2D) = "white" {}
        _DetailTex ("Unwrapped panel texture", 2D) = "white" {}
        _UseDetail ("Use projected panels", Range(0,1)) = 0
        _Desaturate ("Reduce original color", Range(0,1)) = 0
        _Tint ("Hull tint", Color) = (1,1,1,1)
        _Accent ("Paint color", Color) = (1,1,1,1)
        _PaintStrength ("Paint coverage", Range(0,1)) = 0
        _Lift ("Texture exposure", Range(1,3)) = 2.2
        _Glow ("Emission", Range(0,2)) = 0.1
        _UseAlpha ("Use texture opacity", Range(0,1)) = 0
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0.08
    }
    SubShader {
        Tags { "Queue"="AlphaTest" "RenderType"="TransparentCutout" }
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows alphatest:_Cutoff
        #pragma target 3.0
        sampler2D _MainTex, _DetailTex;
        fixed4 _Tint, _Accent;
        half _Lift, _Glow, _UseAlpha, _UseDetail, _Desaturate, _PaintStrength;
        struct Input { float2 uv_MainTex; float3 worldPos; };
        void surf(Input input, inout SurfaceOutputStandard output) {
            fixed4 paint = tex2D(_MainTex, input.uv_MainTex);
            float3 localPosition = mul(unity_WorldToObject, float4(input.worldPos, 1)).xyz;
            fixed4 panels = tex2D(_DetailTex, localPosition.xz * .035 + .5);
            paint.rgb = lerp(paint.rgb, panels.rgb, _UseDetail);
            half luminance = dot(paint.rgb, half3(.24, .67, .09));
            paint.rgb = lerp(paint.rgb, half3(luminance, luminance, luminance), _Desaturate);
            half3 exposed = saturate(pow(max(paint.rgb, half3(.002,.002,.002)), .62) *
                _Lift + .09) * _Tint.rgb;
            half paintMask = smoothstep(.37, .83, luminance) * _PaintStrength;
            exposed = lerp(exposed, saturate(exposed * _Accent.rgb * 2.25), paintMask);
            output.Albedo = exposed;
            output.Metallic = lerp(.28, .72, paintMask);
            output.Smoothness = lerp(.4, .62, paintMask);
            output.Emission = exposed * _Glow;
            output.Alpha = lerp(1, paint.a * saturate(max(paint.r,
                max(paint.g, paint.b)) * 2), _UseAlpha);
        }
        ENDCG
    }
    Fallback "42 and Beyond/TexturedShip"
}
