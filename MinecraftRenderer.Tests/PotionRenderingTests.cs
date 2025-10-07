using MinecraftRenderer;
using MinecraftRenderer.Nbt;
using MinecraftRenderer.TexturePacks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace MinecraftRenderer.Tests;

public sealed class PotionRenderingTests : IDisposable
{
	private static readonly string AssetsDirectory =
		Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "minecraft"));

	private static readonly string TexturePacksDirectory =
		Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "texturepacks"));

	private readonly string _tempRoot;

	public PotionRenderingTests()
	{
		_tempRoot = Path.Combine(Path.GetTempPath(), $"mcrenderer_test_{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempRoot);
	}

	[Fact]
	public void HarvestHarbingerPotionUsesHypixelPlusTexture()
	{
		// Arrange: Load renderer with Hypixel+ pack
		var hypixelPackPath = Path.Combine(TexturePacksDirectory, "Hypixel+ 0.23.4 for 1.21.8");
		if (!Directory.Exists(hypixelPackPath))
		{
			// Skip test if pack not available
			return;
		}

		var registry = TexturePackRegistry.Create();
		registry.RegisterPack(hypixelPackPath);

		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory, registry);

		var options = MinecraftBlockRenderer.BlockRenderOptions.Default with
		{
			PackIds = new[] { "hypixelplus" },
			Size = 128
		};

		// Act: Build NBT matching the EliteAPI flow
		var attributes = new Dictionary<string, string>
		{
			["potion"] = "harvest_harbinger",
			["splash"] = "0",
			["potion_type"] = "POTION",
			["potion_level"] = "5"
		};

		var customDataEntries = new List<KeyValuePair<string, NbtTag>>
		{
			new("id", new NbtString("POTION"))
		};
		customDataEntries.AddRange(attributes.Select(a => new KeyValuePair<string, NbtTag>(a.Key, new NbtString(a.Value))));

		var components = new NbtCompound([
			new KeyValuePair<string, NbtTag>("minecraft:custom_data", new NbtCompound(customDataEntries))
		]);
		Console.WriteLine($"Components count: {components.Count}");

		var root = new NbtCompound([
			new KeyValuePair<string, NbtTag>("id", new NbtString("minecraft:potion")),
			new KeyValuePair<string, NbtTag>("count", new NbtByte(1)),
			new KeyValuePair<string, NbtTag>("components", components)
		]);
		var extractedPreview = MinecraftBlockRenderer.ExtractItemRenderDataFromNbt(root);
		Console.WriteLine($"Extracted preview custom data count: {extractedPreview?.CustomData?.Count ?? -1}");

		Console.WriteLine("=== Rendering Harvest Harbinger Potion ===");
		Console.WriteLine($"Custom Data Keys: {string.Join(", ", customDataEntries.Select(e => e.Key))}");

		// Use reflection to check model resolution with the texture-pack-aware renderer
		var optionsWithItemData = options with
		{
			ItemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: new NbtCompound(customDataEntries))
		};
		var resolveRendererMethod = typeof(MinecraftBlockRenderer).GetMethod("ResolveRendererForOptions",
			System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
		var resolveItemModelMethod = typeof(MinecraftBlockRenderer).GetMethod("ResolveItemModel",
			System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
		if (resolveRendererMethod != null && resolveItemModelMethod != null)
		{
			var rendererArgs = new object?[] { optionsWithItemData, null };
			var packAwareRenderer = resolveRendererMethod.Invoke(renderer, rendererArgs) as MinecraftBlockRenderer;
			var forwardedOptions = rendererArgs[1] is MinecraftBlockRenderer.BlockRenderOptions forwarded
				? forwarded
				: optionsWithItemData;

			if (packAwareRenderer != null)
			{
				var itemRegistryField = typeof(MinecraftBlockRenderer).GetField("_itemRegistry",
					System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
				var registryValue = itemRegistryField?.GetValue(packAwareRenderer) as ItemRegistry;
				if (registryValue != null && registryValue.TryGetInfo("potion", out var packItemInfo))
				{
					var selectorProperty = typeof(ItemRegistry.ItemInfo).GetProperty("Selector",
						System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
					var selectorValue = selectorProperty?.GetValue(packItemInfo);
					Console.WriteLine($"Selector type (pack-aware): {selectorValue?.GetType().FullName ?? "<null>"}");

					var resolveResult = resolveItemModelMethod.Invoke(packAwareRenderer,
						new object?[] { "potion", packItemInfo, forwardedOptions });
					if (resolveResult != null)
					{
						var modelField = resolveResult.GetType().GetField("Item1");
						var candidatesField = resolveResult.GetType().GetField("Item3");
						var modelName = candidatesField?.GetValue(resolveResult) as string;
						Console.WriteLine($"Resolved Model Name: {modelName}");
						Assert.False(modelName is null, "Resolved model name should not be null");
						const string expectedModelFragment = "harvest_harbinger";
						Assert.Contains(expectedModelFragment, modelName);
					}

					var resourceId = packAwareRenderer.ComputeResourceId("potion", forwardedOptions);
					Console.WriteLine($"ComputeResourceId Model: {resourceId.Model}");
					Assert.False(string.IsNullOrWhiteSpace(resourceId.Model), "ComputeResourceId should produce a model");
					Assert.Contains("harvest_harbinger", resourceId.Model!);
				}
			}
		}

		using var image = renderer.RenderItemFromNbt(root, options);

		// Assert: Should render successfully (model should be found)
		Assert.NotNull(image);
		Assert.Equal(128, image.Width);
		Assert.Equal(128, image.Height);

		// Save for visual inspection
		var outputPath = Path.Combine(_tempRoot, "harvest_harbinger_potion.png");
		image.SaveAsPng(outputPath);
		Console.WriteLine($"Saved to: {outputPath}");

		// Check that we got some non-transparent pixels (potion rendered)
		var hasContent = false;
		image.ProcessPixelRows(accessor =>
		{
			for (var y = 0; y < accessor.Height && !hasContent; y++)
			{
				var row = accessor.GetRowSpan(y);
				for (var x = 0; x < accessor.Width; x++)
				{
					if (row[x].A > 128)
					{
						hasContent = true;
						break;
					}
				}
			}
		});

		Assert.True(hasContent, "Rendered potion should have visible pixels");
	}

	[Fact]
	public void VanillaPotionRendersWithoutPack()
	{
		// Arrange: Renderer without texture pack
		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);

		var options = MinecraftBlockRenderer.BlockRenderOptions.Default with
		{
			Size = 128
		};

		var root = new NbtCompound([
			new KeyValuePair<string, NbtTag>("id", new NbtString("minecraft:potion")),
			new KeyValuePair<string, NbtTag>("count", new NbtByte(1))
		]);

		Console.WriteLine("=== Rendering Vanilla Potion ===");

		// Act
		using var image = renderer.RenderItemFromNbt(root, options);

		// Assert
		Assert.NotNull(image);
		Assert.Equal(128, image.Width);
		Assert.Equal(128, image.Height);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempRoot))
		{
			try
			{
				Directory.Delete(_tempRoot, true);
			}
			catch
			{
				// Ignore cleanup errors
			}
		}
	}
}
