namespace MinecraftRenderer;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using MinecraftRenderer.Nbt;
using MinecraftRenderer.Snbt;
using MinecraftRenderer.TexturePacks;

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
		Vector3 AdditionalTranslation = default,
		TransformDefinition? OverrideGuiTransform = null,
		IReadOnlyList<string>? PackIds = null,
		ItemRenderData? ItemData = null)
	{
		public static BlockRenderOptions Default { get; } = new();
	}

	public sealed record ItemRenderData(
		Color? Layer0Tint = null,
		IReadOnlyDictionary<int, Color>? AdditionalLayerTints = null,
		bool DisableDefaultLayer0Tint = false,
		NbtCompound? CustomData = null,
		NbtCompound? Profile = null)
	{
		public Color? GetLayerTint(int layerIndex)
		{
			if (AdditionalLayerTints is not null && AdditionalLayerTints.TryGetValue(layerIndex, out var explicitTint))
			{
				return explicitTint;
			}

			if (layerIndex == 0 && Layer0Tint.HasValue)
			{
				return Layer0Tint.Value;
			}

			return null;
		}
	}

	public static bool DebugDisableCulling = false;

	private readonly BlockModelResolver _modelResolver;
	private readonly TextureRepository _textureRepository;
	private readonly BlockRegistry _blockRegistry;
	private readonly ItemRegistry? _itemRegistry;
	private readonly RenderPackContext _packContext;
	private readonly string? _assetsDirectory;
	private readonly IReadOnlyList<OverlayRoot> _baseOverlayRoots;
	private readonly TexturePackRegistry? _packRegistry;
	private readonly ConcurrentDictionary<string, MinecraftBlockRenderer> _packRendererCache =
		new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, Image<Rgba32>> _biomeTintedTextureCache = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, Lazy<Image<Rgba32>>> _playerSkinCache =
		new(StringComparer.OrdinalIgnoreCase);
	private bool _disposed;

	public TextureRepository TextureRepository => _textureRepository;

	private MinecraftBlockRenderer(BlockModelResolver modelResolver, TextureRepository textureRepository,
		BlockRegistry blockRegistry, ItemRegistry? itemRegistry, string? assetsDirectory,
		IReadOnlyList<OverlayRoot> baseOverlayRoots, TexturePackRegistry? packRegistry,
		RenderPackContext packContext)
	{
		_modelResolver = modelResolver;
		_textureRepository = textureRepository;
		_blockRegistry = blockRegistry;
		_itemRegistry = itemRegistry;
		_assetsDirectory = assetsDirectory;
		_baseOverlayRoots = baseOverlayRoots;
		_packRegistry = packRegistry;
		_packContext = packContext;
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
			var textureRepository = new TextureRepository(dataDirectory,
				File.Exists(textureContentPath) ? textureContentPath : null);
			ItemRegistry? itemRegistry = null;
			if (File.Exists(itemsPath))
			{
				itemRegistry = ItemRegistry.LoadFromFile(itemsPath);
			}

			var packContext = RenderPackContext.Create(null, Array.Empty<OverlayRoot>(), null);
			return new MinecraftBlockRenderer(modelResolver, textureRepository, blockRegistry, itemRegistry, null,
				Array.Empty<OverlayRoot>(), null, packContext);
		}

		if (IsMinecraftAssetsRoot(dataDirectory))
		{
			return CreateFromMinecraftAssets(dataDirectory);
		}

		throw new DirectoryNotFoundException(
			$"The directory '{dataDirectory}' does not contain the expected aggregated JSON files or Minecraft asset folders.");
	}

	public static MinecraftBlockRenderer CreateFromMinecraftAssets(string assetsDirectory,
		TexturePackRegistry? texturePackRegistry = null,
		IReadOnlyList<string>? defaultPackIds = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(assetsDirectory);

		var overlayRoots = DiscoverOverlayRoots(assetsDirectory);
		TexturePackStack? defaultPackStack = null;
		if (texturePackRegistry is not null && defaultPackIds is { Count: > 0 })
		{
			defaultPackStack = texturePackRegistry.BuildPackStack(defaultPackIds);
		}
		var packContext = RenderPackContext.Create(assetsDirectory, overlayRoots, defaultPackStack);
		var overlayPaths = packContext.OverlayRoots
			.Select(static root => root.Path)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		var modelResolver = BlockModelResolver.LoadFromMinecraftAssets(assetsDirectory, overlayPaths, packContext.AssetNamespaces);
		var blockRegistry =
			BlockRegistry.LoadFromMinecraftAssets(assetsDirectory, modelResolver.Definitions, overlayPaths, packContext.AssetNamespaces);
		var itemRegistry =
			ItemRegistry.LoadFromMinecraftAssets(assetsDirectory, modelResolver.Definitions, overlayPaths, packContext.AssetNamespaces);
		var texturesRoot = Directory.Exists(Path.Combine(assetsDirectory, "textures"))
			? Path.Combine(assetsDirectory, "textures")
			: assetsDirectory;
		var textureRepository = new TextureRepository(texturesRoot, overlayRoots: overlayPaths,
			assetNamespaces: packContext.AssetNamespaces);

		return new MinecraftBlockRenderer(modelResolver, textureRepository, blockRegistry, itemRegistry,
			assetsDirectory, overlayRoots, texturePackRegistry, packContext);
	}

	private static IReadOnlyList<OverlayRoot> DiscoverOverlayRoots(string assetsDirectory)
	{
		var overlays = new List<OverlayRoot>();
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

			if (overlays.Any(root => string.Equals(root.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
			{
				return;
			}

			var sourceId = $"customdata_{overlays.Count}";
			overlays.Add(new OverlayRoot(fullPath, sourceId, OverlayRootKind.CustomData));
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
		var effectiveOptions = options ?? BlockRenderOptions.Default;
		var renderer = ResolveRendererForOptions(effectiveOptions, out var forwardedOptions);
		return renderer.RenderBlockInternal(blockName, forwardedOptions);
	}

	public Image<Rgba32> RenderItem(string itemName, BlockRenderOptions? options = null)
	{
		var effectiveOptions = options ?? BlockRenderOptions.Default;
		var renderer = ResolveRendererForOptions(effectiveOptions, out var forwardedOptions);
		return renderer.RenderGuiItemInternal(itemName, forwardedOptions);
	}

	public Image<Rgba32> RenderItem(string itemName, ItemRenderData itemData, BlockRenderOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(itemData);
		var baseOptions = options ?? BlockRenderOptions.Default;
		var effectiveOptions = baseOptions with { ItemData = itemData };
		var renderer = ResolveRendererForOptions(effectiveOptions, out var forwardedOptions);
		return renderer.RenderGuiItemInternal(itemName, forwardedOptions);
	}

	public Image<Rgba32> RenderItemFromNbt(NbtDocument document, BlockRenderOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(document);
		var compound = document.RootCompound
		                ?? throw new ArgumentException("SNBT document must have a compound root.", nameof(document));
		return RenderItemFromNbt(compound, options);
	}

	public Image<Rgba32> RenderItemFromNbt(NbtCompound compound, BlockRenderOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(compound);
		var itemId = SnbtItemUtilities.TryGetItemId(compound)
		             ?? throw new ArgumentException("SNBT item payload did not contain an item id.", nameof(compound));
		var normalizedItemId = NormalizeItemTextureKey(itemId);

		var itemData = ExtractItemRenderDataFromComponents(compound);
		return itemData is not null
			? RenderItem(normalizedItemId, itemData, options)
			: RenderItem(normalizedItemId, options);
	}

	private Image<Rgba32> RenderBlockInternal(string blockName, BlockRenderOptions options)
	{
		EnsureNotDisposed();
		var modelName = blockName;
		if (_blockRegistry.TryGetModel(blockName, out var mappedModel) && !string.IsNullOrWhiteSpace(mappedModel))
		{
			modelName = mappedModel;
		}

		var model = _modelResolver.Resolve(modelName);
		return RenderModel(model, options, blockName);
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;

		foreach (var renderer in _packRendererCache.Values)
		{
			renderer.Dispose();
		}
		_packRendererCache.Clear();
		_textureRepository.Dispose();

		foreach (var image in _biomeTintedTextureCache.Values)
		{
			image.Dispose();
		}

		_biomeTintedTextureCache.Clear();

		foreach (var skin in _playerSkinCache.Values)
		{
			if (skin.IsValueCreated)
			{
				skin.Value.Dispose();
			}
		}

		_playerSkinCache.Clear();
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

	private static readonly Lazy<BiomeTintConfiguration> BiomeTintConfigurationLazy =
		new(BiomeTintConfiguration.LoadDefault);

	private static BiomeTintConfiguration BiomeTints => BiomeTintConfigurationLazy.Value;

	private const float ConstantTintStrength = 1.45f;
	private const float ColorTintBlend = 0.82f;

	private enum BiomeTintKind
	{
		Grass,
		Foliage,
		DryFoliage
	}

	private static readonly Dictionary<BiomeTintKind, (float Temperature, float Downfall)> DefaultBiomeTintCoordinates =
		new()
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

		var constantColors = BiomeTints.ConstantColors;
		if (constantColors.TryGetValue(name, out var constantColor))
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

	private static ItemRenderData? ExtractItemRenderDataFromComponents(NbtCompound root)
	{
		var components = ResolveComponentsCompound(root);
		if (components is null)
		{
			return null;
		}

		Color? layer0Tint = null;
		var disableDefaultLayer0Tint = false;
		Dictionary<int, Color>? additionalLayerTints = null;
		NbtCompound? customData = null;
		NbtCompound? profile = null;

		if (components.TryGetValue("minecraft:dyed_color", out var dyedTag) &&
		    TryExtractColor(dyedTag, out var dyedColor))
		{
			layer0Tint = dyedColor;
			disableDefaultLayer0Tint = true;
		}

		if (components.TryGetValue("minecraft:custom_data", out var customDataTag) && customDataTag is NbtCompound customCompound &&
		    customCompound.Count > 0)
		{
			customData = customCompound;
		}

		if (components.TryGetValue("minecraft:profile", out var profileTag) && profileTag is NbtCompound profileCompound &&
		    profileCompound.Count > 0)
		{
			profile = profileCompound;
		}

		if (layer0Tint.HasValue || additionalLayerTints is { Count: > 0 } || disableDefaultLayer0Tint || customData is not null || profile is not null)
		{
			return new ItemRenderData(layer0Tint, additionalLayerTints, disableDefaultLayer0Tint, customData, profile);
		}

		return null;
	}

	private static NbtCompound? ResolveComponentsCompound(NbtCompound root)
	{
		if (root.TryGetValue("components", out var componentsTag) && componentsTag is NbtCompound components)
		{
			return components;
		}

		if (root.TryGetValue("tag", out var legacyTag) && legacyTag is NbtCompound legacyCompound)
		{
			var nested = ResolveComponentsCompound(legacyCompound);
			if (nested is not null)
			{
				return nested;
			}
		}

		return null;
	}

	private static bool TryExtractColor(NbtTag tag, out Color color)
	{
		switch (tag)
		{
			case NbtInt intTag:
				color = ColorFromRgb(intTag.Value);
				return true;
			case NbtLong longTag:
				color = ColorFromRgb(unchecked((int)longTag.Value));
				return true;
			case NbtCompound compound:
			{
				if (compound.TryGetValue("rgb", out var rgbTag) && TryExtractColor(rgbTag, out color))
				{
					return true;
				}

				if (compound.TryGetValue("value", out var valueTag) && TryExtractColor(valueTag, out color))
				{
					return true;
				}

				if (compound.TryGetValue("color", out var colorTag) && TryExtractColor(colorTag, out color))
				{
					return true;
				}

				if (TryExtractChannelColor(compound, out color))
				{
					return true;
				}

				break;
			}
		}

		color = default;
		return false;
	}

	private static bool TryExtractChannelColor(NbtCompound compound, out Color color)
	{
		if (TryGetByte(compound, "red", out var r) && TryGetByte(compound, "green", out var g) &&
		    TryGetByte(compound, "blue", out var b))
		{
			color = new Color(new Rgba32(r, g, b, 255));
			return true;
		}

		if (TryGetByte(compound, "r", out r) && TryGetByte(compound, "g", out g) && TryGetByte(compound, "b", out b))
		{
			color = new Color(new Rgba32(r, g, b, 255));
			return true;
		}

		color = default;
		return false;
	}

	private static bool TryGetByte(NbtCompound compound, string key, out byte value)
	{
		if (!compound.TryGetValue(key, out var tag))
		{
			value = 0;
			return false;
		}

		return TryGetByte(tag, out value);
	}

	private static bool TryGetByte(NbtTag tag, out byte value)
	{
		switch (tag)
		{
			case NbtByte b:
				value = unchecked((byte)b.Value);
				return true;
			case NbtShort s when s.Value >= byte.MinValue && s.Value <= byte.MaxValue:
				value = (byte)s.Value;
				return true;
			case NbtInt i when i.Value >= byte.MinValue && i.Value <= byte.MaxValue:
				value = (byte)i.Value;
				return true;
		}

		value = 0;
		return false;
	}

	private static Color ColorFromRgb(int rgb)
	{
		var value = unchecked((uint)rgb);
		var r = (byte)((value >> 16) & 0xFF);
		var g = (byte)((value >> 8) & 0xFF);
		var b = (byte)(value & 0xFF);
		return new Color(new Rgba32(r, g, b, 255));
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
		var config = BiomeTints;
		var textureKey = NormalizeResourceKey(textureId);
		var blockKey = NormalizeResourceKey(blockName);
		var isItemTexture = IsLikelyItemTexture(textureId);

		if (isItemTexture &&
		    (IsInSet(config.ItemTintExclusions, textureKey) || IsInSet(config.ItemTintExclusions, blockKey)))
		{
			kind = default;
			return false;
		}

		if (IsDry(config, textureKey) || IsDry(config, blockKey))
		{
			kind = BiomeTintKind.DryFoliage;
			return true;
		}

		if (IsGrass(config, textureKey) || IsGrass(config, blockKey))
		{
			kind = BiomeTintKind.Grass;
			return true;
		}

		if (IsFoliage(config, textureKey) || IsFoliage(config, blockKey))
		{
			kind = BiomeTintKind.Foliage;
			return true;
		}

		kind = default;
		return false;

		static bool IsInSet(HashSet<string> set, string? key)
			=> !string.IsNullOrEmpty(key) && set.Contains(key);

		static bool IsDry(BiomeTintConfiguration config, string? key)
			=> IsInSet(config.DryFoliageTextures, key) || IsInSet(config.DryFoliageBlocks, key);

		static bool IsGrass(BiomeTintConfiguration config, string? key)
			=> IsInSet(config.GrassTextures, key) || IsInSet(config.GrassBlocks, key);

		static bool IsFoliage(BiomeTintConfiguration config, string? key)
			=> IsInSet(config.FoliageTextures, key) || IsInSet(config.FoliageBlocks, key);
	}

	private static Color? TryGetConstantTint(string textureId, string? blockName)
	{
		var textureKey = NormalizeResourceKey(textureId);
		var constantColors = BiomeTints.ConstantColors;
		if (constantColors.TryGetValue(textureKey, out var color))
		{
			return color;
		}

		var blockKey = NormalizeResourceKey(blockName);
		if (constantColors.TryGetValue(blockKey, out color))
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