namespace MinecraftRenderer;

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

public sealed partial class MinecraftBlockRenderer : IDisposable
{
	public record BlockRenderOptions(
		int Size = 512,
		float YawInDegrees = 0f,
		float PitchInDegrees = 0f,
		float RollInDegrees = 0f,
		float PerspectiveAmount = 0f,
		bool UseGuiTransform = true,
		float Padding = 0.12f,
		float AdditionalScale = 1f,
		Vector3 AdditionalTranslation = default)
	{
		public static BlockRenderOptions Default { get; } = new();
	}
	
	public static bool DebugDisableCulling = true;

	private readonly BlockModelResolver _modelResolver;
	private readonly TextureRepository _textureRepository;
	private readonly BlockRegistry _blockRegistry;
	private readonly ItemRegistry? _itemRegistry;
	private readonly Dictionary<string, Image<Rgba32>> _biomeTintedTextureCache = new(StringComparer.OrdinalIgnoreCase);
	private bool _disposed;

	public TextureRepository TextureRepository => _textureRepository;

	private MinecraftBlockRenderer(BlockModelResolver modelResolver, TextureRepository textureRepository, BlockRegistry blockRegistry, ItemRegistry? itemRegistry)
	{
		_modelResolver = modelResolver;
		_textureRepository = textureRepository;
		_blockRegistry = blockRegistry;
		_itemRegistry = itemRegistry;
	}

	public static MinecraftBlockRenderer CreateFromDataDirectory(string dataDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

		var modelsPath = Path.Combine(dataDirectory, "blocks_models.json");
		var texturesPath = Path.Combine(dataDirectory, "blocks_textures.json");
		var textureContentPath = Path.Combine(dataDirectory, "texture_content.json");
		var itemsPath = Path.Combine(dataDirectory, "items_textures.json");

		if (File.Exists(modelsPath) && File.Exists(texturesPath))
		{
			var modelResolver = BlockModelResolver.LoadFromFile(modelsPath);
			var blockRegistry = BlockRegistry.LoadFromFile(texturesPath);
			var textureRepository = new TextureRepository(dataDirectory, File.Exists(textureContentPath) ? textureContentPath : null);
			ItemRegistry? itemRegistry = null;
			if (File.Exists(itemsPath))
			{
				itemRegistry = ItemRegistry.LoadFromFile(itemsPath);
			}

			return new MinecraftBlockRenderer(modelResolver, textureRepository, blockRegistry, itemRegistry);
		}

		if (IsMinecraftAssetsRoot(dataDirectory))
		{
			return CreateFromMinecraftAssets(dataDirectory);
		}

		throw new DirectoryNotFoundException($"The directory '{dataDirectory}' does not contain the expected aggregated JSON files or Minecraft asset folders.");
	}

	public static MinecraftBlockRenderer CreateFromMinecraftAssets(string assetsDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(assetsDirectory);

		var overlayRoots = DiscoverOverlayRoots(assetsDirectory);
		var modelResolver = BlockModelResolver.LoadFromMinecraftAssets(assetsDirectory, overlayRoots);
		var blockRegistry = BlockRegistry.LoadFromMinecraftAssets(assetsDirectory, modelResolver.Definitions, overlayRoots);
		var itemRegistry = ItemRegistry.LoadFromMinecraftAssets(assetsDirectory, modelResolver.Definitions, overlayRoots);
		var texturesRoot = Directory.Exists(Path.Combine(assetsDirectory, "textures"))
			? Path.Combine(assetsDirectory, "textures")
			: assetsDirectory;
		var textureRepository = new TextureRepository(texturesRoot, overlayRoots: overlayRoots);

		return new MinecraftBlockRenderer(modelResolver, textureRepository, blockRegistry, itemRegistry);
	}

