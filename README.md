# VS3D – Third-Person Renderer for Vampire Survivors

A BepInEx mod that adds a third-person (and first-person) 3D view to Vampire Survivors. Each stage is rebuilt in 3D from the game's own art: floors become the ground, wall art is stood up as walls, pits are sunk into the floor, and props, characters, enemies and pickups are drawn as camera-facing billboards. Gameplay is unchanged; F8 switches back to the original 2D view at any time.

## Download

Get the latest zip from the [Releases](../../releases) page. Install instructions are in the README inside the zip. In short:

1. Install BepInEx 6 IL2CPP **be.788 or newer** ([be.788 download](https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip)) into the game folder, launch the game once, then close it.
2. Copy the zip's `BepInEx` folder into the game folder.
3. Start a run. It begins in 3D.

## Controls

| Key | Action |
|---|---|
| F8 | Switch between 2D and 3D |
| Mouse / wheel | Orbit / zoom (scroll in past the closest zoom for first person) |
| Q / E | Turn the camera in steps |
| F7 | Show/hide the 3D stage |
| F9 | Debug panel |
| F10 | Dump the stage classification to `BepInEx/VS3D_grid.txt` |

## Building from source

Requirements: .NET SDK 6 or newer, and the game with BepInEx be.788+ installed and launched once (the build references `BepInEx/core` and the generated `BepInEx/interop` assemblies).

```bash
cd Mod
dotnet build -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Vampire Survivors"
```

`GameDir` defaults to the standard Steam location. The build copies `VS3D.dll` into `<GameDir>/BepInEx/plugins/VS3D/`. Copy `config/VS3D/tiles/*.json` from this repo to `<GameDir>/BepInEx/config/VS3D/tiles/` as well; they classify each map's obstacle groups.

The default build is VS-only. `-p:VsOnly=false` builds an experimental variant that can borrow 3D environment pieces from a local Vampire Crawlers install; it is not part of the releases.

The `vs3d_core` asset bundle (ground texture) shipped in the release zip is built from a separate Unity project and is not included here.

## Layout

- `Mod/Bootstrap`: plugin entry point and the runtime driver
- `Mod/SimulationBridge`: read-only view of the game's entities and player
- `Mod/Camera`: third/first-person camera, 2D/3D switch, render hooks
- `Mod/Rendering`: billboards, health bar, ground and the stage rebuild (`Stage/CellClassifier.cs` decides what is wall, pit, prop or floor)
- `config/VS3D/tiles`: per-map obstacle classes

## Disclaimer

VS3D is a fan-made mod and is not affiliated with poncle. Vampire Survivors and its art belong to poncle. This repository contains no game files; all art is read from your own installation at runtime.
