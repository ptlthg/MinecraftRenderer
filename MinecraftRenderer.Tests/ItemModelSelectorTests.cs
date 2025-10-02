using System;
using System.Collections.Generic;
using System.IO;
using MinecraftRenderer.Nbt;
using MinecraftRenderer.TexturePacks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace MinecraftRenderer.Tests;

public sealed class ItemModelSelectorTests : IDisposable
{
	private static readonly string AssetsDirectory =
		Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "minecraft"));

	private readonly string _tempRoot;

	public ItemModelSelectorTests()
	{
		_tempRoot = Path.Combine(Path.GetTempPath(), "MinecraftRenderer_ItemModelSelector", Guid.NewGuid().ToString());
		Directory.CreateDirectory(_tempRoot);
	}

	[Fact]
	public void PlayerHeadCustomDataUsesTexturePackModel()
	{
		var packId = "customheadpack";
		var packRoot = CreateCustomHeadPack(packId, new Rgba32(0xD4, 0x34, 0x2C, 0xFF));

		var registry = TexturePackRegistry.Create();
		registry.RegisterPack(packRoot);

		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory, registry);

		var options = MinecraftBlockRenderer.BlockRenderOptions.Default with
		{
			PackIds = new[] { packId },
			Size = 64
		};

		var customData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("custom_head_test"))
		});

		var itemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: customData);
		using var customRender = renderer.RenderItem("player_head", itemData, options);
		var customPixel = SampleOpaquePixel(customRender);
		Assert.Equal(0xD4, customPixel.R);
		Assert.Equal(0x34, customPixel.G);
		Assert.Equal(0x2C, customPixel.B);

		using var fallbackRender = renderer.RenderItem("player_head", options);
		var fallbackPixel = SampleOpaquePixel(fallbackRender);
		Assert.NotEqual(customPixel, fallbackPixel);
	}

	[Fact]
	public void PlayerHeadNestedCustomDataUsesTexturePackModel()
	{
		var packId = "nestedcustomheadpack";
		var modelName = "nested_custom_player_head";
		var itemDefinition =
"""
{
	"model": {
		"type": "condition",
		"property": "component",
		"predicate": "custom_data",
		"value": {
			"id": "nested_head_test",
			"runes": {
				"AXE_FADING_GREEN": 2
			}
		},
		"on_true": {
			"type": "model",
			"model": "minecraft:item/nested_custom_player_head"
		},
		"on_false": {
			"type": "model",
			"model": "minecraft:item/player_head"
		}
	}
}
""";

		var packRoot = CreateCustomHeadPack(packId, new Rgba32(0x12, 0x34, 0x56, 0xFF), itemDefinition, modelName);

		var registry = TexturePackRegistry.Create();
		registry.RegisterPack(packRoot);

		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory, registry);

		var options = MinecraftBlockRenderer.BlockRenderOptions.Default with
		{
			PackIds = new[] { packId },
			Size = 64
		};

		var customData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("nested_head_test")),
			new KeyValuePair<string, NbtTag>("runes", new NbtCompound(new[]
			{
				new KeyValuePair<string, NbtTag>("AXE_FADING_GREEN", new NbtInt(2))
			}))
		});

		var itemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: customData);
		using var customRender = renderer.RenderItem("player_head", itemData, options);
		var customPixel = SampleOpaquePixel(customRender);
		Assert.Equal(0x12, customPixel.R);
		Assert.Equal(0x34, customPixel.G);
		Assert.Equal(0x56, customPixel.B);

		using var fallbackRender = renderer.RenderItem("player_head", options);
		var fallbackPixel = SampleOpaquePixel(fallbackRender);
		Assert.NotEqual(customPixel, fallbackPixel);
	}

	private string CreateCustomHeadPack(string id, Rgba32 color, string? itemDefinitionOverride = null,
		string? modelNameOverride = null)
	{
		var packRoot = Path.Combine(_tempRoot, id);
		Directory.CreateDirectory(packRoot);

		File.WriteAllText(Path.Combine(packRoot, "meta.json"),
			$"{{\n  \"id\": \"{id}\",\n  \"name\": \"{id}\",\n  \"version\": \"1.0.0\",\n  \"description\": \"Test pack\",\n  \"authors\": [\"tests\"]\n}}\n");
		File.WriteAllText(Path.Combine(packRoot, "pack.mcmeta"),
			"{\"pack\": {\"pack_format\": 32, \"description\": \"Test\"}}\n");

		var itemsDir = Path.Combine(packRoot, "assets", "minecraft", "items");
		Directory.CreateDirectory(itemsDir);
		var modelName = modelNameOverride ?? "custom_player_head";
		File.WriteAllText(Path.Combine(itemsDir, "player_head.json"),
			itemDefinitionOverride ?? BuildDefaultPlayerHeadDefinition(modelName));

		var modelsDir = Path.Combine(packRoot, "assets", "minecraft", "models", "item");
		Directory.CreateDirectory(modelsDir);
		File.WriteAllText(Path.Combine(modelsDir, $"{modelName}.json"),
			BuildDefaultPlayerHeadModel(modelName));

		var texturesDir = Path.Combine(packRoot, "assets", "minecraft", "textures", "item");
		Directory.CreateDirectory(texturesDir);
		using var image = new Image<Rgba32>(16, 16, color);
		image.Save(Path.Combine(texturesDir, $"{modelName}.png"));

		return packRoot;
	}

	private static string BuildDefaultPlayerHeadDefinition(string modelName)
		=> "{\n  \"model\": {\n    \"type\": \"condition\",\n    \"property\": \"component\",\n    \"predicate\": \"custom_data\",\n    \"value\": { \"id\": \"custom_head_test\" },\n    \"on_true\": {\n      \"type\": \"model\",\n      \"model\": \"minecraft:item/" + modelName + "\"\n    },\n    \"on_false\": {\n      \"type\": \"model\",\n      \"model\": \"minecraft:item/player_head\"\n    }\n  }\n}\n";

	private static string BuildDefaultPlayerHeadModel(string modelName)
		=> "{\n  \"parent\": \"minecraft:item/generated\",\n  \"textures\": {\n    \"layer0\": \"minecraft:item/" + modelName + "\"\n  }\n}\n";

	private static Rgba32 SampleOpaquePixel(Image<Rgba32> image)
	{
		for (var y = 0; y < image.Height; y++)
		{
			for (var x = 0; x < image.Width; x++)
			{
				var pixel = image[x, y];
				if (pixel.A > 200)
				{
					return pixel;
				}
			}
		}

		throw new InvalidOperationException("No opaque pixel found in rendered item.");
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_tempRoot))
			{
				Directory.Delete(_tempRoot, recursive: true);
			}
		}
		catch
		{
			// Ignore cleanup failures.
		}
	}
}
