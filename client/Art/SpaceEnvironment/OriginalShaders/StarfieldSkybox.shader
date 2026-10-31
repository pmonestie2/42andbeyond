Shader "BlackHole/StarfieldSkybox"
{
    Properties
    {
        _SkyIntensity("Sky Intensity", Range(0, 3)) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Background"
            "Queue" = "Background"
            "RenderPipeline" = "UniversalPipeline"
            "PreviewType" = "Skybox"
        }

        Pass
        {
            Name "StarfieldSkybox"

            Cull Off
            ZWrite Off

            HLSLPROGRAM
            #pragma vertex VertexStage
            #pragma fragment FragmentStage
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "ProceduralSky.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _SkyIntensity;
            CBUFFER_END

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
                output.directionOS = input.positionOS.xyz;
                return output;
            }

            half4 FragmentStage(Varyings input) : SV_Target
            {
                float3 direction = normalize(input.directionOS);
                float3 color = SampleProceduralSky(direction) * _SkyIntensity;
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
