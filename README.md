# MinecraftRenderer

.NET renderer for Minecraft heads, blocks, and block-based items using [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp).

## Features

- Render player heads with arbitrary rotations and overlays using `MinecraftHeadRenderer`.
- Render block and item models (including GUI transforms and per-face textures) with `MinecraftBlockRenderer`.
- Default block and item renders keep the Minecraft inventory GUI rotation while staying orthographic by default.
- Loads vanilla model and texture metadata directly from the JSON files in the `minecraft/` directory – You need to unzip `client.jar` for this (it's the assets directory).
- Ships with a small unit test suite to verify rendering stays functional.

## Getting started

### Option 1: Automatic Asset Download (Recommended)

The easiest way to get started is to download Minecraft assets automatically:

```csharp
using MinecraftRenderer;

// Download and extract assets (requires accepting Minecraft's EULA)
var assetsPath = await MinecraftAssetDownloader.DownloadAndExtractAssets(
    version: "1.21.9",
    acceptEula: true,
    progress: new Progress<(int Percentage, string Status)>(p => 
        Console.WriteLine($"[{p.Percentage}%] {p.Status}"))
);

// Create renderer from downloaded assets
using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(assetsPath);
using var image = renderer.RenderBlock("stone", new MinecraftBlockRenderer.BlockRenderOptions(Size: 256));
image.Save("stone.png");
```

**Note:** Minecraft assets are © Mojang Studios and subject to [Minecraft's EULA](https://www.minecraft.net/en-us/eula) and [Usage Guidelines](https://www.minecraft.net/en-us/usage-guidelines).

### Option 2: Manual Asset Setup

1. Extract `client.jar` from Minecraft (unzip the jar file)
2. Copy the `assets/minecraft/` directory to your project

Then use the renderer:

```csharp
using MinecraftRenderer;

var dataPath = Path.Combine(Environment.CurrentDirectory, "minecraft");
using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(dataPath);
using var image = renderer.RenderBlock("stone", new MinecraftBlockRenderer.BlockRenderOptions(Size: 256));
image.Save("stone.png");
```

### Rendering Heads

```csharp
using MinecraftRenderer;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using var skin = Image.Load<Rgba32>("steve.png");
var head = MinecraftHeadRenderer.RenderHead(new MinecraftHeadRenderer.RenderOptions(256, -35, 25, 0), skin);
head.Save("head.png");
```

3. Render a head with the existing head renderer:

	```csharp
	using MinecraftRenderer;
	using SixLabors.ImageSharp;
	using SixLabors.ImageSharp.PixelFormats;

	using var skin = Image.Load<Rgba32>("steve.png");
	var head = MinecraftHeadRenderer.RenderHead(new MinecraftHeadRenderer.RenderOptions(256, -35, 25, 0), skin);
	head.Save("head.png");
	```

## Minecraft Asset Management

### Downloading Assets

The `MinecraftAssetDownloader` class provides helper methods for working with Minecraft assets:

```csharp
// Get available versions
var versions = await MinecraftAssetDownloader.GetAvailableVersions(includeSnapshots: false);
Console.WriteLine($"Available versions: {string.Join(", ", versions.Take(5))}");

// Get latest version
var latestRelease = await MinecraftAssetDownloader.GetLatestVersion(snapshot: false);
Console.WriteLine($"Latest release: {latestRelease}");

// Download specific version
var assetsPath = await MinecraftAssetDownloader.DownloadAndExtractAssets(
    version: "1.21.9",
    outputPath: "./minecraft",
    acceptEula: true,
    forceRedownload: false,
    progress: new Progress<(int Percentage, string Status)>(p => 
        Console.WriteLine($"[{p.Percentage}%] {p.Status}"))
);
```

**Important Legal Notice:**
- Minecraft assets are © Mojang Studios
- You must accept [Minecraft's EULA](https://www.minecraft.net/en-us/eula) to download assets
- Review [Minecraft's Usage Guidelines](https://www.minecraft.net/en-us/usage-guidelines) before use
- This library does not redistribute Minecraft assets - it downloads them from official Mojang servers at runtime
- The MinecraftRenderer library itself is licensed under MIT, but Minecraft assets remain the property of Mojang Studios

## Data files

The project expects the vanilla JSON metadata and textures located under the `minecraft/` directory:

- `blocks_models.json`: merged block model definitions.
- `blocks_textures.json`: mappings from block names to model IDs.
- `items_textures.json`: mappings for item names (used when available).
- `texture_content.json` plus the resource folders (e.g. `minecraft/blocks/`, `minecraft/items/`): PNG texture data.

To integrate other Minecraft versions or resource packs, replace these files with your own exported data.

## Atlas generation

Quickly inspect all rendered assets by exporting atlas images across multiple camera angles:

```powershell
dotnet run --project CreateAtlases/CreateAtlases.csproj -- --output atlases
```

The CLI auto-discovers the `minecraft/` directory when run from the repo root. Customise the export with options such as:

```powershell
dotnet run --project CreateAtlases/CreateAtlases.csproj -- --tile-size 192 --columns 10 --rows 10 --views isometric_right,front
dotnet run --project CreateAtlases/CreateAtlases.csproj -- --blocks stone,grass_block --items diamond_sword
```

Working with Hypixel-style SNBT exports? Point the generator at a directory of `.snbt` item stacks to produce dedicated atlases (each file becomes its own tile) while still honouring any texture packs passed via `--texture-pack-id`:

```powershell
dotnet run --project CreateAtlases/CreateAtlases.csproj -- --snbt-dir snbt-test-items --texture-pack-id firmskyblock
```

SNBT atlases are written to an `atlases/snbt/` subfolder alongside the standard block/item output, and their manifests include any per-item parsing or render errors.

Prefer to drive it from code? Use the generator API directly:

```csharp
using MinecraftRenderer;

var dataPath = Path.Combine(Environment.CurrentDirectory, "data");
using var renderer = MinecraftBlockRenderer.CreateFromDataDirectory(dataPath);

var results = MinecraftAtlasGenerator.GenerateAtlases(
	renderer,
	outputDirectory: Path.Combine(Environment.CurrentDirectory, "atlases"),
	views: MinecraftAtlasGenerator.DefaultViews,
	tileSize: 160,
	columns: 12,
	rows: 12);
```

Each atlas is accompanied by a JSON manifest listing the block/item occupying each grid cell (with render errors captured per entry). Provide `blockFilter`/`itemFilter` sequences to focus on subsets or tweak `tileSize`/`columns`/`rows` to split the output across more pages.

## Hypixel Inventory Parsing

Parse Hypixel Skyblock inventory data (1.8.9 NBT format) from the Hypixel API:

```csharp
using MinecraftRenderer.Hypixel;

// Parse base64-encoded, gzipped NBT inventory data
var inventoryData = await hypixelApi.GetInventoryDataAsync(playerUuid);
var items = InventoryParser.ParseInventory(inventoryData);

foreach (var item in items)
{
	Console.WriteLine($"{item.SkyblockId}: {item.DisplayName}");
	
	// Get deterministic texture ID for caching/lookup
	var textureId = TextureResolver.GetTextureId(item);
	
	// Access rich metadata
	if (item.Enchantments != null)
		Console.WriteLine($"  Enchantments: {item.Enchantments.Count}");
	if (item.Gems != null)
		Console.WriteLine($"  Gems: {string.Join(", ", item.Gems.Keys)}");
	if (item.CustomData != null)
		Console.WriteLine($"  Custom texture: {TextureResolver.GetCustomTexturePath(item)}");
}
```

The `HypixelItemData` record provides convenient access to:
- **SkyblockId**: Hypixel's internal item ID (e.g., `"JUJU_SHORTBOW"`)
- **ItemId**: Normalized Minecraft ID (e.g., `"skyblock:juju_shortbow"`)
- **DisplayName**: Formatted display name with color codes
- **Enchantments**: NBT compound of enchantments
- **Gems**: NBT compound of socketed gems
- **Attributes**: NBT compound of item attributes (Kuudra armor, etc.)
- **CustomData**: Firmament texture pack overrides

The `TextureResolver` generates deterministic texture IDs that account for gems, attributes, and custom textures:
- Plain items: `skyblock:juju_shortbow`
- Items with gems: `skyblock:axe_of_the_shredded?gems=COMBAT_0,JASPER_0`
- Items with attributes: `skyblock:fervor_helmet?attrs=shard,tier`
- Custom textures: `custom:firmament/special_texture`

These IDs can be used as cache keys or to look up models in texture packs.

## Custom Skull Textures

For player heads (particularly Hypixel Skyblock custom items), you can provide a custom texture resolver when the item NBT doesn't include profile data:

```csharp
var options = BlockRenderOptions.Default with
{
    Size = 256,
    ItemData = new ItemRenderData(CustomData: customDataCompound),
    SkullTextureResolver = (customDataId, profile) =>
    {
        // Look up texture by custom_data.id (e.g., from NEU ItemRepo)
        if (customDataId == "JERRY_STAFF")
        {
            return "ewogICJ0aW1lc3RhbXAiIDogMTYzMzQ..."; // Base64 texture payload
        }
        return null; // Fall back to profile or default skin
    }
};

using var image = renderer.RenderGuiItem("minecraft:player_head", options);
```

The resolver receives:
- `customDataId`: The `custom_data.id` value (Skyblock item ID), if present
- `profile`: The profile NBT compound, if present

Return either:
- Base64-encoded texture payload (standard Minecraft format)
- Direct URL to `textures.minecraft.net`
- `null` to fall back to profile data or default skin

See [docs/skull-texture-resolver.md](docs/skull-texture-resolver.md) for integration examples with NEU ItemRepo and caching strategies.

## Running tests

The solution includes an xUnit test project that renders a stone block and asserts opaque pixels are produced. Run the suite with:

```powershell
dotnet test
```

## License

See [LICENSE](LICENSE).
