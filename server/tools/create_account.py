"""Create a local account without placing its password in process arguments."""
import argparse
import getpass
import os
import subprocess

from build import ROOT, build


def main() -> None:
    """Prompt for credentials and run the account creator with named options."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('account')
    parser.add_argument('--character', required=True)
    parser.add_argument('--class', dest='class_code', default='je', choices=('te', 'tt', 'ts', 'jd', 'js', 'je', 'pw', 'pp', 'ps'))
    parser.add_argument('--native-progression', action='store_true')
    parser.add_argument('--no-build', action='store_true')
    args = parser.parse_args()
    binary = ROOT / 'target/debug' / ('beyond-server.exe' if os.name == 'nt' else 'beyond-server') if args.no_build else build()
    password = getpass.getpass('Local account password: ')
    command = [str(binary), '--create-account', args.account, '--character', args.character,
               '--class', args.class_code, '--state-dir', str(ROOT / 'state')]
    if args.native_progression:
        command.append('--native-progression')
    subprocess.run(command,
                   input=password + '\n', text=True, check=True)


if __name__ == '__main__':
    main()
