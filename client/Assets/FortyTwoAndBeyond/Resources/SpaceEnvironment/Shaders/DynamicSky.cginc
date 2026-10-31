// Shared by the regular sky and the black-hole geodesic sky samples.
#ifndef FORTYTWO_DYNAMIC_SKY_INCLUDED
#define FORTYTWO_DYNAMIC_SKY_INCLUDED
sampler2D _DeepStars, _NebulaTex;
float _DeepStarExposure,_SectorYaw;
float4 _SectorTint;
float SkyHashScalar(float2 p)
{
    float3 h=frac(float3(p.xyx)*.1031);h+=dot(h,h.yzx+33.33);return frac((h.x+h.y)*h.z);
}
float SkySmoothValueNoise(float2 p)
{
    float2 cell=floor(p),f=frac(p);f=f*f*(3-2*f);
    return lerp(lerp(SkyHashScalar(cell),SkyHashScalar(cell+float2(1,0)),f.x),
                lerp(SkyHashScalar(cell+float2(0,1)),SkyHashScalar(cell+1),f.x),f.y);
}
float SkyFractionalBrownianMotion(float2 p)
{
    return .57*SkySmoothValueNoise(p)+.29*SkySmoothValueNoise(p*2.03+7.1)+.14*SkySmoothValueNoise(p*4.09+19.3);
}
float3 SkyHashVector3(float3 p)
{
    return frac(sin(float3(dot(p,float3(127.1,311.7,74.7)),dot(p,float3(269.5,183.3,246.1)),dot(p,float3(113.5,271.9,124.6))))*43758.5453);
}
float2 SkyCoordinates(float3 direction)
{
    return float2(atan2(direction.z,direction.x)/6.28318530718+.5,asin(clamp(direction.y,-1,1))/3.14159265359+.5);
}
float SkyStarLayer(float2 uv,float gridSize,float probability,float speed,float seed)
{
    float2 grid=(uv+float2(_Time.y*speed,0))*float2(gridSize*2,gridSize);
    float2 cell=floor(grid);cell.x=fmod(cell.x,gridSize*2);
    float random=SkyHashScalar(cell+seed);
    float2 center=float2(SkyHashScalar(cell+seed+19.7),SkyHashScalar(cell+seed+51.3))*.64+.18;
    float distanceToStar=length(frac(grid)-center);
    float2 pixelX=ddx(uv),pixelY=ddy(uv);
    pixelX.x-=round(pixelX.x);pixelY.x-=round(pixelY.x);
    float aa=clamp(length(abs(pixelX)+abs(pixelY))*gridSize*.65,.018,.15);
    float core=1-smoothstep(.008,.030+aa,distanceToStar);
    float shimmer=.78+.22*sin(_Time.y*(.65+random)+random*39);
    return core*step(1-probability,random)*shimmer*(.35+random*.65);
}
float3 SampleProceduralSky(float3 direction)
{
    float sine=sin(_SectorYaw),cosine=cos(_SectorYaw);
    direction.xz=float2(direction.x*cosine-direction.z*sine,direction.x*sine+direction.z*cosine);
    float2 uv=SkyCoordinates(normalize(direction));
    // Lat-long longitude is periodic; its screen-space derivative must wrap too.
    float2 pixelX=ddx(uv),pixelY=ddy(uv);
    pixelX.x-=round(pixelX.x);pixelY.x-=round(pixelY.x);
    float t=_Time.y;
    float2 distantUv=uv+float2(t*.00020,sin(t*.034)*.003);
    float3 catalog=tex2Dgrad(_DeepStars,distantUv,pixelX,pixelY).rgb;
    catalog=1-exp(-catalog*max(.1,_DeepStarExposure));
    // Independent advection rates make the bright filaments and nearer veils flow past one another.
    float2 flowUv=uv*float2(7,4)+float2(t*.018,-t*.009);
    float flow=lerp(SkyFractionalBrownianMotion(flowUv),SkyFractionalBrownianMotion(flowUv-float2(7,0)),smoothstep(0,1,uv.x));
    float2 warp=float2(sin(uv.y*25+t*.10),cos(uv.x*18.84955592-t*.08))*.006;
    float3 clouds=tex2Dgrad(_NebulaTex,uv+float2(t*.00085,-t*.00012)+warp,pixelX,pixelY).rgb;
    float3 veil=tex2Dgrad(_NebulaTex,uv*float2(2,1.1)+float2(-t*.00042,t*.00018)+warp*1.8,pixelX*float2(2,1.1),pixelY*float2(2,1.1)).rgb;
    float breath=.88+.12*sin(t*.23+flow*5);
    float3 nebula=(clouds*.25+veil*float3(.13,.17,.24)*(.25+flow*.45))*breath;
    float fine=SkyStarLayer(uv,175,.22,.00038,9.3);
    float bright=SkyStarLayer(uv,76,.34,.00068,43.7);
    float near=SkyStarLayer(uv,38,.18,.00110,89.2);
    float3 stars=fine*float3(.36,.53,.8)*.42+bright*float3(.78,.86,1)*1.0+near*float3(1,.83,.61)*1.35;
    return (float3(.006,.011,.024)+catalog*.44+nebula+stars)*_SectorTint.rgb;
}
#endif
