#ifndef FORTYTWO_SPACE_MOTION_INCLUDED
#define FORTYTWO_SPACE_MOTION_INCLUDED
float _BodyFlow;
sampler2D _TerrainMap;
float _TerrainBlend,_TerrainSeed;
float3 ComposePlanetSurface(float2 uv,float3 albedo)
{
    if(_TerrainBlend<=0)return albedo;
    float longitude=uv.x*6.2831853,latitude=(uv.y-.5)*3.14159265;
    float3 p=float3(cos(longitude)*cos(latitude),sin(latitude),sin(longitude)*cos(latitude));
    float patch=.5+.24*sin(dot(p,float3(4.7,3.1,5.3))+_TerrainSeed)+.16*sin(dot(p,float3(9.3,-7.7,8.1))+_TerrainSeed*1.7);
    float3 terrain=tex2D(_TerrainMap,float2(uv.x*1.15+_TerrainSeed*.017,uv.y)).rgb;
    return lerp(albedo,terrain,smoothstep(.2,.8,patch)*_TerrainBlend);
}
float2 AnimatePlanetUV(float2 uv)
{
    float t=_Time.y;
    float bands=sin(uv.y*70+sin(uv.y*19)*2);
    uv.x+=_BodyFlow*(t*.0025+bands*t*.0007);
    uv.y+=_BodyFlow*sin(uv.x*24+t*.35+uv.y*31)*.0025;
    return uv;
}
float2 AnimateCloudUV(float2 uv)
{
    uv.x+=sin(uv.y*34+_Time.y*.34)*.004;
    uv.y+=sin(uv.x*27-_Time.y*.27)*.003;
    return uv;
}
#endif
