"""Bundle the public item definitions for offline client search and inspection."""
from __future__ import annotations
import argparse
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
LABELS = {1: 'Ammunition', 4: 'Combat level required', 5: 'Weapon damage', 6: 'Damage type',
          7: 'Effect range (m)', 8: 'Effect radius (m)', 9: 'Energy use', 10: 'Energy drain / sec',
          11: 'Skill required', 12: 'Explore level required', 13: 'Requirement', 14: 'Overall level required',
          15: 'Missile maneuverability', 16: 'Reactor capacity', 19: 'Weapon range (m)',
          20: 'Reactor recharge / sec', 21: 'Reload (sec)', 22: 'Rounds per shot',
          23: 'Shield use', 24: 'Shield capacity', 25: 'Shield drain / sec', 26: 'Shield recharge / sec',
          27: 'Other requirement', 28: 'Requirement level', 29: 'Engine signature', 31: 'Impulse speed',
          32: 'Trade level required', 33: 'Warp speed', 34: 'Free-warp energy / sec'}


def effect_text(effect: dict) -> str:
    """Returns:
        str: An effect description with its catalog magnitudes substituted in order.
    """
    values = iter(effect['values'])
    def substitute(match: re.Match) -> str:
        """Returns:
            str: The next formatted effect magnitude, or an unresolved marker.
        """
        value = next(values, None)
        return '?' if value is None else f'{value:.{min(3, int(match[1]))}f}'
    text = re.sub(r'%value\d+\.(\d)f%', substitute, effect['tooltip']).replace('\\n', '\n')
    return effect['description'] + ('\n' + text if text else '')


def main() -> None:
    """Generate a deterministic resource, or verify that it matches the public catalog."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    rows = []
    for item in sorted(json.loads((ROOT / 'server/data/items.json').read_text()), key=lambda row: (row['name'].lower(), row['id'])):
        stats = []
        for field in item['fields']:
            index, value = field['index'], field['value']['value']
            if index not in LABELS or (value == 0 and index != 6) or value == '':
                continue
            if index == 6:
                value = ['Impact', 'Explosive', 'Plasma', 'Energy', 'EMP', 'Chemical'][int(value)] if 0 <= int(value) < 6 else str(value)
            elif isinstance(value, float):
                value = f'{value:,.2f}'.rstrip('0').rstrip('.')
            stats.append(f'{LABELS[index]}: {value}')
        rows.append(dict(id=item['id'], name=item['name'], type=item['item_type'], icon=item['icon'],
                         level=item['level'], price=item['price'], stack=item['max_stack'],
                         description=item['description'].replace('\\n', '\n'), manufacturer=item['manufacturer'],
                         stats='\n'.join(stats), effects='\n\n'.join(effect_text(effect) for effect in item['activated'] + item['equipped'])))
    encoded = json.dumps({'items': rows}, ensure_ascii=True, separators=(',', ':')) + '\n'
    target = ROOT / 'client/Assets/FortyTwoAndBeyond/Resources/ItemDirectory.bytes'
    if args.check:
        if not target.is_file() or target.read_text() != encoded:
            raise SystemExit('Item directory needs regeneration.')
    else:
        target.write_text(encoded)
    print(f'{len(rows)} local item definitions: {target}')


if __name__ == '__main__':
    main()
