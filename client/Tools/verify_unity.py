"""Build the Mac player and capture the actual Unity view using disposable server state."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import time

from play_local import find_editor, stop_owned


def main() -> None:
    """Run editor checks without reading or modifying existing player state."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--editor')
    parser.add_argument('--skip-build', action='store_true')
    parser.add_argument('--art-only', action='store_true', help='Capture the flight view without warp travel')
    parser.add_argument('--buff-only', action='store_true', help='Exercise mounted Turbo Weapons through the quick bar')
    parser.add_argument('--batchmode', action='store_true', help='Run the editor without its interactive window')
    args = parser.parse_args()
    project = Path(__file__).resolve().parents[1]
    root = project.parent
    editor = find_editor(args.editor)
    output = project / 'Builds/verification'
    output.mkdir(parents=True, exist_ok=True)
    if args.art_only and args.buff_only:
        parser.error('--art-only and --buff-only are mutually exclusive')
    captures = ('turbo-buff.png',) if args.buff_only else ('chase.png',) if args.art_only else (
        'chase.png', 'chart.png', 'asteroid.png', 'station.png', 'gate.png')
    for name in captures:
        capture = output / name
        if capture.exists():
            capture.unlink()
    if not args.skip_build:
        subprocess.run([
            str(editor), '-batchmode', '-quit', '-projectPath', str(project),
            '-executeMethod', 'FortyTwoAndBeyond.Editor.BuildClient.BuildMac', '-logFile', str(output / 'build.log'),
        ], check=True, timeout=600)
    cargo = shutil.which('cargo') or str(Path.home() / '.cargo/bin/cargo')
    subprocess.run([cargo, 'build', '--locked', '--manifest-path', str(root / 'server/Cargo.toml')], check=True)
    with tempfile.TemporaryDirectory(prefix='client-visual-') as temporary:
        state = Path(temporary)
        executable = root / 'server/target/debug/beyond-server'
        if args.buff_only:
            subprocess.run([str(executable), '--state-dir', str(state), '--seed-demo-accounts'],
                           check=True, stdout=subprocess.DEVNULL)
        for account, character, password in (
            ('local', 'SandboxPilot', 'local'), ('peer', 'Visitor', 'pass')
        ):
            subprocess.run([
                str(executable), '--state-dir', str(state), '--create-account', account,
                '--character', character,
            ], input=password + '\n', text=True, check=True, stdout=subprocess.DEVNULL)
        sector = json.loads((root / 'server/data/asteroid_belt_alpha.json').read_text())
        sector['mobs'][0]['spawn'] = [1000.0, 0.0, 0.0]
        hostile = next(mob for mob in sector['mobs'] if mob['aggressive'])
        hostile['spawn'] = [2400.0, 0.0, 0.0]
        hostile['aggressive'] = False
        hostile['aggression'] = 0
        visual_sector = state / 'visual-sector.json'
        visual_sector.write_text(json.dumps(sector))
        with (output / 'server.log').open('w') as log:
            server = subprocess.Popen([
                str(executable), '--bind', '127.0.0.1',
                '--state-dir', str(state), '--content', str(visual_sector),
            ], stdout=log, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 15
                while not (state / 'server.local.json').exists():
                    if server.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError(f'Disposable server failed to start; see {output / "server.log"}.')
                    time.sleep(.1)
                status = json.loads((state / 'server.local.json').read_text())
                if status['pid'] != server.pid:
                    raise RuntimeError('Server ownership mismatch.')
                editor_command = [str(editor)]
                if args.batchmode:
                    editor_command.append('-batchmode')
                editor_command.extend([
                    '-projectPath', str(project),
                    '--42andbeyond-buff-visual-smoke' if args.buff_only else
                    '--42andbeyond-art-smoke' if args.art_only else '--42andbeyond-visual-smoke',
                    '-logFile', str(output / 'visual.log'),
                ])
                subprocess.run(editor_command, check=True, timeout=420)
            finally:
                if server.poll() is None:
                    (state / 'stop.request').write_text('stop\n')
                stop_owned(server)
    for name in captures:
        if not (output / name).is_file():
            raise FileNotFoundError(f'Missing Unity screenshot: {name}')
    check_name = 'mounted-device quick bar' if args.buff_only else 'flight art' if args.art_only else 'Unity visual smoke'
    print(f'PASS: {check_name}. Inspect screenshots in {output}', flush=True)


if __name__ == '__main__':
    main()
