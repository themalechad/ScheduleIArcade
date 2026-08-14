# Schedule I Arcade M2.51

M2.51 frames every upright cabinet from its actual fitted CRT face instead of assuming a shared
root-facing axis. This puts the Doom play view directly in front of the correct monitor rather than
at a cabinet side or outside the arcade. The Doom cabinet atlas lettering and fitted CRT U axis are
now mirrored for the supplied mesh UVs, so DOOM I and DOOM II read normally from the player side.

See TEST_M2.51.txt for the focused regression pass.

## M2.50

M2.50 connects the two stand-up cabinets to their matching separately installed Doom TV mods
without packaging, loading, or redistributing either mod or its WAD. Cabinet 1 runs the live
framebuffer from ScheduleIDoomTV.dll / SchedualDoomTv/WAD/Doom1.WAD; cabinet 2 runs the live
framebuffer from ScheduleIDoom2TV.dll / SchedualDoom2Tv/WAD/Doom2.wad. Both cabinets validate
their expected DLL, loaded MelonLoader assembly, WAD header, and native runtime before use. A missing
or invalid dependency is plainly marked TEST MODE and remains playable with the safe test screen.

See TEST_M2.50.txt for the focused regression pass.

## M2.49

M2.49 moves the rear arcade exit a further 5m backward along its west-facing route. It now targets
the blue service door at `(-53.65, -3.0, 146.1)` instead of the nearer red door, while preserving
the west-facing player direction.

See `TEST_M2.49.txt` for the focused regression pass.

## M2.48

M2.48 places both stand-up CRTs on the actual recessed monitor plane instead of the cabinets'
outer collision shell, with a 2 mm visual offset to prevent flickering. Both entire cabinet meshes
now receive original generated retro hell-sci-fi UV atlases: stand-up 1 has an orange/bone `DOOM I`
skin, while stand-up 2 has a steel/green `DOOM II` skin. Their idle CRTs use matching titles and
colours. No ripped Doom artwork is included or redistributed.

See `TEST_M2.48.txt` for the focused regression pass.

## M2.47

M2.47 guarantees that Pac-Man's small route pellets stay visible on the in-world screen, even when
the supplied source sprite's dark edge pixels are downscaled. Exit hotspots are now at natural eye
height and measure closeness along the floor, so players can use a door while looking straight at it.
The west-side return is shifted 1.25m backward to land beside the real rear door rather than ahead of
it. The exterior fallback choice now uses the same high-contrast arcade-card treatment as interior
machine and exit prompts.

See `TEST_M2.47.txt` for the focused regression pass.

## M2.46

M2.46 makes exit interaction deliberate: a prompt appears only when the player is close and looking
directly at the matching doorway. The Street Fighter mural door is now a second exit. Aimed interaction
there sends the player to `(-59.9, -3.0, 146.1)` at the back/west side of the real Arcade, facing west;
the original entry doorway still returns to the normal exterior entrance point.

The Pac-Man cocktail screen is now a small flush tabletop inset instead of a full-top overlay, and its
texture orientation is corrected. The playable board has been rebuilt as a 28 x 31 classic maze with a
central ghost pen, pellets, energizers, scoring, lives, frightened ghosts, and a wrapping tunnel. It
continues to render the compatible Pac-Man / ghost / pellet art extracted from the supplied package.

The stand-up cabinet displays now sit only millimetres in front of the authored monitor plane. Cabinet 02
has been turned to use its real front axis, so both cabinet screens face the arcade floor rather than a
wall. The editable Blender layout and exported runtime scene carry the same corrected orientation.

See `TEST_M2.46.txt` for the focused regression pass.

## M2.45

M2.45 replaces the black idle rectangles on the Arcade TV, both stand-up cabinets, and the Pac-Man
cocktail table with bright generated attract displays. Each has its own readable title and `PRESS E`
instruction, so the source pack's near-black helper texture can no longer hide an interactive screen.

