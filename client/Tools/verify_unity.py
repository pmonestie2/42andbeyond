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
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--art-only', action='store_true', help='Capture the flight view without warp travel')
    mode.add_argument('--buff-only', action='store_true', help='Exercise mounted Turbo Weapons through the quick bar')
    mode.add_argument('--ui-only', action='store_true', help='Capture the tactical target and equipment panels')
    mode.add_argument('--follow-only', action='store_true', help='Verify server-directed ship heading')
    mode.add_argument('--warp-only', action='store_true', help='Capture the warped flight view and motion')
    mode.add_argument('--rally-only', action='store_true', help='Capture original Rally rigs in a disposable flight scene')
    mode.add_argument('--invite-only', action='store_true', help='Accept a peer invitation through the retained popup')
    parser.add_argument('--batchmode', action='store_true', help='Run the editor without its interactive window')
    parser.add_argument('--character-class', choices=('te', 'tt', 'ts', 'jd', 'js', 'je',
                                                       'pw', 'pp', 'ps'), default='te',
                        help='Class of the disposable local pilot.')
    parser.add_argument('--rally-ability', type=int, choices=(35, 46, 116), default=46,
                        help='Rally tactic to activate in the disposable visual check.')
    parser.add_argument('--isolated-art', action='store_true',
                        help='Capture the player hull without a second ship beside it.')
    args = parser.parse_args()
    if args.isolated_art and not args.art_only:
        parser.error('--isolated-art requires --art-only')
    project = Path(__file__).resolve().parents[1]
    root = project.parent
    editor = find_editor(args.editor)
    output = project / 'Builds/verification'
    output.mkdir(parents=True, exist_ok=True)
    captures = ('group-invite.png',) if args.invite_only else ('rally-native.png', 'rally-oblique.png') if args.rally_only else ('warp-motion.png',) if args.warp_only else (
        'follow-turn.png', 'follow.png', 'follow-object.png') if args.follow_only else (
        'mob-target.png', 'equipment.png', 'ui-chart.png') if args.ui_only else (
        'turbo-buff.png',) if args.buff_only else ('chase.png',) if args.art_only else (
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
        if args.buff_only or args.rally_only:
            subprocess.run([str(executable), '--state-dir', str(state), '--seed-demo-accounts'],
                           check=True, stdout=subprocess.DEVNULL)
        for account, character, password in (
            ('local', 'SandboxPilot', 'local'), ('peer', 'Visitor', 'pass')
        ):
            subprocess.run([
                str(executable), '--state-dir', str(state), '--create-account', account,
                '--character', character,
                *(['--class', args.character_class] if account == 'local' else []),
            ], input=password + '\n', text=True, check=True, stdout=subprocess.DEVNULL)
        sector = json.loads((root / 'server/data/asteroid_belt_alpha.json').read_text())
        sector['mobs'][0]['spawn'] = [0.0, 3000.0, 0.0] if args.follow_only else [1000.0, 0.0, 0.0]
        if args.follow_only:
            beacon = next(point for point in sector['navpoints'] if point['name'] == 'Asteroid AA-244')
            beacon['name'] = 'Follow Test Beacon'
            beacon['position'] = [1800.0, 0.0, 0.0]
            beacon['radius'] = 80.0
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
                editor_command.extend(['-projectPath', str(project),
                    '--42andbeyond-invite-smoke' if args.invite_only else
                    '--42andbeyond-buff-visual-smoke' if args.buff_only else
                    '--42andbeyond-follow-smoke' if args.follow_only else
                    '--42andbeyond-rally-art-smoke' if args.rally_only else
                    '--42andbeyond-warp-motion-smoke' if args.warp_only else
                    '--42andbeyond-art-smoke' if args.art_only else '--42andbeyond-visual-smoke'])
                if args.ui_only:
                    editor_command.append('--42andbeyond-ui-smoke')
                if args.isolated_art:
                    editor_command.append('--42andbeyond-isolated-art')
                if args.rally_only:
                    editor_command.extend(['--42andbeyond-rally-ability', str(args.rally_ability)])
                editor_command.extend(['-logFile', str(output / 'visual.log')])
                subprocess.run(editor_command, check=True, timeout=420)
            finally:
                if server.poll() is None:
                    (state / 'stop.request').write_text('stop\n')
                stop_owned(server)
    for name in captures:
        if not (output / name).is_file():
            raise FileNotFoundError(f'Missing Unity screenshot: {name}')
    check_name = ('peer invitation popup and acceptance' if args.invite_only else
                  'clean Rally effect in chase and oblique views' if args.rally_only else
                  'warp motion and distortion' if args.warp_only else
                  'follow heading' if args.follow_only else
                  'tactical HUD' if args.ui_only else
                  'mounted-device quick bar' if args.buff_only else
                  'flight art' if args.art_only else 'Unity visual smoke')
    print(f'PASS: {check_name}. Inspect screenshots in {output}', flush=True)


if __name__ == '__main__':
    main()
