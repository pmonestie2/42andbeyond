Shader "42Beyond/ArmoredResourceInstrument"
{
    Properties {
        _Bezel("Brushed metal housing",2D)="black"{}
        _Tint("Energy color",Color)=(1,.45,.03,1)
        _Fraction("Resource fraction",Range(0,1))=1
        _Kind("Reactor / Shield / Hull",Float)=0
        _Clock("Animation seconds",Float)=0
        _Compact("Compact face spacing",Float)=0
        _HullFraction("Hull fraction",Float)=1
        _HasHull("Show hull",Float)=1
        _Modern("Minimal flight instrument",Float)=0
    }
    SubShader { Cull Off ZWrite Off ZTest Always
        Pass {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            sampler2D _Bezel;
            float4 _Tint;
            float _Fraction,_Kind,_Clock,_Compact,_HullFraction,_HasHull,_Modern;
            float energyLine(float distance,float width){return exp(-abs(distance)/width);}
            float ring(float radius,float target,float width){return energyLine(radius-target,width);}
            float modPositive(float value,float period){return value-period*floor(value/period);}
            float segment(float2 p,float2 a,float2 b)
            {float2 d=b-a;return length(p-a-d*saturate(dot(p-a,d)/dot(d,d)));}
            float box(float2 p,float2 halfSize)
            {float2 d=abs(p)-halfSize;return length(max(d,0))+min(max(d.x,d.y),0);}

            float3 reactor(float2 p)
            {
                float r=length(p),z=sqrt(saturate(1-r*r));
                float intensity=0;
                for(int i=0;i<5;i++)
                {
                    float a=i*1.2566+_Clock*.17,c=cos(a),s=sin(a);
                    float2 q=float2(c*p.x+s*p.y,-s*p.x+c*p.y);
                    float flatten=.32+.11*sin(_Clock*.35+i);
                    float d=abs(length(float2(q.x,q.y/flatten))-.83);
                    intensity+=energyLine(d,.025)*.75+energyLine(d,.095)*.13;
                }
                float filaments=pow(saturate(.5+.5*sin(p.x*18+sin(p.y*13+_Clock)*2+z*7-_Clock*1.4)),14);
                float core=exp(-dot(p,p)*90)*3;
                float disk=smoothstep(1.04,.96,r);
                return (_Tint.rgb*(.16*z+filaments*.8+intensity*1.3+ring(r,.96,.018)*1.8)
                    +float3(1,.85,.3)*(core+intensity*.65))*disk;
            }
            float3 shield(float2 p)
            {
                float r=length(p),z=sqrt(saturate(1-r*r));
                float2 uv=float2(atan2(p.x,max(.03,z)),asin(clamp(p.y,-1,1)))*4;
                uv.x+=_Clock*.08;
                float2 cell=float2(1,1.73205);
                float2 a=uv-cell*floor(uv/cell)-cell*.5;
                float2 b=uv-cell*.5-cell*floor((uv-cell*.5)/cell)-cell*.5;
                float2 h=dot(a,a)<dot(b,b)?a:b;
                float edge=abs(max(abs(h.x),dot(abs(h),float2(.5,.866025)))-.5);
                float hex=energyLine(edge,.022),pulse=exp(-pow((r-modPositive(_Clock*.22,1.15))/.045,2))*.45;
                float rim=ring(r,.98,.018)*2.4+ring(r,.94,.05)*.4;
                return (_Tint.rgb*(.09+hex*.95+pulse+rim)+float3(.6,1,1)*pow(z,24)*.5)*smoothstep(1.04,.97,r);
            }
            float3 hull(float2 p)
            {
                float2 vertices[13]={float2(0,.95),float2(.17,.6),float2(.2,.2),
                    float2(.75,-.35),float2(.75,-.5),float2(.24,-.42),float2(.2,-.85),
                    float2(-.2,-.85),float2(-.24,-.42),float2(-.75,-.5),float2(-.75,-.35),
                    float2(-.2,.2),float2(-.17,.6)};
                float distance=5;bool inside=false;
                for(int i=0;i<13;i++)
                {
                    float2 a=vertices[i],b=vertices[(i+1)%13];
                    distance=min(distance,segment(p,a,b));
                    if((a.y>p.y)!=(b.y>p.y) && p.x<(b.x-a.x)*(p.y-a.y)/(b.y-a.y)+a.x)inside=!inside;
                }
                float grid=energyLine(sin(p.x*30),.15)+energyLine(sin(p.y*24),.15);
                float shape=energyLine(distance,.014)*1.5+(inside?(grid*.23+.05):0);
                shape+=energyLine(p.x,.012)*step(abs(p.y),.8)*.4;
                float brackets=energyLine(abs(p.x)-.92,.014)*step(abs(p.y),.65)
                    +energyLine(abs(p.y)-.65,.014)*step(abs(abs(p.x)-.92),.09);
                return _Tint.rgb*(shape*(.9+.1*sin(_Clock*1.5))+brackets*.5);
            }
            // A restrained flat face uses the same authoritative opposing-fill geometry.
            float4 modernFace(float2 p)
            {
                float r=length(p);
                float angle=modPositive(atan2(p.x,p.y)*57.29578,360);
                float combined=step(2.5,_Kind)*_HasHull;
                float hullZone=step(240,angle)*combined;
                float progress=lerp(angle/lerp(360,240,combined),(360-angle)/120,hullZone);
                float resource=lerp(_Fraction,_HullFraction,hullZone);
                float3 signal=lerp(_Tint.rgb,float3(.96,.20,.23),hullZone);
                float cell=frac(angle/6);
                float bars=step(.10,cell)*step(cell,.90)*smoothstep(.362,.366,r)*(1-smoothstep(.416,.420,r));
                float filled=step(progress,resource);
                float3 color=float3(.013,.024,.037);
                color+=signal*bars*(.12+filled*.88);
                color+=float3(.12,.24,.32)*(ring(r,.437,.0012)+ring(r,.346,.001))*.45;
                // Keep the central resource symbol quiet and leave labels to the UI layer.
                float edge=1;bool inside=false;
                float2 q=p-float2(0,.012);
                if(_Kind<.5)
                {
                    float2 vertices[6]={float2(.018,.10),float2(-.055,-.005),float2(-.005,.005),
                        float2(-.025,-.095),float2(.06,.025),float2(.007,.015)};
                    for(int n=0;n<6;n++)
                    {
                        float2 a=vertices[n],b=vertices[(n+1)%6];
                        edge=min(edge,segment(q,a,b));
                        if((a.y>q.y)!=(b.y>q.y) && q.x<(b.x-a.x)*(q.y-a.y)/(b.y-a.y)+a.x)inside=!inside;
                    }
                }
                else
                {
                    float2 vertices[5]={float2(-.061,.068),float2(.061,.068),float2(.047,-.014),
                        float2(0,-.071),float2(-.047,-.014)};
                    for(int n=0;n<5;n++)
                    {
                        float2 a=vertices[n],b=vertices[(n+1)%5];
                        edge=min(edge,segment(q,a,b));
                        if((a.y>q.y)!=(b.y>q.y) && q.x<(b.x-a.x)*(q.y-a.y)/(b.y-a.y)+a.x)inside=!inside;
                    }
                }
                color+=_Tint.rgb*((inside?.20:0)+energyLine(edge,.0025)*.48);
                return float4(color,1-smoothstep(.444,.449,r));
            }
            float4 frag(v2f_img i):SV_Target
            {
                float2 p=i.uv-.5;float r=length(p);
                if(_Modern>.5)return modernFace(p);
                float angle=modPositive(atan2(p.x,p.y)*57.29578,360);
                // Opposing fills share the top origin and meet at the 2/3 shield, 1/3 hull boundary.
                float combined=step(2.5,_Kind)*_HasHull;
                float hullZone=step(240,angle)*combined;
                float progress=lerp(angle/lerp(360,240,combined),(360-angle)/120,hullZone);
                float resource=lerp(_Fraction,_HullFraction,hullZone);
                float3 red=float3(1,.075,.045);
                float3 signal=lerp(_Tint.rgb,red,hullZone);
                float cell=frac(angle/6);
                float bars=step(.12,cell)*step(cell,.88)*step(.257,r)*step(r,.282);
                float filled=step(progress,resource);
                float3 color=float3(.006,.011,.016)+_Tint.rgb*.008;
                color+=signal*(ring(r,.299,.0012)*.4+ring(r,.243,.001)*.2);
                color+=signal*bars*(.10+filled*1.7);
                color+=signal*ring(r,.271,.012)*filled*.16;
                float2 core=p-float2(0,-lerp(.04,.065,_Compact));
                float coreRadius=lerp(.147,.115,_Compact);
                float3 energy=_Kind<.5?reactor(core/coreRadius):_Kind<1.5?shield(core/coreRadius):hull(core/lerp(.15,.125,_Compact));
                if(_Kind>2.5)
                {
                    float3 hullShape=hull(core/lerp(.15,.125,_Compact));
                    float hullLight=max(hullShape.r,max(hullShape.g,hullShape.b));
                    energy=shield(core/coreRadius)*(_HasHull>.5?.35:1)+red*hullLight*_HasHull;
                }
                color+=energy*lerp(1,.07,_Compact);
                float aperture=1-smoothstep(.306,.313,r);
                float4 bezel=tex2D(_Bezel,i.uv);
                float alpha=bezel.a+aperture*(1-bezel.a);
                color=bezel.rgb*bezel.a+color*aperture*(1-bezel.a);
                float lamps=energyLine(box(p-float2(0,.438),float2(.036,.005)),.003)
                    +energyLine(box(p+float2(0,.438),float2(.036,.005)),.003)
                    +energyLine(box(p-float2(.438,0),float2(.005,.036)),.003)
                    +energyLine(box(p+float2(.438,0),float2(.005,.036)),.003);
                color+=_Tint.rgb*lamps*(.7+.2*sin(_Clock*1.6));
                color=1-exp(-color*1.2);
                return float4(color,alpha);
            }
            ENDCG
        }
    }
}
