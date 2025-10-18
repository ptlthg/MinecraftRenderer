using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

	private string CreateTestPack(string id, Rgba32 color,
		IReadOnlyDictionary<string, Rgba32>? namespaceColors = null,
		string? rootOverride = null,
		bool includePackPng = true,
		Rgba32? packIconColor = null)
	{
		var baseRoot = rootOverride ?? _tempRoot;
		Directory.CreateDirectory(baseRoot);
		var packRoot = Path.Combine(baseRoot, id);
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

		if (includePackPng)
		{
			var iconColor = packIconColor ?? color;
			var iconPath = Path.Combine(packRoot, "pack.png");
			using var icon = new Image<Rgba32>(32, 32, iconColor);
			icon.Save(iconPath);
		}

		return packRoot;
	}

	[Fact]
	public void RegisterAllPacksRegistersPacksUnderRoot()
	{
		var packsRoot = Path.Combine(_tempRoot, "packs-root");
		Directory.CreateDirectory(packsRoot);
		var nestedRoot = Path.Combine(packsRoot, "nested");
		Directory.CreateDirectory(nestedRoot);

		CreateTestPack("top-pack", new Rgba32(10, 200, 240, 255), rootOverride: packsRoot);
		CreateTestPack("nested-pack", new Rgba32(140, 50, 200, 255), rootOverride: nestedRoot);
		Directory.CreateDirectory(Path.Combine(packsRoot, "ignore-me"));

		var registry = TexturePackRegistry.Create();
		var topLevel = registry.RegisterAllPacks(packsRoot);
		Assert.Single(topLevel);
		Assert.True(registry.TryGetPack("top-pack", out _));
		Assert.False(registry.TryGetPack("nested-pack", out _));

		var recursiveRegistry = TexturePackRegistry.Create();
		var recursive = recursiveRegistry.RegisterAllPacks(packsRoot, searchRecursively: true);
		Assert.Equal(2, recursive.Count);
		Assert.True(recursiveRegistry.TryGetPack("top-pack", out _));
		Assert.True(recursiveRegistry.TryGetPack("nested-pack", out _));
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

	[Fact]
	public void PreloadRegisteredPacksCachesPackRenderers()
	{
		var packRoot = CreateTestPack("warmup-pack", new Rgba32(200, 120, 40, 255));
		var registry = TexturePackRegistry.Create();
		registry.RegisterPack(packRoot);

		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory, registry);

		var cacheField = typeof(MinecraftBlockRenderer).GetField("_packRendererCache",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(cacheField);
		var cache = (ConcurrentDictionary<string, MinecraftBlockRenderer>)cacheField!.GetValue(renderer)!;
		Assert.Empty(cache);

		renderer.PreloadRegisteredPacks();

		var stack = registry.BuildPackStack(new[] { "warmup-pack" });
		Assert.True(cache.ContainsKey(stack.Fingerprint));
	}

	[Fact]
	public void ComputeResourceIdRemainsStableAcrossRepeatedCalls()
	{
		var packRoot = CreateTestPack("stable-pack", new Rgba32(180, 80, 200, 255));
		var registry = TexturePackRegistry.Create();
		registry.RegisterPack(packRoot);

		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory, registry);

		var vanillaFirst = renderer.ComputeResourceId("stone");
		var vanillaSecond = renderer.ComputeResourceId("stone");
		Assert.Equal(vanillaFirst.ResourceId, vanillaSecond.ResourceId);
		Assert.Equal(vanillaFirst.PackStackHash, vanillaSecond.PackStackHash);
		Assert.Equal(vanillaFirst.SourcePackId, vanillaSecond.SourcePackId);

		var packOptions = MinecraftBlockRenderer.BlockRenderOptions.Default with
		{
			PackIds = new[] { "stable-pack" }
		};

		var packFirst = renderer.ComputeResourceId("stone", packOptions);
		var packSecond = renderer.ComputeResourceId("stone", packOptions);
		Assert.Equal(packFirst.ResourceId, packSecond.ResourceId);
		Assert.Equal(packFirst.PackStackHash, packSecond.PackStackHash);
		Assert.Equal(packFirst.SourcePackId, packSecond.SourcePackId);

		var itemFirst = renderer.ComputeResourceId("minecraft:diamond_sword");
		var itemSecond = renderer.ComputeResourceId("minecraft:diamond_sword");
		Assert.Equal(itemFirst.ResourceId, itemSecond.ResourceId);

		Assert.NotEqual(vanillaFirst.ResourceId, packFirst.ResourceId);
		Assert.NotEqual(vanillaFirst.PackStackHash, packFirst.PackStackHash);
		Assert.NotEqual(vanillaFirst.SourcePackId, packFirst.SourcePackId);
	}

	[Fact]
	public void RenderGuiItemWithResourceIdMatchesSeparateOperations()
	{
		var packRoot = CreateTestPack("combined-id-pack", new Rgba32(90, 140, 200, 255));
		var registry = TexturePackRegistry.Create();
		registry.RegisterPack(packRoot);

		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory, registry);

		var baselineOptions = MinecraftBlockRenderer.BlockRenderOptions.Default with
		{
			Size = 128
		};

		var packOptions = baselineOptions with
		{
			PackIds = new[] { "combined-id-pack" }
		};

		var tintedOptions = baselineOptions with
		{
			ItemData = new MinecraftBlockRenderer.ItemRenderData(
				Layer0Tint: new Color(new Rgba32(80, 25, 180, 255)))
		};

		var testCases = new[]
		{
			("minecraft:diamond_sword", baselineOptions),
			("minecraft:diamond_sword", packOptions),
			("minecraft:leather_boots", tintedOptions)
		};

		foreach (var (target, testOptions) in testCases)
		{
			using var combined = renderer.RenderGuiItemWithResourceId(target, testOptions);
			using var separateImage = renderer.RenderGuiItem(target, testOptions);
			var resourceId = renderer.ComputeResourceId(target, testOptions);

			AssertImagesEqual(separateImage, combined.Image);

			Assert.Equal(resourceId.ResourceId, combined.ResourceId.ResourceId);
			Assert.Equal(resourceId.PackStackHash, combined.ResourceId.PackStackHash);
			Assert.Equal(resourceId.SourcePackId, combined.ResourceId.SourcePackId);
			Assert.Equal(resourceId.Model, combined.ResourceId.Model);
			Assert.Equal(resourceId.Textures, combined.ResourceId.Textures);
		}
	}

	[Fact]
	public void GetTexturePackIconReturnsIconWhenAvailable()
	{
		var iconPackRoot = CreateTestPack("icon-pack", new Rgba32(64, 128, 200, 255), includePackPng: true);
		var missingIconPackRoot = CreateTestPack("no-icon-pack", new Rgba32(180, 60, 120, 255), includePackPng: false);

		var registry = TexturePackRegistry.Create();
		registry.RegisterPack(iconPackRoot);
		registry.RegisterPack(missingIconPackRoot);

		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory, registry);

		using var icon = renderer.GetTexturePackIcon("icon-pack");
		Assert.NotNull(icon);
		Assert.True(icon!.Width > 0);
		Assert.True(icon.Height > 0);

		var missingIcon = renderer.GetTexturePackIcon("no-icon-pack");
		Assert.Null(missingIcon);

		var unknownIcon = renderer.GetTexturePackIcon("unknown-pack");
		Assert.Null(unknownIcon);

		var packOptions = MinecraftBlockRenderer.BlockRenderOptions.Default with
		{
			PackIds = new[] { "icon-pack" }
		};
		renderer.ComputeResourceId("stone", packOptions);

		var cacheField = typeof(MinecraftBlockRenderer).GetField("_packRendererCache",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(cacheField);
		var cache = (ConcurrentDictionary<string, MinecraftBlockRenderer>)cacheField!.GetValue(renderer)!;
		var stack = registry.BuildPackStack(new[] { "icon-pack" });
		Assert.True(cache.TryGetValue(stack.Fingerprint, out var packRenderer));
		using var iconFromPackRenderer = packRenderer.GetTexturePackIcon("icon-pack");
		Assert.NotNull(iconFromPackRenderer);
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

	private static void AssertImagesEqual(Image<Rgba32> expected, Image<Rgba32> actual)
	{
		Assert.Equal(expected.Width, actual.Width);
		Assert.Equal(expected.Height, actual.Height);

		for (var y = 0; y < expected.Height; y++)
		{
			var expectedRow = expected.DangerousGetPixelRowMemory(y).Span;
			var actualRow = actual.DangerousGetPixelRowMemory(y).Span;
			Assert.True(expectedRow.SequenceEqual(actualRow), $"Pixel mismatch at row {y}");
		}
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
