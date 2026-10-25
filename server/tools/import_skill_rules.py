"""Export archived class skill training gates without changing the C++ source."""

from __future__ import annotations

import argparse
import json
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ARCHIVED = ROOT.parent / "reference/cpp/trunk/database/Skills.xml"
CLASSES = (
    "Enforcer", "Tradesman", "Scout", "Defender", "Seeker", "Explorer",
    "Warrior", "Privateer", "Sentinel",
)


def export_rules(source: Path, catalog: Path, output: Path) -> None:
    """Export only catalog entries whose requirement has archived evidence."""
    root = ET.fromstring("<Root>" + source.read_text() + "</Root>")
    entries = {
        (int(skill.attrib["ID"]), child.tag): (skill.attrib, child.attrib)
        for skill in root
        for child in skill
    }
    classes = json.loads(catalog.read_text())
    rules = []
    for record in classes:
        index = record["class_index"]
        for skill in record["skills"]:
            pair = entries.get((skill["id"], CLASSES[index]))
            if pair is None:
                continue
            common, specific = pair
            if common["Category"] not in ("Total", "Combat", "Trade", "Explore"):
                raise ValueError("Unknown archived skill requirement track")
            if specific["LvlType"] not in ("PW", "SW", "PT", "ST", "NON"):
                raise ValueError("Unknown archived skill level scheme")
            rules.append({
                "class_index": index,
                "skill": skill["id"],
                "track": common["Category"].lower(),
                "scheme": specific["LvlType"],
                "quested": bool(int(specific["Quest"])),
                "learn_level": int(specific["LearnLvl"]),
            })
    output.write_text(json.dumps(rules, indent=2) + "\n")


def main() -> None:
    """Parse command-line paths and write the deterministic rule catalog."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=ARCHIVED)
    parser.add_argument("--catalog", type=Path, default=ROOT / "data/class_skills.json")
    parser.add_argument("--output", type=Path, default=ROOT / "data/skill_rules.json")
    args = parser.parse_args()
    export_rules(args.source, args.catalog, args.output)


if __name__ == "__main__":
    main()
