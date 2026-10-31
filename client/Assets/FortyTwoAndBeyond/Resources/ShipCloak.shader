Shader "42 and Beyond/ShipCloak"
{
    Properties {
        [HideInInspector] _TravelClip ("Travel clip", Float) = 0
        [HideInInspector] _TravelPlane ("Travel plane", Vector) = (0,0,1,0)
        _MainTex ("Hull texture", 2D) = "white" {}
        _Color ("Hull tint", Color) = (1,1,1,1)
        _CloakAmount ("Cloak transition", Range(0,1)) = 0
        _GhostOpacity ("Visible ghost opacity", Range(0.05,0.5)) = 0.18
        _Legacy ("Original texture exposure", Float) = 0
        _Lift ("Exposure", Float) = 1
        _UseAlpha ("Texture cutouts", Float) = 0
    }
    SubShader {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        ZWrite Off
        CGPROGRAM
        float _TravelClip; float4 _TravelPlane;
        #pragma surface surf Standard alpha:fade noshadow
        #pragma target 3.0
        sampler2D _MainTex;
        fixed4 _Color;
        half _CloakAmount, _GhostOpacity, _Legacy, _Lift, _UseAlpha;
        struct Input { float2 uv_MainTex; float3 viewDir; float3 worldPos; };
        void surf(Input i, inout SurfaceOutputStandard o) {
            if (_TravelClip > .5) clip(dot(float4(i.worldPos,1), _TravelPlane));
            fixed4 paint = tex2D(_MainTex, i.uv_MainTex);
            clip(lerp(1, paint.a * saturate(max(paint.r, max(paint.g, paint.b)) * 2), _UseAlpha) - .08);
            half3 exposed = lerp(paint.rgb, saturate(pow(max(paint.rgb, .002), .62) * _Lift + .09), _Legacy) * _Color.rgb;
            half rim = pow(1 - saturate(dot(normalize(i.viewDir), half3(0,0,1))), 2.4);
            half drift = .5 + .5 * sin(i.worldPos.y * 28 + _Time.y * 1.1);
            o.Albedo = lerp(exposed, exposed * .25 + half3(.035,.10,.15), _CloakAmount);
            o.Metallic = .4; o.Smoothness = .6;
            o.Emission = half3(.08,.33,.46) * _CloakAmount * (rim * .8 + drift * .025);
            // Preserve a readable hull silhouette against the live layered sky.
            o.Alpha = lerp(1, _GhostOpacity, _CloakAmount);
        }
        ENDCG
    }
    Fallback "Transparent/Diffuse"
}