Prompts now use a single high-contrast arcade card treatment. They only appear when the player is
within 2.35 metres and the crosshair is directly on the asset. Screen-bearing machines show one
combined `E` play / `F` move card; tables and pinballs show only their move card. Carrying a machine
uses clear green/red placement status and a separate control reminder.

The interior now has brighter warm ceiling illumination plus cyan and magenta arcade wall washes.
These are point lights only—no long roof light bars have been reintroduced.

The Pac-Man table now also loads Pac-Man, ghost, pellet, energizer, and marquee art extracted from
the supplied `PacMan v1.1.1` Unity data files. That uses the real compatible assets from the package
in the in-world renderer, rather than trying to run its separate Mono `Assembly-CSharp.dll` inside
Schedule I. The Arcade TV view is pulled back to keep the complete TV screen and console in frame.

See `TEST_M2.45.txt` for the focused regression pass.

## M2.44

M2.44 closes the old Street Fighter doorway aperture with a finished interior wall panel after
moving the physical door to the right. The mural artwork is unchanged and unmirrored.

The four pinball machines now use their actual model back axis and sit flush against the west wall;
the two upright cabinets use their actual screen axis and face out into the room. The Blender export
check now rejects machine rows that clip through a wall, and the build script excludes generated
compiler files.

The pre-placed Pac-Man cocktail table is now playable: aim at the tabletop and press E. It opens a
locked overhead play view and runs a self-contained Pac-Man game with pellets, power pellets,
ghosts, scoring, lives and levels. Arrow keys move, Tab pauses/resumes, and Escape leaves the table.
The game is recreated for Schedule I's IL2CPP runtime from the supplied PacMan v1.1.1 gameplay
structure; its standalone Mono Unity executable is not loaded into the incompatible IL2CPP process.

M2.44 also completes the grid/HUD safeguards: the native grid lookup is cleared before its rebuilt
coordinates are registered, and carrying a custom machine no longer draws the unsupported HUD
texture that produced the repeated `Method unstripping failed` error.

See `TEST_M2.44.txt` for the focused regression pass.

## M2.43

M2.43 resolves the remaining placement and TV-session problems from the screenshot review. The
custom-furniture HUD no longer calls an IL2CPP callback during Unity's GUI event loop, eliminating
the repeated `Method unstripping failed` log spam while any machine is picked up. The state is now
cached during the normal update tick.

The TV screen's U axis has been corrected, so its menu/game text is no longer mirrored. Interacting
with the Arcade TV now enters a native-style play mode: the camera moves close to the screen, player
movement/look and the native PlayerInput action map are locked, the hotbar is hidden, and the phone
is closed and disabled. Arrow keys, Tab (TV menu), and Escape (leave TV) remain available for the
TV itself; native phone and other gameplay hotkeys do not run underneath the session.

The native arcade Grid now rebuilds its tile registration and coordinate lookup after its extended
perimeter rows are cloned. This fixes the earlier case where the grid drew beneath an item but the
vanilla TV/furniture ghost remained red. The placement hook also preserves a valid result whenever
the actual closest tile belongs to the fully registered arcade Grid.

The four pinballs now put their backs against the west wall, the two upright cabinet fronts face the
room, and the standalone helper screens are placed on those same visible faces. The physical door
on the Street Fighter wall is moved from its left/north end to its right/south end as an independent
mesh piece; the mural geometry and texture coordinates are unchanged, so the art is not mirrored.
The editable Blender scene retains these corrections, and its preparation script applies them to
future freshly generated scenes.

See `TEST_M2.43.txt` for the focused regression pass.

## M2.42

M2.42 makes every visible custom cabinet and machine movable in game: both stand-up arcades, four
separate pinball machines, Pac-Man cocktail table, air hockey, and the interactive Arcade TV. Aim
at one and press F to pick it up. The held asset is tracked as one item in a furniture-style HUD
slot, its complete model becomes a translucent green/red placement ghost, Q/E rotates it, left
click places it, and right click or Escape cancels. Placement uses each model's real footprint and
floor offset, blocks overlaps and walls, and magnet-snaps flush to all straight walls plus the
north-west chamfer. Moving a playable cabinet preserves its registered screen and game identity.

