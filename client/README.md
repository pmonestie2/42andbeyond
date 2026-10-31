# 42 and Beyond: Unity Client

The HUD bundles IBM Plex Sans Regular, Medium, and SemiBold from
[IBM's Plex repository](https://github.com/IBM/plex). The font files are licensed
under the SIL Open Font License; see
`Assets/FortyTwoAndBeyond/Resources/Fonts/OFL.txt`.

A desktop space client for the existing local Rust server, with a solid 3D ship,
procedural asteroids, stations, gates, a docking bay, a distant ringed planet,
and a sector chart.
Nearby players appear as solid replacement ships with their toon names and interpolated
movement. The server controls their visibility, including range and cloaking; ships
are removed when they leave visibility or log out. Mobs have distinct procedural models,
server names, interpolated movement, hostility markers, shields and hull readouts.
Positions and travel come from the server. Models are original procedural
placeholders; original game art and Net7Proxy are not required.

## Open and run

On the development Mac, Unity 6.3.13f1, Unity Hub, and Rosetta 2 are installed,
and the standalone application can be built from this project. Double-click **Play 42 and Beyond.command**
to connect to the Windows VM server. The launcher installs the current build in
`~/Applications/42 and Beyond.app` and writes its game log under
`~/Library/Logs/42 and Beyond/`, so the game does not request access to the
project's Documents folder. Quit the game before installing a newly built version.
The window can be resized; the HUD scales at small sizes.

Use **Controls → OPEN NEW CLIENT** to open another window of the same installed
game. Each window starts at the login screen and writes a separate player log
under `~/Library/Logs/42 and Beyond/`. Choose a different account in each
window; the server permits one active session per character. The button can be
used again from any window to add more clients. Close all windows before
installing a new build.

Double-click **Play Local.command** in this directory to start the game and its own server together. It automatically
connects the demo character, keeps its state in ignored `.local-state/`, and stops
the owned server when the game closes. It never stops an unrelated server.
Launching it again brings the existing game forward instead of starting another
server. A project-local operating-system lock prevents simultaneous launches
from racing; it releases automatically even if a launcher exits unexpectedly.
Close the running game before using `--rebuild` or changing maps. Port conflicts
with another server are reported without a Python traceback, and failed startup
attempts preserve earlier server log output.

From a terminal, the equivalent is:

```sh
python3 client/Tools/play_local.py
```

Use `--rebuild` after changing client code, or `--sandbox-map` for the small test
sector. Normal play does not require opening the editor. The generated Mac app
is `Builds/42 and Beyond.app` and supports both Apple Silicon and Intel Macs.

For a fresh machine or editor development:

1. Install Unity Hub and a Unity **6.3 LTS** editor for your platform. The project
   records `6000.3.13f1`; a newer 6.3 LTS patch can upgrade it. Choose the Apple
   Silicon editor on an ARM Mac. Unity also requires Rosetta 2 for supporting
   tools, even when the editor itself runs natively on Apple Silicon.
2. In Unity Hub, **Add project from disk** and choose this `client` directory.
3. Open `Assets/FortyTwoAndBeyond/Space.unity`. The runtime bootstrap creates the scene's
   camera, geometry, and connection panel when you press **Play**.
4. Start the Rust server in a separate terminal, from the repository root:

   ```sh
   cargo run --locked --manifest-path server/Cargo.toml -- --bind 127.0.0.1
   ```

5. Press Play, enter `127.0.0.1`, and connect. A fresh server's demo account is
   `local` / `local`. Use a space character that is not already logged in elsewhere.
   Passwords are not saved in project settings or PlayerPrefs.

To keep prototype play separate from existing local characters, add
`--state-dir /tmp/client-play` to the server command. Use a persistent directory
instead if you want that prototype character to survive temporary-file cleanup.
The default map is Asteroid Belt Alpha. For the four-object test map, also add
`--content server/data/sector.json` when running from the repository root.

To keep the server in the Windows VM, start it bound to its private LAN IPv4
address and enter that address in the connection panel. The existing TCP/UDP
ports must be reachable from the Mac. No server changes are required. The login
server field accepts a hostname or IPv4 address, including public addresses, and
defaults to the local development server at `192.168.47.1`. DNS resolution runs
on network workers with cancellation and a five-second timeout. The selected
server must support this client's account bridge and game protocol.

## Flight HUD and fitting

The flight deck and fitting window use Unity UI Toolkit. Their shared styling is in
`Assets/FortyTwoAndBeyond/Resources/UI/FlightDeck.uss`; the runtime controls live in
`Client/FlightDeckHud.cs`. The scene and protocol remain server authoritative.

Open **Fitting / I** for the ship preview, cargo list, weapon and device sockets,
and ship statistics. Drag cargo onto a compatible socket to equip it; ammunition
loads the selected weapon's magazine. Drag fitted equipment into the cargo area
to remove it. Select a weapon to inspect its magazine or use **Unload magazine**.
The flight hardpoint strip displays server ammunition counts and reload time.

Drag panel headers to move the overview, target, propulsion, integrity, hardpoint,
and fitting widgets. The small grip at the action bar's right edge moves the bar;
its keyboard bindings, action assignment, and right-click rank picker remain active.
**Reset layout** in the fitting window restores default positions. **New client**
in the flight toolbar opens another independent client window.

The disposable `Tests/run_loadout_visual.py` check exercises pointer-driven ammo
loading, window dragging, and server-confirmed ammunition consumption. It does not
connect to the live server.

## Controls

| Input | Action |
| --- | --- |
| W / S | Forward / reverse thrust; opposite direction stops, then release and press again to reverse |
| X | Brake or cancel warp |
| Left / Right arrows | Turn while held |
| Up / Down | Pitch while held |
| RMB / wheel / C | Orbit / zoom / reset camera |
| Tab | Toggle the navigation-only sector chart |
| M | Open client-side galaxy travel |
| I | Expand or collapse Systems: ship, mounts and cargo |
| F | Fire on the selected MOB, or stop active weapons |
| B | Activate an equipped Turbo Weapons device |
| Enter | Open chat and server command line |
| Click a target-list entry or world label | Select a destination |
| Click a ship in space or Contacts | Select that ship |
| E | Select the nearest selectable ship, wreck, resource or navigation object |
| D | Cycle through selectable objects in distance order |
| Space / Follow target | Follow the selected visible ship or MOB; manual flight cancels follow |
| Click a wreck in space or Wrecks | Open its loot inventory and choose stacks to collect |
| Q / Warp to target | Ask the server to warp to the target |
| G / Activate gate | Activate a nearby selected gate |
| O / Dock at station | Enter a selected station within 5 km |
| L / Launch | Leave a station interior |

The **Controls** panel in the upper right can rebind every keyboard action in
this table. Assigning a key already in use swaps the two actions. Changes are
saved locally; **Restore defaults** returns to the layout above.

The VM launcher uses the running server at `192.168.47.128`. The client reads that account's available toons and lets you
choose one before entering space. The current Windows VM has narrow inbound
firewall rules for TCP 3807 and UDP 3501, 3806, and 3808 from the Mac's VMware
host interface. If VMware assigns it a different address, edit the host in the
shortcut or type the new address in the login panel.
The normal login form starts with the Mac-hosted server's current VMware bridge
address, `192.168.47.1`. Its **Published sandbox pilots** section reads only the
server's advertised demo class codes. Choose a class to fill its public
`p`-prefixed class code as both account and password (for example, `pje`/`pje`),
then select **Enter Space**. All
nine classes appear when provisioned on that server. **Discover** refreshes the
list after changing the address. You can still type any other authorized account
manually. The VM launcher selects the Windows server explicitly.

Enter in flight opens the server chat command line. Type `/d_jump BBW` or
`/d_spawn 46 3` and press Enter to use the server's developer commands.

Press Q with a selected navigation point, asteroid, wreck, or visible MOB to
request a server warp. The target center must be at least 4 km away; resources
must be within 20 km. Pilot ships are selected for follow and group actions,
but cannot be direct warp destinations. Select **Free Flight** in the
Navigation list, then press Q to warp ahead along the ship's heading. This
selection stays empty as the sector refreshes; choosing a destination restores
targeted warp.
Press Q again while charging or traveling to cancel; the server announces charge,
travel, and recovery. Warp to a gate first, then travel through it once stopped nearby. The server's
gate sequence takes approximately 15 seconds. Loss of window focus brakes solo
impulse flight but preserves active warp and formation travel. Disconnect and leaving Play mode cancel and join the
network worker before its session is released.
Gate and Dock buttons use the server's native target verbs after selection, so
their availability follows the server's surface range and ship state.

Select a station, warp nearby, then choose **Dock**. Characters already docked
can log in directly. Unity presents one shared concourse, with vendors and
contacts from every native station room listed in the scrollable **Station Directory**.
The server preserves its original rooms and placements for the legacy client;
Unity's directory reaches those contacts without requiring room navigation. Manufacturing, Analyze,
Dismantle, and Refine use the adjacent workbench; a vendor opens authoritative
stock and buy/sell quotes in that same workspace. Recipe books larger than 500
entries have **Prev / Next** controls, and the level buttons filter the server's
eligible recipes. The directory stays available while inventory is open.
The directory, workbench, and vendor dialogue can be collapsed. **Launch**
returns to space. The **Station Vault** panel stores full cargo stacks and retrieves
them while docked; the server publishes both container revisions after a move.

**Tab** opens the tactical sector map. Drag empty space to pan, scroll to zoom
around the pointer, use **My ship** to recenter, or **Fit sector** to include all
known navpoints. Click markers or their labels to select a target; the detail
strip shows its name and range and offers **Center target / Warp here**.
Known navigation points remain on the map and in **Object selection** regardless
of distance. Its filter offers **All navigation**, **Gates**, **Stations**, and
**Navpoints**; stations are grouped first in the full list. The Overview filter
can show all contacts, players, hostiles, or neutral mobs. Filters affect the
lists without clearing the selected target or hiding markers from the chart.
Saved discoveries are replayed by the server on login and sector entry, and live
navigation updates appear without reconnecting.

## Players, groups and mobs

The left **Contacts** list shows visible players and mobs. Type an online toon
name to invite somebody anywhere, or click a nearby player and use **Invite to
group** in the fixed target panel. That button sends the native group invitation
to the selected player ID. **Who is online** prints the available names in the message history.
The invited client can accept or decline; the group
panel reflects the server roster and leader role. Members can leave; leaders can
kick members or disband. Invitations expire after the server's 30-second window.
The roster lists the other members, with your own role in its heading. The leader
can choose Slot Back, Block or Pipe formation and release it later. Members can
form up within 5 km of the leader, or leave formation without leaving the group.
Each member must click **Form up with leader** after the leader chooses a shape;
changing the shape requires members to form up again. A formed wing ship follows
the leader's native position and rotation updates through impulse and warp.
Its manual flight and Follow controls stay inactive until **Leave formation** is
chosen. Switching windows preserves a group's travel instead of sending a brake.
The leader can share the selected target with the group. If the server accepts
that target for a member who can see it, the member's target panel follows the server's
selection, even when an older contact was selected locally. A recent local click
stays visible while its target request is pending.

Press Enter or click **Talk / Enter** in **Comms** to open the visible chat
composer. Its **Sector / Group** button selects the channel; Enter or **Send**
sends the message, and Escape cancels. Comms has a movable header and expandable,
scrollable message history, available in flight and while docked. Up/Down recalls
sent messages and commands in the current client session and restores an unsent
draft when stepping past the newest message. Slash commands
still work, including `/invite
NAME`, `/accept`, `/decline`, `/leave`, `/disband`, `/kick NAME`, `/d_spawn
Achilles Drone 1` and `/d_jump`. Typing in chat suspends flight-key input;
Send and Escape return control to flight. A held thrust key resumes after
returning to flight or refocusing the window.

Select a mob in Contacts or click its ship in space for shield and hull readings.
Hostile contacts have red markers; other NPCs have gold markers. Press F or use
**Fire weapons** to target and fire; press F again or use **Stop weapons** to stop.
The server controls firing, hits, damage, movement and death. The client shows
beam streaks, traveling launcher rounds, and hit numbers from native launch and
damage reports. A resolved hit or miss retires the matching in-flight round.
The target panel distinguishes armed weapons
from a shot confirmed by the server, and briefly shows server hit, miss, or
critical results. Its range, arc, ammo, and cooldown cues are guidance from
the latest server state; the server decides whether a shot can fire. The ship
loadout panel shows the server's hull stage and usable cargo capacity, then lists cargo, equipment,
weapon and device mounts; select cargo and a mount to equip, or select installed
gear to remove. The panel checks mount compatibility and an unlocked empty cargo slot before sending
native inventory transfers, and shows item condition, range and any server quote.
Ammo can be loaded through the same server move command; the whole selected cargo
stack is sent for loading, and the server checks its launcher family. Defeated
mobs leave server-owned wrecks. Click a wreck to open its fixed loot panel;
within 6 km of its surface, open it; request listed items within the server's
2 km surface limit. The client tracks
the server's timed tractor packet and blocks repeat collection until completion
or cancellation. **Close Wreck** sends the native close action and cancels an
active tractor; the wreck can be reopened while it remains visible. Wreck and
prospecting windows share a native interface packet;
the client keeps their state separate. The Open Wreck control follows the
server's native loot verb. The radar shows nearby
contacts and navigation targets.
The loadout shows each weapon's server firing state and loaded ammunition.
Select an installed weapon to toggle that hardpoint alone during space combat.
Assign an equipped Turbo Weapons device to Systems, or use the B key. The
server decides energy cost, firing-rate bonus, and duration; the HUD reflects
the server's activation and expiry messages.
Select a supported installed device in expanded Systems to activate Scan Boost,
Increase Speed, Reactor Boost, Turbo Weapons, Turbo Warp, Improved Beam Handling,
Improved Projectile Handling, Shunt to Reactor, Recharge Reactor, Recharge Shields,
Maximize Shields, or a typed Deflect device. Supported core items can also activate Impulse Turbo,
Increase Maneuverability, or instant Reroute Shield Energy from their shield,
reactor, or engine mount. The loadout's Transfer Reactor Energy control cycles
visible pilots as recipients, waits for server target selection, and sends the
equipped device action; the server checks range and both reactor balances.
Peer-targeted Scan Boost devices use the same recipient control, restricted to
visible crew pilots. The server marks the recipient's separate Scan Boost buff
slot and visual; the ship systems panel shows its own timer alongside any active
self buff. Self Scan Boost reactor effects remain separate catalog actions.
Instant transfers and reroutes remain usable while a timed device buff is active.
The active effect label and displayed remaining time
come from the server's native buff slot and removal tick when available. A
matching native ObjectEffect lights a ship-local field, and effect removal clears
it. The server publishes resource and combat changes and announces when the
effect ends.

Explorer pilots can click a mineable asteroid or gas cloud, open its prospecting
window, and choose a server-listed ore slot. The server checks the ship, skill,
distance, reactor energy, and shared resource claim before it awards ore to cargo.
The panel shows the resource level, depletion, stock, and timed Prospect/Tractor
phases from native packets. It tracks a new prospect separately from an earlier
ore tractor, allowing another server-listed ore slot while the claimed ore is
being delivered. The Prospect control follows the server's native
verb; while the ship is moving, its button brakes before opening the window.
The Skills button lists abilities advertised by the connected character's native
RPG packet. Selecting one sends the native activation command, and the panel
shows the server's combat, trade and explore levels, next-level progress bars,
available skill points, charge and cooldown updates. Its support-target row selects
your own ship or a visible group member through the server's native target packet.
The row waits for target acknowledgment before casting and shows a recipient's
Environment Shield when its native effect is visible. Teleport Friend requires a
server-selected visible group member. The server applies target, range,
energy, charge, and cooldown rules. Refresh display labels from
the public Rust catalog with `python3 client/Tools/sync_ability_catalog.py`
after server skill-content changes. Click one of six slots in the Skills panel,
then an ability, to assign the flight bar. Slot keys default to 1–6 and can be
changed in Controls. Assignments are saved per character on this Mac.
When hull reaches zero, a rescue panel requests the native tow, distress, or
wait choices. Characters with Jumpstart can select their own ship and cast it
from the same panel; the server remains authoritative over recovery.
An incapacitated player offered Jumpstart by another pilot sees a separate
yes/no prompt in that panel. Accept and Decline send the native confirmation
actions; they do not answer a group invitation or choose a rescue NPC option.
While docked, Workbench opens manufacturing, analysis, dismantling, and refining.
Select a server-listed formula or place one cargo item in the analyzer, then use
the quoted action when the server marks it ready. Level buttons filter formulas;
Refine Stack uses the server's available run count. Closing the workbench cancels
an unfinished job without spending its materials or credits. The server sends
the completed item, cargo and credit updates.
Use **Component Market** in the workbench, or **Find / Trade** beside a required
component, to open its searchable exchange overlay. It lists real station/vendor
suppliers and buyers with negotiated unit prices and native item icons. Buy or
sell at the station where you are docked; remote cards show the destination.
The private sandbox's **DEV travel** action uses the server's durable station
transition and is hidden when the server disables developer tools. Drag the
exchange header to move it, or press Escape to close it.
The Missions button shows accepted missions, the current stage objective, and
earlier stage descriptions from the native journal. Kill counts and return
instructions update when the server publishes mission progress. NPC conversations
show server-announced mission offers, active objectives, acceptance, and completion;
the response buttons are the choices advertised by that NPC. The journal entry
remains the authority for the current stage and is removed on completion.
Station pilots and visible contacts use original, repo-native layered suit geometry
in `SpaceArt.cs`; contact placement follows the server's room list. These are
authored replacements, not the original NPC models. Suit arms and legs move only
with local walking input or observed peer room poses. Contacts idle at their
advertised room positions and turn toward the pilot during that contact's native
dialogue. Station interiors and the
full original combat and equipment UI remain unfinished.

## Implementation boundaries

- `Assets/FortyTwoAndBeyond/Protocol`: engine-independent .NET Standard 2.1-compatible C#.
  Local TCP account bridge, character selection, UDP registration/handoff,
  ordering, bounded fragmented-frame reassembly, resend requests, keepalives,
  navigation decoding, player/MOB lifecycle and resource decoding, group AUX state, chat, motion commands,
  warp, and correlated gate activation.
- `Assets/FortyTwoAndBeyond/Client`: Unity main-thread input, shared solid meshes, camera and immediate
  mode prototype HUD. One Unity unit is 100 server meters; the view is centered
  on the player. Server +X maps to Unity +Z, and server +Z maps to Unity +Y.
- `ClientWorker`: one session owner; immutable display snapshots and a bounded
  input queue cross the thread boundary. Socket polling is cancellation-aware.
- Built-in rendering with lit surfaces, procedural sky and planet shaders, shared
  asteroid meshes, and interpolated server poses. No asset-store dependencies.
- Rendering targets 120 FPS in the foreground and 60 FPS while the window is visible behind another app.
  Unchanged static scenery is retained; navigation sorting is refreshed at 4 Hz.
  The HUD reports measured FPS and the slowest frame in each one-second interval.
  The client predicts local and visible remote ship movement between the server's
  roughly five-per-second position reports and corrects on each new pose.
  Normal play uses the standalone app, without the Editor's extra overhead.
- The combat and loadout panels cover selection, weapon toggling, health, cargo,
  hardpoints, equip/unequip, and wreck loot. Docking and launch work through the
  station handoff, room selection and avatar presence, NPC dialogue, vendor trading
  and vault storage.
  The account bridge supplies the toon names and selected server slot.

Physical scenery uses server positions and scale with approximate replacement
models. HUD markers stay readable independently of physical model sizes. Labels
are capped and separated to avoid overlap. The distant planet is decorative.
The code does not change the server, launch the original client, or start/stop
the Windows VM.

## Verification

Run pure protocol checks with a .NET 8 SDK; there are no NuGet dependencies:

```sh
dotnet run --project client/Tests/ProtocolChecks.csproj
```

Run the disposable local integration test from the repository root:

```sh
python3 client/Tests/run_local_smoke.py
```

On Windows, use `C:\PYTHON\python.exe` instead of `python3`. The harness builds
Rust and C#, starts an owned loopback server with two temporary accounts, checks
mutual player visibility, movement, logout removal, reentry, group invitation/decline/accept,
roster updates, group chat, kick/leave/disband, formation actions, inventory moves,
server MOB state, weapon effects, damage, wreck loot, station docking, a docked
reconnect, and launch, then stops it
in a cleanup block. It fails if the ports are occupied rather than stopping any
existing server. It must not be pointed at a live character session.

For an opt-in macOS graphical wreck pickup check, build the debug server first,
then run:

```sh
python3 client/Tests/run_loot_visual.py --unity /Applications/Unity/Hub/Editor/6000.3.13f1/Unity.app/Contents/MacOS/Unity
```

This owned loopback fixture kills one MOB, opens its corpse, captures the native
tractor, and requires both a cargo gain and reduced wreck stock before capturing
the completed pickup. It stops its Editor and server processes afterward.

To capture the two simultaneous mining phases against a temporary ore field:

```sh
python3 client/Tests/run_mining_overlap_visual.py --unity /Applications/Unity/Hub/Editor/6000.3.13f1/Unity.app/Contents/MacOS/Unity

# Capture a native missile launch in flight before its server damage result.
python3 client/Tests/run_projectile_visual.py --unity /Applications/Unity/Hub/Editor/6000.3.13f1/Unity.app/Contents/MacOS/Unity

# Capture a low-stage scout's server-owned hull stage and 25-slot cargo limit.
python3 client/Tests/run_hull_capacity_visual.py --unity /Applications/Unity/Hub/Editor/6000.3.13f1/Unity.app/Contents/MacOS/Unity
```

Verified on macOS: C# protocol checks; actual local bridge login, character
selection, UDP entry, map and position delivery, impulse, warp, gate activation
and destination entry, explicit logout/re-login, worker cancellation and join.
Unity builds and graphical checks are run separately from protocol checks.
Unity's editor can log an internal SearchDatabase indexing exception even when
compilation and Play mode succeed. Keyboard feel still needs human feedback.

Reproduce the build and graphical check with:

```sh
python3 client/Tools/verify_unity.py
```

It owns a disposable server, runs the editor, and saves `chase.png`, `chart.png`,
`asteroid.png`, `station.png`, `gate.png`, and logs under ignored `Builds/verification/`. Inspect the resulting images;
file existence alone does not establish that a layout looks correct.

Launcher regression checks (macOS, no game clients are driven):

```sh
python3 -m unittest discover -s client/Tests -p test_launcher.py -v
```

### Optional original-client HUD corner

The HUD can use the original `HUD01.dds` 8×8 corner tile when it is present at
`Assets/FortyTwoAndBeyond/Resources/LegacyHud01.dds`. It falls back to the
built-in corner if the file is absent. This optional binary is ignored by Git.
The locally verified source is `Data/client/mixfiles/mixfile_art_03.mix` in the
owned Windows Earth & Beyond installation: entry 681, offset 16,047,960,
length 240 bytes. The extracted DDS SHA-256 is
`f5396a236a48343948e7a6a62c45df735b2c6f35ac4a3594e30e6df2f5c3bf32`.
Keep the extracted DDS bytes unchanged; Unity creates its local `.meta` file.

### Scene art sources

The station pilot and contact suits are original procedural meshes and materials
authored in `SpaceArt.cs` for this project. They use the existing project hull
detail texture; no external character model or character texture is bundled.

The distant ringed planet uses NASA's [Saturn 3D texture](https://science.nasa.gov/3d-resources/saturn/),
an illustrative map by Don Davis for JPL/Caltech, with NASA acknowledged as the
source. The JPEG is unchanged; its URL, SHA-256 and NASA usage guidance are in
[`SaturnNASA-SOURCE.md`](Assets/FortyTwoAndBeyond/Resources/SaturnNASA-SOURCE.md).
The spacecraft source and CC BY 3.0 attribution are in
[`ViktorShips/README.md`](Assets/FortyTwoAndBeyond/Resources/ViktorShips/README.md).

### Optional local EVE ships

The client can use two hulls from Pierre's local
`EVE Online - Updated Blender Shader` scene. With `cc3.obj` and `gc3.obj` plus
their baked albedo and glow maps in `Resources/EveShips/`, the Caldari hull
appears as the pilot ship and the Gallente hull appears on small mechanical
MOBs. The existing packaged models remain the fallback. Run Blender with
`--python client/Tools/export_eve_ships.py` to regenerate the six files from
the local `.blend` file. The original scene was assembled by Kaneda Emperis
with the EVE Creative Discord group, according to its included readme.
The exported EVE game art is kept out of Git and is only packaged into local
builds made on a machine that has those files.

### Optional procedural planet

The local `~/Downloads/procedural-planet-shader.zip` contains a volcanic planet
mesh and baked surface maps. Run `python3 client/Tools/export_planet_pack.py`
from the repository root to extract them into the git-ignored
`Resources/PlanetPack/` directory. The Unity client uses the bundled
`ProceduralPlanet` surface and `PlanetAtmosphere` shaders for this world; if
the local assets are absent it displays the packaged Saturn scene instead.
The original archive remains unchanged and is not added to Git.

### Procedural sky and optional space packs

`Tools/generate_space_sky.py` generates the bundled `ProceduralSpace.png`
panorama from seed `42042`. It builds a seamless galactic band, dark dust lanes,
nebula color, and stars offline so the sky shader samples one texture at runtime.
Run `python3 client/Tools/generate_space_sky.py` from the repository root to
reproduce it. The earlier `SpaceNebula.png` remains a fallback.

`~/Downloads/asteroids-pack-metallic-version.zip` and
`~/Downloads/blackhole.zip` can be imported with
`python3 client/Tools/export_space_packs.py --blender /opt/homebrew/bin/blender`.
The ten UV-textured asteroid shapes replace nearby rocky silhouettes, and the
black hole's exported ring and core appear in the distant scenery. Their
resized maps and meshes are kept in git-ignored `Resources/AsteroidPack/` and
`Resources/BlackHolePack/` because the archives contain no redistribution
terms. Existing packaged rocks and scenery remain available when the local
assets are absent.

Galaxy travel opens from **Galaxy / M** in flight or at a station, and from the
Tab sector chart. Search the client's known gate/station directory, select a
destination, review the local route, and choose **Go · Auto Travel**. A selected
gate is the final crossing; a station route ends docked. The browser can close
while travel continues; the flight strip keeps Route and Cancel available.
Manual flight takes control and cancels the itinerary. An incapacitated ship,
unknown link, or failed transition stops travel with a readable status.

Route search and breadth-first planning run entirely in the client. The bundled
`Resources/GalaxyNavigation.bytes` map can be regenerated with
`python3 client/Tools/build_navigation_catalog.py`; `--check` verifies it against
the checked-in map data. Observed gates constrain the first crossing in the
current sector. Execution uses the normal warp, gate, launch and docking packets;
there are no galaxy travel server endpoints and no teleport commands.

Equipment dragging lifts the original item icon under the pointer, dims its
source, highlights compatible sockets and shows the prospective operation.
The item remains server-owned while the drop is pending. A confirmed transfer
snaps into its destination and pulses; an invalid drop, Escape or focus loss
returns the card to its source. This also applies to loading and unloading
weapon magazines.

The Tab sector map and navigation directory show navigation points, gates and
stations. Decorative asteroids, border demarcation markers, resource fields,
wrecks and ship contacts do not clutter the navigation chart.

Cloak fades the ship hull into a faint, continuously visible blue ghost and
restores its original materials when the server clears the status. Visible peers
use the server's cloak effect identity too. Combat Cloak's post-cloak damage
bonus does not hide the hull. Sustained effects display without a countdown.

**Systems** replaces both the old shortcut bar and the separate Ship Loadout widget.
Use **Expand / I** for the ship preview, mounts, equipment and cargo; the same
Systems bar docks inside that window, with a scrollable fitting area on smaller
screens. Drag fitted equipment onto Systems; use **+ / −** to add/remove slots.
Right-click any slot for a searchable, scrollable selector grouped by skill,
effect and level. All server-advertised wormhole destinations remain selectable
across transient loading snapshots. Assigning an effect never casts it.
Assigned skills show their level, charging/recovery timer and a draining gray
recovery mask; server-confirmed active effects remain bright. Equipped systems
show their actual icons, magazine counts and active/cooldown state.

Right-click a launcher on the compact Systems bar to choose compatible cargo
ammunition and enable Auto Reload. Inventory does not need to be open.
Use **Menu → Interface Zoom** to enlarge text and buttons from 85% to 200%;
the effective scale is capped to keep the interface inside the window.

Nearby's compact **All / Fight / Loot / Mine / Nav** filters also apply to the
screen-edge contact rim and E/D selection. Click a rim marker to select its
contact; repeated clicks cycle contacts grouped under the same marker.
Manufacturing combines level, item type, text and ownership filters locally.
**Aboard** includes cargo and loaded ammunition; **Vault** uses known vault
contents. **Load all recipes** caches the existing server pages and restores
the original page when finished or cancelled.

The flight background now uses the standalone SpaceEnvironment demo: Solar
System Scope Earth/day/night/cloud maps and orbiting Moon, Jupiter, Saturn with
textured rings and a planetary shadow, an imported procedural Sun/corona, distant
procedural asteroids, and tantaneity's raymarched black-hole prefab and matching
procedural sky. The source geometry, textures and prefab are imported; shader
algorithms use a small built-in-renderer compatibility adapter, so the game does
not require URP or change its gameplay camera. A quarter-resolution HDR Bloom
pass supplies solar/accretion glow and is disabled while docked or on the map.
The shared sky combines NASA's 4K HDR Deep Star Maps 2020 (NASA/Goddard SVS,
Ernie Wright; Gaia DR2: ESA/Gaia/DPAC) with independently advected blue/violet
nebula layers and denser, derivative-filtered procedural stars. The same sky
inputs are sampled by the black-hole lens. Catalog attribution and checksum are
in `Art/SpaceEnvironment/NASADeepStars-SOURCE.json` and ship in StreamingAssets.
Clouds and gas bands flow at different latitudes; axial rotation, Moon orbit,
ring precession, subtle body drift, solar convection and accretion brightness
are intentionally exaggerated for a visibly active space backdrop. The
`SpaceEnvironmentBackground.motionSpeed` control scales transform animation.

