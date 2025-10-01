using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MinecraftRenderer.TexturePacks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace MinecraftRenderer.Tests;

public sealed class TexturePackTests : IDisposable
{
	private static readonly string AssetsDirectory =
		Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "minecraft"));

	private readonly string _tempRoot;

	public TexturePackTests()
	{
		_tempRoot = Path.Combine(Path.GetTempPath(), "MinecraftRenderer_TexturePackTests", Guid.NewGuid().ToString());
		Directory.CreateDirectory(_tempRoot);
	}

	[Fact]
	public void ComputeResourceIdIncludesPackOverrides()
	{
		var packRoot = CreateTestPack("testpack", new Rgba32(220, 20, 60, 255));
		var registry = TexturePackRegistry.Create();
		registry.RegisterPack(packRoot);

		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory, registry);
		using var vanillaStone = renderer.RenderBlock("stone", MinecraftBlockRenderer.BlockRenderOptions.Default);
        vanillaStone.SaveAsPng(Path.Combine(_tempRoot, "vanilla_stone.png"));

		var packOptions = MinecraftBlockRenderer.BlockRenderOptions.Default with
		{
			PackIds = new[] { "testpack" },
			Size = 128
		};

		using var packStone = renderer.RenderBlock("stone", packOptions);
        packStone.SaveAsPng(Path.Combine(_tempRoot, "pack_stone.png"));

		AssertFalseImageEqual(vanillaStone, packStone);

		var vanillaId = renderer.ComputeResourceId("stone");
		var packId = renderer.ComputeResourceId("stone", packOptions);

		Assert.NotEqual(vanillaId.ResourceId, packId.ResourceId);
		Assert.Equal("vanilla", vanillaId.SourcePackId);
		Assert.Equal("testpack", packId.SourcePackId);
	}

	private string CreateTestPack(string id, Rgba32 color, IReadOnlyDictionary<string, Rgba32>? namespaceColors = null)
	{
		var packRoot = Path.Combine(_tempRoot, id);
		Directory.CreateDirectory(packRoot);

		File.WriteAllText(Path.Combine(packRoot, "meta.json"),
			$"{{\n  \"id\": \"{id}\",\n  \"name\": \"{id}\",\n  \"version\": \"1.0.0\",\n  \"description\": \"Test pack\",\n  \"authors\": [\"tests\"]\n}}\n");
		File.WriteAllText(Path.Combine(packRoot, "pack.mcmeta"),
			"{\"pack\": {\"pack_format\": 32, \"description\": \"Test\"}}\n");

		var namespaceEntries = new Dictionary<string, Rgba32>(StringComparer.OrdinalIgnoreCase);
		if (namespaceColors is not null)
		{
			foreach (var entry in namespaceColors)
			{
				namespaceEntries[entry.Key] = entry.Value;
			}
		}

		if (!namespaceEntries.ContainsKey("minecraft"))
		{
			namespaceEntries["minecraft"] = color;
		}

		foreach (var entry in namespaceEntries)
		{
			var texturesDir = Path.Combine(packRoot, "assets", entry.Key, "textures", "block");
			Directory.CreateDirectory(texturesDir);
			using var image = new Image<Rgba32>(16, 16, entry.Value);
			image.Save(Path.Combine(texturesDir, "stone.png"));
		}

		return packRoot;
	}

	[Fact]
	public void PackOverlayRootsRespectCitNamespacePriority()
	{
		var namespaceColors = new Dictionary<string, Rgba32>(StringComparer.OrdinalIgnoreCase)
		{
			["minecraft"] = new Rgba32(32, 32, 32, 255),
			["firmskyblock"] = new Rgba32(64, 224, 135, 255),
			["cittofirmgenerated"] = new Rgba32(220, 220, 20, 255),
			["cit"] = new Rgba32(220, 20, 60, 255)
		};

		var packRoot = CreateTestPack("prioritypack", new Rgba32(128, 128, 128, 255), namespaceColors);
		var registry = TexturePackRegistry.Create();
		registry.RegisterPack(packRoot);

		var stack = registry.BuildPackStack(new[] { "prioritypack" });
		var overlayPaths = stack.OverlayRoots
			.Where(static overlay => string.Equals(overlay.PackId, "prioritypack", StringComparison.OrdinalIgnoreCase))
			.Select(static overlay => overlay.Path)
			.ToList();

		var minecraftPath = Path.GetFullPath(Path.Combine(packRoot, "assets", "minecraft"));
		var firmskyblockPath = Path.GetFullPath(Path.Combine(packRoot, "assets", "firmskyblock"));
		var cittoFirmGeneratedPath = Path.GetFullPath(Path.Combine(packRoot, "assets", "cittofirmgenerated"));
		var citPath = Path.GetFullPath(Path.Combine(packRoot, "assets", "cit"));

		var minecraftIndex = overlayPaths.FindIndex(path => string.Equals(path, minecraftPath, StringComparison.OrdinalIgnoreCase));
		var firmskyblockIndex = overlayPaths.FindIndex(path => string.Equals(path, firmskyblockPath, StringComparison.OrdinalIgnoreCase));
		var cittoFirmGeneratedIndex = overlayPaths.FindIndex(path => string.Equals(path, cittoFirmGeneratedPath, StringComparison.OrdinalIgnoreCase));
		var citIndex = overlayPaths.FindIndex(path => string.Equals(path, citPath, StringComparison.OrdinalIgnoreCase));

		Assert.True(minecraftIndex >= 0, "Expected minecraft namespace to be present in overlay roots.");
		Assert.True(firmskyblockIndex >= 0, "Expected firmskyblock namespace to be present in overlay roots.");
		Assert.True(cittoFirmGeneratedIndex >= 0, "Expected cittofirmgenerated namespace to be present in overlay roots.");
		Assert.True(citIndex >= 0, "Expected cit namespace to be present in overlay roots.");

		Assert.True(minecraftIndex < firmskyblockIndex,
			$"Expected minecraft namespace to have lower priority than firmskyblock (indexes {minecraftIndex} vs {firmskyblockIndex}).");
		Assert.True(firmskyblockIndex < cittoFirmGeneratedIndex,
			$"Expected firmskyblock namespace to have lower priority than cittofirmgenerated (indexes {firmskyblockIndex} vs {cittoFirmGeneratedIndex}).");
		Assert.True(cittoFirmGeneratedIndex < citIndex,
			$"Expected cittofirmgenerated namespace to have lower priority than cit (indexes {cittoFirmGeneratedIndex} vs {citIndex}).");
	}

	private static void AssertFalseImageEqual(Image<Rgba32> baseline, Image<Rgba32> candidate)
	{
		bool PixelsEqual(Image<Rgba32> a, Image<Rgba32> b)
		{
			var width = Math.Min(a.Width, b.Width);
			var height = Math.Min(a.Height, b.Height);
			for (var y = 0; y < height; y += 8)
			{
				var rowA = a.DangerousGetPixelRowMemory(y).Span;
				var rowB = b.DangerousGetPixelRowMemory(y).Span;
				for (var x = 0; x < width; x += 8)
				{
					if (!rowA[x].Equals(rowB[x]))
					{
						return false;
					}
				}
			}

			return true;
		}

		Assert.False(PixelsEqual(baseline, candidate), "Expected images rendered with texture pack to differ from vanilla render.");
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_tempRoot))
			{
				// Directory.Delete(_tempRoot, recursive: true);
			}
		}
		catch
		{
			// Ignore cleanup failures during test teardown.
		}
	}
}