M2.41 adds a pre-placed custom Arcade TV against the clear north wall. Press E to open its pixel
game menu, select Pong or Snake with the arrow keys, and press Enter to play. Tab returns to the TV
menu and Escape exits the session. The screen restores its attract display when play ends.

## M2.40

M2.40 extends the native floor-placement grid from its old 8.0 m x 5.5 m template coverage to an
8.0 m x 8.0 m field. Five complete native tile rows are cloned before Grid/Tile Awake, giving every
north, south, east, west, and chamfer wall a usable floor snap row. The outer half-metre tile edges
meet the finished wall faces, and the thin detection colliders remain 1 mm above the visible floor,
so floor-standing furniture can sit flush against a wall without floating or turning red at the old
grid boundary. The six full-height wall Surface proxies remain aligned from floor to ceiling for
true wall-mounted items.

## M2.39

M2.39 follows the in-game screenshot review:

- Pac-Man is centered at the room origin.
- The combined pinball mesh is split into four complete, independently editable machines and the
  four cabinets are placed one-by-one along the west wall.
- The combined upright arcade mesh is split into two independently editable/playable cabinets and
  placed as a spaced row along the south wall.
- The two long hanging FBX light assemblies and the four generated ceiling panels are removed;
  lighting remains through invisible point lights and the red/white perimeter accent.
- The broad legacy-door triangle deletion and oversized purple east-wall fill are removed, restoring
  the source windows and surrounding artwork while the invisible east collision shell keeps the room
  physically sealed.
- Export QA now validates all eight visible machines independently.

## M2.38

M2.38 gives the isolated arcade room a cohesive finish while preserving the safe M2.37 layout. The
checkerboard is now a readable large-format polished tile floor with subtle grout and edge shading.
Dark baseboards, crown trim, corner covers, native-style red/white perimeter neon, warm ceiling
panels, and balanced fill lighting finish the five-sided room without washing out the original
cabinet art. The chamfer double door now has kick plates, push bars, and a lit exit marker.

The interior is now explicitly sealed: the oversized hidden safety floor and collision roof remain,
and a visible/collidable five-sided acoustic-tile ceiling closes the exact chamfered footprint. Wall
segments overlap at every corner, with trim columns concealing the joins. The build exporter checks
the actual visible-machine bounds and refuses layouts that cross a wall, overlap another machine, or
pull either wall row away from its wall.

Wall build-surface proxies now reference the current chamfer and full north-wall objects (the old
names predated M2.37), inherit the exact finished wall transform, and compensate for parent scale.
The floor grid and its thin tile colliders sit 1 mm above the visible floor, keeping placement reliable
without making placed assets float.

The Blender preparation step also removes stale one-frame FBX animation tracks. Those tracks were
silently restoring original transforms after a reload, which could make the editable cabinets tiny
and discard the corrected layout. A newly generated `ScheduleIArcade_Editable.blend` now retains the
M2.37 placements and subsequent user edits correctly.

The red/white emission values and acoustic ceiling treatment were matched to the user's local
Schedule I asset rip; no ripped game asset is redistributed by this project. See `TEST_M2.38.txt` for
the focused visual, sealing, placement, and Blender verification pass.

## M2.37
M2.37 corrects the annotated layout again: the generated double doors are cut into the diagonal/chamfer wall marked red, the pinball pair moves to the long green-marked wall, the upright playable cabinet moves to the orange/pixel-art wall, and Pac-Man moves into the blue-marked lane. Props now use their own centered pivots, so moving/rotating them cannot orbit around the FBX scene origin.

Blender is now supported as a direct layout editor. Double-click `OPEN_ARCADE_IN_BLENDER.cmd` once to generate `BlenderEditable/ScheduleIArcade_Editable.blend`. After editing and saving the `.blend`, `BUILD_AND_INSTALL.cmd` automatically launches Blender headlessly and exports a raw-scene v2 package so your machine positions/rotations are used in game.


