// Unity HLSL port of CosmosMuseum/cosmos-museum-sun (MIT).
// Copyright (c) 2026 Sebastián Vasquez. Original shader: Panteleymonov Aleksandr.
// Original source and full notice are retained in Art/SpaceEnvironment/StellarSun.
#ifndef FORTYTWO_STELLAR_SUN_INCLUDED
#define FORTYTWO_STELLAR_SUN_INCLUDED
float4 hash4(float4 n){return frac(sin(n)*1399763.5453123);}

float noise4q(float4 x){
float4 n3=float4(0,0.25,0.5,0.75);
float4 p2=floor(x.wwww+n3);
float4 b=floor(x.xxxx+n3)+floor(x.yyyy+n3)*157.0+floor(x.zzzz+n3)*113.0;
float4 p1=b+frac(p2*0.00390625)*float4(164352.0,-164352.0,163840.0,-163840.0);
p2=b+frac((p2+1.0)*0.00390625)*float4(164352.0,-164352.0,163840.0,-163840.0);
float4 f1=frac(x.xxxx+n3);
float4 f2=frac(x.yyyy+n3);
f1=f1*f1*(3.0-2.0*f1);
f2=f2*f2*(3.0-2.0*f2);
float4 n1=float4(0,1.0,157.0,158.0);
float4 n2=float4(113.0,114.0,270.0,271.0);
float4 vs1=lerp(hash4(p1),hash4(n1.yyyy+p1),f1);
float4 vs2=lerp(hash4(n1.zzzz+p1),hash4(n1.wwww+p1),f1);
float4 vs3=lerp(hash4(p2),hash4(n1.yyyy+p2),f1);
float4 vs4=lerp(hash4(n1.zzzz+p2),hash4(n1.wwww+p2),f1);
vs1=lerp(vs1,vs2,f2);vs3=lerp(vs3,vs4,f2);
vs2=lerp(hash4(n2.xxxx+p1),hash4(n2.yyyy+p1),f1);
vs4=lerp(hash4(n2.zzzz+p1),hash4(n2.wwww+p1),f1);
vs2=lerp(vs2,vs4,f2);
vs4=lerp(hash4(n2.xxxx+p2),hash4(n2.yyyy+p2),f1);
float4 vs5=lerp(hash4(n2.zzzz+p2),hash4(n2.wwww+p2),f1);
vs4=lerp(vs4,vs5,f2);
f1=frac(x.zzzz+n3);f2=frac(x.wwww+n3);
f1=f1*f1*(3.0-2.0*f1);f2=f2*f2*(3.0-2.0*f2);
vs1=lerp(vs1,vs2,f1);vs3=lerp(vs3,vs4,f1);vs1=lerp(vs1,vs3,f2);
float r=dot(vs1,float4(0.25,0.25,0.25,0.25));
return r*r*(3.0-2.0*r);
}

float noiseSpere(float3 ray,float3 pos,float r,float3x3 mr,float zoom,float3 subnoise,float anim){
float b=dot(ray,pos);
float c=dot(pos,pos)-b*b;
float3 r1=float3(0.0,0.0,0.0);
float s=0.0;
float d=0.03125;
float d2=zoom/(d*d);
float ar=5.0;
for(int i=0;i<3;i++){
float rq=r*r;
if(c<rq){
float l1=sqrt(rq-c);
r1=ray*(b-l1)-pos;
r1=mul(r1,mr);
s+=abs(noise4q(float4(r1*d2+subnoise*ar,anim*ar))*d);
}
ar-=2.0;d*=4.0;d2*=0.0625;r=r-r*0.02;
}
return s;
}

float ring(float3 ray,float3 pos,float r,float size){
float b=dot(ray,pos);
float c=dot(pos,pos)-b*b;
return max(0.0,1.0-size*abs(r-sqrt(c)));
}

float ringRayNoise(float3 ray,float3 pos,float r,float size,float3x3 mr,float anim){
float b=dot(ray,pos);
float3 pr=ray*b-pos;
float c=length(pr);
pr=mul(pr,mr);pr=normalize(pr);
float s=max(0.0,1.0-size*abs(r-c));
float nd=noise4q(float4(pr*1.0,-anim+c))*2.0;
nd=pow(nd,2.0);
float n=0.4;
float ns=1.0;
if(c>r){
n=noise4q(float4(pr*10.0,-anim+c));
ns=noise4q(float4(pr*50.0,-anim*2.5+c*2.0))*2.0;
}
n=n*n*nd*ns;
return pow(s,4.0)+s*s*n;
}


#endif
