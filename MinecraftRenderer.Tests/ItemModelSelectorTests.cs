using System.Reflection;
using System.Text.Json;
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

	[Fact]
	public void PlayerHeadProfileAndCustomDataStillUsesTexturePackModel()
	{
		var packId = "profilecustomheadpack";
		var packColor = new Rgba32(0x3C, 0x91, 0xE0, 0xFF);
		var packRoot = CreateCustomHeadPack(packId, packColor);

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

		var profile = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtIntArray(new[]
			{
				123456789,
				987654321,
				-135792468,
				246813579
			})),
			new KeyValuePair<string, NbtTag>("properties", new NbtList(NbtTagType.Compound, new NbtTag[]
			{
				new NbtCompound(new[]
				{
					new KeyValuePair<string, NbtTag>("name", new NbtString("textures")),
					new KeyValuePair<string, NbtTag>("value", new NbtString(
						Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
							"{\"textures\":{\"SKIN\":{\"url\":\"https://textures.minecraft.net/texture/placeholder\"}}}"))))
				})
			}))
		});

		var itemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: customData, Profile: profile);
		using var rendered = renderer.RenderItem("player_head", itemData, options);
		var pixel = SampleOpaquePixel(rendered);
		Assert.Equal(packColor.R, pixel.R);
		Assert.Equal(packColor.G, pixel.G);
		Assert.Equal(packColor.B, pixel.B);
	}

	[Fact]
	public void UnsupportedCatharsisSelectFallsBackToFirstCase()
	{
		using var document = JsonDocument.Parse(
			"""
{
	"model": {
		"type": "select",
		"property": "catharsis:data_type",
		"data_type": "midas_weapon_paid",
		"cases": [
			{
				"when": "first",
				"model": {
					"type": "model",
					"model": "minecraft:item/first_case"
				}
			},
			{
				"when": "second",
				"model": {
					"type": "model",
					"model": "minecraft:item/second_case"
				}
			}
		],
		"fallback": {
			"type": "model",
			"model": "minecraft:item/fallback_case"
		}
	}
}
""");

		var resolved = ResolveSelector(document, itemName: "golden_sword");

		Assert.Equal("minecraft:item/first_case", resolved);
	}

	[Fact]
	public void UnsupportedCatharsisRangeDispatchFallsBackToFirstEntry()
	{
		using var document = JsonDocument.Parse(
			"""
{
	"model": {
		"type": "range_dispatch",
		"property": "catharsis:data_type",
		"data_type": "midas_weapon_paid",
		"entries": [
			{
				"threshold": 0,
				"model": {
					"type": "model",
					"model": "minecraft:item/entry_zero"
				}
			},
			{
				"threshold": 1000000,
				"model": {
					"type": "model",
					"model": "minecraft:item/entry_million"
				}
			}
		],
		"fallback": {
			"type": "model",
			"model": "minecraft:item/fallback_case"
		}
	}
}
""");

		var resolved = ResolveSelector(document, itemName: "golden_sword");

		Assert.Equal("minecraft:item/entry_zero", resolved);
	}

	[Fact]
	public void CatharsisDataTypePresentCanFallThroughWhenSkinFallbackExists()
	{
		using var document = JsonDocument.Parse(
			"""
{
	"model": {
		"type": "condition",
		"property": "catharsis:is_data_type_present",
		"data_type": "has_skin_fallback",
		"on_true": { "type": "catharsis:fallthrough" },
		"on_false": {
			"type": "model",
			"model": "minecraft:item/custom_helmet"
		}
	}
}
""");

		var profile = new NbtCompound(Array.Empty<KeyValuePair<string, NbtTag>>());
		var itemData = new MinecraftBlockRenderer.ItemRenderData(Profile: profile);

		var resolvedWithSkin = ResolveSelector(document, itemData);
		var resolvedWithoutSkin = ResolveSelector(document);

		Assert.Null(resolvedWithSkin);
		Assert.Equal("minecraft:item/custom_helmet", resolvedWithoutSkin);
	}

	[Fact]
	public void CatharsisSelectDataTypeReadsKnownCustomDataValue()
	{
		using var document = JsonDocument.Parse(
			"""
{
	"model": {
		"type": "select",
		"property": "catharsis:data_type",
		"data_type": "selected_arrow",
		"cases": [
			{
				"when": "ARMORSHRED_ARROW",
				"model": { "type": "model", "model": "minecraft:item/armorshred" }
			}
		],
		"fallback": { "type": "model", "model": "minecraft:item/no_arrow" }
	}
}
""");

		var customData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("selected_arrow", new NbtString("ARMORSHRED_ARROW"))
		});
		var itemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: customData);

		Assert.Equal("minecraft:item/armorshred", ResolveSelector(document, itemData));
		Assert.Equal("minecraft:item/no_arrow", ResolveSelector(document));
	}

	[Fact]
	public void CatharsisNumericDataTypeReadsKnownCustomDataValue()
	{
		using var document = JsonDocument.Parse(
			"""
{
	"model": {
		"type": "range_dispatch",
		"property": "catharsis:data_type",
		"data_type": "fuel",
		"entries": [
			{
				"threshold": 0,
				"model": { "type": "model", "model": "minecraft:item/empty" }
			},
			{
				"threshold": 1000,
				"model": { "type": "model", "model": "minecraft:item/charged" }
			}
		]
	}
}
""");

		var customData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("fuel", new NbtInt(1500))
		});
		var itemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: customData);

		Assert.Equal("minecraft:item/charged", ResolveSelector(document, itemData));
		Assert.Equal("minecraft:item/empty", ResolveSelector(document));
	}

	[Fact]
	public void CatharsisNumericDataTypeSupportsMidasPaidValue()
	{
		using var document = JsonDocument.Parse(
			"""
{
	"model": {
		"type": "range_dispatch",
		"property": "catharsis:data_type",
		"data_type": "midas_weapon_paid",
		"entries": [
			{
				"threshold": 0,
				"model": { "type": "model", "model": "minecraft:item/base_midas" }
			},
			{
				"threshold": 50000000,
				"model": { "type": "model", "model": "minecraft:item/rich_midas" }
			}
		]
	}
}
""");

		var customData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("midas_weapon_paid", new NbtLong(60_000_000))
		});
		var itemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: customData);

		Assert.Equal("minecraft:item/rich_midas", ResolveSelector(document, itemData));
		Assert.Equal("minecraft:item/base_midas", ResolveSelector(document));
	}

	[Fact]
	public void CatharsisSelectDataTypeSupportsFungiCutterMode()
	{
		using var document = JsonDocument.Parse(
			"""
{
	"model": {
		"type": "select",
		"property": "catharsis:data_type",
		"data_type": "fungi_cutter_mode",
		"cases": [
			{
				"when": "RED",
				"model": { "type": "model", "model": "minecraft:item/red_fungi_cutter" }
			}
		],
		"fallback": { "type": "model", "model": "minecraft:item/brown_fungi_cutter" }
	}
}
""");

		var customData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("fungi_cutter_mode", new NbtString("RED"))
		});
		var itemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: customData);

		Assert.Equal("minecraft:item/red_fungi_cutter", ResolveSelector(document, itemData));
		Assert.Equal("minecraft:item/brown_fungi_cutter", ResolveSelector(document));
	}

	[Fact]
	public void CatharsisBooleanDataTypeReadsCustomDataValue()
	{
		using var document = JsonDocument.Parse(
			"""
{
	"model": {
		"type": "condition",
		"property": "catharsis:data_type",
		"data_type": "personal_accessory_active",
		"on_true": { "type": "model", "model": "minecraft:item/accessory_active" },
		"on_false": { "type": "model", "model": "minecraft:item/accessory_inactive" }
	}
}
""");

		var activeData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("personal_accessory_active", new NbtByte(1))
		});
		var inactiveData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("personal_accessory_active", new NbtString("0"))
		});

		Assert.Equal("minecraft:item/accessory_active",
			ResolveSelector(document, new MinecraftBlockRenderer.ItemRenderData(CustomData: activeData)));
		Assert.Equal("minecraft:item/accessory_inactive",
			ResolveSelector(document, new MinecraftBlockRenderer.ItemRenderData(CustomData: inactiveData)));
		Assert.Equal("minecraft:item/accessory_inactive", ResolveSelector(document));
	}

	[Fact]
	public void ComponentConditionsAndSelectsUseDyedColorData()
	{
		using var conditionDocument = JsonDocument.Parse(
			"""
{
	"model": {
		"type": "condition",
		"property": "has_component",
		"component": "dyed_color",
		"on_true": { "type": "model", "model": "minecraft:item/dyed" },
		"on_false": { "type": "model", "model": "minecraft:item/base" }
	}
}
""");
		using var selectDocument = JsonDocument.Parse(
			"""
{
	"model": {
		"type": "select",
		"property": "component",
		"component": "dyed_color",
		"cases": [
			{
				"when": 16711680,
				"model": { "type": "model", "model": "minecraft:item/red" }
			}
		],
		"fallback": { "type": "model", "model": "minecraft:item/other" }
	}
}
""");

		var itemData = new MinecraftBlockRenderer.ItemRenderData(Layer0Tint: Color.FromPixel(new Rgba32(255, 0, 0)));

		Assert.Equal("minecraft:item/dyed", ResolveSelector(conditionDocument, itemData));
		Assert.Equal("minecraft:item/base", ResolveSelector(conditionDocument));
		Assert.Equal("minecraft:item/red", ResolveSelector(selectDocument, itemData));
	}

	[Fact]
	public void CatharsisHasGemstonesReadsSocketedGemData()
	{
		using var document = JsonDocument.Parse(
			"""
{
	"model": {
		"type": "condition",
		"property": "catharsis:has_gemstones",
		"amount": 1,
		"slot": "JADE",
		"quality": "PERFECT",
		"on_true": { "type": "model", "model": "minecraft:item/with_jade" },
		"on_false": { "type": "model", "model": "minecraft:item/no_jade" }
	}
}
""");

		var socketedGemData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("gems", new NbtCompound(new[]
			{
				new KeyValuePair<string, NbtTag>("JADE_0", new NbtString("PERFECT")),
				new KeyValuePair<string, NbtTag>("AMBER_0", new NbtString("ROUGH"))
			}))
		});
		var emptyGemData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("gems", new NbtCompound(new[]
			{
				new KeyValuePair<string, NbtTag>("JADE_0", new NbtString(string.Empty))
			}))
		});

		Assert.Equal("minecraft:item/with_jade",
			ResolveSelector(document, new MinecraftBlockRenderer.ItemRenderData(CustomData: socketedGemData)));
		Assert.Equal("minecraft:item/no_jade",
			ResolveSelector(document, new MinecraftBlockRenderer.ItemRenderData(CustomData: emptyGemData)));
		Assert.Equal("minecraft:item/no_jade", ResolveSelector(document));
	}

	[Fact]
	public void ResourceIdIgnoresUnusedCustomDataValues()
	{
		var packId = "resourceidstablepack";
		var packRoot = CreateCustomItemPack(packId, "golden_sword", "custom_golden_sword",
			new Rgba32(0x44, 0x88, 0xCC, 0xFF));

		var registry = TexturePackRegistry.Create();
		registry.RegisterPack(packRoot);

		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory, registry, new[] { packId });
		var options = MinecraftBlockRenderer.BlockRenderOptions.Default with
		{
			Size = 64
		};

		var baseCustomData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("selector_match"))
		});
		var richerCustomData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("selector_match")),
			new KeyValuePair<string, NbtTag>("midas_weapon_paid", new NbtLong(50_000_000)),
			new KeyValuePair<string, NbtTag>("gems", new NbtCompound(new[]
			{
				new KeyValuePair<string, NbtTag>("JADE_0", new NbtString("PERFECT"))
			}))
		});

		var baseResource = renderer.ComputeResourceId("golden_sword",
			options with { ItemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: baseCustomData) });
		var richerResource = renderer.ComputeResourceId("golden_sword",
			options with { ItemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: richerCustomData) });

		Assert.Equal(baseResource.ResourceId, richerResource.ResourceId);
	}

	[Fact]
	public void CompositeItemModelsRenderConditionalOverlays()
	{
		var assetsRoot = CreateCompositeAssetsRoot("compositeoverlayassets");

		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(assetsRoot);
		var options = MinecraftBlockRenderer.BlockRenderOptions.Default with
		{
			Size = 64
		};

		Assert.Contains("golden_sword", renderer.GetKnownItemNames());

		var noGemData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("selector_match"))
		});
		var jadeGemData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("selector_match")),
			new KeyValuePair<string, NbtTag>("gems", new NbtCompound(new[]
			{
				new KeyValuePair<string, NbtTag>("JADE_0", new NbtString("PERFECT"))
			}))
		});

		using var baseRender = renderer.RenderItem("golden_sword",
			new MinecraftBlockRenderer.ItemRenderData(CustomData: noGemData), options);
		using var overlayRender = renderer.RenderItem("golden_sword",
			new MinecraftBlockRenderer.ItemRenderData(CustomData: jadeGemData), options);

		AssertImageContainsColor(baseRender, new Rgba32(0xCC, 0x22, 0x22, 0xFF));
		AssertImageDoesNotContainColor(baseRender, new Rgba32(0x22, 0xCC, 0x66, 0xFF));
		AssertImageContainsColor(overlayRender, new Rgba32(0xCC, 0x22, 0x22, 0xFF));
		AssertImageContainsColor(overlayRender, new Rgba32(0x22, 0xCC, 0x66, 0xFF));
	}

	[Fact]
	public void CompositeResourceIdIncludesResolvedOverlayModels()
	{
		var assetsRoot = CreateCompositeAssetsRoot("compositeresourceassets");

		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(assetsRoot);
		var options = MinecraftBlockRenderer.BlockRenderOptions.Default with
		{
			Size = 64
		};

		var noGemData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("selector_match"))
		});
		var jadeGemData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("selector_match")),
			new KeyValuePair<string, NbtTag>("gems", new NbtCompound(new[]
			{
				new KeyValuePair<string, NbtTag>("JADE_0", new NbtString("PERFECT"))
			}))
		});

		var baseResource = renderer.ComputeResourceId("golden_sword",
			options with { ItemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: noGemData) });
		var overlayResource = renderer.ComputeResourceId("golden_sword",
			options with { ItemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: jadeGemData) });

		Assert.True(baseResource.ResourceId != overlayResource.ResourceId,
			$"base model={baseResource.Model} textures={string.Join(',', baseResource.Textures)}; overlay model={overlayResource.Model} textures={string.Join(',', overlayResource.Textures)}");
		Assert.DoesNotContain("minecraft:item/composite_jade", baseResource.Textures);
		Assert.Contains("minecraft:item/composite_jade", overlayResource.Textures);
	}

	[Fact]
	public void CompositeItemModelsRenderLaterModelsAboveEarlierModels()
	{
		var assetsRoot = CreateOrderedCompositeAssetsRoot("compositeorderassets");

		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(assetsRoot);
		var options = MinecraftBlockRenderer.BlockRenderOptions.Default with
		{
			Size = 64
		};

		using var render = renderer.RenderItem("golden_sword", options);

		AssertImageContainsColor(render, new Rgba32(0x22, 0x44, 0xEE, 0xFF));
		AssertImageDoesNotContainColor(render, new Rgba32(0xCC, 0x22, 0x22, 0xFF));
		AssertImageDoesNotContainColor(render, new Rgba32(0x22, 0xCC, 0x66, 0xFF));
	}

	[Fact]
	public void ResourceIdKeepsCustomHeadTextureData()
	{
		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
		var options = MinecraftBlockRenderer.BlockRenderOptions.Default with
		{
			Size = 64
		};

		var firstTextureData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("texture", new NbtString("first_texture"))
		});
		var secondTextureData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("texture", new NbtString("second_texture"))
		});

		var firstResource = renderer.ComputeResourceId("player_head",
			options with { ItemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: firstTextureData) });
		var secondResource = renderer.ComputeResourceId("player_head",
			options with { ItemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: secondTextureData) });

		Assert.NotEqual(firstResource.ResourceId, secondResource.ResourceId);
	}

	[Fact]
	public void ResourceIdKeepsResolvedSkullTextureData()
	{
		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
		var options = MinecraftBlockRenderer.BlockRenderOptions.Default with
		{
			Size = 64,
			SkullTextureResolver = context => context.CustomDataId switch {
				"FIRST_HEAD" => "first_resolved_texture",
				"SECOND_HEAD" => "second_resolved_texture",
				_ => null
			}
		};

		var firstHeadData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("FIRST_HEAD"))
		});
		var secondHeadData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("SECOND_HEAD"))
		});

		var firstResource = renderer.ComputeResourceId("player_head",
			options with { ItemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: firstHeadData) });
		var secondResource = renderer.ComputeResourceId("player_head",
			options with { ItemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: secondHeadData) });

		Assert.NotEqual(firstResource.ResourceId, secondResource.ResourceId);
	}

	private static string? ResolveSelector(JsonDocument document,
		MinecraftBlockRenderer.ItemRenderData? itemData = null,
		string displayContext = "gui",
		string? itemName = null)
	{
		var assembly = typeof(MinecraftBlockRenderer).Assembly;
		var parserType = assembly.GetType("MinecraftRenderer.ItemModelSelectorParser");
		Assert.NotNull(parserType);

		var parseMethod = parserType!.GetMethod("ParseFromRoot", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
		Assert.NotNull(parseMethod);

		var selector = parseMethod!.Invoke(null, new object[] { document.RootElement });
		Assert.NotNull(selector);

		var contextType = assembly.GetType("MinecraftRenderer.ItemModelContext");
		Assert.NotNull(contextType);

		var context = Activator.CreateInstance(contextType!, itemData, displayContext, itemName);
		Assert.NotNull(context);

		var resolveMethod = selector!.GetType().GetMethod("Resolve", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
		Assert.NotNull(resolveMethod);

		return (string?)resolveMethod!.Invoke(selector, new[] { context });
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

	private string CreateCustomItemPack(string id, string itemName, string modelName, Rgba32 color,
		string? itemDefinitionOverride = null)
	{
		var packRoot = Path.Combine(_tempRoot, id);
		Directory.CreateDirectory(packRoot);

		File.WriteAllText(Path.Combine(packRoot, "meta.json"),
			$"{{\n  \"id\": \"{id}\",\n  \"name\": \"{id}\",\n  \"version\": \"1.0.0\",\n  \"description\": \"Test pack\",\n  \"authors\": [\"tests\"]\n}}\n");
		File.WriteAllText(Path.Combine(packRoot, "pack.mcmeta"),
			"{\"pack\": {\"pack_format\": 32, \"description\": \"Test\"}}\n");

		var itemsDir = Path.Combine(packRoot, "assets", "minecraft", "items");
		Directory.CreateDirectory(itemsDir);
		File.WriteAllText(Path.Combine(itemsDir, $"{itemName}.json"),
			itemDefinitionOverride ?? BuildDefaultCustomItemDefinition(modelName, itemName));

		var modelsDir = Path.Combine(packRoot, "assets", "minecraft", "models", "item");
		Directory.CreateDirectory(modelsDir);
		File.WriteAllText(Path.Combine(modelsDir, $"{modelName}.json"),
			"{\n  \"parent\": \"minecraft:item/generated\",\n  \"textures\": {\n    \"layer0\": \"minecraft:item/" + modelName + "\"\n  }\n}\n");

		var texturesDir = Path.Combine(packRoot, "assets", "minecraft", "textures", "item");
		Directory.CreateDirectory(texturesDir);
		using var image = new Image<Rgba32>(16, 16, color);
		image.Save(Path.Combine(texturesDir, $"{modelName}.png"));

		return packRoot;
	}

	private string CreateCompositeAssetsRoot(string id)
	{
		var assetsRoot = Path.Combine(_tempRoot, id);
		Directory.CreateDirectory(Path.Combine(assetsRoot, "blockstates"));

		var itemsDir = Path.Combine(assetsRoot, "items");
		Directory.CreateDirectory(itemsDir);
		File.WriteAllText(Path.Combine(itemsDir, "golden_sword.json"),
			"""
{
  "model": {
    "type": "composite",
    "models": [
      {
        "type": "model",
        "model": "minecraft:item/composite_base"
      },
      {
        "type": "condition",
        "property": "catharsis:has_gemstones",
        "amount": 1,
        "slot": "JADE",
        "on_true": {
          "type": "model",
          "model": "minecraft:item/composite_jade"
        },
        "on_false": {
          "type": "empty"
        }
      }
    ]
  }
}
""");

		var modelsDir = Path.Combine(assetsRoot, "models", "item");
		Directory.CreateDirectory(modelsDir);
		File.WriteAllText(Path.Combine(modelsDir, "composite_base.json"), BuildBuiltinGeneratedModel("composite_base"));
		File.WriteAllText(Path.Combine(modelsDir, "composite_jade.json"), BuildBuiltinGeneratedModel("composite_jade"));

		var texturesDir = Path.Combine(assetsRoot, "textures", "item");
		Directory.CreateDirectory(texturesDir);
		using (var baseTexture = new Image<Rgba32>(16, 16, new Rgba32(0xCC, 0x22, 0x22, 0xFF)))
		{
			baseTexture.Save(Path.Combine(texturesDir, "composite_base.png"));
		}

		using (var overlayTexture = new Image<Rgba32>(16, 16))
		{
			for (var y = 0; y < 4; y++)
			{
				for (var x = 0; x < 4; x++)
				{
					overlayTexture[x, y] = new Rgba32(0x22, 0xCC, 0x66, 0xFF);
				}
			}

			overlayTexture.Save(Path.Combine(texturesDir, "composite_jade.png"));
		}

		return assetsRoot;
	}

	private string CreateOrderedCompositeAssetsRoot(string id)
	{
		var assetsRoot = Path.Combine(_tempRoot, id);
		Directory.CreateDirectory(Path.Combine(assetsRoot, "blockstates"));

		var itemsDir = Path.Combine(assetsRoot, "items");
		Directory.CreateDirectory(itemsDir);
		File.WriteAllText(Path.Combine(itemsDir, "golden_sword.json"),
			"""
{
  "model": {
    "type": "composite",
    "models": [
      { "type": "model", "model": "minecraft:item/order_base" },
      { "type": "model", "model": "minecraft:item/order_first_overlay" },
      { "type": "model", "model": "minecraft:item/order_second_overlay" }
    ]
  }
}
""");

		var modelsDir = Path.Combine(assetsRoot, "models", "item");
		Directory.CreateDirectory(modelsDir);
		File.WriteAllText(Path.Combine(modelsDir, "order_base.json"), BuildBuiltinGeneratedModel("order_base"));
		File.WriteAllText(Path.Combine(modelsDir, "order_first_overlay.json"), BuildBuiltinGeneratedModel("order_first_overlay"));
		File.WriteAllText(Path.Combine(modelsDir, "order_second_overlay.json"), BuildBuiltinGeneratedModel("order_second_overlay"));

		var texturesDir = Path.Combine(assetsRoot, "textures", "item");
		Directory.CreateDirectory(texturesDir);
		using (var baseTexture = new Image<Rgba32>(16, 16, new Rgba32(0xCC, 0x22, 0x22, 0xFF)))
		{
			baseTexture.Save(Path.Combine(texturesDir, "order_base.png"));
		}

		using (var firstOverlay = new Image<Rgba32>(16, 16, new Rgba32(0x22, 0xCC, 0x66, 0xFF)))
		{
			firstOverlay.Save(Path.Combine(texturesDir, "order_first_overlay.png"));
		}

		using (var secondOverlay = new Image<Rgba32>(16, 16, new Rgba32(0x22, 0x44, 0xEE, 0xFF)))
		{
			secondOverlay.Save(Path.Combine(texturesDir, "order_second_overlay.png"));
		}

		return assetsRoot;
	}

	private static string BuildDefaultPlayerHeadDefinition(string modelName)
		=> "{\n  \"model\": {\n    \"type\": \"condition\",\n    \"property\": \"component\",\n    \"predicate\": \"custom_data\",\n    \"value\": { \"id\": \"custom_head_test\" },\n    \"on_true\": {\n      \"type\": \"model\",\n      \"model\": \"minecraft:item/" + modelName + "\"\n    },\n    \"on_false\": {\n      \"type\": \"model\",\n      \"model\": \"minecraft:item/player_head\"\n    }\n  }\n}\n";

	private static string BuildDefaultPlayerHeadModel(string modelName)
		=> "{\n  \"parent\": \"minecraft:item/generated\",\n  \"textures\": {\n    \"layer0\": \"minecraft:item/" + modelName + "\"\n  }\n}\n";

	private static string BuildGeneratedModel(string modelName)
		=> "{\n  \"parent\": \"minecraft:item/generated\",\n  \"textures\": {\n    \"layer0\": \"minecraft:item/" + modelName + "\"\n  }\n}\n";

	private static string BuildBuiltinGeneratedModel(string modelName)
		=> "{\n  \"parent\": \"minecraft:builtin/generated\",\n  \"textures\": {\n    \"layer0\": \"minecraft:item/" + modelName + "\"\n  }\n}\n";

	private static string BuildDefaultCustomItemDefinition(string modelName, string fallbackItemName)
		=> "{\n  \"model\": {\n    \"type\": \"condition\",\n    \"property\": \"component\",\n    \"predicate\": \"custom_data\",\n    \"value\": { \"id\": \"selector_match\" },\n    \"on_false\": {\n      \"type\": \"model\",\n      \"model\": \"minecraft:item/" + fallbackItemName + "\"\n    },\n    \"on_true\": {\n      \"type\": \"model\",\n      \"model\": \"minecraft:item/" + modelName + "\"\n    }\n  }\n}\n";

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

	private static void AssertImageContainsColor(Image<Rgba32> image, Rgba32 expected)
	{
		for (var y = 0; y < image.Height; y++)
		{
			for (var x = 0; x < image.Width; x++)
			{
				if (IsSimilarColor(image[x, y], expected))
				{
					return;
				}
			}
		}

		throw new Xunit.Sdk.XunitException($"Expected rendered image to contain color rgba({expected.R},{expected.G},{expected.B},{expected.A}).");
	}

	private static void AssertImageDoesNotContainColor(Image<Rgba32> image, Rgba32 expected)
	{
		for (var y = 0; y < image.Height; y++)
		{
			for (var x = 0; x < image.Width; x++)
			{
				if (IsSimilarColor(image[x, y], expected))
				{
					throw new Xunit.Sdk.XunitException($"Expected rendered image not to contain color rgba({expected.R},{expected.G},{expected.B},{expected.A}).");
				}
			}
		}
	}

	private static bool IsSimilarColor(Rgba32 actual, Rgba32 expected)
	{
		const int tolerance = 2;
		return Math.Abs(actual.R - expected.R) <= tolerance
		       && Math.Abs(actual.G - expected.G) <= tolerance
		       && Math.Abs(actual.B - expected.B) <= tolerance
		       && Math.Abs(actual.A - expected.A) <= tolerance;
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
