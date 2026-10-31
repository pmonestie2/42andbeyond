// Renderer adapter only; the imported shading and raymarch algorithms are retained.
#ifndef FORTYTWO_BUILTIN_SPACE_INCLUDED
#define FORTYTWO_BUILTIN_SPACE_INCLUDED
#include "UnityCG.cginc"
#ifndef TWO_PI
#define TWO_PI 6.28318530718
#endif
#define TransformObjectToHClip(position) UnityObjectToClipPos(position)
float3 TransformObjectToWorld(float3 position) { return mul(unity_ObjectToWorld,float4(position,1)).xyz; }
float4 TransformWorldToHClip(float3 position) { return mul(UNITY_MATRIX_VP,float4(position,1)); }
float3 TransformObjectToWorldNormal(float3 normal) { return UnityObjectToWorldNormal(normal); }
struct VertexPositionInputs { float3 positionWS; float4 positionCS; };
VertexPositionInputs GetVertexPositionInputs(float3 position)
{
    VertexPositionInputs output;
    output.positionWS=TransformObjectToWorld(position);
    output.positionCS=UnityObjectToClipPos(position);
    return output;
}
#define TEXTURE2D(name) sampler2D name
#define SAMPLER(name)
#define SAMPLE_TEXTURE2D(textureName,samplerName,uv) tex2D(textureName,uv)
#endif
