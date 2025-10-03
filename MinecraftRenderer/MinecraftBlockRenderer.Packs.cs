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
using MinecraftRenderer.Nbt;
using MinecraftRenderer.TexturePacks;

public sealed partial class MinecraftBlockRenderer
{
	public sealed record ResourceIdResult(string ResourceId, string SourcePackId, string PackStackHash)
	{
		public string? Model { get; init; }
		public IReadOnlyList<string> Textures { get; init; } = Array.Empty<string>();
	}

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
		var lookupTarget = normalizedTarget;
		var namespaceSeparator = lookupTarget.IndexOf(':');
		if (namespaceSeparator >= 0)
		{
			lookupTarget = lookupTarget[(namespaceSeparator + 1)..];
		}
		string? modelPath = null;
		string? primaryModelIdentifier = null;
		var resolvedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string variantKey;

		ItemRegistry.ItemInfo? itemInfo = null;
		var hasItemRegistry = _itemRegistry is not null;
		var hasItemInfo = hasItemRegistry && _itemRegistry!.TryGetInfo(lookupTarget, out itemInfo);
		var shouldTreatAsItem = hasItemRegistry && (hasItemInfo || options.ItemData is not null);

		void ProcessItem(ItemRegistry.ItemInfo? info)
		{
			if (_itemRegistry is null)
			{
				variantKey = $"literal:{normalizedTarget}";
				return;
			}

			string? referenceModel = null;
			if (_itemRegistry.TryGetModel(lookupTarget, out var itemModel) && !string.IsNullOrWhiteSpace(itemModel))
			{
				referenceModel = NormalizeModelIdentifier(itemModel);
			}

			BlockModelInstance? effectiveModel = null;
			string? effectiveModelIdentifier = null;
			IReadOnlyList<string>? modelCandidates = null;
			string? resolvedModelName = null;

			if (info?.Selector is not null)
			{
				var displayContext = DetermineDisplayContext(options);
				var selectorContext = new ItemModelContext(options.ItemData, displayContext);
				var dynamicModel = info.Selector.Resolve(selectorContext);
				if (!string.IsNullOrWhiteSpace(dynamicModel))
				{
					effectiveModel = ResolveModelOrNull(dynamicModel);
					if (effectiveModel is not null)
					{
						effectiveModelIdentifier = dynamicModel;
					}
				}
			}

			if (effectiveModel is null)
			{
				(effectiveModel, modelCandidates, resolvedModelName) = ResolveItemModel(lookupTarget, info, options);
				effectiveModelIdentifier = resolvedModelName ?? effectiveModel?.Name;
			}

			if (effectiveModel is null && !string.IsNullOrWhiteSpace(resolvedModelName))
			{
				effectiveModel = ResolveModelOrNull(resolvedModelName);
				if (effectiveModel is not null && string.IsNullOrWhiteSpace(effectiveModelIdentifier))
				{
					effectiveModelIdentifier = resolvedModelName;
				}
			}

			if (effectiveModel is null && modelCandidates is not null)
			{
				foreach (var candidate in modelCandidates)
				{
					var candidateModel = ResolveModelOrNull(candidate);
					if (candidateModel is null)
					{
						continue;
					}

					effectiveModel = candidateModel;
					effectiveModelIdentifier = string.IsNullOrWhiteSpace(candidate) ? candidateModel.Name : candidate;
					break;
				}
			}

			if (effectiveModel is not null)
			{
				var identifier = NormalizeModelIdentifier(effectiveModelIdentifier ?? effectiveModel.Name);
				primaryModelIdentifier = identifier;
				modelPath = identifier;
				referenceModel = identifier;
				foreach (var texture in CollectResolvedTextures(effectiveModel))
				{
					resolvedTextures.Add(texture);
				}
			}
			else if (info?.Texture is not null)
			{
				resolvedTextures.Add(info.Texture);
			}

			if (options.ItemData?.CustomData is not null &&
			    TryGetHeadTextureOverride(options.ItemData.CustomData, out var headTexture))
			{
				resolvedTextures.Add(headTexture);
			}

			if (resolvedTextures.Count == 0 && referenceModel is not null)
			{
				resolvedTextures.Add(referenceModel);
			}

			var itemDataKey = options.ItemData is not null ? BuildItemRenderDataKey(options.ItemData) : string.Empty;
			modelPath ??= referenceModel ?? normalizedTarget;
			variantKey = $"item:{normalizedTarget}:{modelPath}:{JoinTextures(resolvedTextures)}:{itemDataKey}";
		}