M2.36 applies the latest annotated room layout: the double door is on the red-marked far-right section of the back wall, the combined pinball mesh is moved into the green back-wall row, the upright arcade cabinet is moved to the orange left-wall area, and the Pac-Man cocktail cabinet is rotated and moved into the blue lane. The 90-second no-fade ambience loop and Air Hockey texture fix remain. The included Unity editor tools now generate an editable prefab with matching red/green/orange/blue layout guides so the room can be rearranged visually without changing C#.

# M2.28 - Dedicated Arcade Property/Grid Identity

The arcade now owns its own build PropertyCode/container and stable Grid GUID. See `TEST_M2.28.txt`.

# Schedule I Arcade M2.27

Buildable/test package for MelonLoader 0.7.3 + S1API IL2CPP.

## What changed in M2.23

- Removed the entire experimental Cuke vending-machine replacement system.
- Removed the entire experimental ATM replacement system.
- Removed AssetRipper machine harvesting, GLB machine loading, machine purchase UI, ATM UI, and live-machine cloning code.
- The downloaded fake red vending prop and OUT OF ORDER machine stay hidden, leaving those areas open for player decoration.
- Added native Schedule I wall-surface registration to the real textured arcade interior. This is intended to let wall-mounted vanilla buildables, especially a TV, target the arcade walls through the normal building system.
- Build-surface discovery runs once on arcade entry and does not reintroduce the old recurring world-scan lag.

## Build/install

Run `BUILD_AND_INSTALL.cmd`.

The script resolves the current Schedule I install, MelonLoader 0.7.3, S1API and generated IL2CPP references, compiles `ScheduleIArcade.dll`, and installs the DLL plus the packaged real arcade scene assets.

## Test

1. Enter through the vanilla Northtown Arcade door.
2. Confirm the fake vending and OUT OF ORDER props are still hidden.
3. Equip/place a wall-mounted buildable such as the TV and aim at an arcade wall.
4. Look for `Arcade interior building enabled on ... wall surface(s).` in MelonLoader.
5. Verify normal cabinet/exit behavior still works.


M2.24 build-placement fix:
- Adds a native owned-property Grid proxy over the checkerboard arcade floor so floor GridItems such as the vanilla TV can be placed normally.
- Keeps the existing wall Surface proxies for true wall-mounted items.
- Uses one-time discovery on arcade entry; no recurring global scans.


## M2.26 placement crash fix

The proxy Grid now stays parented under the real owned Property hierarchy. Schedule I's native `GridItem.InitializeGridItem` calls `GetProperty(grid.transform).PropertyCode`; moving the proxy Grid under the synthetic arcade room made that lookup null even when `Grid.ParentProperty` was populated. M2.26 positions the Grid in arcade world space without breaking the vanilla Property ancestry, rebinds cloned Tiles to the cloned Grid, and refreshes the Grid coordinate lookup data. Wall Surface proxies follow the same hierarchy-safe rule.


## M2.27 disappearing placed furniture fix

M2.27 changes the native build proxy lifecycle. The cloned Grid is created inactive, receives a fresh serialized GUID before `Grid.Awake`, and is only activated after its Property ancestry, tiles, collision and arcade world transform are ready. A targeted `BuildManager.CreateGridItem` postfix tracks furniture created on that proxy Grid and keeps the spawned visual object under the custom arcade room after vanilla/FishNet initialization. Wall Surface proxies are also created inactive and receive a valid `BakedGUID` before `Surface.Awake`, preventing the invalid-GUID `FormatException` seen in M2.26.


M2.31 AMBIENCE COMPILE FIX
- Includes the user-supplied arcade ambience MP3 at Mods/ScheduleIArcade/Assets/Audio/arcade_ambience.wav.
- Fades in on arcade entry, loops only while inside, and fades out/stops on exit.
- Preferences: AmbienceEnabled, AmbienceVolume (default 0.35), AmbienceFadeSeconds (default 1.25).
