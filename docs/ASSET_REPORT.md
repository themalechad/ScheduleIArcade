# Uploaded Arcade Asset Report

Source: `arcade-scene.zip`

The FBX is binary FBX 7.4 (exported from 3ds Max 2018). It contains seven mesh models:

| Model | Vertices | Material | Notes |
|---|---:|---|---|
| Enviorment | 929 | 02 - Default | Room shell / environment |
| Pinball Machine | 2716 | 03 - Default | Pinball prop |
| Arcade Machine | 958 | 08 - Default | Upright cabinet used for milestone 1 |
| Table Top | 910 | 13 - Default | Tabletop machine |
| Crane Game | 481 | 15 - Default | Crane/claw machine |
| Air Hockey | 935 | 19 - Default | Air hockey prop |
| Vendind Machine | 914 | 20 - Default | Vending machine (name is misspelled in source FBX) |

The FBX embeds old absolute texture references from the original author's machine. The Unity preparation script deliberately ignores those paths and rebuilds materials from the textures shipped beside the FBX.

## Texture sets

- Environment: albedo + normal, 1024x1024
- Upright arcade: albedo + normal + specular + emissive, 512x512
- Pinball: albedo + normal + specular + emissive, 512x512
- Tabletop: albedo + normal + specular + emissive, 512x512
- Air hockey: albedo + normal + specular + emissive, 512x512
- Crane: albedo + specular + opacity, 512x512
- Vending: albedo + normal + specular, 512x512

## Wall visibility strategy for M1

The downloaded environment is kept OPAQUE. The generated environment material uses `Cull Off` as a development fallback so single-sided downloaded wall faces render from inside and outside. The environment also gets a non-convex static MeshCollider so walls/floor are physically solid. If testing exposes bad geometry, the production pass should thicken/repair those wall faces in Blender rather than relying permanently on double-sided rendering.
