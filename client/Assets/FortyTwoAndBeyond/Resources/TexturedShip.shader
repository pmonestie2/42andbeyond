Shader "42 and Beyond/TexturedShip"
{
    Properties {
        [HideInInspector] _TravelClip ("Travel clip", Float) = 0
        [HideInInspector] _TravelPlane ("Travel plane", Vector) = (0,0,1,0)
        _Color ("Hull tint", Color) = (0.72,0.75,0.8,1)
        _MainTex ("Diffuse", 2D) = "white" {}
        _BumpMap ("Normal", 2D) = "bump" {}
        _SpecGlossMap ("Specular", 2D) = "black" {}
        _EmissionMap ("Emission", 2D) = "black" {}
    }
    SubShader {
        Tags { "RenderType"="Opaque" }
        CGPROGRAM
        float _TravelClip; float4 _TravelPlane;
        #pragma surface surf StandardSpecular fullforwardshadows
        #pragma target 3.0
        fixed4 _Color;
        sampler2D _MainTex, _BumpMap, _SpecGlossMap, _EmissionMap;
        struct Input { float3 worldPos; float2 uv_MainTex; };
        void surf(Input i, inout SurfaceOutputStandardSpecular o) {
            if (_TravelClip > .5) clip(dot(float4(i.worldPos,1), _TravelPlane));
            fixed4 diffuse = tex2D(_MainTex, i.uv_MainTex);
            o.Albedo = diffuse.rgb * _Color.rgb;
            o.Normal = UnpackNormal(tex2D(_BumpMap, i.uv_MainTex));
            fixed3 finish = tex2D(_SpecGlossMap, i.uv_MainTex).rgb;
            half reflectance = dot(finish, half3(.299, .587, .114));
            o.Specular = finish * .52;
            o.Smoothness = .16 + reflectance * .45;
            o.Emission = tex2D(_EmissionMap, i.uv_MainTex).rgb * .9;
            o.Alpha = 1;
        }
        ENDCG
    }
    Fallback "42 and Beyond/Surface"
}
