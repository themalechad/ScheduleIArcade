# Next Steps After Milestone 1.1 Works In Game

1. Validate automatic detection of the Northtown Arcade `StaticDoor` on Schedule I `0.4.6f12` and capture the MelonLoader log if the building name differs from `Arcade`.
2. Position `ARCADE_SPAWN` and `ARCADE_EXIT` exactly at the custom model's entrance/exit door.
3. Adjust `ARCADE_SCREEN_01` so the runtime texture perfectly overlays the first upright cabinet monitor.
4. Confirm room scale, floor height, wall collision, and material/shader appearance in Schedule I.
5. Add proper player movement/camera lock and cabinet-facing pose while a game is active.
6. Duplicate/mark additional upright cabinets (`ARCADE_CABINET_02`, `ARCADE_CABINET_03`, etc.).
7. Add cabinet-to-game configuration instead of the current test-pattern default.
8. Add the first external game runtime behind `IArcadeGame`.
9. Add machine occupancy synchronization for multiplayer before sharing any framebuffer data.
10. Add optional attract screens/high-score persistence once the multi-cabinet path is stable.
