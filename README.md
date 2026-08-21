# Schedule I Arcade

Schedule I Arcade expands the vanilla Arcade building into a finished, enterable arcade with
playable machines, custom lighting and ambience, movable cabinets, and native-style furniture
placement. It is a MelonLoader mod for the IL2CPP version of **Schedule I**.

Current mod version: `0.2.65-paid-play-public-release`

> This is a community project and is not affiliated with or endorsed by TVGS.

## What the mod adds

- A sealed, polished arcade interior aligned inside the vanilla Arcade building.
- Entry through the real exterior Arcade door and two usable interior exits.
- A playable Pac-Man cocktail table with pellets, energizers, ghosts, lives, scoring, levels,
  wrap tunnels, and interpolated movement.
- Four playable Space Cadet-style pinball machines with fitted playfield displays, flippers,
  plunger lane, bumpers, targets, scoring, lives, and smoother capped ball physics.
- Two Doom-themed stand-up cabinets. They connect to separately installed Doom TV mods when those
  mods and their WADs pass validation; otherwise, the cabinets use a safe test screen.
- An Arcade TV with playable Pong and Snake.
- Movable cabinets and machines with a full-model placement ghost, rotation controls, collision
  checks, floor/wall snapping, and HUD tracking.
- An expanded native placement grid so normal Schedule I furniture can be placed against the
  arcade floor and walls.
- Warm ceiling light, cyan/magenta arcade wash lighting, attract screens, and looping ambience.
- Editable machine layout through the included Blender scene and export tools.

## $10 paid play

Every playable machine costs **$10 cash each time a new session starts successfully**. This applies
to Pac-Man, all four pinball machines, both Doom cabinets, the Arcade TV, and any registered test
cabinet.

- The price is shown on the in-world attract screens and interaction prompt.
- The normal Schedule I cash balance and HUD are used.
- A session does not start when the player has less than $10.
- Looking at, moving, or placing a machine never costs money.
- Occupied or unavailable machines do not charge the player.
- If a machine fails after taking payment, the $10 is refunded automatically.

## Controls

### Arcade interaction

| Action | Control |
| --- | --- |
| Play the machine in view | `E` |
| Pick up the machine in view | `F` |
| Rotate a held machine | `Q` / `E` |
| Place a held machine | Left mouse button |
| Cancel placement | Right mouse button or `Esc` |
| Leave an active game | `Esc` |

Interaction prompts only appear when the player is close enough and looking at the machine.

### Games

| Machine | Controls |
| --- | --- |
| Pac-Man cocktail table | Arrow keys move, `Tab` pauses, `Esc` exits |
| Space Cadet pinball | Left/right arrows control flippers, `Space` or Up launches, `Esc` exits |
| Doom I / Doom II | `WASD` or arrow keys play, `Esc` exits |
| Arcade TV | Arrow keys play/navigate, `Enter` selects, `Tab` returns to menu, `Esc` exits |

While a machine session is active, normal movement, camera look, phone hotkeys, and the hotbar are
locked so game input does not leak into Schedule I.

## Requirements

- Schedule I installed through Steam.
- MelonLoader `0.7.3` installed in Schedule I.
- S1API for IL2CPP (`S1API.Il2Cpp.MelonLoader.dll`) installed in the game's `Mods` folder.
- Windows PowerShell and a .NET SDK with the .NET 6 reference pack to build from source.
- Blender `4.3.x` for the included layout export/build pipeline.

The game should be closed before building or installing the DLL.

## Build and install

1. Clone or download this repository.
2. Install MelonLoader and S1API into Schedule I.
3. Close Schedule I.
4. Run `BUILD_AND_INSTALL.cmd` from the repository root.
5. Launch the game, load a save, and use the vanilla Arcade entrance.

The build script finds the Steam installation, validates MelonLoader and S1API, exports the tracked
Blender layout, runs layout QA, compiles `ScheduleIArcade.dll`, and installs the DLL and runtime
assets into the correct Schedule I `Mods` paths.

