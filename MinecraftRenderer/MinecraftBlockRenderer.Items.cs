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

		if (TryRenderBedItem(itemName, model, options, out var bedComposite))
		{
			return bedComposite;
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

		private bool TryRenderBedItem(string itemName, BlockModelInstance? itemModel, BlockRenderOptions options, out Image<Rgba32> rendered)
		{
			rendered = null!;

			var normalizedName = NormalizeItemTextureKey(itemName);
			if (!normalizedName.EndsWith("_bed", StringComparison.OrdinalIgnoreCase) && !string.Equals(normalizedName, "bed", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			var colorName = string.Equals(normalizedName, "bed", StringComparison.OrdinalIgnoreCase)
				? "red"
				: normalizedName[..^4];

			var bedTextureId = $"minecraft:entity/bed/{colorName}";
			if (!_textureRepository.TryGetTexture(bedTextureId, out _))
			{
				bedTextureId = "minecraft:entity/bed/red";
				if (!_textureRepository.TryGetTexture(bedTextureId, out _))
				{
					return false;
				}
			}

			var headModel = ResolveModelOrNull("bed/bed_head");
			var footModel = ResolveModelOrNull("bed/bed_foot");
			if (headModel is null || footModel is null)
			{
				return false;
			}

			var elements = new List<ModelElement>();
			elements.AddRange(CloneAndTranslateElements(headModel, new Vector3(0f, 0f, -16f), flipBottomFaces: false, flipNorthSouthFaces: false));
			elements.AddRange(CloneAndTranslateElements(footModel, Vector3.Zero, flipBottomFaces: true, flipNorthSouthFaces: true));

			if (elements.Count == 0)
			{
				return false;
			}

			var textures = CloneTextureDictionary(itemModel);
			textures["bed"] = bedTextureId;
			if (!textures.ContainsKey("particle"))
			{
				textures["particle"] = DetermineBedParticleTexture(colorName, bedTextureId);
			}

			var displaySource = itemModel;
			if (displaySource is null || displaySource.Display.Count == 0)
			{
				displaySource = ResolveModelOrNull("item/template_bed") ?? displaySource;
			}

			var display = CloneDisplayDictionary(displaySource);
			AdjustBedGuiTransform(display);
			var parentChain = itemModel is not null
				? new List<string>(itemModel.ParentChain)
				: new List<string>();

			var composite = new BlockModelInstance(
				"minecraft:generated/bed_composite",
				parentChain,
				textures,
				display,
				elements);

			rendered = RenderModel(composite, options);
			return true;
		}

		private BlockModelInstance? ResolveModelOrNull(string name)
		{
			try
			{
				return _modelResolver.Resolve(name);
			}
			catch (KeyNotFoundException)
			{
				return null;
			}
			catch (InvalidOperationException)
			{
				return null;
			}
		}

		private static List<ModelElement> CloneAndTranslateElements(BlockModelInstance source, Vector3 translation, bool flipBottomFaces, bool flipNorthSouthFaces)
		{
			var result = new List<ModelElement>(source.Elements.Count);
			for (var i = 0; i < source.Elements.Count; i++)
			{
				result.Add(CloneAndTranslateElement(source.Elements[i], translation, flipBottomFaces, flipNorthSouthFaces));
			}

			return result;
		}

		private static ModelElement CloneAndTranslateElement(ModelElement element, Vector3 translation, bool flipBottomFaces, bool flipNorthSouthFaces)
		{
			var from = element.From + translation;
			var to = element.To + translation;

			ElementRotation? rotation = null;
			if (element.Rotation is not null)
			{
				rotation = new ElementRotation(
					element.Rotation.AngleInDegrees,
					element.Rotation.Origin + translation,
					element.Rotation.Axis,
					element.Rotation.Rescale);
			}

			var faces = new Dictionary<BlockFaceDirection, ModelFace>(element.Faces.Count);
			var elementHeight = element.To.Y - element.From.Y;
			var shouldFlipLargeFaces = elementHeight > 3.01f;
			foreach (var (direction, face) in element.Faces)
			{
				if (flipBottomFaces && direction == BlockFaceDirection.Down && shouldFlipLargeFaces)
				{
					var uv = face.Uv;
					if (uv.HasValue)
					{
						var raw = uv.Value;
						uv = new Vector4(raw.Z, raw.Y, raw.X, raw.W);
					}

					var rotated = face.Rotation.HasValue ? NormalizeRotation(face.Rotation.Value + 180) : (int?)null;
					faces[direction] = new ModelFace(face.Texture, uv, rotated, face.TintIndex, face.CullFace);
				}
				else if (flipNorthSouthFaces && shouldFlipLargeFaces && (direction == BlockFaceDirection.North || direction == BlockFaceDirection.South))
				{
					var uv = face.Uv;
					if (uv.HasValue)
					{
						var raw = uv.Value;
						uv = new Vector4(raw.X, raw.W, raw.Z, raw.Y);
					}

					var rotated = NormalizeRotation((face.Rotation ?? 0) + 180);
					faces[direction] = new ModelFace(face.Texture, uv, rotated, face.TintIndex, face.CullFace);
				}
				else
				{
					faces[direction] = new ModelFace(face.Texture, face.Uv, face.Rotation, face.TintIndex, face.CullFace);
				}
			}

			return new ModelElement(from, to, rotation, faces, element.Shade);
		}

		private static Dictionary<string, string> CloneTextureDictionary(BlockModelInstance? source)
		{
			var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (source is null || source.Textures.Count == 0)
			{
				return result;
			}

			foreach (var (key, value) in source.Textures)
			{
				result[key] = value;
			}

			return result;
		}

		private Dictionary<string, TransformDefinition> CloneDisplayDictionary(BlockModelInstance? source)
		{
			var result = new Dictionary<string, TransformDefinition>(StringComparer.OrdinalIgnoreCase);
			if (source is null || source.Display.Count == 0)
			{
				return result;
			}

			foreach (var (key, transform) in source.Display)
			{
				result[key] = new TransformDefinition
				{
					Rotation = transform.Rotation is null ? null : (float[])transform.Rotation.Clone(),
					Translation = transform.Translation is null ? null : (float[])transform.Translation.Clone(),
					Scale = transform.Scale is null ? null : (float[])transform.Scale.Clone()
				};
			}

			return result;
		}

		private static void AdjustBedGuiTransform(Dictionary<string, TransformDefinition> display)
		{
			const float RotationAdjustment = -45f;

			if (!display.TryGetValue("gui", out var gui))
			{
				var rotation = new[] { 30f, 160f + RotationAdjustment, 0f };
				display["gui"] = new TransformDefinition
				{
					Rotation = rotation,
					Translation = new[] { 2f, 3f, 0f },
					Scale = new[] { 0.5325f, 0.5325f, 0.5325f }
				};
				return;
			}

			var rotationArray = gui.Rotation is null ? new float[3] : CloneVector(gui.Rotation, 3);
			EnsureLength(ref rotationArray, 3);
			rotationArray[1] = NormalizeRotation(rotationArray[1] + RotationAdjustment);

			var translationArray = gui.Translation is null ? null : CloneVector(gui.Translation, gui.Translation.Length);
			var scaleArray = gui.Scale is null ? null : CloneVector(gui.Scale, gui.Scale.Length);

			display["gui"] = new TransformDefinition
			{
				Rotation = rotationArray,
				Translation = translationArray,
				Scale = scaleArray
			};
		}

		private static float[] CloneVector(float[] source, int length)
		{
			var result = new float[length];
			var copyLength = Math.Min(length, source.Length);
			Array.Copy(source, result, copyLength);
			return result;
		}

		private static void EnsureLength(ref float[] array, int length)
		{
			if (array.Length == length)
			{
				return;
			}

			Array.Resize(ref array, length);
		}

		private static int NormalizeRotation(int rotation)
		{
			var normalized = rotation % 360;
			if (normalized < 0)
			{
				normalized += 360;
			}

			return normalized;
		}

		private static float NormalizeRotation(float rotation)
		{
			var normalized = rotation % 360f;
			if (normalized < 0f)
			{
				normalized += 360f;
			}

			return normalized;
		}

		private string DetermineBedParticleTexture(string colorName, string fallbackTextureId)
		{
			var candidates = new List<string>
			{
				$"minecraft:block/{colorName}_wool",
				fallbackTextureId
			};

			foreach (var candidate in candidates)
			{
				if (_textureRepository.TryGetTexture(candidate, out _))
				{
					return candidate;
				}
			}

			return fallbackTextureId;
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
