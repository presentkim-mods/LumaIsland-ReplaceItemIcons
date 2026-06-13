# ReplaceItemIcons

A [BepInEx](https://github.com/BepInEx/BepInEx) plugin for **Luma Island** that allows you to replace any in-game item icon with custom PNG images.

## Features

- **Replace icons** — Drop a `.png` file named after an item (e.g., `Copper_Ore.png`) next to the mod DLL, and the in-game icon will automatically be replaced on next launch.
- **Extract icons** (optional) — Enable the `EnableItemIconExtract` config option to dump every item's original icon as a PNG into an `extracted/` folder. Useful as a reference for creating replacement textures.
- **High-performance extraction** — Uses GPU async readback with configurable throughput limits to minimise frame stutter during extraction.
- **Safe unload** — Restores original icons when the plugin is disabled or unloaded.

## Installation

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) for Luma Island.
2. Place `ReplaceItemIcons.dll` in `BepInEx/plugins/ReplaceItemIcons/`.
3. To replace icons, place PNG files with the matching item name in the same directory.
4. Launch the game.

> PNG files are matched case-insensitively. Use `FilterMode.Point` (no anti-aliasing) for pixel-art icons.

## Usage

### Replace Icons

Place PNG files next to `ReplaceItemIcons.dll`.  
The filename (without extension) must match the item's internal `Name` field exactly (case-insensitive).

Example files:
```
BepInEx/plugins/ReplaceItemIcons/
├── ReplaceItemIcons.dll
├── Copper_Ore.png
├── Iron_Ore.png
└── Starfruit.png
```

### Extract Icons

Edit `BepInEx/config/kim.present.lumaisland.replaceitemicons.cfg`:

```ini
[Extract Settings]
EnableItemIconExtract = true
```

The plugin will extract every item icon as a PNG to the `extracted/` folder on the next launch.

## Building

Open the solution in Visual Studio or build with MSBuild:

```
msbuild ReplaceItemIcons.sln
```

The `.csproj` expects the game to be installed at `C:\Program Files (x86)\Steam\steamapps\common\Luma Island` (adjust as needed). A post-build step copies the DLL to `BepInEx/plugins/ReplaceItemIcons/`.

## Technical Details

- **Patching**: A Harmony prefix on `InventoryItemsExtensions.GetSprite(InventoryItemsData, int?)` checks a `Dictionary<string, Sprite>` before calling the original method.
- **Extraction pipeline**: Renders each item sprite to an orthographic camera, reads back the pixel data via `AsyncGPUReadback`, and encodes it as PNG using `ImageConversion.EncodeNativeArrayToPNG`.
- **Throughput tuning**: Constants `MaxInFlightReadbacks`, `MaxReadbackStartsPerFrame`, and `MaxPngWritesPerFrame` balance extraction speed against frame stutter — see the comments in `ItemIconExtractor.cs` for details.

## License

MIT
