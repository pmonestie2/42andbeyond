"""Import the standalone space demo's baked assets with built-in-renderer shader adapters."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil


def copy_asset(source: Path, destination: Path) -> None:
    """Copy an asset and its GUID metadata without changing the source project."""
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, destination)
    metadata = source.with_name(source.name + '.meta')
    if metadata.is_file():
        shutil.copy2(metadata, destination.with_name(destination.name + '.meta'))


def adapt_shader(text: str) -> str:
    """Returns:
        str: The same shader algorithms using built-in pipeline transforms and sampling.
    """
    text = text.replace('"RenderPipeline"="UniversalPipeline"', '')
    text = text.replace('"RenderPipeline" = "UniversalPipeline"', '')
    text = text.replace('HLSLPROGRAM', 'CGPROGRAM').replace('ENDHLSL', 'ENDCG')
    text = text.replace('"Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"', '"BuiltInSpace.cginc"')
    text = text.replace('CBUFFER_START(UnityPerMaterial)', '').replace('CBUFFER_END', '')
    # Avoid subpixel granulation turning into a checkerboard on the small flight-view Sun.
    text = text.replace('float limb=.45', 'float grainVisibility=1-smoothstep(.001,.004,max(length(ddx(uv)),length(ddy(uv))));\n float limb=.45')
    text = text.replace('(1+.16*cells)*limb', '(1+.16*cells*grainVisibility)*limb')
    if 'Shader "BlackHole/Gargantua"' in text or 'Shader "BlackHole/StarfieldSkybox"' in text:
        text = text.replace('    Properties\n    {', '    Properties\n    {\n        _DeepStars("NASA HDR star map", 2D) = "black" {}\n        _NebulaTex("Flowing nebula", 2D) = "black" {}\n        _DeepStarExposure("Star map exposure", Float) = 2.6')
    if 'Shader "BlackHole/Gargantua"' in text or 'Shader "BlackHole/StarfieldSkybox"' in text:
        text=text.replace('_DeepStarExposure("Star map exposure", Float) = 2.6', '_DeepStarExposure("Star map exposure", Float) = 2.6\n        _SectorYaw("Sector orientation", Float) = 0\n        _SectorTint("Sector palette", Color) = (1,1,1,1)')
    if 'Shader "BlackHole/StarfieldSkybox"' in text:
        text = text.replace('            ZWrite Off', '            ZWrite Off\n            ZTest LEqual')
        text = text.replace('output.positionCS = TransformObjectToHClip(input.positionOS.xyz);', '''output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                #if defined(UNITY_REVERSED_Z)
                output.positionCS.z = 0;
                #else
                output.positionCS.z = output.positionCS.w;
                #endif''')
    if 'Shader "SpaceEnvironmentDemo/Planet Surface"' in text:
        text = text.replace(' Pass {', ' Pass {\n Blend Off ZWrite On ZTest LEqual Cull Back')
        text = text.replace('_BaseMap("Planet albedo"', '_TerrainMap("Terrain detail",2D)="black"{}\n  _TerrainBlend("Terrain blend",Float)=0\n  _TerrainSeed("Terrain seed",Float)=0\n  _BaseMap("Planet albedo"')
        text = text.replace('float3 col=tex*_Tint.rgb*', 'tex=ComposePlanetSurface(i.uv,tex);\n float3 col=tex*_Tint.rgb*')
        text = text.replace('_BaseMap("Planet albedo"', '_BodyFlow("Atmospheric flow", Float)=0\n  _BaseMap("Planet albedo"')
        text = text.replace('#include "BuiltInSpace.cginc"', '#include "BuiltInSpace.cginc"\n #include "SpaceMotion.cginc"')
        text = text.replace('float nl=dot(n,l); float3 tex=', 'i.uv=AnimatePlanetUV(i.uv);\n float nl=dot(n,l); float3 tex=')
    if 'Shader "SpaceEnvironmentDemo/Cloud Layer"' in text:
        text = text.replace('#include "BuiltInSpace.cginc"', '#include "BuiltInSpace.cginc"\n #include "SpaceMotion.cginc"')
        text = text.replace('float c=SAMPLE_TEXTURE2D', 'i.uv=AnimateCloudUV(i.uv);float c=SAMPLE_TEXTURE2D')
        text = text.replace('(.035+1.6*lit)', '(.15+1.6*lit)')
    if 'Shader "SpaceEnvironmentDemo/Atmosphere"' in text:
        text = text.replace(')),4.5)', ')),3.8)').replace('))+.18)', '))+.32)')
    if 'Shader "SpaceEnvironmentDemo/Saturn Rings"' in text:
        text = text.replace('float4 c=SAMPLE_TEXTURE2D', '''float3 towardCamera=normalize(_WorldSpaceCameraPos-i.w);
 float3 planetOffset=_PlanetCenter.xyz-i.w;
 float front=dot(planetOffset,towardCamera);
 if(front>0 && dot(planetOffset,planetOffset)-front*front<_PlanetRadius*_PlanetRadius)discard;
 float4 c=SAMPLE_TEXTURE2D''')
    if 'Shader "SpaceEnvironmentDemo/Solar Corona"' in text:
        text = text.replace('_Time.y*.13', '_Time.y*.45')
    return text


def import_demo(source: Path, project: Path) -> dict[str, object]:
    """Import owned copies and preserve original shader sources and attribution.

    Returns:
        dict[str, object]: Source and adapted file hashes for the imported assets.

    Raises:
        FileNotFoundError: The completed standalone demo is missing.
    """
    if not (source / 'Scenes/SpaceEnvironmentDemo.unity').is_file():
        raise FileNotFoundError(f'Build the standalone demo first: {source}')
    destination = project / 'Assets/FortyTwoAndBeyond/Resources/SpaceEnvironment'
    provenance = project / 'Art/SpaceEnvironment'
    provenance.mkdir(parents=True, exist_ok=True)
    imported: dict[str, str] = {}
    for folder, pattern in (('Textures', '*'), ('Generated', '*.mat'), ('Generated', '*.asset')):
        for asset in sorted((source / folder).glob(pattern)):
            if not asset.is_file() or asset.suffix == '.meta' or asset.name.startswith('AsteroidShape'):
                continue
            if asset.suffix == '.asset' and asset.name == 'SpaceEnvironmentVolume.asset':
                continue
            copy_asset(asset, destination / folder / asset.name)
            imported[str(asset.relative_to(source))] = hashlib.sha256(asset.read_bytes()).hexdigest()
    upstream = source / 'ThirdParty/URPBlackHole'
    for folder in ('Materials', 'Prefabs'):
        for asset in (upstream / folder).iterdir():
            if asset.suffix != '.meta':
                copy_asset(asset, destination / 'BlackHole' / folder / asset.name)
    shader_sources = list((source / 'Shaders').glob('*.shader')) + list((upstream / 'Shaders').glob('*.shader'))
    for shader in shader_sources:
        target = destination / 'Shaders' / shader.name
        copy_asset(shader, target)
        target.write_text(adapt_shader(shader.read_text()))
        original = provenance / 'OriginalShaders' / shader.name
        original.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(shader, original)
        imported[str(shader.relative_to(source))] = hashlib.sha256(shader.read_bytes()).hexdigest()
    copy_asset(upstream / 'Shaders/ProceduralSky.hlsl', destination / 'Shaders/ProceduralSky.hlsl')
    # Drift the sky identically in the black-hole raymarcher and the regular skybox.
    sky = destination / 'Shaders/ProceduralSky.hlsl'
    original_sky = sky.read_text()
    sky.write_text('// Game sky shared by the ordinary background and geodesic lensing.\n#include "DynamicSky.cginc"\n')
    (provenance / 'OriginalShaders/ProceduralSky.hlsl').write_text(original_sky)
    notices = project / 'Assets/StreamingAssets/SpaceEnvironmentNotices'
    notices.mkdir(parents=True, exist_ok=True)
    for folder in ('URPBlackHole', 'ProceduralPlanets'):
        shutil.copy2(source / 'ThirdParty' / folder / 'LICENSE.txt', provenance / (folder + '-LICENSE.txt'))
        shutil.copy2(source / 'ThirdParty' / folder / 'LICENSE.txt', notices / (folder + '-LICENSE.txt'))
    shutil.copy2(source / 'ThirdParty/ASSET_SOURCES.json', provenance / 'ASSET_SOURCES.json')
    shutil.copy2(source / 'ThirdParty/ASSET_SOURCES.json', notices / 'ASSET_SOURCES.json')
    (notices / 'Attribution.txt').write_text('Solar System Scope textures by INOVE, based on NASA imagery. CC BY 4.0: https://creativecommons.org/licenses/by/4.0/ — source https://www.solarsystemscope.com/textures/ . Maps retained; render import capped to 4K except rings.\nURP Black Hole by tantaneity and Procedural Planets by Sebastian Lague: MIT, see included license notices. Shaders adapted for the built-in renderer; procedural meshes baked from the generator.\n')
    nasa_notice = provenance / 'NASADeepStars-SOURCE.json'
    if nasa_notice.is_file():
        shutil.copy2(nasa_notice, notices / nasa_notice.name)
        with (notices / 'Attribution.txt').open('a') as stream:
            stream.write('NASA Deep Star Maps 2020: NASA/Goddard Space Flight Center Scientific Visualization Studio; Ernie Wright (USRA); Gaia DR2: ESA/Gaia/DPAC. https://svs.gsfc.nasa.gov/4851/ . No endorsement implied.\n')
    adapter = destination / 'Shaders/BuiltInSpace.cginc' 
    adapter.write_text('''// Renderer adapter only; the imported shading and raymarch algorithms are retained.
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
''')
    manifest: dict[str, object] = {'sourceFiles': imported, 'adaptation': 'Core.hlsl transforms, texture sampling and material CBUFFER replaced with built-in equivalents. Shared dynamic sky layers NASA HDR stars, denser filtered procedural stars and flowing nebulae; gas/cloud layers have differential flow and solar granulation fades below pixel resolution. Skybox depth pinned to the far plane, planet surfaces explicitly opaque and Saturn ring occlusion geometric. Meshes, texture maps, prefab and source notices preserved. No demo camera or URP components imported.'}
    (provenance / 'IMPORT.json').write_text(json.dumps(manifest, indent=2) + '\n')
    return manifest


def main() -> None:
    """Import only the explicitly selected demo assets into the game project."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=Path.home() / 'Documents/42unity/Unity42AndBeyond/Assets/SpaceEnvironmentDemo')
    args = parser.parse_args()
    result = import_demo(args.source.expanduser().resolve(), Path(__file__).resolve().parents[1])
    print(f"Imported {len(result['sourceFiles'])} source assets and renderer adapters.")


if __name__ == '__main__':
    main()
