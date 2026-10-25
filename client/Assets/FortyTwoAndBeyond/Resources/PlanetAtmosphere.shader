Shader "42 and Beyond/PlanetAtmosphere"
{
    SubShader {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Pass {
            Blend SrcAlpha One
            ZWrite Off
            Cull Back
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct Input { float4 vertex:POSITION; float3 normal:NORMAL; };
            struct Output { float4 vertex:SV_POSITION; float3 normal:TEXCOORD0; float3 view:TEXCOORD1; };
            Output vert(Input i) {
                Output o;
                o.vertex = UnityObjectToClipPos(i.vertex);
                o.normal = UnityObjectToWorldNormal(i.normal);
                float3 world = mul(unity_ObjectToWorld, i.vertex).xyz;
                o.view = _WorldSpaceCameraPos - world;
                return o;
            }
            fixed4 frag(Output i):SV_Target {
                float facing = saturate(dot(normalize(i.normal), normalize(i.view)));
                float rim = pow(1 - facing, 3);
                float3 color = lerp(float3(.12,.64,.79), float3(.8,.21,.13), rim * .25);
                return fixed4(color * rim, rim * .54);
            }
            ENDCG
        }
    }
}
