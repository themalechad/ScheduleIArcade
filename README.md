# Schedule I Arcade

Schedule I Arcade turns the vanilla Arcade building into a finished, enterable arcade filled with
playable games, movable machines, custom lighting, ambience, and native-style furniture placement.
It is built for the IL2CPP version of **Schedule I** using MelonLoader and S1API.

Current release: **v0.2.65**

[Download the complete mod](https://github.com/firebirdjsb/ScheduleIArcade/releases/download/v0.2.65/ScheduleIArcade-v0.2.65.zip)
· [Download the DLL only](https://github.com/firebirdjsb/ScheduleIArcade/releases/download/v0.2.65/ScheduleIArcade.dll)
· [View the release](https://github.com/firebirdjsb/ScheduleIArcade/releases/tag/v0.2.65)

> This is an independent community mod and is not affiliated with or endorsed by TVGS.

## Features

### A complete arcade interior

- A walk-in shop inside the real Northtown Arcade building, like the game's other shops: there is
  **no teleport**. Look at the Arcade's glass corner doors and press `E` to open them, walk through
  a short vestibule, and open the arcade's own double door the same way. Doors close by themselves
  once you walk away. Leave the same way you came in.
- The building's own glass door models swing open on hinges; the corner panel's sealing collider is
  replaced at runtime by wall stubs and a lintel around the measured 2.15 m x 2.10 m doorway, and
  everything is restored when the world unloads.
- The vestibule (side walls, ceiling, a 4.9 degree ramp from street level up to the room floor) is
  built from positions measured at runtime, so the path is continuous and collidable.
- "Inside the arcade" (machine prompts, ambience, building) is decided by your position, not by a
  teleport flag.
- Interior floor, ceiling, skirting, trim and neon use Schedule I's own loaded materials (for
  example `Carpet`, `Ceiling`, `Skirting`, `Trim` and the Arcade building's red/white neon), and
  wall sconces reuse the building's own light fixture mesh. These are live references to assets the
  game has already loaded: nothing from the game is extracted, packaged or committed.
- The mural wall's single door is decorative; the old rear "west side" teleport exit was removed
  because the 0.8 m gap behind that wall is not a walkable route.

### Arcade lighting and ambience

- Warm ceiling illumination without the old hanging light bars.
- Cyan and magenta wall washes for a stronger arcade atmosphere.
- Bright generated attract displays instead of out-of-place black screens.
- A looping arcade ambience track that plays only while the player is inside.
- Configurable ambience enable state, volume, and fade duration.

### Pac-Man cocktail table

- A playable Pac-Man-style game fitted into the physical cocktail-table screen.
- A 28 × 31 maze with small pellets, energizers, score, lives, levels, ghost pen, and wrap tunnel.
- Four ghosts with frightened behavior.
- Smoothed/interpolated movement for Pac-Man and the ghosts.
- An overhead locked play view that keeps the complete table display visible.

### Four Space Cadet-style pinball machines

- Four independently placed and movable machines.
- Live game displays fitted to each machine's sloped glass.
- Left and right flippers, plunger lane, one-way lane gate, bumpers, slingshots, drop targets, an
  orbit lane, scoring, a ball saver, lives, multipliers, and restart flow.
- Frame-rate independent physics: fixed 1/480 s sub-steps, capsule colliders that cannot be
  tunnelled through, and flippers that pass their swing speed to the ball.
- The playfield is closed except for the centre drain between the flippers, and everything drawn is
  exactly what the ball collides with.
- Returning balls can be caught by the plunger and launched again.
- Rounded player collision prevents the cabinets from trapping the player against nearby machines.
- Tuned gravity, impulses, speed caps, and stopper reactions for smoother, less explosive ball
  movement.
- Unified Space Cadet-inspired cabinet artwork and correctly oriented playfield text.

### Doom I and Doom II cabinets

- Two Doom-themed stand-up cabinets with fitted, correctly oriented CRT displays.
- Cabinet 1 connects to the separately installed Doom I TV mod.
- Cabinet 2 connects to the separately installed Doom II TV mod.
- Each bridge validates its DLL, loaded MelonLoader assembly, WAD header, expected WAD path, and
  native runtime before starting.
- If a Doom dependency is missing the cabinet shows **OUT OF ORDER - NO CHARGE** with the exact
  missing file, and never takes the $10. If the external runtime stops mid-game the $10 is refunded.
- Doom WADs and the external Doom TV mods are not bundled with this project.

### Arcade TV

- A pre-placed interactive TV with playable Pong and Snake.
- A pixel-art game menu with keyboard navigation.
- Correctly oriented screen texture and a pulled-back camera view that shows the whole display.

### Native-style play sessions

- The camera moves to the physical screen or playfield when a game starts.
- Player movement, camera look, phone shortcuts, and the normal hotbar are locked during play.
- Only the controls required by the active arcade game remain enabled.
- Escape safely restores the camera, player controls, hotbar, and original attract screen.
- Interaction prompts only appear when the player is close and looking at the correct machine.
- Standing-height interaction zones remove the need to crouch or aim at the floor.

### $10 per play

Every playable machine costs **$10 cash** each time a new session starts successfully.

- Uses the normal Schedule I cash balance and HUD.
- The price is shown on machine attract screens and interaction prompts.
- Players with less than $10 receive a clear message and are not charged.
- Looking at, moving, placing, or exiting a machine is free.
- Occupied or unavailable machines do not charge the player.
- If startup fails after payment, the $10 is refunded automatically.

### Movable machines and custom placement

The following custom assets can be picked up and repositioned:

- Arcade TV
- Pac-Man cocktail table
- Four pinball machines
- Doom I cabinet
- Doom II cabinet
- Air hockey table

Moving an asset creates a full-model translucent placement ghost, tracks it in a furniture-style HUD
slot, and preserves the asset's game and screen registration after placement. Placement blocks wall
penetration and machine overlaps and can snap flush to the arcade's straight walls and chamfer.

### Vanilla furniture placement support

- Extends the native Arcade floor grid to cover the complete room.
- Registers floor tiles and full-height wall surfaces with the native property/build system.
- Allows normal Schedule I furniture to be placed flush against the arcade walls from floor level.
- Keeps placed furniture associated with the arcade room instead of disappearing after creation.

### Editable Blender layout

- Includes `BlenderEditable/ScheduleIArcade_Editable.blend` for machine placement changes.
- The build pipeline exports the saved Blender transforms into the runtime scene.
- Automated layout QA rejects visible machines that overlap, penetrate a wall, leave the sealed
  footprint, or fail wall-row alignment.
- Ordinary layout editing does not require Unity Editor.

## Installation

### Requirements

- Schedule I installed through Steam
- MelonLoader `0.7.3`
- S1API for IL2CPP (`S1API.Il2Cpp.MelonLoader.dll`)

### Recommended installation

1. Download `ScheduleIArcade-v0.2.65.zip` from the
   [latest release](https://github.com/firebirdjsb/ScheduleIArcade/releases/tag/v0.2.65).
2. Close Schedule I.
3. Extract the ZIP into the Schedule I game directory.
4. Allow its `Mods` directory to merge with the existing `Mods` directory.
5. Confirm these paths exist:

   ```text
   Schedule I/Mods/ScheduleIArcade.dll
   Schedule I/Mods/ScheduleIArcade/Assets/
   ```

6. Launch Schedule I, load a save, and interact with the vanilla Arcade entrance.

The standalone DLL download is intended for users who already have the matching
`Mods/ScheduleIArcade/Assets` directory from the complete package.

## Controls

### General interaction and placement

| Action | Control |
| --- | --- |
| Play the machine in view | `E` |
| Pick up the machine in view | `F` |
| Rotate a held machine | `Q` / `E` |
| Place a held machine | Left mouse button |
| Cancel placement | Right mouse button or `Esc` |
| Exit an active game | `Esc` |

### Game controls

| Machine | Controls |
| --- | --- |
| Pac-Man | Arrow keys move, `Tab` pauses, `Enter`/`Space` restarts, `Esc` exits |
| Pinball | Left arrow/`Z` and right arrow/`X` control flippers, `Space`/Up launches, `Esc` exits |
| Doom I / Doom II | `WASD` or arrow keys play, `Esc` exits |
| Arcade TV | Arrows navigate/play, `Enter`/`Space` selects, `Tab` returns to menu, `Esc` exits |

## Optional Doom integration

The stand-up cabinets expect these separately installed files:

### Doom I

```text
Schedule I/Mods/ScheduleIDoomTV.dll
Schedule I/Mods/SchedualDoomTv/WAD/Doom1.WAD
```

### Doom II

```text
Schedule I/Mods/ScheduleIDoom2TV.dll
Schedule I/Mods/SchedualDoom2Tv/WAD/Doom2.wad
```

Each install also needs the Doom TV mod's own native runtime at
`Mods/SchedualDoomTv/Runtime/doomgeneric_s1.dll` (Doom II: `Mods/SchedualDoom2Tv/Runtime/...`).
The Doom cabinets are front-ends for those separately distributed Doom TV mods; this project does
not contain a Doom engine or any WAD. On load the MelonLoader log states for each cabinet either
that the runtime and WAD were found or exactly which file is missing. A commercial `DOOM1.WAD`/
`DOOM2.WAD` must come from the user's own copy of the game.

## Configuration

MelonLoader stores these settings under the `ScheduleIArcade` preferences category:

| Setting | Default | Description |
| --- | ---: | --- |
| `AmbienceEnabled` | `true` | Enables arcade ambience while inside |
| `AmbienceVolume` | `0.25` | Ambience volume from `0.0` to `1.0` |
| `AmbienceFadeSeconds` | `1.25` | Ambience transition duration |
| `DebugLogs` | `false` | Enables additional diagnostic logging |

## Building from source

Additional build requirements:

- Windows PowerShell
- A .NET SDK (6 or newer). If the .NET 6 targeting pack is not installed the script compiles
  against the installed .NET 6.0.x runtime assemblies instead and says so (the DLL then references
  `System.Private.CoreLib` directly, which loads on MelonLoader's net6 runtime).
- Blender `4.3.x` only when re-exporting `BlenderEditable/ScheduleIArcade_Editable.blend`; pass
  `-SkipBlenderExport` to use the packaged layout.
- A local Schedule I installation containing MelonLoader, S1API, and generated IL2CPP assemblies

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Build-And-Install.ps1
```

For a compile-only check that does not install into the game:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Build-And-Install.ps1 -BuildOnly -SkipBlenderExport -GameDir "D:\SteamLibrary\steamapps\common\Schedule I"
```

The script compiles against the game's own `MelonLoader
et6`, `Mods\S1API.Il2Cpp.MelonLoader.dll`
and generated `MelonLoader\Il2CppAssemblies`, and prints the S1API and MelonLoader versions it used.

### Tests

```powershell
dotnet run -c Release --project tests\ScheduleIArcade.Tests
dotnet run -c Release --project tests\ArcadeGeometryCheck -- "D:\SteamLibrary\steamapps\common\Schedule I"
```

`ScheduleIArcade.Tests` runs the pinball simulation headlessly (launch, flipper impulse, colliders,
tunnelling, closed side lanes, drain/relaunch, 10-240 Hz frame-rate independence) and measures the
old v0.2.65 physics for comparison. `ArcadeGeometryCheck` reads the installed `level1` read-only and
checks that the walk-in route from the street into the room is clear for a standing player.

The script exports the Blender-authored layout, runs layout QA, compiles the mod, and writes the DLL
to `Build/Release/ScheduleIArcade.dll`. A normal build also installs the DLL and runtime assets into
the detected Schedule I directory.

## Project structure

- `src/ScheduleIArcade/` — mod source and game implementations
- `BlenderEditable/` — editable arcade scene
- `BlenderTools/` — scene preparation, inspection, export, and QA scripts
- `dist/Mods/ScheduleIArcade/Assets/` — packaged runtime geometry, textures, audio, and sprites
- `Tools/` — build/install pipeline and asset inventory
- `UnityAssetsToCopy/` — optional Unity editor source assets and tools

## Troubleshooting

- **The door prompt does not appear:** finish loading a save, stand within about 2 m of the Arcade's
  glass corner doors and look at them. The log line `Walk-in arcade ready` confirms the entrance
  was prepared; an error there names the missing game object.
- **A machine will not start:** confirm the player has at least $10 cash and read the interaction
  feedback message.
- **A Doom cabinet says OUT OF ORDER:** the prompt and the MelonLoader log name the missing Doom TV
  mod DLL, WAD or native runtime.
- **The mod loads but the room is missing:** install the complete ZIP so the
  `Mods/ScheduleIArcade/Assets` directory is present.
- **A placement ghost is red:** move it clear of walls and other assets or target a registered floor
  or wall grid location.
- **The build cannot find references:** verify the local game, MelonLoader, S1API, and IL2CPP
  assemblies.
- **The build cannot export the room:** install Blender 4.3.x and verify its executable can be found.

## Credits and asset provenance

The Space Cadet-style pinball implementation was informed by the component structure of
[SpaceCadetPinball](https://github.com/k4zmu2a/SpaceCadetPinball), which is MIT licensed. This mod
contains a new Unity/IL2CPP implementation and does not bundle the upstream C++ source or original
Windows Space Cadet game data.

The optional Doom TV mods and Doom WADs are not redistributed here.

The project owner has confirmed that the bundled custom models, textures, audio, artwork, and
compatible sprites were either AI-generated for this project or obtained from royalty-free sources.
Individual royalty-free assets remain subject to any terms or attribution requirements attached to
their original source.

Schedule I, Doom, Pac-Man, Space Cadet, and related names and artwork belong to their respective
owners.
