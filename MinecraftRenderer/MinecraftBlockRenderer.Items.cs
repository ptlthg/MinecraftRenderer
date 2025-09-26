namespace MinecraftRenderer;

using System;
using System.Collections.Generic;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

public sealed partial class MinecraftBlockRenderer
{
	private static readonly (string Suffix, string Replacement)[] InventoryModelSuffixes =
	{
		("_fence", "_fence_inventory"),
		("_wall", "_wall_inventory"),
		("_button", "_button_inventory")
	};

	public Image<Rgba32> RenderGuiItem(string itemName, BlockRenderOptions? options = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(itemName);
		options ??= BlockRenderOptions.Default;
		EnsureNotDisposed();

		ItemRegistry.ItemInfo? itemInfo = null;
		if (_itemRegistry is not null)
		{
			_itemRegistry.TryGetInfo(itemName, out itemInfo);
		}

		var (model, modelCandidates) = ResolveItemModel(itemName, itemInfo);

		if (TryRenderGuiTextureLayers(itemName, itemInfo, model, options, out var flatRender))
		{
			return flatRender;
		}

		if (model is not null && IsBillboardModel(model))
		{
			var billboardTextures = CollectBillboardTextures(model, itemInfo);
			if (TryRenderFlatItemFromIdentifiers(billboardTextures, model, options, out flatRender))
			{
				return flatRender;
			}
		}

		if (model is not null && model.Elements.Count > 0)
		{
			return RenderModel(model, options);
		}

		if (TryRenderBlockEntityFallback(itemName, itemInfo, model, modelCandidates, options, out var blockRender))
		{
			return blockRender;
		}

		return RenderFallbackTexture(itemName, itemInfo, model, options);
	}

