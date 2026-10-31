"""Port the MIT Cosmos Museum procedural Sun's noise and corona to Unity's renderer."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess


def import_sun(source: Path, project: Path) -> None:
    """Extract the inspected shader math and bundle its original source and attribution.

    Raises:
        ValueError: The upstream shader functions no longer have the expected boundaries.
    """
    html = (source / 'sun.html').read_text()
    match = re.search(r'(vec4 hash4\(.*?)(?=vec4 noiseSpace\()', html, re.S)
    if match is None:
        raise ValueError('Upstream solar shader function layout changed.')
    math = match.group(1)
    for before, after in [('vec4', 'float4'), ('vec3', 'float3'), ('vec2', 'float2'),
                          ('mat3', 'float3x3'), ('fract(', 'frac('), ('mix(', 'lerp(')]:
        math = math.replace(before, after)
    math = re.sub(r'float([234])\(([-+]?\d+(?:\.\d+)?)\)',
                  lambda m: 'float' + m[1] + '(' + ','.join([m[2]] * int(m[1])) + ')', math)
    math = math.replace('r1=r1*mr;', 'r1=mul(r1,mr);').replace('pr*=mr;', 'pr=mul(pr,mr);')
    destination = project / 'Assets/FortyTwoAndBeyond/Resources/SpaceEnvironment/Shaders'
    provenance = project / 'Art/SpaceEnvironment/StellarSun'
    notices = project / 'Assets/StreamingAssets/SpaceEnvironmentNotices'
    provenance.mkdir(parents=True, exist_ok=True)
    notices.mkdir(parents=True, exist_ok=True)
    for filename in ['sun.html', 'README.md', 'LICENSE']:
        shutil.copy2(source / filename, provenance / filename)
    shutil.copy2(source / 'LICENSE', notices / 'StellarSun-LICENSE.txt')
    (destination / 'StellarSun.cginc').write_text('''// Unity HLSL port of CosmosMuseum/cosmos-museum-sun (MIT).
// Copyright (c) 2026 Sebastián Vasquez. Original shader: Panteleymonov Aleksandr.
// Original source and full notice are retained in Art/SpaceEnvironment/StellarSun.
#ifndef FORTYTWO_STELLAR_SUN_INCLUDED
#define FORTYTWO_STELLAR_SUN_INCLUDED
''' + math + '\n#endif\n')
    source_data = {
        'source': 'https://github.com/CosmosMuseum/cosmos-museum-sun',
        'commit': subprocess.check_output(['git', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip(),
        'license': 'MIT',
        'credit': 'Cosmos Museum / Sebastián Vasquez; original procedural shader by Panteleymonov Aleksandr (2015–2016).',
        'sha256': hashlib.sha256(html.encode()).hexdigest(),
        'adaptations': 'GLSL to HLSL; actual 3D opaque photosphere plus separately blended corona, warm palette, scene lighting and GPU time uniforms. No web UI or background renderer imported.',
    }
    for target in [provenance / 'SOURCE.json', notices / 'StellarSun-SOURCE.json']:
        target.write_text(json.dumps(source_data, indent=2) + '\n')


def main() -> None:
    """Import from an explicit local checkout; game builds need no external checkout."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', required=True, type=Path)
    args = parser.parse_args()
    import_sun(args.source.resolve(), Path(__file__).resolve().parents[1])
    print('Imported procedural Sun shader math, original source and MIT notices.')


if __name__ == '__main__':
    main()
