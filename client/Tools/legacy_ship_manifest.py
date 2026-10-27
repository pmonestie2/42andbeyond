"""Converted Earth & Beyond ship assets consumed by the Unity client."""

from __future__ import annotations


STATIC_SHIPS = (
    "TVF01A", "TVT01A", "TVE01A", "JVF01A", "JVT01A", "JVE01A",
    "PVF01A", "PVT01A", "PVE01A", "npc01", "npc02", "PVS02",
    "PVS01", "PPVF01A", "PVS07", "PVS12", "PVS09", "PVS13",
    "PTVF01A", "MOB_03", "MOB_02", "PVS06",
)

MAX_UPGRADE_SHIPS = {
    "OriginalTE": ("sth23", "stp23", "stw23", "ste01a"),
    "OriginalTT": ("sth13", "stp13", "stw13", "ste01a"),
    "OriginalTS": ("sth33", "stp33", "stw33", "ste01a"),
    "OriginalJD": ("sjh23", "sjp23", "sjw23", "sje01a"),
    "OriginalJS": ("sjh13", "sjp13", "sjw13", "sje01a"),
    "OriginalJE": ("sjh33", "sjp33", "sjw33", "sje01a"),
    "OriginalPW": ("sph23", "spp23", "spw23", "spe01a"),
    "OriginalPP": ("sph13", "spp13", "spw13", "spe01a"),
    "OriginalPS": ("sph33", "spp33", "spw33", "spe01a"),
}


def source_stems() -> tuple[str, ...]:
    """Returns:
        tuple[str, ...]: Original W3D models needed to regenerate converted ships.
    """
    components = {stem for parts in MAX_UPGRADE_SHIPS.values() for stem in parts}
    return STATIC_SHIPS + tuple(sorted(components))
