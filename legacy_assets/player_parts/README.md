# Original player ship parts

These files were extracted byte for byte from Pierre's installed Earth & Beyond
client. `manifest.json` records each source MIX archive, offset, size, and SHA-256.
The original W3D and DDS files are not edited.

The max-upgrade ships use `s[race]h[style]3` for the hull,
`s[race]p[style]3` for the profession section, `s[race]w[style]3` for the
wings, and `s[race]e01a` for the engine. Terran Enforcer, Jenquai Defender,
and Progen Warrior use style 2; the traders use style 1; explorers use style 3.
`ship_all.dds` is the shared texture atlas.
`SPP23-1.w3d` and `SPP23-2.w3d` are the Progen Warrior's original
91-frame arm extension and retraction clips. Their source bytes are preserved
here. The client exports the opening shoulder motion as a rigged FBX animation.
`SJP33-1/2.w3d` and `SJW33-1/2.w3d` provide the Jenquai Explorer's profession
and wing motions. The client exports both sections as rigged FBX animations.

`client/Tools/extract_enb_player_parts.py` reproduces the extraction from the
owner's MIX archives. `client/Tools/import_legacy_ships.py` converts the W3Ds to
OBJ/PNG for Unity; `client/Tools/build_original_player_ships.py` assembles the
four components in their original ship coordinates for all nine classes.
For PW, the assembler exports eight profession-arm objects separately so
Unity can move them around their native pivots.
`client/Tools/export_original_animated_parts.py` exports the native bones,
meshes, textures, and animation clips to standard FBX assets. PW uses the
opening shoulder motion; the late elbow fold would sweep the arm tips across
the nose of this assembled hull.
