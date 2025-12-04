using System;
using System.Collections.Generic;
using System.IO;
using MinecraftRenderer.TexturePacks;
using Xunit;

namespace MinecraftRenderer.Tests;

public class ModelLoadingTests : IDisposable
{
	private readonly string _tempPath;

	public ModelLoadingTests()
	{
		_tempPath = Path.Combine(Path.GetTempPath(), "MinecraftRendererTests", Guid.NewGuid().ToString());
		Directory.CreateDirectory(_tempPath);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempPath))
		{
			Directory.Delete(_tempPath, true);
		}
	}

	[Fact]
	public void ShouldLoadModelFromCustomNamespace()
	{
		// Arrange
		var packPath = Path.Combine(_tempPath, "TestPack");
		Directory.CreateDirectory(packPath);
		
		var assetsPath = Path.Combine(packPath, "assets");
		Directory.CreateDirectory(assetsPath);
		
		// Create minecraft namespace
		var minecraftPath = Path.Combine(assetsPath, "minecraft");
		Directory.CreateDirectory(minecraftPath);

		// Create custom namespace
		var customNamespace = "cittofirmgenerated";
		var customNamespacePath = Path.Combine(assetsPath, customNamespace);
		var customModelPath = Path.Combine(customNamespacePath, "models", "item");
		Directory.CreateDirectory(customModelPath);

		var modelJson = @"{
			""textures"": {
				""0"": ""minecraft:block/dirt""
			},
			""elements"": [
				{
					""from"": [0, 0, 0],
					""to"": [16, 16, 16],
					""faces"": {
						""north"": {""uv"": [0, 0, 16, 16], ""texture"": ""#0""}
					}
				}
			]
		}";
		File.WriteAllText(Path.Combine(customModelPath, "custom_model.json"), modelJson);

		// Manually build AssetNamespaceRegistry
		var registry = new MinecraftRenderer.Assets.AssetNamespaceRegistry();
		registry.AddNamespace("minecraft", minecraftPath, "test_pack", false);
		registry.AddNamespace(customNamespace, customNamespacePath, "test_pack", false);
		
		var baseAssetsPath = Path.Combine(_tempPath, "BaseAssets");
		Directory.CreateDirectory(baseAssetsPath);
		Directory.CreateDirectory(Path.Combine(baseAssetsPath, "assets", "minecraft"));

		var modelResolver = BlockModelResolver.LoadFromMinecraftAssets(
			baseAssetsPath, 
			null, 
			registry
		);

		// Act
		var model = modelResolver.Resolve("cittofirmgenerated:item/custom_model");

		// Assert
		Assert.NotNull(model);
		Assert.Single(model.Elements);
		Assert.Equal("cittofirmgenerated:item/custom_model", model.Name);
	}

	[Fact]
	public void ShouldLoadModelFromNestedFolderStructure()
	{
		// Arrange
		var packPath = Path.Combine(_tempPath, "TestPackNested");
		Directory.CreateDirectory(packPath);

		var assetsPath = Path.Combine(packPath, "assets");
		Directory.CreateDirectory(assetsPath);
		
		var minecraftPath = Path.Combine(assetsPath, "minecraft");
		Directory.CreateDirectory(minecraftPath);

		var customNamespace = "cittofirmgenerated";
		var customNamespacePath = Path.Combine(assetsPath, customNamespace);
		// Structure: assets/cittofirmgenerated/models/item/helmet_icon/crown_of_avarice.json
		var customModelPath = Path.Combine(customNamespacePath, "models", "item", "helmet_icon");
		Directory.CreateDirectory(customModelPath);

		var modelJson = @"{
			""textures"": {
				""0"": ""minecraft:block/gold_block""
			},
			""elements"": [
				{
					""from"": [0, 0, 0],
					""to"": [16, 16, 16],
					""faces"": {
						""north"": {""uv"": [0, 0, 16, 16], ""texture"": ""#0""}
					}
				}
			]
		}";
		File.WriteAllText(Path.Combine(customModelPath, "crown_of_avarice.json"), modelJson);

		// Manually build AssetNamespaceRegistry
		var registry = new MinecraftRenderer.Assets.AssetNamespaceRegistry();
		registry.AddNamespace("minecraft", minecraftPath, "test_pack_nested", false);
		registry.AddNamespace(customNamespace, customNamespacePath, "test_pack_nested", false);
		
		var baseAssetsPath = Path.Combine(_tempPath, "BaseAssetsNested");
		Directory.CreateDirectory(baseAssetsPath);
		Directory.CreateDirectory(Path.Combine(baseAssetsPath, "assets", "minecraft"));

		var modelResolver = BlockModelResolver.LoadFromMinecraftAssets(
			baseAssetsPath, 
			null, 
			registry
		);

		// Act
		// The key should be "cittofirmgenerated:item/helmet_icon/crown_of_avarice"
		var model = modelResolver.Resolve("cittofirmgenerated:item/helmet_icon/crown_of_avarice");

		// Assert
		Assert.NotNull(model);
		Assert.Single(model.Elements);
		Assert.Equal("cittofirmgenerated:item/helmet_icon/crown_of_avarice", model.Name);
	}
}
