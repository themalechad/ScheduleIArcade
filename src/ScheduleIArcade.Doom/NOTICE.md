# ScheduleIArcade.Doom — embedded Doom engine

`ScheduleIArcade.Doom.dll` is a separate assembly that runs Doom inside the Schedule I arcade
cabinets. It is licensed under the **GNU General Public License, version 2** (see `LICENSE`), the
licence of the engine it is built from. The arcade mod (`ScheduleIArcade.dll`) does not link to it at
compile time; it loads it at runtime and talks to `ScheduleIArcade.Doom.EmbeddedDoomRuntime` by
reflection.

## Engine provenance

- Upstream: **Managed Doom** by Nobuaki Tanaka (sinshu), a C# port of the GPL Linux Doom source.
- Repository: https://github.com/sinshu/managed-doom
- Version: tag **v2.1a**, source archive
  https://github.com/sinshu/managed-doom/archive/refs/tags/v2.1a.tar.gz
  (SHA-256 `e8caeb5b9dab59d8dcf61dfa57ea102d9bf0a9d66750532ec2323374adf9a19e`, downloaded 2026-11-09).
- Licence: GPL-2.0 (`licenses/LICENSE_ManagedDoom.txt` upstream; copied here as `LICENSE`).

### What is included from upstream

`Engine/` is `ManagedDoom/src/` from v2.1a **except** `src/Silk/` (the Silk.NET/OpenGL window,
OpenAL audio, GLFW input and MeltySynth music backends). None of Silk.NET, TrippyGL, DrippyAL,
MeltySynth, GLFW, OpenAL Soft or the TimGM6mb soundfont is used or shipped.

### Modifications to upstream files

| File | Change |
| --- | --- |
| `Engine/ConfigUtilities.cs` | Added `DataDirectoryOverride` (2026-11-09) so save games/config go to `UserData/ScheduleIArcade/Doom` instead of next to the game executable. |

All other files under `Engine/` are byte-for-byte upstream.

### Added by this project (GPL-2.0)

`Host/` replaces the Silk backend with an embeddable host: `EmbeddedVideo` (software renderer to
an RGBA buffer, converted to Unity's bottom-up layout), `EmbeddedSound` (decodes the WAD's DMX sound
lumps and emits play events instead of using an audio device), `EmbeddedUserInput` (key-state tic
commands, adapted from `SilkUserInput`) and `EmbeddedDoomRuntime` (35 Hz tic loop and the
primitive-typed API the mod uses). Music is not played.

## Game data: Freedoom

- Freedoom 0.13.0, https://freedoom.github.io/ —
  https://github.com/freedoom/freedoom/releases/download/v0.13.0/freedoom-0.13.0.zip
  (SHA-256 `3f9b264f3e3ce503b4fb7f6bdcb1f419d93c7b546f4df3e874dd878db9688f59`, as published in the
  project's signed `freedoom-0.13.0-CHECKSUM`).
- Licence: BSD-style, `licenses/Freedoom-COPYING.txt`; credits in `licenses/Freedoom-CREDITS.txt`.
- `freedoom1.wad` (Freedoom: Phase 1) runs on cabinet 1 and `freedoom2.wad` (Freedoom: Phase 2) on
  cabinet 2. They contain no id Software data. `Tools/Fetch-Freedoom.ps1` downloads and verifies
  them; they are not committed to this repository.
- Optional: a player's own `DOOM.WAD`/`DOOM1.WAD` or `DOOM2.WAD` placed in `Mods/ScheduleIArcade/Doom/`
  is used instead. Commercial Doom data is never distributed with this project.

## Corresponding source

The release ZIP contains `Mods/ScheduleIArcade/Doom/ScheduleIArcade.Doom-source.zip` with the
complete source of this assembly (`Engine/`, `Host/`, this notice, the licence and `BUILD.txt`). It
is also in this repository under `src/ScheduleIArcade.Doom/`.

## Building

`Tools/Build-And-Install.ps1` compiles this folder with Roslyn: C# 12, `/unsafe+`,
`/optimize+ /deterministic+`, referencing only the .NET 6 framework (no NuGet packages).
C# 12 is pinned because C# 14's first-class span conversions change an overload in
`Video/Renderer.cs`.