		if (shouldTreatAsItem)
		{
			ProcessItem(itemInfo);
		}
		else if (_blockRegistry.TryGetModel(lookupTarget, out var blockModelPath) &&
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
		else if (hasItemRegistry)
		{
			ProcessItem(itemInfo);
		}
		else
		{
			variantKey = $"literal:{normalizedTarget}";
		}

		var sourcePackId = DetermineSourcePackId(modelPath, resolvedTextures);
		if (string.Equals(sourcePackId, VanillaPackId, StringComparison.OrdinalIgnoreCase) &&
		    !string.IsNullOrWhiteSpace(primaryModelIdentifier) &&
		    TryResolvePackFromAsset(primaryModelIdentifier, "models", ".json", out var modelPackId))
		{
			sourcePackId = modelPackId;
		}

		var descriptor = $"{RendererVersion}|{_packContext.PackStackHash}|{variantKey}";
		var resourceId = ComputeResourceIdHash(descriptor);
		var texturesList = resolvedTextures.Count > 0
			? resolvedTextures.OrderBy(static t => t, StringComparer.OrdinalIgnoreCase).ToArray()
			: Array.Empty<string>();

		return new ResourceIdResult(resourceId, sourcePackId, _packContext.PackStackHash)
		{
			Model = modelPath,
			Textures = texturesList
		};
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

		if (data.CustomData is not null)
		{
			builder.Append(";custom=");
			builder.Append(BuildCustomDataKey(data.CustomData));
		}
		else
		{
			builder.Append(";custom=none");
		}

		if (data.Profile is not null)
		{
			builder.Append(";profile=");
			builder.Append(BuildProfileKey(data.Profile));
		}
		else
		{
			builder.Append(";profile=none");
		}

		return builder.ToString();
	}

		private static string BuildProfileKey(NbtCompound profile)
		{
			if (profile.TryGetValue("properties", out var propertiesTag) && propertiesTag is NbtList properties)
			{
				foreach (var entry in properties)
				{
					if (entry is not NbtCompound propertyCompound)
					{
						continue;
					}

					if (propertyCompound.TryGetValue("name", out var nameTag) &&
					    nameTag is NbtString nameValue &&
					    nameValue.Value.Equals("textures", StringComparison.OrdinalIgnoreCase) &&
					    propertyCompound.TryGetValue("value", out var valueTag) &&
					    valueTag is NbtString valueString &&
					    !string.IsNullOrWhiteSpace(valueString.Value))
					{
						var hash = SHA256.HashData(Encoding.UTF8.GetBytes(valueString.Value));
						return Convert.ToHexString(hash);
					}
				}
			}

		if (profile.TryGetValue("id", out var idTag))
		{
			var formatted = FormatNbtValue(idTag);
			if (!string.IsNullOrWhiteSpace(formatted))
			{
				return formatted;
			}
		}

		return "none";
		}

