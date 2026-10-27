Shader "42 and Beyond/BlackHoleCore"
{
    SubShader {
        Tags { "RenderType"="Opaque" }
        Pass {
            ZWrite On
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct Input { float4 vertex:POSITION; float3 normal:NORMAL; };
            struct Output { float4 vertex:SV_POSITION; float3 normal:TEXCOORD0; float3 view:TEXCOORD1; };
            Output vert(Input i) {
                Output o;
                o.vertex=UnityObjectToClipPos(i.vertex);
                float3 world=mul(unity_ObjectToWorld,i.vertex).xyz;
                o.normal=UnityObjectToWorldNormal(i.normal);
                o.view=_WorldSpaceCameraPos-world;
                return o;
            }
            fixed4 frag(Output i):SV_Target {
                float rim=pow(1-saturate(dot(normalize(i.normal),normalize(i.view))),3);
                return fixed4(float3(.001,.002,.006)+rim*float3(.24,.55,.86),1);
            }
            ENDCG
        }
    }
}