For a compile-only verification without installing into the game:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Build-And-Install.ps1 -BuildOnly
```

The compiled DLL is written to `Build/Release/ScheduleIArcade.dll`.

## Optional Doom cabinet integration

The Doom games and WADs are not bundled by this mod. The cabinet bridge expects these separately
installed files:

### Doom I cabinet

- `Mods/ScheduleIDoomTV.dll`
- `Mods/SchedualDoomTv/WAD/Doom1.WAD`

### Doom II cabinet

- `Mods/ScheduleIDoom2TV.dll`
- `Mods/SchedualDoom2Tv/WAD/Doom2.wad`

The bridge validates the expected DLL, loaded MelonLoader assembly, WAD header, and native runtime.
If any required component is missing or invalid, the matching cabinet stays available in test mode
instead of attempting to load the WAD.

## Editing the room in Blender

Run `OPEN_ARCADE_IN_BLENDER.cmd` to open the tracked editable scene:

`BlenderEditable/ScheduleIArcade_Editable.blend`

Save the scene normally. The next build exports the updated machine transforms and rejects layouts
where visible machines overlap, penetrate the sealed room, or pull wall rows away from their walls.
The runtime shell, placement proxies, screens, and game logic remain generated by the mod.

See `BLENDER_EDITING_GUIDE.txt` and `BlenderTools/README_BLENDER.txt` for more detail.

## Configuration

MelonLoader stores the `ScheduleIArcade` preference category in its normal preferences file.

| Preference | Default | Purpose |
| --- | ---: | --- |
| `AmbienceEnabled` | `true` | Enables arcade ambience while inside |
| `AmbienceVolume` | `0.25` | Ambience volume from `0.0` to `1.0` |
| `AmbienceFadeSeconds` | `1.25` | Fade-in and fade-out duration |
| `DebugLogs` | `false` | Enables additional diagnostic logging |

## Project layout

- `src/ScheduleIArcade/` — C# mod source.
- `Games/` — Pac-Man, pinball, Arcade TV, Doom bridge, and test game runtimes.
- `Services/` — room loading, entry/exit, interactions, sessions, placement, lighting, and audio.
- `BlenderEditable/` — tracked editable arcade layout.
- `BlenderTools/` — deterministic export and layout validation tools.
- `dist/Mods/ScheduleIArcade/Assets/` — runtime scene, textures, ambience, and compatible sprites.

## Troubleshooting

- **No entrance prompt:** load a save completely, approach the vanilla Arcade door, and look at it.
- **A game will not start:** confirm the player has at least $10 cash and read the prompt feedback.
- **A Doom cabinet shows test mode:** verify the matching Doom TV DLL, WAD path, and WAD filename.
- **A build cannot find references:** verify MelonLoader, S1API, and Schedule I's generated IL2CPP
  assemblies are installed.
- **A build cannot export the room:** install Blender 4.3.x or correct the Blender executable path
  used by the build script.
- **Placement is red:** move the ghost clear of walls and other assets, or select a registered floor
  or wall grid location.

## Credits and asset notice

The pinball implementation was informed by the component structure of
[SpaceCadetPinball](https://github.com/k4zmu2a/SpaceCadetPinball), which is MIT licensed. This mod
contains a new Unity/IL2CPP implementation and does not bundle the upstream C++ source or original
Windows Space Cadet game data.

The Doom TV mods and Doom WADs are optional external dependencies and are not redistributed here.
See `THIRD_PARTY_NOTICES.md` for additional attribution.

The project owner has confirmed that the bundled custom models, textures, audio, artwork, and
compatible sprites were either AI-generated for the project or obtained from royalty-free sources.
Individual royalty-free assets remain subject to any terms or attribution requirements attached to
their original source; keep those records with redistributed asset packages. See
`docs/MODEL_LICENSE_REQUIRED.txt` for the recorded provenance status.

Schedule I, Doom, Pac-Man, Space Cadet, and related names and artwork belong to their respective
owners.
