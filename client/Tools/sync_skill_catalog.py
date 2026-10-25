"""Copy public skill names from the Rust class catalog into a Unity resource."""

import json
from pathlib import Path


def main() -> None:
    """Write one stable display label per server skill ID."""
    root = Path(__file__).resolve().parents[2]
    classes = json.loads((root / "server/data/class_skills.json").read_text())
    names: dict[int, str] = {}
    for character_class in classes:
        for skill in character_class["skills"]:
            identity, name = skill["id"], skill["name"]
            if identity in names and names[identity] != name:
                raise ValueError(f"Conflicting public skill name for {identity}")
            names[identity] = name
    target = root / "client/Assets/FortyTwoAndBeyond/Resources/SkillCatalog.txt"
    target.write_text("".join(f"{identity}\t{names[identity]}\n" for identity in sorted(names)))


if __name__ == "__main__":
    main()
