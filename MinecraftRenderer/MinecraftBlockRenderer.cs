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

		var modelResolver = BlockModelResolver.LoadFromMinecraftAssets(assetsDirectory);
		var blockRegistry = BlockRegistry.LoadFromMinecraftAssets(assetsDirectory, modelResolver.Definitions);
		var itemRegistry = ItemRegistry.LoadFromMinecraftAssets(assetsDirectory, modelResolver.Definitions);
		var texturesRoot = Directory.Exists(Path.Combine(assetsDirectory, "textures"))
			? Path.Combine(assetsDirectory, "textures")
			: assetsDirectory;
		var textureRepository = new TextureRepository(texturesRoot);

		return new MinecraftBlockRenderer(modelResolver, textureRepository, blockRegistry, itemRegistry);
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
		return RenderModel(model, options);
	}

	public Image<Rgba32> RenderItem(string itemName, BlockRenderOptions? options = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(itemName);
		options ??= BlockRenderOptions.Default;
		EnsureNotDisposed();

		ItemRegistry.ItemInfo? itemInfo = null;
		if (_itemRegistry is not null)
		{
			_itemRegistry.TryGetInfo(itemName, out itemInfo);
		}

		var modelName = itemInfo?.Model;
		if (string.IsNullOrWhiteSpace(modelName))
		{
			if (_blockRegistry.TryGetModel(itemName, out var blockModel) && !string.IsNullOrWhiteSpace(blockModel))
			{
				modelName = blockModel;
			}
			else
			{
				modelName = itemName;
			}
		}

		BlockModelInstance? model = null;
		var hasModel = false;
		foreach (var candidate in BuildModelCandidates(modelName!, itemName))
		{
			try
			{
				model = _modelResolver.Resolve(candidate);
				hasModel = true;
				break;
			}
			catch (KeyNotFoundException)
			{
				continue;
			}
			catch (InvalidOperationException)
			{
				continue;
			}
		}

		var usesBuiltinGenerated = UsesBuiltinGenerated(model);
		var isBuiltinEntityItem = UsesBuiltinEntity(model) || IsBuiltinEntityItemName(itemName);

		if (isBuiltinEntityItem && TryRenderBuiltinEntityItem(itemName, itemInfo, options, out var builtinEntityRender))
		{
			return builtinEntityRender;
		}

		if (model is not null && IsBillboardModel(model))
		{
			var billboardLayers = CollectBillboardTextures(model, itemInfo);
			var resolvedBillboardLayers = ResolveTextureIdentifiers(billboardLayers, model);
			if (resolvedBillboardLayers.Count > 0)
			{
				return RenderFlatItem(resolvedBillboardLayers, options);
			}
		}

		var layerIdentifiers = CollectItemLayerTextures(model, itemInfo);
		if (!hasModel || model is null || model.Elements.Count == 0 || usesBuiltinGenerated)
		{
			if (TryRenderGeneratedGeometry(itemName, model, itemInfo, options, out var generated))
			{
				return generated;
			}

			if (layerIdentifiers.Count > 0)
			{
				var resolvedLayers = ResolveTextureIdentifiers(layerIdentifiers, model);
				if (resolvedLayers.Count > 0)
				{
					return RenderFlatItem(resolvedLayers, options);
				}
			}

			if (TryRenderEmbeddedTexture(itemName, options, out var embeddedFlat))
			{
				return embeddedFlat;
			}
		}

		if (hasModel && model is not null && model.Elements.Count > 0)
		{
			return RenderModel(model, options);
		}

		if (TryRenderEmbeddedTexture(itemName, options, out var fallbackFlat))
		{
			return fallbackFlat;
		}

		throw new InvalidOperationException($"Unable to resolve a model or texture for item '{itemName}'.");
	}

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
}
