# Milestone 1.1 - Vanilla Arcade Door + One Playable Cabinet

This milestone keeps the first playable slice small, but it now uses the real Schedule I Arcade as the entrance instead of development hotkeys.

## Required behavior

1. Load the custom arcade room from the AssetBundle.
2. Keep the room textured, opaque, and collidable.
3. Spawn the custom room away from the normal world while remaining in the loaded Main scene.
4. Discover the existing Northtown Arcade `StaticDoor` at runtime.
5. Player knocks/interacts with that vanilla door normally.
6. If Schedule I opens `NPCSummonMenu`, add one extra native-looking option: **Enter Arcade**.
7. If no NPC menu appears because nobody can answer, show the small fallback **Enter Arcade** choice.
8. Choosing **Enter Arcade** teleports the local player to `ARCADE_SPAWN`.
9. Player can walk around the custom room using its generated collision.
10. Look at `ARCADE_CABINET_01` and press **E**.
11. A live animated test framebuffer appears on `ARCADE_SCREEN_01`.
12. Press **ESC** to stop the machine session; the player remains inside the arcade.
13. Walk to the custom room's visible entrance/exit door where `ARCADE_EXIT` is placed.
14. Press **E** on the exit prompt.
15. Return to the exact exterior position captured at the vanilla Arcade door when the player entered.

## Important design rule

The custom room does not replace Schedule I's exterior Arcade building and does not create a second exterior entrance. The existing Northtown Arcade is the world anchor. The mod only redirects the player's choice to a separate custom interior.

## Not yet in this milestone

- Final camera/player pose lock while a machine is actively being played.
- Multiple cabinet assignments.
- ROM/WAD loading.
- Emulator cores.
- Multiplayer machine occupancy synchronization.
- Spectator screen synchronization.
- High scores, tokens, prizes, or economy.

Those come after this entrance/exit + room/cabinet path is validated in the current IL2CPP build.
