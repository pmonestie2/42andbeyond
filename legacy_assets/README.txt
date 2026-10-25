Earth & Beyond original ship assets

410 W3D models, 292 textures, 1 additional archive variants.
models/: Original unconverted ship hulls, modular parts and model dependencies.
textures/: Referenced original DDS/TGA textures.
variants/: Differing archive payloads preserved unchanged.
ships.csv: Native asset IDs, ship descriptions and model names.
manifest.json: Source archive offsets, SHA-256 hashes, and references absent from this installation.

Game files were read without modification. These are native assets, not OBJ/FBX conversions.
Texture references are read from W3D texture-name chunks; .dds equivalents of named .tga files are included.
Default files prefer loose art, then highest-numbered archives; differing versions are preserved. Runtime override order is not asserted.
