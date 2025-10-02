namespace MinecraftRenderer;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using MinecraftRenderer.Assets;
using MinecraftRenderer.TexturePacks;

public sealed partial class MinecraftBlockRenderer
{
	public sealed record ResourceIdResult(string ResourceId, string SourcePackId, string PackStackHash);

	private const string VanillaPackId = "vanilla";
	private static readonly string RendererVersion = typeof(MinecraftBlockRenderer).Assembly.GetName().Version?.ToString() ?? "0";

	private MinecraftBlockRenderer ResolveRendererForOptions(BlockRenderOptions options,
		out BlockRenderOptions forwardedOptions)
	{
		if (_packRegistry is null || options.PackIds is null || options.PackIds.Count == 0)
		{
			forwardedOptions = options;
			return this;
		}

		if (_packContext.PackIds.Count > 0 && PackSequencesEqual(options.PackIds, _packContext.PackIds))
		{
			forwardedOptions = options with { PackIds = null };
			return this;
		}

		var renderer = GetRendererForPackStack(options.PackIds);
		forwardedOptions = options with { PackIds = null };
		return renderer;
	}

	private bool PackSequencesEqual(IReadOnlyList<string> candidate, IReadOnlyList<string> baseline)
	{
		if (candidate.Count != baseline.Count)
		{
			return false;
		}

		for (var i = 0; i < candidate.Count; i++)
		{
			if (!string.Equals(candidate[i], baseline[i], StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
		}

		return true;
	}

	private MinecraftBlockRenderer GetRendererForPackStack(IReadOnlyList<string> packIds)
	{
		if (_packRegistry is null)
		{
			throw new InvalidOperationException(
				"This renderer was created without a texture pack registry and cannot resolve pack combinations.");
		}

		if (string.IsNullOrWhiteSpace(_assetsDirectory))
		{
			throw new InvalidOperationException(
				"Texture pack rendering requires a renderer created from Minecraft assets (not aggregated data files).");
		}

		var packStack = _packRegistry.BuildPackStack(packIds);
		return _packRendererCache.GetOrAdd(packStack.Fingerprint, _ => CreatePackRenderer(packStack));
	}

	private MinecraftBlockRenderer CreatePackRenderer(TexturePackStack packStack)
	{
		var packContext = RenderPackContext.Create(_assetsDirectory, _baseOverlayRoots, packStack);
		var overlayPaths = packContext.OverlayRoots
			.Select(static root => root.Path)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		var modelResolver = BlockModelResolver.LoadFromMinecraftAssets(_assetsDirectory!, overlayPaths,
			packContext.AssetNamespaces);
		var blockRegistry = BlockRegistry.LoadFromMinecraftAssets(_assetsDirectory!, modelResolver.Definitions,
			overlayPaths, packContext.AssetNamespaces);
		var itemRegistry = ItemRegistry.LoadFromMinecraftAssets(_assetsDirectory!, modelResolver.Definitions,
			overlayPaths, packContext.AssetNamespaces);
		var texturesRoot = Directory.Exists(Path.Combine(_assetsDirectory!, "textures"))
			? Path.Combine(_assetsDirectory!, "textures")
			: _assetsDirectory!;
		var textureRepository = new TextureRepository(texturesRoot, overlayRoots: overlayPaths,
			assetNamespaces: packContext.AssetNamespaces);

		return new MinecraftBlockRenderer(modelResolver, textureRepository, blockRegistry, itemRegistry, _assetsDirectory,
			_baseOverlayRoots, null, packContext);
	}

	public ResourceIdResult ComputeResourceId(string target, BlockRenderOptions? options = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(target);
		var effectiveOptions = options ?? BlockRenderOptions.Default;
		var renderer = ResolveRendererForOptions(effectiveOptions, out var forwardedOptions);
		return renderer.ComputeResourceIdInternal(target, forwardedOptions);
	}

	private ResourceIdResult ComputeResourceIdInternal(string target, BlockRenderOptions options)
	{
		EnsureNotDisposed();

		var normalizedTarget = target.Trim();
		string? modelPath = null;
		var resolvedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string variantKey;

		if (_blockRegistry.TryGetModel(normalizedTarget, out var blockModelPath) &&
		    !string.IsNullOrWhiteSpace(blockModelPath))
		{
			modelPath = blockModelPath;
			var model = _modelResolver.Resolve(blockModelPath);
			modelPath = model.Name;
			foreach (var texture in CollectResolvedTextures(model))
			{
				resolvedTextures.Add(texture);
			}

			variantKey = $"block:{normalizedTarget}:{model.Name}:{JoinTextures(resolvedTextures)}";
		}
		else if (_itemRegistry is not null)
		{
			_itemRegistry.TryGetInfo(normalizedTarget, out var itemInfo);
			string? referenceModel = null;
			if (_itemRegistry.TryGetModel(normalizedTarget, out var itemModel) && !string.IsNullOrWhiteSpace(itemModel))
			{
				referenceModel = itemModel;
			}

			var (model, _) = ResolveItemModel(normalizedTarget, itemInfo);
			if (model is not null)
			{
				modelPath = model.Name;
				referenceModel ??= model.Name;
				foreach (var texture in CollectResolvedTextures(model))
				{
					resolvedTextures.Add(texture);
				}
			}
			else if (itemInfo?.Texture is not null)
			{
				resolvedTextures.Add(itemInfo.Texture);
			}

			if (resolvedTextures.Count == 0 && referenceModel is not null)
			{
				resolvedTextures.Add(referenceModel);
			}

			var itemDataKey = options.ItemData is not null ? BuildItemRenderDataKey(options.ItemData) : string.Empty;
			modelPath ??= referenceModel ?? normalizedTarget;
			variantKey = $"item:{normalizedTarget}:{modelPath}:{JoinTextures(resolvedTextures)}:{itemDataKey}";
		}
		else
		{
			variantKey = $"literal:{normalizedTarget}";
		}

		var sourcePackId = DetermineSourcePackId(modelPath, resolvedTextures);
		var descriptor = $"{RendererVersion}|{_packContext.PackStackHash}|{variantKey}";
		var resourceId = ComputeResourceIdHash(descriptor);
		return new ResourceIdResult(resourceId, sourcePackId, _packContext.PackStackHash);
	}

	private static string JoinTextures(IReadOnlyCollection<string> textures)
	{
		if (textures.Count == 0)
		{
			return string.Empty;
		}

		return string.Join(',', textures.OrderBy(static t => t, StringComparer.OrdinalIgnoreCase));
	}

	private static IReadOnlyCollection<string> CollectResolvedTextures(BlockModelInstance model)
	{
		var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var (_, texture) in model.Textures)
		{
			var resolved = ResolveTexture(texture, model);
			if (!string.IsNullOrWhiteSpace(resolved))
			{
				set.Add(resolved);
			}
		}

		return set;
	}

	private static string BuildItemRenderDataKey(ItemRenderData data)
	{
		var builder = new StringBuilder();
		if (data.Layer0Tint.HasValue)
		{
			builder.Append("l0=");
			builder.Append(data.Layer0Tint.Value.ToPixel<Rgba32>().ToHex());
		}
		else
		{
			builder.Append("l0=none");
		}

		builder.Append(";disable=");
		builder.Append(data.DisableDefaultLayer0Tint ? '1' : '0');

		if (data.AdditionalLayerTints is { Count: > 0 })
		{
			foreach (var layer in data.AdditionalLayerTints.OrderBy(static kvp => kvp.Key))
			{
				builder.Append(";l");
				builder.Append(layer.Key.ToString(CultureInfo.InvariantCulture));
				builder.Append('=');
				builder.Append(layer.Value.ToPixel<Rgba32>().ToHex());
			}
		}

		return builder.ToString();
	}

	private string DetermineSourcePackId(string? modelPath, IReadOnlyCollection<string> textureIds)
	{
		var searchRoots = _packContext.SearchRoots;
		var normalizedModel = NormalizeModelPath(modelPath);
		if (!string.IsNullOrWhiteSpace(normalizedModel))
		{
			for (var i = searchRoots.Count - 1; i >= 0; i--)
			{
				var root = searchRoots[i];
				if (root.Kind == OverlayRootKind.Vanilla)
				{
					continue;
				}

				var candidate = Path.Combine(root.Path, "models", normalizedModel + ".json");
				if (File.Exists(candidate))
				{
					return root.SourceId;
				}
			}
		}

		foreach (var textureId in textureIds)
		{
			var normalizedTexture = NormalizeTexturePath(textureId);
			if (string.IsNullOrWhiteSpace(normalizedTexture))
			{
				continue;
			}

			for (var i = searchRoots.Count - 1; i >= 0; i--)
			{
				var root = searchRoots[i];
				if (root.Kind == OverlayRootKind.Vanilla)
				{
					continue;
				}

				var candidate = Path.Combine(root.Path, "textures", normalizedTexture + ".png");
				if (File.Exists(candidate))
				{
					return root.SourceId;
				}
			}
		}

		return VanillaPackId;
	}

	private static string NormalizeModelPath(string? modelPath)
	{
		if (string.IsNullOrWhiteSpace(modelPath))
		{
			return string.Empty;
		}

		var normalized = modelPath.Replace('\\', '/').Trim();
		if (normalized.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[10..];
		}

		normalized = normalized.TrimStart('/');
		if (normalized.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[7..];
		}

		return normalized;
	}

	private static string NormalizeTexturePath(string textureId)
	{
		if (string.IsNullOrWhiteSpace(textureId))
		{
			return string.Empty;
		}

		var normalized = textureId.Replace('\\', '/').Trim();
		if (normalized.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[10..];
		}

		normalized = normalized.TrimStart('/');
		if (normalized.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[9..];
		}

		if (normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[..^4];
		}

		return normalized;
	}

	private static string ComputeResourceIdHash(string input)
	{
		using var sha = SHA256.Create();
		var bytes = Encoding.UTF8.GetBytes(input);
		var hash = sha.ComputeHash(bytes);
		return EncodeBase32(hash);
	}

	private static string EncodeBase32(ReadOnlySpan<byte> bytes)
	{
		const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
		var outputLength = (bytes.Length * 8 + 4) / 5;
		var builder = new StringBuilder(outputLength);
		var buffer = 0;
		var bitsLeft = 0;

		foreach (var b in bytes)
		{
			buffer = (buffer << 8) | b;
			bitsLeft += 8;
			while (bitsLeft >= 5)
			{
				var index = (buffer >> (bitsLeft - 5)) & 0b11111;
				bitsLeft -= 5;
				builder.Append(alphabet[index]);
			}
		}

		if (bitsLeft > 0)
		{
			var index = (buffer << (5 - bitsLeft)) & 0b11111;
			builder.Append(alphabet[index]);
		}

		return builder.ToString();
	}

	private sealed record OverlaySearchRoot(string Path, string SourceId, OverlayRootKind Kind);

	private sealed class RenderPackContext
	{
		private RenderPackContext(string assetsRoot, IReadOnlyList<OverlayRoot> overlayRoots,
			IReadOnlyList<string> packIds, string packStackHash, IReadOnlyList<RegisteredResourcePack> packs)
		{
			AssetsRoot = assetsRoot;
			OverlayRoots = overlayRoots;
			PackIds = packIds;
			PackStackHash = packStackHash;
			Packs = packs;
			SearchRoots = BuildSearchRoots();
			AssetNamespaces = BuildAssetNamespaces();
		}

		public string AssetsRoot { get; }
		public IReadOnlyList<OverlayRoot> OverlayRoots { get; }
		public IReadOnlyList<string> PackIds { get; }
		public string PackStackHash { get; }
		public IReadOnlyList<RegisteredResourcePack> Packs { get; }
		public IReadOnlyList<OverlaySearchRoot> SearchRoots { get; }
        public AssetNamespaceRegistry AssetNamespaces { get; }

		private IReadOnlyList<OverlaySearchRoot> BuildSearchRoots()
		{
			var roots = new List<OverlaySearchRoot>();
			foreach (var overlay in OverlayRoots)
			{
				roots.Add(new OverlaySearchRoot(overlay.Path, overlay.SourceId, overlay.Kind));
			}

			if (!string.IsNullOrWhiteSpace(AssetsRoot))
			{
				roots.Add(new OverlaySearchRoot(AssetsRoot, VanillaPackId, OverlayRootKind.Vanilla));
			}

			return roots;
		}

		private AssetNamespaceRegistry BuildAssetNamespaces()
		{
			var registry = new AssetNamespaceRegistry();

			if (!string.IsNullOrWhiteSpace(AssetsRoot) && Directory.Exists(AssetsRoot))
			{
				RegisterNamespaceRoot(registry, "minecraft", AssetsRoot, VanillaPackId, isVanilla: true);
			}

			foreach (var overlay in OverlayRoots)
			{
				AddOverlayNamespaces(registry, overlay);
			}

			return registry;
		}

		private static void AddOverlayNamespaces(AssetNamespaceRegistry registry, OverlayRoot overlay)
		{
			if (string.IsNullOrWhiteSpace(overlay.Path) || !Directory.Exists(overlay.Path))
			{
				return;
			}

			var normalized = Path.GetFullPath(overlay.Path);
			var directoryInfo = new DirectoryInfo(normalized);
			if (directoryInfo.Parent is { } parent && parent.Name.Equals("assets", StringComparison.OrdinalIgnoreCase))
			{
				RegisterNamespaceRoot(registry, directoryInfo.Name, normalized, overlay.SourceId,
					overlay.Kind == OverlayRootKind.Vanilla);
				return;
			}

			var assetsDirectory = Path.Combine(normalized, "assets");
			if (Directory.Exists(assetsDirectory))
			{
				foreach (var namespaceDirectory in Directory.EnumerateDirectories(assetsDirectory, "*",
					SearchOption.TopDirectoryOnly))
				{
					var namespaceName = Path.GetFileName(namespaceDirectory);
					RegisterNamespaceRoot(registry, namespaceName, Path.GetFullPath(namespaceDirectory), overlay.SourceId,
						overlay.Kind == OverlayRootKind.Vanilla);
				}
				return;
			}

			RegisterNamespaceRoot(registry, "minecraft", normalized, overlay.SourceId, overlay.Kind == OverlayRootKind.Vanilla);
		}

		private static void RegisterNamespaceRoot(AssetNamespaceRegistry registry, string namespaceName, string path,
			string sourceId, bool isVanilla)
		{
			registry.AddNamespace(namespaceName, path, sourceId, isVanilla);
			var texturesPath = Path.Combine(path, "textures");
			if (Directory.Exists(texturesPath))
			{
				registry.AddNamespace(namespaceName, texturesPath, sourceId, isVanilla);
			}
		}

		public static RenderPackContext Create(string? assetsDirectory, IReadOnlyList<OverlayRoot> baseOverlayRoots,
			TexturePackStack? packStack)
		{
			var overlays = new List<OverlayRoot>(baseOverlayRoots);
			if (packStack is not null)
			{
				overlays.AddRange(packStack.OverlayRoots.Select(static overlay =>
					new OverlayRoot(overlay.Path, overlay.PackId, OverlayRootKind.ResourcePack)));
			}

			var assetsRoot = assetsDirectory is null ? string.Empty : Path.GetFullPath(assetsDirectory);
			var packIds = packStack?.Packs.Select(static pack => pack.Id).ToArray() ?? Array.Empty<string>();
			var packStackHash = packStack?.Fingerprint ?? VanillaPackId;
			var packs = packStack?.Packs ?? Array.Empty<RegisteredResourcePack>();

			return new RenderPackContext(assetsRoot, overlays, packIds, packStackHash, packs);
		}
	}

	private readonly record struct OverlayRoot(string Path, string SourceId, OverlayRootKind Kind);

	private enum OverlayRootKind
	{
		CustomData,
		ResourcePack,
		Vanilla
	}
}
