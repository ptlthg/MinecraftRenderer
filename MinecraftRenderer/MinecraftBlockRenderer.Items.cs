namespace MinecraftRenderer;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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

	private bool TryRenderGeneratedGeometry(string itemName, BlockModelInstance? model, ItemRegistry.ItemInfo? itemInfo, BlockRenderOptions options, out Image<Rgba32> generated)
	{
		generated = null!;

		if (IsShulkerBox(itemName))
		{
			var textureReference = itemInfo?.Texture;
			if (model?.Textures.TryGetValue("particle", out var particleTexture) == true)
			{
				textureReference = particleTexture;
			}

			if (!string.IsNullOrWhiteSpace(textureReference))
			{
				var resolvedTexture = ResolveTexture(textureReference!, model);
				if (!string.IsNullOrWhiteSpace(resolvedTexture) && !resolvedTexture.Equals("minecraft:missingno", StringComparison.OrdinalIgnoreCase))
				{
					var generatedModel = CreateGeneratedCubeModel(itemName, resolvedTexture);
					generated = RenderModel(generatedModel, options);
					return true;
				}
			}
		}

		return false;
	}

	private bool TryRenderBuiltinEntityItem(string itemName, ItemRegistry.ItemInfo? itemInfo, BlockRenderOptions options, out Image<Rgba32> rendered)
	{
		if (TryRenderEmbeddedTexture(itemName, options, out rendered))
		{
			return true;
		}

		if (itemInfo is not null && !string.IsNullOrWhiteSpace(itemInfo.Texture))
		{
			var resolved = ResolveTexture(itemInfo.Texture, null);
			if (!string.IsNullOrWhiteSpace(resolved) && _textureRepository.TryGetTexture(resolved, out _))
			{
				rendered = RenderFlatItem(new[] { resolved }, options);
				return true;
			}
		}

		rendered = null!;
		return false;
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

	private static bool IsShulkerBox(string itemName)
		=> itemName.EndsWith("_shulker_box", StringComparison.OrdinalIgnoreCase)
		|| string.Equals(itemName, "shulker_box", StringComparison.OrdinalIgnoreCase);

	private static BlockModelInstance CreateGeneratedCubeModel(string name, string textureId)
	{
		var textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["all"] = textureId
		};

		var faces = new Dictionary<BlockFaceDirection, ModelFace>
		{
			[BlockFaceDirection.North] = new ModelFace("#all", new Vector4(0, 0, 16, 16), 0, null, null),
			[BlockFaceDirection.South] = new ModelFace("#all", new Vector4(0, 0, 16, 16), 0, null, null),
			[BlockFaceDirection.East] = new ModelFace("#all", new Vector4(0, 0, 16, 16), 0, null, null),
			[BlockFaceDirection.West] = new ModelFace("#all", new Vector4(0, 0, 16, 16), 0, null, null),
			[BlockFaceDirection.Up] = new ModelFace("#all", new Vector4(0, 0, 16, 16), 0, null, null),
			[BlockFaceDirection.Down] = new ModelFace("#all", new Vector4(0, 0, 16, 16), 0, null, null)
		};

		var element = new ModelElement(
			new Vector3(0, 0, 0),
			new Vector3(16, 16, 16),
			null,
			faces,
			true);

		return new BlockModelInstance(
			$"{name}_generated",
			Array.Empty<string>(),
			textures,
			new Dictionary<string, TransformDefinition>(StringComparer.OrdinalIgnoreCase),
			new List<ModelElement> { element });
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

	private static bool UsesBuiltinGenerated(BlockModelInstance? model)
		=> UsesModelReference(model, "generated");

	private static bool UsesBuiltinEntity(BlockModelInstance? model)
		=> UsesModelReference(model, "entity");

	private static bool UsesModelReference(BlockModelInstance? model, string reference)
	{
		if (model is null)
		{
			return false;
		}

		if (MatchesModelReference(model.Name, reference))
		{
			return true;
		}

		foreach (var parent in model.ParentChain)
		{
			if (MatchesModelReference(parent, reference))
			{
				return true;
			}
		}

		return false;
	}

	private static bool MatchesModelReference(string candidate, string reference)
	{
		if (string.IsNullOrWhiteSpace(candidate))
		{
			return false;
		}

		var normalized = NormalizeModelReference(candidate);

		if (normalized.Equals(reference, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (normalized.Equals($"item/{reference}", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (normalized.Equals($"builtin/{reference}", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (normalized.StartsWith($"builtin/{reference}/", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		return false;
	}

	private static string NormalizeModelReference(string value)
	{
		var normalized = value.Trim().Replace('\\', '/');

		if (normalized.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[10..];
		}

		return normalized;
	}

	private static bool IsBuiltinEntityItemName(string itemName)
		=> !string.IsNullOrWhiteSpace(itemName)
		&& (itemName.EndsWith("_bed", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(itemName, "bed", StringComparison.OrdinalIgnoreCase));
}
