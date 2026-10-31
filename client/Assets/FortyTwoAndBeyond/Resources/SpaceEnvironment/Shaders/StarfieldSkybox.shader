Shader "BlackHole/StarfieldSkybox"
{
    Properties
    {
        _DeepStars("NASA HDR star map", 2D) = "black" {}
        _NebulaTex("Flowing nebula", 2D) = "black" {}
        _DeepStarExposure("Star map exposure", Float) = 2.6
        _SectorYaw("Sector orientation", Float) = 0
        _SectorTint("Sector palette", Color) = (1,1,1,1)
        _SkyIntensity("Sky Intensity", Range(0, 3)) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Background"
            "Queue" = "Background"
            
            "PreviewType" = "Skybox"
        }

        Pass
        {
            Name "StarfieldSkybox"

            Cull Off
            ZWrite Off
            ZTest LEqual

            CGPROGRAM
            #pragma vertex VertexStage
            #pragma fragment FragmentStage
            #pragma target 3.5

            #include "BuiltInSpace.cginc"
            #include "ProceduralSky.hlsl"

            
                float _SkyIntensity;
            

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 directionOS : TEXCOORD0;
            };

            Varyings VertexStage(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                #if defined(UNITY_REVERSED_Z)
                output.positionCS.z = 0;
                #else
                output.positionCS.z = output.positionCS.w;
                #endif
                output.directionOS = input.positionOS.xyz;
                return output;
            }

            half4 FragmentStage(Varyings input) : SV_Target
            {
                float3 direction = normalize(input.directionOS);
                float3 color = SampleProceduralSky(direction) * _SkyIntensity;
                return half4(color, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
