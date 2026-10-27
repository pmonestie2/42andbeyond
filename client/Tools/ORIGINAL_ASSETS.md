# Original ship import

The original W3D and DDS files remain unchanged in `legacy_assets/`. The
source manifests pin their SHA-256 hashes. Blender converts geometry and
textures; Unity imports the results from `client/Assets/FortyTwoAndBeyond/Resources/LegacyShips/`.

Use the OpenSAGE Blender add-on at commit
`53d77d38bb39b6aa2414ebfe092fc2a1c3cee1db` (the version used for this
conversion). Regenerate the declared ships on this Mac:

```sh
python3 client/Tools/import_original_ship_assets.py \
  --addon-root /path/to/OpenSAGE.BlenderPlugin
```

The command validates the original hashes, builds in a temporary directory,
checks every OBJ material texture, publishes changed files while preserving
Unity `.meta` GUIDs, and runs the Unity import audit. It does not start a server
or the installed game. For a quick audit without conversion:

```sh
python3 client/Tools/import_original_ship_assets.py --check-only
```

There are 70 converted OBJ models: the original static ships, modular parts,
nine complete max-upgrade player hulls, the JE static base, and the eight PW
arm segments retained for inspection. The active PW arm rig, JE profession,
and JE wings are FBX assets with embedded textures and imported animation
clips. Unity assembles the animated sections with their static hulls at runtime.

To inspect motion in Blender, open `client/Builds/PWAnimatedPreview.blend` or
`client/Builds/JEAnimatedPreview.blend` and play the timeline with Space. The
FBX files are `PWArmsAnimated.fbx`, `JEProfessionAnimated.fbx`, and
`JEWingsAnimated.fbx`; use **File → Import → FBX** to inspect their rigs and clips.

## Original effect sprites

Run `python3 client/Tools/import_legacy_effect_textures.py` to verify the
archived DDS hashes and regenerate 45 Unity PNGs in `Resources/LegacyEffects`.
`--check-only` compares their decoded pixels against the original files.
The original glow and exhaust sprites now supply fireball, smoke, and missile
exhaust textures. The original Rally damage and firing W3D rigs are converted
to animated FBX by `export_legacy_skill_rigs.py`; `NativeRallyVisual` uses their
original texture stages. The five native Combat Trance descriptors use the
original `sk_comtra02` sprite and the emitter's archived lifetime, rate, and
size. Its Unity circle shape is an approximation of the native emitter.
Environment Shield uses its original persistent `shld_env01a` animated W3D
sphere and the archived `shld_env01` and `JVE0108` texture stages. The onset
`shld_env01b` is also converted for later phase sequencing. Its size is fitted
to the Unity ship. Psionic Shield uses its `shld_psy02a` W3D rig and four
original texture stages.
Hacking uses its original `sk_hack02` delivery sprite with the native eight-tile
and vertical scroll settings. The beam endpoints follow the authoritative target
effect while that effect exists.
Menace uses the original `sk_menc01a` skull rig and `sk_menace01` texture over
the affected mob. The original Scare aggregate references a missing native
loop clip, so its skull geometry is shared with Intimidate and tinted for the
higher tier.
Befriend uses the original `FANTA01` sprite and the archived W3D emitter's
one-second particle life and 40-per-second emission on the affected mob.
Self Recharge Shields and Hull Patch use the original `shld_reg07c` animated
flash and three archived texture stages. The server retains descriptor 136
for its native 0.9-second lifetime so the client can render it.
Shield Inversion uses the original sustained `shld_inv01d` lightning rig and
its archived textures, aimed at the target selected when the effect starts.
Self Destruct shows the original `sk_sdest01` numbered atlas for its three
seconds after discharge; the server now removes descriptor 206 when that
window ends.
Fold Space vanishing and materialization use the original `sk_fold01` sprite
atlas and three source emitter records for rate, life, and size. The native
aggregate's child placement is approximated around the server-owned ship.
Create Wormhole uses the original `tsg0206` and `aline` sprites with the three
native emitter timing records. A Unity ring gives their sparse particle field
a visible portal mouth during the server's cast effect.

Regenerate one animated skill rig with Blender and the same pinned add-on:

```sh
blender -b --factory-startup --python client/Tools/export_legacy_skill_rigs.py -- \
  --addon-root /path/to/OpenSAGE.BlenderPlugin --part psionic
```

The supported parts are `damage`, `firing`, `environment`,
`environment_loop`, `psionic`, `menace`, `recharge`, and `inversion`.

Original `effect.ini`, `basset.ini`, `rs.ini`, W3D models, DDS textures, and
their hash manifest are under `legacy_assets/effects/`. To recover additional
skill definitions, models, and textures, see `legacy_assets/effects/other/`
and `legacy_assets/effects/defensive/`.
To recover additional
skill assets from the original Windows installation, run
`extract_legacy_effect_assets.py` with its `--ini-dir`, `--mix-dir`,
`--output`, and `--effect-id` arguments on that machine. The extracted sources
remain byte for byte unchanged; converted Unity assets are separate.

The explosion timing, geometry, and particle emission still come from Unity
code. The native Rally PassEffect tiers and Combat Trance armature motion also
need their effect-specific formats decoded before they can be reproduced
exactly.
The full static OBJ gallery is `client/Builds/ConvertedShipGallery.blend`.

## Original enemy models

`extract_legacy_enemy_ships.py` reads `server/data/universe.json` directly and
resolves all 139 enemy asset IDs through the original `basset.ini` and `rs.ini`.
Run it on Windows with the original client installed:

```powershell
python client/Tools/extract_legacy_enemy_ships.py --catalog server/data/universe.json --installation 'C:\Program Files (x86)\EA GAMES\Earth & Beyond' --output legacy_assets/enemy_ships/native_enemy_ships.zip
```

Unpack the models, textures, definitions, and manifest into
`legacy_assets/enemy_ships/`, preserving the ZIP's directories. Source file
hashes and unavailable references are recorded in the manifest. Convert on
macOS using Blender and the OpenSAGE add-on (texture staging uses `sips`):

```sh
blender -b --factory-startup --python client/Tools/import_legacy_enemy_ships.py -- \
  --source legacy_assets/enemy_ships \
  --output client/Assets/FortyTwoAndBeyond/Resources/LegacyEnemies \
  --addon-root /path/to/OpenSAGE.BlenderPlugin
```

The converter follows aggregate base models, attached hull pieces, skeleton
rest poses, and external HLOD objects. It selects the highest visible LOD and
exports OBJ/MTL meshes with native PNG textures. Animation and native particle
sources remain archived; these enemy hull exports are static. `Catalog.txt`
maps server IDs to resources, loaded on demand by `SpaceArt.Mob`. Unity's
`EnemyMaterialImport` binds the exact MTL textures and prevents unrelated
models with the same material names from sharing the wrong material.

`EnemyImportAudit.VerifyAndBuildMac` verifies all 139 model mappings and builds
the client. There are 138 distinct model resources; 138 IDs have diffuse
textures. The remaining swarm's mesh material is authored without a diffuse
map, while its native emitter textures remain in the source archive.
