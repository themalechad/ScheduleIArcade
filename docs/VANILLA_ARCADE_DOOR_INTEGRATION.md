# Vanilla Arcade Door Integration

The entrance bridge is intentionally built around Schedule I's existing door/NPC systems rather than placing a new world-space teleport button on top of the building.

## Vanilla structure being reused

The game's stripped project exposes this relevant chain:

```text
ScheduleOne.Doors.StaticDoor
  AccessPoint
  IntObj
  Building -> NPCEnterableBuilding
  Interacted()
  NPCSelected(NPC)

ScheduleOne.Map.NPCEnterableBuilding
  BuildingName
  Occupants
  GetSummonableNPCs()

ScheduleOne.UI.NPCSummonMenu
  Canvas
  EntryContainer
  Entries
  IsOpen
  Open(...)
  Close()
  NPCSelected(NPC)
```

On the IL2CPP branch these game-side types may be generated under `Il2CppScheduleOne.*`. The mod therefore discovers them by runtime type/member names instead of taking a hard compile-time dependency on Assembly-CSharp types.

## Entrance behavior

`ArcadeEntranceService` scans loaded `MonoBehaviour`s for a `StaticDoor` whose linked building name or hierarchy contains `Arcade`. The strongest match is `BuildingName == "Arcade"`.

When the player looks at that door and presses **E**, Schedule I still executes its normal door interaction. The mod records that the next summon menu belongs to the Arcade.

If `NPCSummonMenu` opens, the mod clones one of the existing menu entry GameObjects, preserving the game's own layout/style, replaces its label with:

```text
Enter Arcade
```

and assigns the cloned button to the custom interior teleport.

The clone is destroyed when the vanilla menu closes so other buildings' summon menus are not permanently modified.

## No-occupant fallback

A vanilla `StaticDoor` may decline to open `NPCSummonMenu` when there are no summonable NPCs inside. After a short wait, the mod presents a minimal fallback choice so the custom arcade remains enterable even when nobody is available to answer the knock.

This fallback is only used when the native menu did not appear.

## Exterior return behavior

Immediately before entering, the mod records `Player.Local.Position` while the player is standing at the real Arcade door. That exact position is the primary exit destination.

If it is unavailable, the service falls back to the vanilla door's `AccessPoint`, then finally to a point in front of the door transform.

This makes leaving the custom room return the player to the existing Northtown Arcade exterior instead of an arbitrary development coordinate.