		private static string BuildCustomDataKey(NbtCompound compound)
		{
			var segments = new List<string>();
			foreach (var pair in compound.OrderBy(static kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
			{
				var key = pair.Key ?? string.Empty;
				var value = FormatNbtValue(pair.Value);
				segments.Add($"{key}={value}");
			}

			return segments.Count == 0 ? "empty" : string.Join('|', segments);
		}

		private static string FormatNbtValue(NbtTag tag)
			=> tag switch
			{
				NbtString s => s.Value,
				NbtInt i => i.Value.ToString(CultureInfo.InvariantCulture),
				NbtLong l => l.Value.ToString(CultureInfo.InvariantCulture),
				NbtShort s16 => s16.Value.ToString(CultureInfo.InvariantCulture),
				NbtByte b => b.Value.ToString(CultureInfo.InvariantCulture),
				NbtDouble d => d.Value.ToString(CultureInfo.InvariantCulture),
				NbtFloat f => f.Value.ToString(CultureInfo.InvariantCulture),
				NbtCompound compound => '{' + BuildCustomDataKey(compound) + '}',
				NbtList list => '[' + string.Join(',', list.Select(FormatNbtValue)) + ']',
				NbtIntArray intArray => '[' + string.Join(',', intArray.Values.Select(v => v.ToString(CultureInfo.InvariantCulture))) + ']',
				NbtLongArray longArray => '[' + string.Join(',', longArray.Values.Select(v => v.ToString(CultureInfo.InvariantCulture))) + ']',
				NbtByteArray byteArray => '[' + string.Join(',', byteArray.Values.Select(v => v.ToString(CultureInfo.InvariantCulture))) + ']',
				_ => string.Empty
			};

	private string DetermineSourcePackId(string? modelPath, IReadOnlyCollection<string> textureIds)
	{
		if (TryResolvePackFromAsset(modelPath, "models", ".json", out var packId))
		{
			return packId;
		}

		foreach (var textureId in textureIds)
		{
			if (TryResolvePackFromAsset(textureId, "textures", ".png", out packId))
			{
				return packId;
			}
		}

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

		private bool TryResolvePackFromAsset(string? assetId, string category, string extension, out string packId)
		{
			packId = VanillaPackId;
			if (string.IsNullOrWhiteSpace(assetId) || _packContext.AssetNamespaces is null)
			{
				return false;
			}

			var (namespaceName, relativePath) = NormalizeAssetPath(assetId);
			if (string.IsNullOrWhiteSpace(relativePath))
			{
				return false;
			}

			if (relativePath.StartsWith(category + "/", StringComparison.OrdinalIgnoreCase))
			{
				relativePath = relativePath[(category.Length + 1)..];
			}

			if (relativePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
			{
				relativePath = relativePath[..^extension.Length];
			}

			relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);

			var roots = _packContext.AssetNamespaces.ResolveRoots(namespaceName);
			for (var i = roots.Count - 1; i >= 0; i--)
			{
				var root = roots[i];
				var basePath = root.Path;
				string candidate;

				if (basePath.EndsWith(category, StringComparison.OrdinalIgnoreCase))
				{
					candidate = Path.Combine(basePath, relativePath + extension);
				}
				else
				{
					candidate = Path.Combine(basePath, category, relativePath + extension);
				}

				if (File.Exists(candidate))
				{
					packId = root.SourceId;
					return !root.IsVanilla;
				}
			}

			return false;
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

		private static string NormalizeModelIdentifier(string identifier)
		{
			if (string.IsNullOrWhiteSpace(identifier))
			{
				return identifier;
			}

			var trimmed = identifier.Trim();
			if (trimmed.IndexOf(':') >= 0)
			{
				return trimmed;
			}

			trimmed = trimmed.TrimStart('/');
			return string.IsNullOrWhiteSpace(trimmed) ? "minecraft:" : $"minecraft:{trimmed}";
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

		private static (string NamespaceName, string RelativePath) NormalizeAssetPath(string assetId)
		{
			var normalized = assetId.Replace('\\', '/').Trim();
			if (string.IsNullOrWhiteSpace(normalized))
			{
				return ("minecraft", string.Empty);
			}

			var namespaceName = "minecraft";
			var colonIndex = normalized.IndexOf(':');
			if (colonIndex >= 0)
			{
				namespaceName = normalized[..colonIndex];
				normalized = normalized[(colonIndex + 1)..];
			}

			normalized = normalized.TrimStart('/');
			return (namespaceName, normalized);
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
