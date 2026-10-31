#ifndef BLACKHOLE_PROCEDURAL_SKY_INCLUDED
#define BLACKHOLE_PROCEDURAL_SKY_INCLUDED

#define SKY_GRID_NEAR 90.0
#define SKY_GRID_FAR 40.0
#define SKY_STAR_CORE_RADIUS 0.045
#define SKY_STAR_PROBABILITY 0.09
#define SKY_STAR_FAR_BOOST 1.5
#define SKY_STAR_OFFSET_SPREAD 0.7
#define SKY_STAR_INTENSITY 1.8
#define SKY_STAR_TINT float3(0.85, 0.9, 1.0)
#define SKY_NEBULA_SCALE 1.1
#define SKY_NEBULA_CONTRAST 1.9
#define SKY_NEBULA_INTENSITY 0.7
#define SKY_NEBULA_BAND_TIGHTNESS 1.0
#define SKY_NEBULA_COLOR_A float3(0.5, 0.2, 0.13)
#define SKY_NEBULA_COLOR_B float3(0.1, 0.14, 0.34)

float SkyHashScalar(float2 p)
{
    return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453123);
}

float3 SkyHashVector3(float3 p)
{
    p = float3(
        dot(p, float3(127.1, 311.7, 74.7)),
        dot(p, float3(269.5, 183.3, 246.1)),
        dot(p, float3(113.5, 271.9, 124.6)));
    return frac(sin(p) * 43758.5453123);
}

float SkySmoothValueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return lerp(
        lerp(SkyHashScalar(i), SkyHashScalar(i + float2(1, 0)), u.x),
        lerp(SkyHashScalar(i + float2(0, 1)), SkyHashScalar(i + float2(1, 1)), u.x),
        u.y);
}

float SkyFractionalBrownianMotion(float2 p)
{
    float v = 0.0;
    v += 0.5000 * SkySmoothValueNoise(p);
    v += 0.2500 * SkySmoothValueNoise(p * 2.03);
    v += 0.1250 * SkySmoothValueNoise(p * 4.01);
    v += 0.0625 * SkySmoothValueNoise(p * 8.07);
    return v * 1.0666;
}

float SkyStarLayer(float3 direction, float gridSize, float coreRadius, float probability)
{
    float3 scaled = direction * gridSize;
    float3 cellIndex = floor(scaled);
    float3 cellPosition = frac(scaled) - 0.5;
    float3 random = SkyHashVector3(cellIndex);
    float distanceToStar = length(cellPosition - (random - 0.5) * SKY_STAR_OFFSET_SPREAD);
    float core = smoothstep(coreRadius, 0.0, distanceToStar);
    float existence = step(1.0 - probability, random.z);
    return core * existence * (0.3 + 0.7 * random.y);
}

float3 SampleProceduralSky(float3 direction)
{
    float stars = SkyStarLayer(direction, SKY_GRID_NEAR, SKY_STAR_CORE_RADIUS, SKY_STAR_PROBABILITY);
    stars += SkyStarLayer(direction, SKY_GRID_FAR, SKY_STAR_CORE_RADIUS * 1.5, SKY_STAR_PROBABILITY * 0.6) * SKY_STAR_FAR_BOOST;
    float3 starColor = SKY_STAR_TINT * stars * SKY_STAR_INTENSITY;

    float cloudA = SkyFractionalBrownianMotion(direction.xy * SKY_NEBULA_SCALE);
    float cloudB = SkyFractionalBrownianMotion(direction.zy * SKY_NEBULA_SCALE + 7.31);
    float cloud = saturate(cloudA * cloudB * SKY_NEBULA_CONTRAST);
    float band = exp(-direction.y * direction.y * SKY_NEBULA_BAND_TIGHTNESS * SKY_NEBULA_BAND_TIGHTNESS);
    float tint = SkyFractionalBrownianMotion(direction.xz * SKY_NEBULA_SCALE * 0.5 + 3.17);
    float3 nebulaColor = lerp(SKY_NEBULA_COLOR_A, SKY_NEBULA_COLOR_B, tint) * cloud * band * SKY_NEBULA_INTENSITY;

    return starColor + nebulaColor;
}

#endif
