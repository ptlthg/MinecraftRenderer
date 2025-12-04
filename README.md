# MinecraftRenderer

.NET renderer for Minecraft heads, blocks, and GUI-ready items using [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp).

## Features

- `MinecraftBlockRenderer` renders block and item models with Minecraft's GUI transforms, lighting, biome tints, and pack overlays.
- `RenderItemFromNbt` and `RenderItemFromNbtWithResourceId` turn vanilla or Hypixel SNBT payloads into images (and expose deterministic resource IDs plus animation metadata).
- `MinecraftHeadRenderer` draws player heads from skins, custom data, or resolver-provided textures.
- Texture pack stacks, overlays, and custom data directories are supported without rebuilding the renderer.
- Skull rendering accepts pluggable resolvers that see the full item context (`SkullResolverContext`).
- Ships with an xUnit suite that exercises model rendering, lighting, texture packs, and Hypixel item parsing.

## Install

Install from the [NuGet package](https://www.nuget.org/packages/MinecraftRenderer)!
```powershell
dotnet add package MinecraftRenderer
```

## Quick start

1. Restore the solution and run the tests:

	```powershell
	dotnet test
	```

2. Create a renderer backed by aggregated JSON data (see **Data files**):

	```csharp
	using MinecraftRenderer;

	var dataPath = Path.Combine(Environment.CurrentDirectory, "minecraft");
	using var renderer = MinecraftBlockRenderer.CreateFromDataDirectory(dataPath);
	```

3. Render a block or item:

	```csharp
	using var block = renderer.RenderBlock(
		"stone",
		MinecraftBlockRenderer.BlockRenderOptions.Default with { Size = 256 });
	block.Save("stone.png");

	using var item = renderer.RenderItem(
		"minecraft:diamond_sword",
		MinecraftBlockRenderer.BlockRenderOptions.Default with { Size = 128 });
	item.Save("diamond_sword.png");
	```

4. Render directly from SNBT and capture the resource ID that fingerprints the model/texture stack:

	```csharp
	var nbt = NbtDocument.Parse(@"{
	  id: ""minecraft:player_head"",
	  Count: 1b,
	  components: {
	    ""minecraft:profile"": {
	      id: ""abcd-efgh"",
	      properties: [{ name: ""textures"", value: ""...base64..."" }]
	    }
	  }
	}");

	using var rendered = renderer.RenderItemFromNbtWithResourceId(nbt);
	rendered.Image.Save("head.png");
	Console.WriteLine($"Resource ID: {rendered.ResourceId.ResourceId}");
	```

`RenderAnimatedItemFromNbtWithResourceId` returns an `AnimatedRenderedResource` when any bound textures carry animation metadata; static items still produce a single frame.

## Hypixel Skyblock

The main intention of this was for rendering Hypixel Skyblock items on https://elitebot.dev/. To do this you'll have to write an adapter to transform skyblock items from however you have them saved into something that this renderer will recognize. The main challenge here is that Hypixel sends you item data in 1.8.9 format, but this renderer wants modern SNBT item data.

You can see an example implementation here: https://github.com/EliteFarmers/API/tree/master/EliteAPI/Features/Textures/Services
That project also uses the [SkyblockRepo](https://www.nuget.org/packages/SkyblockRepo) package.

## Rendering APIs

- `RenderBlock`, `RenderItem`, and `RenderGuiItemFromTextureId` cover direct model/texture rendering.
- `RenderItemFromNbt` and `RenderItem` overloads accept `ItemRenderData` to apply dyes, custom data, or skull profiles.
- `RenderItemFromNbtWithResourceId` / `RenderAnimatedItemFromNbtWithResourceId` return `RenderedResource` / `AnimatedRenderedResource` (image plus pack-aware fingerprint, model path, and resolved textures).
- `BlockRenderOptions` control camera, transforms, lighting, texture packs, and skull texture resolvers. Clone with `with` to tweak individual properties.

## Data files

Optional `customdata/` overlays located next to the assets tree are detected automatically. Texture pack registries can be supplied via `TexturePackRegistry` to build layered pack stacks.

The library ships with `MinecraftAssetDownloader` to fetch and unzip asset files directly from Mojang:

```csharp
using MinecraftRenderer;

Console.WriteLine("Downloading Minecraft 1.21.10 assets...");
var assetsPath = await MinecraftAssetDownloader.DownloadAndExtractAssets(
	version: "1.21.10",
	acceptEula: true, // Review https://www.minecraft.net/en-us/eula first
	progress: new Progress<(int Percentage, string Status)>(p =>
		Console.WriteLine($"[{p.Percentage}%] {p.Status}"))
);

Console.WriteLine($"Assets extracted to: {assetsPath}");

using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(assetsPath);
using var stone = renderer.RenderBlock("stone", new MinecraftBlockRenderer.BlockRenderOptions(Size: 256));
stone.Save("stone.png");
```

`MinecraftAssetDownloader.GetAvailableVersions` and `GetLatestVersion` can help you pick the right version before downloading, but not all versions are guaranteed to work.

## Atlas generator

`CreateAtlases` is a CLI utility that dumps pages of renders for debugging or content reviews. Run it from the repo root when you want a quick visual diff:

```powershell
dotnet run --project CreateAtlases/CreateAtlases.csproj -- --output atlases
```

Additional flags exist for custom camera views, SNBT item directories, or animated outputs, but the tool is not required for normal library usage.

It's possible that this might be expanded into proper atlases for actual use, but right now it is just for debugging.

## Running tests

Execute the regression suite from the repository root:

```powershell
dotnet test
```

## License

See [LICENSE](LICENSE).