	private (BlockModelInstance? Model, IReadOnlyList<string> Candidates) ResolveItemModel(string itemName, ItemRegistry.ItemInfo? itemInfo)
	{
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

		var candidates = BuildModelCandidates(modelName!, itemName).ToList();
		BlockModelInstance? model = null;

		foreach (var candidate in candidates)
		{
			try
			{
				model = _modelResolver.Resolve(candidate);
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

		return (model, candidates);
	}

	private bool TryRenderGuiTextureLayers(string itemName, ItemRegistry.ItemInfo? itemInfo, BlockModelInstance? model, BlockRenderOptions options, out Image<Rgba32> rendered)
	{
		var candidates = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		void TryAdd(string? candidate)
		{
			if (!string.IsNullOrWhiteSpace(candidate) && IsGuiTexture(candidate) && seen.Add(candidate))
			{
				candidates.Add(candidate);
			}
		}

		if (model is not null)
		{
			var orderedLayers = model.Textures
				.Where(static kvp => kvp.Key.StartsWith("layer", StringComparison.OrdinalIgnoreCase))
				.OrderBy(static kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);

			foreach (var layer in orderedLayers)
			{
				TryAdd(layer.Value);
			}
		}

		if (itemInfo is not null && !string.IsNullOrWhiteSpace(itemInfo.Texture))
		{
			TryAdd(itemInfo.Texture);
		}

		var normalized = NormalizeItemTextureKey(itemName);
		TryAdd($"minecraft:item/{normalized}");
		TryAdd($"minecraft:item/{normalized}_overlay");
		TryAdd($"item/{normalized}");
		TryAdd($"textures/item/{normalized}");

		if (candidates.Count == 0)
		{
			rendered = null!;
			return false;
		}

		return TryRenderFlatItemFromIdentifiers(candidates, model, options, out rendered);
	}

	private bool TryRenderFlatItemFromIdentifiers(IEnumerable<string> identifiers, BlockModelInstance? model, BlockRenderOptions options, out Image<Rgba32> rendered)
	{
		var resolved = ResolveTextureIdentifiers(identifiers, model);
		var available = new List<string>();

		foreach (var textureId in resolved)
		{
			if (_textureRepository.TryGetTexture(textureId, out _))
			{
				available.Add(textureId);
			}
		}

		if (available.Count == 0)
		{
			rendered = null!;
			return false;
		}

		rendered = RenderFlatItem(available, options);
		return true;
	}

	private bool TryRenderBlockEntityFallback(string itemName, ItemRegistry.ItemInfo? itemInfo, BlockModelInstance? model, IReadOnlyList<string> modelCandidates, BlockRenderOptions options, out Image<Rgba32> rendered)
	{
		foreach (var candidate in EnumerateBlockFallbackNames(itemName, itemInfo, model, modelCandidates))
		{
			if (TryRenderBlockItem(candidate, options, out rendered))
			{
				return true;
			}
		}

		rendered = null!;
		return false;
	}

	private static IReadOnlyList<string> EnumerateBlockFallbackNames(string itemName, ItemRegistry.ItemInfo? itemInfo, BlockModelInstance? model, IReadOnlyList<string> modelCandidates)
	{
		var results = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		void AddRange(IEnumerable<string> values)
		{
			foreach (var value in values)
			{
				if (seen.Add(value))
				{
					results.Add(value);
				}
			}
		}

		AddRange(NormalizeToBlockCandidates(itemName));
		AddRange(NormalizeToBlockCandidates(itemInfo?.Model));
		AddRange(NormalizeToBlockCandidates(itemInfo?.Texture));
		AddRange(modelCandidates.SelectMany(NormalizeToBlockCandidates));

		if (model is not null)
		{
			AddRange(NormalizeToBlockCandidates(model.Name));

			foreach (var parent in model.ParentChain)
			{
				AddRange(NormalizeToBlockCandidates(parent));
			}

			foreach (var texture in model.Textures.Values)
			{
				AddRange(NormalizeToBlockCandidates(texture));
			}
		}

		return results;
	}

	private static IEnumerable<string> NormalizeToBlockCandidates(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			yield break;
		}

		var normalized = value.Trim().Replace('\\', '/');

		if (normalized.StartsWith("#", StringComparison.Ordinal))
		{
			yield break;
		}

		if (normalized.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[10..];
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

		if (normalized.StartsWith("item/", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("items/", StringComparison.OrdinalIgnoreCase))
		{
			yield break;
		}

		if (normalized.StartsWith("builtin/", StringComparison.OrdinalIgnoreCase))
		{
			yield break;
		}

		normalized = normalized.Trim('/');
		if (string.IsNullOrWhiteSpace(normalized))
		{
			yield break;
		}

		yield return normalized;
	}

	private static bool IsGuiTexture(string textureId)
	{
		if (string.IsNullOrWhiteSpace(textureId))
		{
			return false;
		}

		var normalized = textureId.Replace('\\', '/');
		return normalized.Contains("/item/", StringComparison.OrdinalIgnoreCase)
			|| normalized.Contains(":item/", StringComparison.OrdinalIgnoreCase)
			|| normalized.Contains("/items/", StringComparison.OrdinalIgnoreCase)
			|| normalized.Contains(":items/", StringComparison.OrdinalIgnoreCase)
			|| normalized.Contains("textures/item/", StringComparison.OrdinalIgnoreCase);
	}

	private static string NormalizeItemTextureKey(string itemName)
	{
		var normalized = itemName.Trim();
		if (normalized.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[10..];
		}

		return normalized.Replace('\\', '/').Trim('/');
	}

	private Image<Rgba32> RenderFallbackTexture(string itemName, ItemRegistry.ItemInfo? itemInfo, BlockModelInstance? model, BlockRenderOptions options)
	{
		if (TryRenderFlatItemFromIdentifiers(CollectItemLayerTextures(model, itemInfo), model, options, out var rendered))
		{
			return rendered;
		}

		if (itemInfo is not null && !string.IsNullOrWhiteSpace(itemInfo.Texture) && TryRenderEmbeddedTexture(itemInfo.Texture, options, out rendered))
		{
			return rendered;
		}

		foreach (var candidate in EnumerateTextureFallbackCandidates(itemName))
		{
			if (TryRenderEmbeddedTexture(candidate, options, out rendered))
			{
				return rendered;
			}
		}

		return RenderFlatItem(new[] { "minecraft:missingno" }, options);
	}

	private static IEnumerable<string> EnumerateTextureFallbackCandidates(string itemName)
	{
		var normalized = NormalizeItemTextureKey(itemName);

		yield return normalized;
		yield return $"minecraft:item/{normalized}";
		yield return $"item/{normalized}";
		yield return $"textures/item/{normalized}";
		yield return $"minecraft:block/{normalized}";
		yield return $"block/{normalized}";
	}

	private static IEnumerable<string> BuildModelCandidates(string primaryName, string itemName)
	{
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var candidate in EnumerateCandidateNames(primaryName))
		{
			if (seen.Add(candidate))
			{
				yield return candidate;
			}
		}

		if (!string.Equals(primaryName, itemName, StringComparison.OrdinalIgnoreCase))
		{
			foreach (var candidate in EnumerateCandidateNames(itemName))
			{
				if (seen.Add(candidate))
				{
					yield return candidate;
				}
			}
		}
	}

	private static IEnumerable<string> EnumerateCandidateNames(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			yield break;
		}

		yield return name;

		foreach (var variant in GenerateInventoryVariants(name))
		{
			yield return variant;
		}
	}

	private static IEnumerable<string> GenerateInventoryVariants(string name)
	{
		var (prefix, baseName) = SplitModelName(name);
		if (string.IsNullOrWhiteSpace(baseName))
		{
			yield break;
		}

		if (!baseName.EndsWith("_inventory", StringComparison.OrdinalIgnoreCase))
		{
			yield return prefix + baseName + "_inventory";

			foreach (var (suffix, replacement) in InventoryModelSuffixes)
			{
				if (baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				{
					var replaced = baseName[..^suffix.Length] + replacement;
					yield return prefix + replaced;
				}
			}
		}
	}

	private static (string Prefix, string BaseName) SplitModelName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return (string.Empty, string.Empty);
		}

		var lastSlash = name.LastIndexOf('/');
		if (lastSlash >= 0)
		{
			return (name[..(lastSlash + 1)], name[(lastSlash + 1)..]);
		}

		return (string.Empty, name);
	}

