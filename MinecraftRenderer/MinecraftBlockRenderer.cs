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

	public IReadOnlyList<string> GetKnownItemNames() => _itemRegistry?.GetAllItemNames() ?? Array.Empty<string>();

	public Image<Rgba32> RenderBlock(string blockName, BlockRenderOptions? options = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(blockName);
		options ??= BlockRenderOptions.Default;

		string modelName = blockName;
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

	private static Color? GetColorFromBlockName(string? blockName)
	{
		if (string.IsNullOrWhiteSpace(blockName))
		{
			return null;
		}

		var name = blockName.ToLowerInvariant();
		if (name.Contains(':'))
		{
			name = name.Split(':')[1];
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
}
