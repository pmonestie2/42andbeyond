Shader "BlackHole/Gargantua"
{
    Properties
    {
        _DeepStars("NASA HDR star map", 2D) = "black" {}
        _NebulaTex("Flowing nebula", 2D) = "black" {}
        _DeepStarExposure("Star map exposure", Float) = 2.6
        _SectorYaw("Sector orientation", Float) = 0
        _SectorTint("Sector palette", Color) = (1,1,1,1)
        _HorizonScale("Event Horizon Scale", Range(0.02, 0.2)) = 0.08
        _StepCount("Raymarch Steps", Range(64, 320)) = 280
        _StepSize("Raymarch Step Scale", Range(0.02, 0.2)) = 0.05
        _DiskInnerRadius("Disk Inner Radius", Range(1.2, 6)) = 2.6
        _DiskOuterRadius("Disk Outer Radius", Range(4, 12)) = 9
        _DiskBrightness("Disk Brightness", Range(0, 30)) = 7
        _DiskFalloff("Disk Radial Falloff", Range(0.5, 4)) = 2.2
        [HDR] _DiskColor("Disk Color", Color) = (1, 0.55, 0.38, 1)
        [HDR] _DiskHotColor("Disk Hot Color", Color) = (1, 0.93, 0.85, 1)
        _DiskDetail("Disk Ring Detail", Range(0.5, 8)) = 5.5
        _DiskRotationSpeed("Disk Rotation Speed", Range(0, 3)) = 1.2
        _DopplerStrength("Doppler Strength", Range(0, 1)) = 0.55
        _ParticleIntensity("Particle Intensity", Range(0, 8)) = 3
        _ParticleThreshold("Particle Sparsity", Range(0, 0.95)) = 0.62
        _SpiralArms("Spiral Arms", Range(0, 6)) = 2
        _SpiralIntensity("Spiral Intensity", Range(0, 1)) = 0.45
        _TurbulenceWarp("Turbulence Warp", Range(0, 3)) = 1
        _DustStrength("Dust Lane Strength", Range(0, 1)) = 0.55
        _StarfieldIntensity("Starfield Intensity", Range(0, 2)) = 1
        [Toggle(_SUPERSAMPLE_4X)] _Supersample("Supersample 4x (Anti-aliasing)", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            
        }

        Pass
        {
            Name "BlackHoleRaymarch"

            Cull Front
            ZWrite Off
            ZTest LEqual
            Blend One Zero

            CGPROGRAM
            #pragma vertex VertexStage
            #pragma fragment FragmentStage
            #pragma target 3.5
            #pragma shader_feature_local _SUPERSAMPLE_4X

            #include "BuiltInSpace.cginc"
            #include "ProceduralSky.hlsl"

            #define MAX_RAYMARCH_STEPS 320
            #define MESH_BOUND_RADIUS 0.5
            #define CAPTURE_RADIUS 0.95
            #define GEODESIC_BEND_FACTOR 1.5
            #define MIN_STEP_LENGTH 0.03
            #define MAX_STEP_FRACTION_OF_BOUND 0.06
            #define INNER_EDGE_SOFTNESS 1.18
            #define OUTER_FADE_START 0.55
            #define STREAK_ANGULAR_FREQUENCY 26.0
            #define STREAK_BASE 0.25
            #define STREAK_AMPLITUDE 1.2
            #define STREAK_WARP_RADIAL_FREQUENCY 0.6
            #define STREAK_WARP_ANGULAR_FREQUENCY 3.0
            #define STREAK_WARP_AMOUNT 3.2
            #define STREAK_FINE_RADIAL 4.5
            #define STREAK_FINE_ANGULAR 0.72
            #define STREAK_FINE_BASE 0.32
            #define STREAK_FINE_AMPLITUDE 1.35
            #define STREAK_FINE_OFFSET 19.3
            #define RIDGE_OCTAVE2_SCALE 2.3
            #define RIDGE_OCTAVE1_WEIGHT 0.62
            #define RIDGE_OCTAVE2_WEIGHT 0.38
            #define GRAIN_SCALE 7.5
            #define GRAIN_BASE 0.7
            #define GRAIN_AMPLITUDE 0.6
            #define CLOUD_NOISE_SCALE 1.2
            #define CLOUD_SPEED_RATIO 0.7
            #define CLOUD_BASE 0.5
            #define CLOUD_AMPLITUDE 0.95
            #define CLOUD_NOISE_OFFSET float2(31.7, 17.3)
            #define DOMAIN_WARP_OFFSET 23.1
            #define DUST_RADIAL_FREQUENCY 2.4
            #define DUST_ANGULAR_RATIO 0.7
            #define DUST_OFFSET 53.7
            #define DUST_THRESHOLD_LOW 0.5
            #define DUST_THRESHOLD_HIGH 0.6
            #define CLUMP_RADIAL_FREQUENCY 2.2
            #define CLUMP_ANGULAR_FREQUENCY 26.0
            #define CLUMP_CONTRAST_GAIN 3.0
            #define SPIRAL_WINDING 2.5
            #define SPIRAL_SHARPNESS 2.0
            #define SPIRAL_DRIFT 0.25
            #define DOPPLER_BEAMING_EXPONENT 3.0
            #define DOPPLER_SHIFT_GAIN 1.0
            #define HEAT_FALLOFF_EXPONENT 3.0
            #define DISK_COVERAGE_GAIN 1.4
            #define TRANSMITTANCE_CUTOFF 0.01

            
                float _HorizonScale;
                float _StepCount;
                float _StepSize;
                float _DiskInnerRadius;
                float _DiskOuterRadius;
                float _DiskBrightness;
                float _DiskFalloff;
                half4 _DiskColor;
                half4 _DiskHotColor;
                float _DiskDetail;
                float _DiskRotationSpeed;
                float _DopplerStrength;
                float _ParticleIntensity;
                float _ParticleThreshold;
                float _SpiralArms;
                float _SpiralIntensity;
                float _TurbulenceWarp;
                float _DustStrength;
                float _StarfieldIntensity;
            

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            float SeamlessAngularNoise(float2 coordsA, float2 coordsB, float blend)
            {
                return lerp(SkySmoothValueNoise(coordsA), SkySmoothValueNoise(coordsB), blend);
            }

            float RidgedValueNoise(float2 p)
            {
                float n = SkySmoothValueNoise(p);
                n = 1.0 - abs(2.0 * n - 1.0);
                return n * n;
            }

            float SeamlessRidgedFilament(float2 coordsA, float2 coordsB, float blend)
            {
                float octave1 = lerp(RidgedValueNoise(coordsA), RidgedValueNoise(coordsB), blend);
                float octave2 = lerp(RidgedValueNoise(coordsA * RIDGE_OCTAVE2_SCALE), RidgedValueNoise(coordsB * RIDGE_OCTAVE2_SCALE), blend);
                return RIDGE_OCTAVE1_WEIGHT * octave1 + RIDGE_OCTAVE2_WEIGHT * octave2;
            }

            float3 EvaluateDiskEmission(float3 crossingPoint, float3 rayDirection, out float coverage)
            {
                float orbitRadius = length(crossingPoint.xz);
                float innerFade = smoothstep(_DiskInnerRadius, _DiskInnerRadius * INNER_EDGE_SOFTNESS, orbitRadius);
                float outerFade = 1.0 - smoothstep(_DiskOuterRadius * OUTER_FADE_START, _DiskOuterRadius, orbitRadius);
                float radialMask = innerFade * outerFade;

                float angularVelocity = rsqrt(orbitRadius * orbitRadius * orbitRadius + 0.001);
                float orbitAngle = atan2(crossingPoint.z, crossingPoint.x);
                float rotationPhase = orbitAngle - _Time.y * _DiskRotationSpeed * angularVelocity;
                float normalizedPhase = frac(rotationPhase / TWO_PI);

                float radialCoordinate = orbitRadius * _DiskDetail;

                float2 streakWarpCoordsA = float2(orbitRadius * STREAK_WARP_RADIAL_FREQUENCY, normalizedPhase * STREAK_WARP_ANGULAR_FREQUENCY);
                float2 streakWarpCoordsB = float2(orbitRadius * STREAK_WARP_RADIAL_FREQUENCY, (normalizedPhase - 1.0) * STREAK_WARP_ANGULAR_FREQUENCY);
                float streakWarp = SeamlessAngularNoise(streakWarpCoordsA, streakWarpCoordsB, normalizedPhase) - 0.5;
                float warpedRadial = radialCoordinate + _TurbulenceWarp * STREAK_WARP_AMOUNT * streakWarp;

                float2 streakCoordsA = float2(warpedRadial, normalizedPhase * STREAK_ANGULAR_FREQUENCY);
                float2 streakCoordsB = float2(warpedRadial, (normalizedPhase - 1.0) * STREAK_ANGULAR_FREQUENCY);
                float streaks = lerp(
                    SkyFractionalBrownianMotion(streakCoordsA),
                    SkyFractionalBrownianMotion(streakCoordsB),
                    normalizedPhase);
                streaks *= streaks;

                float2 fineStreakCoordsA = float2(
                    warpedRadial * STREAK_FINE_RADIAL,
                    normalizedPhase * STREAK_ANGULAR_FREQUENCY * STREAK_FINE_ANGULAR) + STREAK_FINE_OFFSET;
                float2 fineStreakCoordsB = float2(
                    warpedRadial * STREAK_FINE_RADIAL,
                    (normalizedPhase - 1.0) * STREAK_ANGULAR_FREQUENCY * STREAK_FINE_ANGULAR) + STREAK_FINE_OFFSET;
                float fineStreaks = SeamlessRidgedFilament(fineStreakCoordsA, fineStreakCoordsB, normalizedPhase);
                streaks *= STREAK_FINE_BASE + STREAK_FINE_AMPLITUDE * fineStreaks;

                float grain = SeamlessRidgedFilament(streakCoordsA * GRAIN_SCALE, streakCoordsB * GRAIN_SCALE, normalizedPhase);

                float2 clumpCoordsA = float2(orbitRadius * CLUMP_RADIAL_FREQUENCY, normalizedPhase * CLUMP_ANGULAR_FREQUENCY);
                float2 clumpCoordsB = float2(orbitRadius * CLUMP_RADIAL_FREQUENCY, (normalizedPhase - 1.0) * CLUMP_ANGULAR_FREQUENCY);
                float clumps = SeamlessAngularNoise(clumpCoordsA, clumpCoordsB, normalizedPhase);
                clumps = saturate((clumps - _ParticleThreshold) * CLUMP_CONTRAST_GAIN);
                clumps *= clumps;

                float armPattern = 0.5 + 0.5 * sin(
                    _SpiralArms * orbitAngle
                    + SPIRAL_WINDING * log(orbitRadius + 1.0)
                    - _Time.y * _DiskRotationSpeed * SPIRAL_DRIFT);
                armPattern = pow(armPattern, SPIRAL_SHARPNESS);
                float spiralModulation = lerp(1.0, armPattern, _SpiralIntensity);

                float cloudRotation = _Time.y * _DiskRotationSpeed * CLOUD_SPEED_RATIO * angularVelocity;
                float sinRotation;
                float cosRotation;
                sincos(cloudRotation, sinRotation, cosRotation);
                float2 rotatedOrbit = float2(
                    crossingPoint.x * cosRotation - crossingPoint.z * sinRotation,
                    crossingPoint.x * sinRotation + crossingPoint.z * cosRotation);
                float2 cloudCoord = rotatedOrbit * CLOUD_NOISE_SCALE + CLOUD_NOISE_OFFSET;
                float2 warpField = float2(
                    SkyFractionalBrownianMotion(cloudCoord),
                    SkyFractionalBrownianMotion(cloudCoord + DOMAIN_WARP_OFFSET)) - 0.5;
                float clouds = SkyFractionalBrownianMotion(cloudCoord + _TurbulenceWarp * warpField);

                float2 dustCoordsA = float2(radialCoordinate * DUST_RADIAL_FREQUENCY, normalizedPhase * STREAK_ANGULAR_FREQUENCY * DUST_ANGULAR_RATIO) + DUST_OFFSET;
                float2 dustCoordsB = float2(radialCoordinate * DUST_RADIAL_FREQUENCY, (normalizedPhase - 1.0) * STREAK_ANGULAR_FREQUENCY * DUST_ANGULAR_RATIO) + DUST_OFFSET;
                float dust = SeamlessAngularNoise(dustCoordsA, dustCoordsB, normalizedPhase);
                float dustLane = 1.0 - _DustStrength * smoothstep(DUST_THRESHOLD_LOW, DUST_THRESHOLD_HIGH, dust);

                float density = radialMask
                    * (STREAK_BASE + STREAK_AMPLITUDE * streaks)
                    * (GRAIN_BASE + GRAIN_AMPLITUDE * grain)
                    * (CLOUD_BASE + CLOUD_AMPLITUDE * clouds)
                    * spiralModulation
                    * dustLane;

                float particleDensity = radialMask * clumps * dustLane;

                float radialBrightness = pow(saturate(_DiskInnerRadius / orbitRadius), _DiskFalloff);

                float3 orbitTangent = float3(-crossingPoint.z, 0.0, crossingPoint.x) * rsqrt(orbitRadius * orbitRadius + 0.0001);
                float dopplerShift = dot(orbitTangent, -rayDirection);
                float beaming = pow(max(1.0 + _DopplerStrength * dopplerShift * DOPPLER_SHIFT_GAIN, 0.0), DOPPLER_BEAMING_EXPONENT);

                float heat = pow(saturate(_DiskInnerRadius / orbitRadius), HEAT_FALLOFF_EXPONENT);
                float3 diskColor = lerp(_DiskColor.rgb, _DiskHotColor.rgb, saturate(heat + max(dopplerShift, 0.0) * _DopplerStrength));

                float3 baseEmission = diskColor * density * radialBrightness * beaming;
                float3 particleEmission = _DiskHotColor.rgb * particleDensity * radialBrightness * beaming * _ParticleIntensity;

                coverage = saturate((density + particleDensity) * DISK_COVERAGE_GAIN);
                return baseEmission + particleEmission;
            }

            Varyings VertexStage(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                return output;
            }

            float3 TraceBlackHole(float3 rayDirectionWS)
            {
                float simulationScale = 1.0 / (MESH_BOUND_RADIUS * _HorizonScale);
                float boundRadiusSim = MESH_BOUND_RADIUS * simulationScale;

                float3 rayOriginSim = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1.0)).xyz * simulationScale;
                float3 rayDirectionSim = normalize(mul((float3x3)unity_WorldToObject, rayDirectionWS));

                float midpointDistance = dot(-rayOriginSim, rayDirectionSim);
                float closestApproachSq = dot(rayOriginSim, rayOriginSim) - midpointDistance * midpointDistance;
                float halfChord = sqrt(max(boundRadiusSim * boundRadiusSim - closestApproachSq, 0.0));
                float entryDistance = max(midpointDistance - halfChord, 0.0);

                float3 position = rayOriginSim + rayDirectionSim * entryDistance;
                float3 velocity = rayDirectionSim;

                float3 angularMomentum = cross(position, velocity);
                float angularMomentumSq = dot(angularMomentum, angularMomentum);

                float maxStepLength = boundRadiusSim * MAX_STEP_FRACTION_OF_BOUND;

                float3 diskEmission = 0.0;
                float transmittance = 1.0;
                float escaped = 0.0;

                [loop]
                for (int stepIndex = 0; stepIndex < MAX_RAYMARCH_STEPS; stepIndex++)
                {
                    if (stepIndex >= (int)_StepCount)
                    {
                        break;
                    }

                    float radius = length(position);

                    if (radius < CAPTURE_RADIUS)
                    {
                        break;
                    }

                    if (radius > boundRadiusSim && dot(position, velocity) > 0.0)
                    {
                        escaped = 1.0;
                        break;
                    }

                    float stepLength = clamp(radius * _StepSize, MIN_STEP_LENGTH, maxStepLength);
                    float radiusSq = radius * radius;
                    float3 acceleration = -GEODESIC_BEND_FACTOR * angularMomentumSq * position / (radiusSq * radiusSq * radius);

                    float3 previousPosition = position;
                    velocity += acceleration * stepLength;
                    position += velocity * stepLength;

                    if (previousPosition.y * position.y < 0.0 && transmittance > TRANSMITTANCE_CUTOFF)
                    {
                        float interpolant = previousPosition.y / (previousPosition.y - position.y);
                        float3 crossingPoint = lerp(previousPosition, position, interpolant);
                        float coverage;
                        float3 crossingEmission = EvaluateDiskEmission(crossingPoint, normalize(velocity), coverage);
                        diskEmission += crossingEmission * transmittance;
                        transmittance *= 1.0 - coverage;
                    }
                }

                float3 exitDirectionWS = normalize(mul((float3x3)unity_ObjectToWorld, velocity));
                float3 lensedSky = SampleProceduralSky(exitDirectionWS) * _StarfieldIntensity * escaped;

                return diskEmission * _DiskBrightness + lensedSky * transmittance;
            }

            half4 FragmentStage(Varyings input) : SV_Target
            {
                float3 rayDirectionWS = normalize(input.positionWS - _WorldSpaceCameraPos);

            #if defined(_SUPERSAMPLE_4X)
                float3 derivativeX = ddx(rayDirectionWS);
                float3 derivativeY = ddy(rayDirectionWS);
                float3 accumulated = 0.0;
                accumulated += TraceBlackHole(normalize(rayDirectionWS - 0.375 * derivativeX - 0.125 * derivativeY));
                accumulated += TraceBlackHole(normalize(rayDirectionWS + 0.125 * derivativeX - 0.375 * derivativeY));
                accumulated += TraceBlackHole(normalize(rayDirectionWS - 0.125 * derivativeX + 0.375 * derivativeY));
                accumulated += TraceBlackHole(normalize(rayDirectionWS + 0.375 * derivativeX + 0.125 * derivativeY));
                float3 finalColor = accumulated * 0.25;
            #else
                float3 finalColor = TraceBlackHole(rayDirectionWS);
            #endif

                return half4(finalColor, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