The Sun now uses a Unity port of the free MIT
[Cosmos Museum procedural Sun](https://github.com/CosmosMuseum/cosmos-museum-sun):
multiscale 4D convection and noise-driven coronal rays, with an opaque 3D core
and a separate additive volume. Its source, pinned commit, license and repeatable
importer are in `Art/SpaceEnvironment/StellarSun/` and
`Tools/import_stellar_sun.py`. The original WebGL UI is not imported. Source
notices ship with the app. Re-import from an explicit checkout with
`python3 client/Tools/import_stellar_sun.py --source /path/to/cosmos-museum-sun`.

The skybox explicitly writes far-plane clip depth, keeping stars behind opaque
celestial bodies. A renderer regression check switches the sky between black
and bright and verifies that Earth, Saturn and Sun interiors remain unchanged
at the flight camera's actual near/far range. Saturn's ring shader also rejects
fragments geometrically hidden by the planet, avoiding distant depth ties.

The native wormhole uses a 256-by-64 rim/throat mesh, full-precision plasma,
multiscale flow noise and derivative-filtered filaments instead of aliased,
thin bands. Portal geometry remains independent of authoritative transfer state.

Background meshes have no gameplay colliders or target markers; server-owned
resource asteroids, navigation and combat are unaffected. The imported black
hole lenses its procedural sky, not scene geometry. It uses 160 raymarch steps
with four rotated samples to smooth the bright accretion filaments.

Re-import the completed standalone demo with
`python3 client/Tools/import_space_environment.py --source /path/to/Assets/SpaceEnvironmentDemo`.
Source notices, pinned commits, original shaders and import hashes are kept in
`client/Art/SpaceEnvironment/` (MIT for URP Black Hole and Procedural Planets;
CC BY 4.0, Solar System Scope / INOVE, for maps). The default source is
`~/Documents/42unity/Unity42AndBeyond/Assets/SpaceEnvironmentDemo`. Only the
explicit re-import command requires that project; the game and its builds use
self-contained assets under Resources/SpaceEnvironment.

An isolated GPU and Play-mode check can run without any server or account:
`Unity -batchmode -projectPath client -executeMethod FortyTwoAndBeyond.Editor.ImportedEnvironmentSmoke.Run -logFile /tmp/environment-smoke.log`.
It checks source-material support, Earth spin and Moon orbit, shader animation,
HDR/Bloom and the absence of scenery cameras/colliders, then saves views under
`client/Builds/verification/dynamic-*.png`.

**Items** on the flight toolbar, or **Search galaxy items** in the station
directory, opens the client-only database of 4,568 public item definitions. Search
by name, ID or manufacturer and filter by equipment category. Vendor
item rows open a stats overlay with an explicit Buy/Sell button and live owned
counts across cargo, mounts, ammunition and the vault.

Developer travel: `/d_travel Saturn`, `/d_travel ABA`, `/d_travel ragnar`, or
`/d_travel 1076`. Names ignore case, spaces and punctuation; abbreviations and
unique partial names work. Ambiguous queries list choices. `/d_jump` remains an alias.

In-sector warp now deforms the rendered 3D scene toward the travel heading,
including planets, gates and ships. Existing starlight stretches at the edges
with a mild refractive ripple; the former blue foreground ribbons are removed.
The HUD stays sharp, nearby silhouettes use depth-aware short trails, and the
extra camera pass and depth request are disabled when warp presentation ends.

Sector transfer presentation uses native gate render state (`0x31`), gate camera
approval (`0x92`) and the JE Create Wormhole cast descriptor (`689`). The approved
gate rotates its armored assembly and induction collar, sequences cyan channels,
and contracts to 76% of its resting size. Wormholes use an animated plasma rim
and a recessed spiral throat. Both have departure/arrival hull clipping and a
short cinematic camera; these change only the rendered hull, never network pose.
The worker retains the transfer cause across the sector handshake, including
same-batch cast-effect removal. Logout, cancellation and station entry clear it.

Verify both actual native transfers on an owned loopback server with:
`python3 client/Tools/verify_unity.py --skip-build --transit-only`.
Captures are written to `Builds/verification/{wormhole,gate}-{departure,arrival}.png`.

Wormhole arrivals are keyed to a monotonically increasing sector-entry generation,
including same-sector transfers. Native cooldown deadlines survive handoffs.
Completion is correlated through ordered removal/cooldown packets rather than a
one-second wall-time guess, so delayed reliable delivery retains the animation.
The transit smoke repeats three same-sector casts while deliberately skipping every
cast frame and checks cleanup while the map is open.


UI regression matrix (Mac Unity Editor with rendering enabled):

```sh
python3 client/Tests/run_loadout_visual.py --unity /Applications/Unity/Hub/Editor/6000.3.13f1/Unity.app/Contents/MacOS/Unity --batchmode --shortcuts-only
```

Build `server/target/debug/beyond-server` first. This starts and stops only an
owned loopback fixture with temporary accounts and inventory. It checks equipped
weapon/device/passive shortcut dragging, exact-mount firing and ammo consumption,
selector levels/destinations, active/recovery display, and persisted assignments.
It then renders 960×640, 1366×768, 1920×1080, 2560×1440 and 3840×2160 viewports,
including larger interface scaling: panel bounds, collapse/expand, full Systems
fitting, destination/target persistence when inventory opens, header dragging, and chat input/Send visibility and font size. The Talk button
must pass a real panel hit test so overlapping widgets cannot hide it.
The actual login submit bounds are checked at the smallest and largest sizes.
Fresh fitting/login screenshots and `loadout-ui.log` go to `Builds/verification/`.
Saved collapse/shortcut settings and owned Editor resolution presets are restored;
the live server and installed client are never driven by these tests.

Sector scenery now uses stable sector-ID seeds with six to nine photographic
worlds, variable atmosphere/clouds, rings, orbiting moons, axial rotation and
sky orientation. Returning to a sector restores its recipe. Known Solar System
sector names retain their matching primary planet. Additional Solar System
Scope maps and NASA Eros/Itokawa shape models have source hashes and notices in
`Art/SpaceEnvironment/` and bundled StreamingAssets. Re-bake the NASA meshes with:
`Blender -b --factory-startup --python client/Tools/bake_nasa_asteroids.py`.
The isolated environment check verifies 100 distinct deterministic recipes,
return identity, a bounded reusable scene-object pool, and representative renders.

Wormhole completion intent survives unrelated packet bursts until sector entry
or an explicit replacement action. Protocol checks cover 512 packets on either
side of completion; the owned transit smoke performs four wormholes, including
three repeated same-sector arrivals with deliberately skipped cast frames, plus
gate departure/arrival and map cleanup.


The resource instruments use segmented luminous tracks, percentage badges and
explicit shield/reactor/hull values. Target shields use the native AUX capacity
instead of inventing a 100-point maximum; unknown capacities remain unknown.
Chat has a bright caret and contrasting selection. Its expanded default panel
centers between the side instruments and lifts the unmoved Systems bar.
Dragged Systems coordinates are suspended while the bar docks inside inventory.
The UI matrix also exercises a moved bar through resizing, all 24 embedded slots,
chat focus/caret styling, authoritative MOB shield values, and a reset default
layout. Captures include `ui-fitting-expanded-24.png`, `ui-chat-*.png`,
`ui-polished-flight.png` and `ui-polished-flight-clear.png`.


The self ship-integrity panel now uses the approved armored circular instruments:
a separate amber reactor alongside a combined blue shield/red hull dial. Their fills and numerical values
come from the same immutable authoritative resource snapshot. The GPU faces
update at at most 24 Hz while visible, pause behind inventory or collapsed panels,
and are disposed with the client. Compact label/core spacing keeps percentages
clear of the animated imagery; hover shows the complete current/max values.
The overview retains a scrollable contact list and shortens when needed to leave
room for the instruments. Target and fitting capacity readouts remain numerical.


Shield and hull now share one armored instrument: blue outer shield segments and
red inner hull segments, with independent current/max values. Self retains its
separate amber reactor. The same defense dial appears on selected contacts and
in the movable/collapsible five-other-pilot group strip; clicking an available
pilot targets that ship. Zero native hull capacity produces a shield-only MOB
instrument. Missing group telemetry shows unknown readings rather than full bars.
A runtime hover card displays full current/max values and percentages for self,
crew, and targets, including the reactor; no Editor-only tooltip behavior is needed.
Native peer AUX snapshots now decode the avatar-name prefix before capacities.
The UI checks include five-member fixture rendering, a missing-telemetry slot,
a shield-only contact, and full-value hover cards.

Flight presentation: left/right arrows turn the ship (saved A/D defaults migrate; custom keys remain). Server heading samples drive continuous visual rotation between updates. Defense dials share one full circle: blue shield occupies two thirds, red hull one third, with opposing fill directions that meet at their shared boundary; percentages sit inside and full values appear on hover. Organic contacts use the whole circle for shields. Mob defense is shown in the contact hover tooltip.

Normal-flight visuals buffer authoritative server-timestamped poses (normally 200 ms apart) with a 40 ms jitter margin. Position uses velocity-continuous Hermite interpolation; heading blends rate-projected endpoints. Late packets predict for at most 150 ms, and sector/warp transitions clear the buffer. `/fps` toggles frame FPS, worst frame time, pose cadence, interpolation delay and pose age; `/fps on` and `/fps off` are explicit variants.

Expanded Systems fitting has no outer scroll container. Its full mount assembly scales to the available window area; cargo retains its independent scrollbar. The viewport regression verifies the complete assembly remains visible without scrolling.