	private static bool IsBillboardModel(BlockModelInstance model)
	{
		if (model.Elements.Count == 0)
		{
			return false;
		}

		if (model.Textures.ContainsKey("cross"))
		{
			return true;
		}

		for (var i = 0; i < model.ParentChain.Count; i++)
		{
			if (ParentIndicatesBillboard(model.ParentChain[i]))
			{
				return true;
			}
		}

		return ParentIndicatesBillboard(model.Name);

		static bool ParentIndicatesBillboard(string value)
			=> value.Contains("cross", StringComparison.OrdinalIgnoreCase)
			|| value.Contains("tinted_cross", StringComparison.OrdinalIgnoreCase)
			|| value.Contains("seagrass", StringComparison.OrdinalIgnoreCase);
	}

	private static List<string> CollectBillboardTextures(BlockModelInstance model, ItemRegistry.ItemInfo? itemInfo)
	{
		var textures = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		void TryAdd(string? candidate)
		{
			if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
			{
				textures.Add(candidate);
			}
		}

		if (model.Textures.TryGetValue("cross", out var crossTexture))
		{
			TryAdd(crossTexture);
		}

		if (model.Textures.TryGetValue("texture", out var genericTexture))
		{
			TryAdd(genericTexture);
		}

		if (textures.Count == 0 && itemInfo is not null)
		{
			TryAdd(itemInfo.Texture);
		}

		return textures;
	}

	private static List<string> CollectItemLayerTextures(BlockModelInstance? model, ItemRegistry.ItemInfo? itemInfo)
	{
		var layers = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		if (model is not null)
		{
			var orderedLayers = model.Textures
				.Where(kvp => kvp.Key.StartsWith("layer", StringComparison.OrdinalIgnoreCase))
				.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);

			foreach (var layer in orderedLayers)
			{
				if (!string.IsNullOrWhiteSpace(layer.Value) && seen.Add(layer.Value))
				{
					layers.Add(layer.Value);
				}
			}
		}

		if (itemInfo is not null && !string.IsNullOrWhiteSpace(itemInfo.Texture) && seen.Add(itemInfo.Texture))
		{
			layers.Add(itemInfo.Texture);
		}

		return layers;
	}

	private static List<string> ResolveTextureIdentifiers(IEnumerable<string> identifiers, BlockModelInstance? model)
	{
		var resolved = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var identifier in identifiers)
		{
			if (string.IsNullOrWhiteSpace(identifier))
			{
				continue;
			}

			var textureId = ResolveTexture(identifier, model);
			if (string.IsNullOrWhiteSpace(textureId) || textureId.Equals("minecraft:missingno", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (seen.Add(textureId))
			{
				resolved.Add(textureId);
			}
		}

		return resolved;
	}

	private bool TryRenderEmbeddedTexture(string textureId, BlockRenderOptions options, out Image<Rgba32> rendered)
	{
		if (_textureRepository.TryGetTexture(textureId, out _))
		{
			rendered = RenderFlatItem(new[] { textureId }, options);
			return true;
		}

		rendered = null!;
		return false;
	}

	private bool TryRenderBlockItem(string blockName, BlockRenderOptions options, out Image<Rgba32> rendered)
	{
		try
		{
			rendered = RenderBlock(blockName, options);
			return true;
		}
		catch (Exception)
		{
			rendered = null!;
			return false;
		}
	}

	private Image<Rgba32> RenderFlatItem(IReadOnlyList<string> layerTextureIds, BlockRenderOptions options)
	{
		var canvas = new Image<Rgba32>(options.Size, options.Size, Color.Transparent);

		foreach (var textureId in layerTextureIds)
		{
			var texture = _textureRepository.GetTexture(textureId);
			var scale = MathF.Min(options.Size / (float)texture.Width, options.Size / (float)texture.Height);
			var targetWidth = Math.Max(1, (int)MathF.Round(texture.Width * scale));
			var targetHeight = Math.Max(1, (int)MathF.Round(texture.Height * scale));

			using var resized = texture.Clone(ctx => ctx.Resize(new ResizeOptions
			{
				Size = new Size(targetWidth, targetHeight),
				Sampler = KnownResamplers.NearestNeighbor,
				Mode = ResizeMode.Stretch
			}));

			var offset = new Point((canvas.Width - targetWidth) / 2, (canvas.Height - targetHeight) / 2);
			canvas.Mutate(ctx => ctx.DrawImage(resized, offset, 1f));
		}

		return canvas;
	}

}
