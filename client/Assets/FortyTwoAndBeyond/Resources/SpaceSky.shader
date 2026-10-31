Shader "42 and Beyond/SpaceSky"
{
    Properties {
        _NebulaTex ("Deep space panorama", 2D) = "black" {}
        _Exposure ("Sky exposure", Range(0,3)) = 1
        _AccentTex ("Local nebula panorama", 2D) = "black" {}
        _AccentExposure ("Nebula exposure", Range(0,1)) = .2
        _AccentBlend ("Nebula blend", Range(0,1)) = 0
        _VeilTex ("Near nebula veil", 2D) = "black" {}
        _VeilStrength ("Veil glow", Range(0,1)) = 0
        _Yaw ("Sector panorama rotation", Range(0,1)) = 0
        _Parallax ("View parallax", Vector) = (0,0,0,0)
    }
    SubShader {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _NebulaTex, _AccentTex, _VeilTex;
            float _Exposure, _AccentExposure, _AccentBlend, _VeilStrength, _Yaw;
            float4 _Parallax;
            struct Input { float4 vertex:POSITION; };
            struct Output { float4 vertex:SV_POSITION; float3 direction:TEXCOORD0; };
            Output vert(Input v) { Output o; o.vertex=UnityObjectToClipPos(v.vertex);
                o.direction=v.vertex.xyz; return o; }
            float starHash(float2 cell) {
                return frac(sin(dot(cell,float2(127.1,311.7)))*43758.5453);
            }
            fixed4 frag(Output i):SV_Target {
                float3 d=normalize(i.direction);
                float2 uv=float2(atan2(d.z,d.x)/6.2831853+.5,acos(clamp(d.y,-1,1))/3.1415926);
                uv.x=frac(uv.x+_Yaw);
                float seconds=_Time.y;
                float2 drift=float2(seconds*.000065,sin(seconds*.017)*.0014);
                float3 nebula=tex2D(_NebulaTex,uv+drift).rgb;
                float3 accent=tex2D(_AccentTex,uv+_Parallax.xy*.22-drift*2).rgb;
                float3 color=lerp(nebula*_Exposure,accent*_AccentExposure,_AccentBlend);
                float4 veil=tex2D(_VeilTex,uv+_Parallax.xy+drift*3);
                float veilPulse=.9+.1*sin(seconds*.11+uv.x*17+uv.y*9);
                color+=veil.rgb*veil.a*_VeilStrength*veilPulse;
                // A tilted galactic plane gives the sky a composition and deep dark foreground lanes.
                float longitude=uv.x*6.2831853;
                float plane=dot(d,normalize(float3(.04,1,.2)))+
                    sin(longitude*2+seconds*.012)*.018;
                float band=exp(-plane*plane*42);
                float broadBand=exp(-plane*plane*9);
                float3 fineCloud=tex2D(_NebulaTex,float2(uv.x*3.7+seconds*.00009,
                    uv.y*2.3-seconds*.000025)+_Parallax.xy*.6).rgb;
                float detail=dot(fineCloud,float3(.27,.51,.22));
                float dust=smoothstep(.18,.52,detail);
                float cloudLight=.62+.38*sin(longitude*3.1+detail*5+seconds*.045);
                float3 bandTint=lerp(float3(.055,.19,.36),float3(.46,.23,.09),
                    .5+.5*sin(longitude+1.3));
                color*=1-band*dust*.64;
                color+=bandTint*(band*(.18+detail*1.3)+broadBand*.06)*cloudLight;
                color+=float3(.13,.28,.39)*band*pow(saturate(1-dust),3)*.3;

                // A distant inclined spiral, with its nucleus and arms softened by the same dust field.
                float3 galaxyDirection=normalize(float3(-.34,-.1,1));
                float3 galaxyRight=normalize(cross(float3(0,1,0),galaxyDirection));
                float3 galaxyUp=cross(galaxyDirection,galaxyRight);
                float2 galaxy=float2(dot(d,galaxyRight),dot(d,galaxyUp))*float2(5.5,12);
                float galaxyRadius=length(galaxy);
                float galaxyAngle=atan2(galaxy.y,galaxy.x);
                float spiral=.5+.5*cos(galaxyAngle*2-log(max(.04,galaxyRadius))*5);
                float disk=exp(-galaxyRadius*galaxyRadius*3.5)*
                    smoothstep(0,.12,galaxyRadius)*pow(spiral,3);
                float nucleus=exp(-galaxyRadius*galaxyRadius*180);
                float facing=step(.8,dot(d,galaxyDirection));
                color+=facing*(float3(.34,.53,.85)*disk*(.8+detail*2)+
                    float3(1,.84,.62)*nucleus);
                float2 stellarDrift=float2(seconds*.00011,seconds*.000018);
                float2 grid=(uv+stellarDrift+_Parallax.xy*1.7)*float2(190,95);
                float2 cell=floor(grid);
                float seed=starHash(cell);
                float2 center=float2(starHash(cell+17.3),starHash(cell+41.7));
                float twinkle=.58+.42*sin(seconds*(.65+starHash(cell+73.1)*1.4)+seed*31);
                float glint=(1-smoothstep(.025,.12,length(frac(grid)-center))) *
                    step(.985,seed)*twinkle;
                color+=glint*lerp(float3(.36,.61,1),float3(1,.78,.56),
                    starHash(cell+9.2))*1.05;
                float2 dustGrid=(uv+stellarDrift*.32)*float2(620,310);
                float dustSeed=starHash(floor(dustGrid)+83.1);
                float dustStar=(1-smoothstep(.015,.11,length(frac(dustGrid)-.5)))*
                    step(.988,dustSeed);
                color+=dustStar*float3(.55,.68,.9)*(.24+band*.6);
                float2 nearGrid=(uv+stellarDrift*2.6+_Parallax.xy*2.4)*float2(96,48);
                float2 nearCell=floor(nearGrid);
                float nearSeed=starHash(nearCell+117.4);
                float2 nearCenter=float2(starHash(nearCell+2.3),starHash(nearCell+47.9));
                float2 nearOffset=abs(frac(nearGrid)-nearCenter);
                float nearCore=1-smoothstep(.015,.105,length(nearOffset));
                float nearRays=exp(-nearOffset.x*34)*exp(-nearOffset.y*6)+
                    exp(-nearOffset.y*34)*exp(-nearOffset.x*6);
                float nearPulse=.68+.32*sin(seconds*(.42+nearSeed)+nearSeed*29);
                color+=(nearCore*.75+nearRays*.11)*step(.992,nearSeed)*nearPulse*
                    float3(.52,.77,1);
                return float4(color,1);
            }
            ENDCG
        }
    }
}
