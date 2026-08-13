# Cabinet Architecture

A cabinet is discovered by naming markers in the Unity prefab. No custom MonoBehaviour has to be serialized into the AssetBundle, which avoids IL2CPP script-component problems.

Required hierarchy for cabinet 01:

```text
ARCADE_CABINET_01
└── ...cabinet mesh/colliders...
    └── ARCADE_SCREEN_01   <- Renderer receiving the game framebuffer
```

Runtime flow:

```text
Cabinet raycast
  -> CabinetRegistryService
  -> ArcadeSessionService
  -> ArcadeGameRegistry
  -> IArcadeGame
  -> Texture framebuffer
  -> cabinet screen Material
```

Future cabinets can map to different game IDs. The game runtime is separated from the cabinet, so a cabinet does not know whether it is running Doom, a native minigame, or an emulator-backed title.