	private static IReadOnlyList<string> DiscoverOverlayRoots(string assetsDirectory)
	{
		var overlays = new List<string>();
		var assetRoot = Path.GetFullPath(assetsDirectory);
		var parent = Directory.GetParent(assetRoot)?.FullName;

		void TryAdd(string? candidate)
		{
			if (string.IsNullOrWhiteSpace(candidate))
			{
				return;
			}

			var fullPath = Path.GetFullPath(candidate);
			if (!Directory.Exists(fullPath))
			{
				return;
			}

			if (!overlays.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
			{
				overlays.Add(fullPath);
			}
		}

		if (parent is not null)
		{
			TryAdd(Path.Combine(parent, "customdata"));
		}

		TryAdd(Path.Combine(assetRoot, "customdata"));

		return overlays;
	}

	private static bool IsMinecraftAssetsRoot(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}

		return Directory.Exists(Path.Combine(path, "models"))
			&& Directory.Exists(Path.Combine(path, "blockstates"))
			&& Directory.Exists(Path.Combine(path, "textures"));
	}

	public IReadOnlyList<string> GetKnownBlockNames() => _blockRegistry.GetAllBlockNames();

	public IReadOnlyList<string> GetKnownItemNames() => _itemRegistry?.GetAllItemNames() ?? [];

	public Image<Rgba32> RenderBlock(string blockName, BlockRenderOptions? options = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(blockName);
		options ??= BlockRenderOptions.Default;

		var modelName = blockName;
		if (_blockRegistry.TryGetModel(blockName, out var mappedModel) && !string.IsNullOrWhiteSpace(mappedModel))
		{
			modelName = mappedModel;
		}

		var model = _modelResolver.Resolve(modelName);
		return RenderModel(model, options, blockName);
	}

	public Image<Rgba32> RenderItem(string itemName, BlockRenderOptions? options = null)
		=> RenderGuiItem(itemName, options);

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_textureRepository.Dispose();

