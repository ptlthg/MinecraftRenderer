namespace MinecraftRenderer.Tests;

using MinecraftRenderer.Nbt;
using Xunit;

public class SkullResolverContextTests
{
	private static readonly string AssetsDirectory =
		Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "minecraft"));

	[Fact]
	public void SkullResolverContext_ProvidesFullItemData()
	{
		var renderer = MinecraftBlockRenderer.CreateFromDataDirectory(AssetsDirectory);

		var customData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("CUSTOM_SKULL_ITEM")),
			new KeyValuePair<string, NbtTag>("metadata", new NbtString("extra_info")),
			new KeyValuePair<string, NbtTag>("level", new NbtInt(5))
		});

		MinecraftBlockRenderer.SkullResolverContext? capturedContext = null;

		var options = MinecraftBlockRenderer.BlockRenderOptions.Default with
		{
			Size = 64,
			ItemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: customData),
			SkullTextureResolver = context =>
			{
				capturedContext = context;
				// Return null to use default skin for this test
				return null;
			}
		};

		using var image = renderer.RenderGuiItem("minecraft:player_head", options);

		Assert.NotNull(capturedContext);
		Assert.Equal("minecraft:player_head", capturedContext.ItemId);
		Assert.Equal("CUSTOM_SKULL_ITEM", capturedContext.CustomDataId);
		Assert.NotNull(capturedContext.CustomData);
		Assert.True(capturedContext.CustomData.ContainsKey("metadata"));
		Assert.True(capturedContext.CustomData.ContainsKey("level"));
	}

	[Fact]
	public void SkullResolverContext_CanAccessNestedNbtData()
	{
		var renderer = MinecraftBlockRenderer.CreateFromDataDirectory(AssetsDirectory);

		var customData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("COMPLEX_ITEM")),
			new KeyValuePair<string, NbtTag>("nested", new NbtCompound(new[]
			{
				new KeyValuePair<string, NbtTag>("value", new NbtString("deep_data"))
			}))
		});

		string? extractedNestedValue = null;

		var options = MinecraftBlockRenderer.BlockRenderOptions.Default with
		{
			Size = 64,
			ItemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: customData),
			SkullTextureResolver = context =>
			{
				// Access nested NBT data
				if (context.CustomData?.TryGetValue("nested", out var nestedTag) == true &&
				    nestedTag is NbtCompound nestedCompound &&
				    nestedCompound.TryGetValue("value", out var valueTag) &&
				    valueTag is NbtString valueStr)
				{
					extractedNestedValue = valueStr.Value;
				}
				return null;
			}
		};

		using var image = renderer.RenderGuiItem("minecraft:player_head", options);

		Assert.Equal("deep_data", extractedNestedValue);
	}

	[Fact]
	public void SkullResolverContext_HandlesNullCustomData()
	{
		var renderer = MinecraftBlockRenderer.CreateFromDataDirectory(AssetsDirectory);

		MinecraftBlockRenderer.SkullResolverContext? capturedContext = null;

		var options = MinecraftBlockRenderer.BlockRenderOptions.Default with
		{
			Size = 64,
			// No ItemData provided
			SkullTextureResolver = context =>
			{
				capturedContext = context;
				return null;
			}
		};

		using var image = renderer.RenderGuiItem("minecraft:player_head", options);

		Assert.NotNull(capturedContext);
		Assert.Equal("minecraft:player_head", capturedContext.ItemId);
		Assert.Null(capturedContext.CustomDataId);
		Assert.Null(capturedContext.CustomData);
		Assert.Null(capturedContext.Profile);
	}
}