		foreach (var image in _biomeTintedTextureCache.Values)
		{
			image.Dispose();
		}
		_biomeTintedTextureCache.Clear();
		
	}

	private void EnsureNotDisposed()
	{
		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(MinecraftBlockRenderer));
		}
	}

	private static readonly Dictionary<string, Color> ColorMap = new(StringComparer.OrdinalIgnoreCase)
	{
		{ "white", new Color(new Rgb24(249, 255, 254)) },
		{ "orange", new Color(new Rgb24(249, 128, 29)) },
		{ "magenta", new Color(new Rgb24(199, 78, 189)) },
		{ "light_blue", new Color(new Rgb24(58, 179, 218)) },
		{ "yellow", new Color(new Rgb24(254, 216, 61)) },
		{ "lime", new Color(new Rgb24(128, 199, 31)) },
		{ "pink", new Color(new Rgb24(243, 139, 170)) },
		{ "gray", new Color(new Rgb24(71, 79, 82)) },
		{ "light_gray", new Color(new Rgb24(157, 157, 151)) },
		{ "cyan", new Color(new Rgb24(22, 156, 156)) },
		{ "purple", new Color(new Rgb24(137, 50, 184)) },
		{ "blue", new Color(new Rgb24(60, 68, 170)) },
		{ "brown", new Color(new Rgb24(131, 84, 50)) },
		{ "green", new Color(new Rgb24(94, 124, 22)) },
		{ "red", new Color(new Rgb24(176, 46, 38)) },
		{ "black", new Color(new Rgb24(29, 29, 33)) }
	};
	
	// https://minecraft.wiki/w/Color#Constant_colors
	private static readonly Dictionary<string, Color> ConstantColors = new(StringComparer.OrdinalIgnoreCase)
	{
		{ "birch_leaves", new Color(new Rgb24(128, 167, 85)) },
		{ "spruce_leaves", new Color(new Rgb24(97, 153, 97)) },
		{ "lily_pad", new Color(new Rgb24(32, 128, 48)) },
		// Also attached melon/pumpkin stems, but those are outside the scope of this
	};

	private const float ConstantTintStrength = 1.45f;
	private const float ColorTintBlend = 0.82f;

	private enum BiomeTintKind
	{
		Grass,
		Foliage,
		DryFoliage
	}

	private static readonly HashSet<string> GrassTintTextures = new(StringComparer.OrdinalIgnoreCase)
	{
		"grass",
		"tall_grass",
		"short_grass",
		"large_fern",
		"fern",
		"grass_block_top",
		"grass_block_side_overlay",
		"grass_block_snow",
		"hanging_roots",
		"pale_hanging_moss",
		"pale_hanging_moss_tip",
		"moss",
		"moss_block",
		"moss_carpet",
		"pale_moss_block",
		"pale_moss_carpet",
		"sugar_cane",
		"cattail",
		"kelp",
		"kelp_top",
		"kelp_plant",
		"seagrass",
		"seagrass_top",
		"tall_seagrass_top",
		"sea_grass"
	};

	private static readonly HashSet<string> GrassTintBlocks = new(StringComparer.OrdinalIgnoreCase)
	{
		"grass_block",
		"grass",
		"tall_grass",
		"short_grass",
		"large_fern",
		"fern",
		"hanging_roots",
		"pale_hanging_moss",
		"pale_hanging_moss_tip",
		"moss_block",
		"moss_carpet",
		"pale_moss_block",
		"pale_moss_carpet",
		"seagrass",
		"tall_seagrass",
		"kelp",
		"kelp_plant",
		"sugar_cane",
		"cattail",
		"potted_fern"
	};

	private static readonly HashSet<string> FoliageTintTextures = new(StringComparer.OrdinalIgnoreCase)
	{
		"oak_leaves",
		"spruce_leaves",
		"birch_leaves",
		"jungle_leaves",
		"acacia_leaves",
		"dark_oak_leaves",
		"mangrove_leaves",
		"pale_oak_leaves",
		"azalea_leaves",
		"flowering_azalea_leaves",
		"vine",
		"cave_vines",
		"cave_vines_body",
		"cave_vines_body_lit",
		"cave_vines_head",
		"cave_vines_head_lit",
		"cave_vines_lit",
		"cave_vines_plant",
		"cave_vines_plant_lit",
		"oak_sapling",
		"spruce_sapling",
		"birch_sapling",
		"jungle_sapling",
		"acacia_sapling",
		"dark_oak_sapling",
		"mangrove_propagule",
		"pale_oak_sapling",
		"azalea",
		"flowering_azalea",
		"big_dripleaf_top",
		"big_dripleaf_stem",
		"big_dripleaf_stem_bottom",
		"big_dripleaf_stem_mid",
		"small_dripleaf_top",
		"small_dripleaf_stem",
		"small_dripleaf_stem_top"
	};

	private static readonly HashSet<string> FoliageTintBlocks = new(StringComparer.OrdinalIgnoreCase)
	{
		"oak_leaves",
		"spruce_leaves",
		"birch_leaves",
		"jungle_leaves",
		"acacia_leaves",
		"dark_oak_leaves",
		"mangrove_leaves",
		"pale_oak_leaves",
		"azalea_leaves",
		"flowering_azalea_leaves",
		"vine",
		"cave_vines",
		"cave_vines_plant",
		"cave_vines_lit",
		"cave_vines_plant_lit",
		"oak_sapling",
		"spruce_sapling",
		"birch_sapling",
		"jungle_sapling",
		"acacia_sapling",
		"dark_oak_sapling",
		"mangrove_propagule",
		"pale_oak_sapling",
		"azalea",
		"flowering_azalea",
		"big_dripleaf",
		"big_dripleaf_stem",
		"small_dripleaf",
		"small_dripleaf_stem",
		"potted_oak_sapling",
		"potted_spruce_sapling",
		"potted_birch_sapling",
		"potted_jungle_sapling",
		"potted_acacia_sapling",
		"potted_dark_oak_sapling",
		"potted_mangrove_propagule",
		"potted_pale_oak_sapling",
		"potted_azalea_bush",
		"potted_flowering_azalea_bush"
	};

	private static readonly HashSet<string> DryFoliageTintTextures = new(StringComparer.OrdinalIgnoreCase)
	{
		"dead_bush",
		"leaf_litter",
		"leaf_litter_1",
		"leaf_litter_2",
		"leaf_litter_3",
		"leaf_litter_4",
		"short_dry_grass",
		"tall_dry_grass"
	};

	private static readonly HashSet<string> DryFoliageTintBlocks = new(StringComparer.OrdinalIgnoreCase)
	{
		"dead_bush",
		"leaf_litter",
		"leaf_litter_1",
		"leaf_litter_2",
		"leaf_litter_3",
		"leaf_litter_4",
		"short_dry_grass",
		"tall_dry_grass",
		"potted_dead_bush"
	};

	private static readonly HashSet<string> ItemTintExclusions = new(StringComparer.OrdinalIgnoreCase)
	{
		"oak_sapling",
		"spruce_sapling",
		"birch_sapling",
		"jungle_sapling",
		"acacia_sapling",
		"dark_oak_sapling",
		"mangrove_propagule",
		"pale_oak_sapling",
		"azalea",
		"flowering_azalea",
		"cherry_sapling"
	};

	private static readonly Dictionary<BiomeTintKind, (float Temperature, float Downfall)> DefaultBiomeTintCoordinates = new()
	{
		[BiomeTintKind.Grass] = (0.5f, 1.0f),
		[BiomeTintKind.Foliage] = (0.5f, 1.0f),
		[BiomeTintKind.DryFoliage] = (0.5f, 0.25f)
	};

	private static Color? GetColorFromBlockName(string? blockName)
	{
		if (string.IsNullOrWhiteSpace(blockName))
		{
			return null;
		}

		var name = NormalizeResourceKey(blockName);
		if (name.EndsWith("bundle", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("_bundle", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		
		if (ConstantColors.TryGetValue(name, out var constantColor))
		{
			return constantColor;
		}

		foreach (var (colorName, color) in ColorMap)
		{
			if (name.StartsWith(colorName))
			{
				return color;
			}
		}

		return null;
	}

	private Image<Rgba32> GetBiomeTintedTexture(string textureId, BiomeTintKind kind)
	{
		var cacheKey = $"{NormalizeResourceKey(textureId)}|{kind}";
		if (_biomeTintedTextureCache.TryGetValue(cacheKey, out var cached))
		{
			return cached;
		}

		var colormap = kind switch
		{
			BiomeTintKind.Grass => _textureRepository.GrassColorMap,
			BiomeTintKind.Foliage => _textureRepository.FoliageColorMap,
			BiomeTintKind.DryFoliage => _textureRepository.DryFoliageColorMap,
			_ => null
		};

		if (colormap is null)
		{
			return _textureRepository.GetTexture(textureId);
		}

		var tintColor = SampleBiomeTintColor(colormap, kind);
		var tinted = ApplyBiomeTint(_textureRepository.GetTexture(textureId), tintColor);
		_biomeTintedTextureCache[cacheKey] = tinted;
		return tinted;
	}

	private static Color SampleBiomeTintColor(Image<Rgba32> colormap, BiomeTintKind kind)
	{
		if (!DefaultBiomeTintCoordinates.TryGetValue(kind, out var coordinates))
		{
			coordinates = (0.5f, 1.0f);
		}

		var temperature = Math.Clamp(coordinates.Temperature, 0f, 1f);
		var downfall = Math.Clamp(coordinates.Downfall, 0f, 1f);
		var rainfall = Math.Clamp(downfall * temperature, 0f, 1f);
		var x = Math.Clamp((int)MathF.Round((1f - temperature) * (colormap.Width - 1)), 0, colormap.Width - 1);
		var y = Math.Clamp((int)MathF.Round((1f - rainfall) * (colormap.Height - 1)), 0, colormap.Height - 1);
		return colormap[x, y];
	}

	private static string NormalizeResourceKey(string? identifier)
	{
		if (string.IsNullOrWhiteSpace(identifier))
		{
			return string.Empty;
		}

		var normalized = identifier.Replace('\\', '/').Trim();
		if (normalized.StartsWith('#'))
		{
			return string.Empty;
		}

		var stateSeparator = normalized.IndexOf('[');
		if (stateSeparator >= 0)
		{
			normalized = normalized[..stateSeparator];
		}

		var colonIndex = normalized.IndexOf(':');
		if (colonIndex >= 0)
		{
			normalized = normalized[(colonIndex + 1)..];
		}

		normalized = normalized.TrimStart('/');
		if (normalized.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[9..];
		}

		if (normalized.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[7..];
		}

		if (normalized.StartsWith("block/", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[6..];
		}
		else if (normalized.StartsWith("blocks/", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[7..];
		}
		else if (normalized.StartsWith("item/", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[5..];
		}
		else if (normalized.StartsWith("items/", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[6..];
		}

		normalized = normalized.Trim('/');
		var slashIndex = normalized.LastIndexOf('/');
		if (slashIndex >= 0)
		{
			normalized = normalized[(slashIndex + 1)..];
		}

		return normalized.ToLowerInvariant();
	}

	private static bool IsLikelyItemTexture(string? identifier)
	{
		if (string.IsNullOrWhiteSpace(identifier))
		{
			return false;
		}

		var normalized = identifier.Replace('\\', '/');
		if (normalized.StartsWith("item/", StringComparison.OrdinalIgnoreCase) ||
			normalized.StartsWith("items/", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (normalized.StartsWith("textures/item/", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		return normalized.Contains("/item/", StringComparison.OrdinalIgnoreCase)
			|| normalized.Contains(":item/", StringComparison.OrdinalIgnoreCase)
			|| normalized.Contains("/items/", StringComparison.OrdinalIgnoreCase)
			|| normalized.Contains(":items/", StringComparison.OrdinalIgnoreCase);
	}

	private static bool TryGetBiomeTintKind(string textureId, string? blockName, out BiomeTintKind kind)
	{
		var textureKey = NormalizeResourceKey(textureId);
		var blockKey = NormalizeResourceKey(blockName);
		var isItemTexture = IsLikelyItemTexture(textureId);

		if (isItemTexture && (IsInSet(ItemTintExclusions, textureKey) || IsInSet(ItemTintExclusions, blockKey)))
		{
			kind = default;
			return false;
		}

		if (IsDry(textureKey) || IsDry(blockKey))
		{
			kind = BiomeTintKind.DryFoliage;
			return true;
		}

		if (IsGrass(textureKey) || IsGrass(blockKey))
		{
			kind = BiomeTintKind.Grass;
			return true;
		}

		if (IsFoliage(textureKey) || IsFoliage(blockKey))
		{
			kind = BiomeTintKind.Foliage;
			return true;
		}

		kind = default;
		return false;

		static bool IsInSet(HashSet<string> set, string? key)
			=> !string.IsNullOrEmpty(key) && set.Contains(key);

		static bool IsDry(string? key)
			=> IsInSet(DryFoliageTintTextures, key) || IsInSet(DryFoliageTintBlocks, key);

		static bool IsGrass(string? key)
			=> IsInSet(GrassTintTextures, key) || IsInSet(GrassTintBlocks, key);

		static bool IsFoliage(string? key)
			=> IsInSet(FoliageTintTextures, key) || IsInSet(FoliageTintBlocks, key);
	}

	private static Color? TryGetConstantTint(string textureId, string? blockName)
	{
		var textureKey = NormalizeResourceKey(textureId);
		if (ConstantColors.TryGetValue(textureKey, out var color))
		{
			return color;
		}

		var blockKey = NormalizeResourceKey(blockName);
		if (ConstantColors.TryGetValue(blockKey, out color))
		{
			return color;
		}

		return null;
	}

	private static Image<Rgba32> ApplyBiomeTint(Image<Rgba32> baseTexture, Color tintColor)
	{
		var tinted = baseTexture.Clone();
		var tintVector = tintColor.ToPixel<Rgba32>().ToVector4();

		tinted.ProcessPixelRows(accessor =>
		{
			for (var y = 0; y < accessor.Height; y++)
			{
				var row = accessor.GetRowSpan(y);
				for (var x = 0; x < row.Length; x++)
				{
					var originalPixel = row[x];
					if (originalPixel.A == 0)
					{
						continue;
					}

					var originalVector = originalPixel.ToVector4();
					var finalVector = originalVector * tintVector;
					finalVector.W = originalVector.W;

					row[x].FromVector4(finalVector);
				}
			}
		});

		return tinted;
	}
	
}
