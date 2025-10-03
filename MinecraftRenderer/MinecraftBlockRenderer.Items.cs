namespace MinecraftRenderer;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using MinecraftRenderer.Nbt;

public sealed partial class MinecraftBlockRenderer
{
	public Image<Rgba32> RenderGuiItem(string itemName, BlockRenderOptions? options = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(itemName);
		var effectiveOptions = options ?? BlockRenderOptions.Default;
		var renderer = ResolveRendererForOptions(effectiveOptions, out var forwardedOptions);
		return renderer.RenderGuiItemInternal(itemName, forwardedOptions);
	}

	private static readonly (string Suffix, string Replacement)[] InventoryModelSuffixes =
	{
		("_fence", "_fence_inventory"),
		("_wall", "_wall_inventory"),
		("_button", "_button_inventory")
	};

	private static readonly string[] BottomAlignedItemSuffixes =
	{
		"_carpet",
		"_trapdoor",
		"_pressure_plate",
		"_weighted_pressure_plate"
	};

	private static readonly string[] BannerSuffixes =
	{
		"_banner"
	};

	private static readonly HttpClient PlayerSkinClient = CreatePlayerSkinClient();

	private static readonly HashSet<string> AnimatedDialItems = new(StringComparer.OrdinalIgnoreCase)
	{
		"compass",
		"recovery_compass",
		"clock"
	};

	private static readonly Color DefaultLeatherArmorColor = new(new Rgba32(0xA0, 0x65, 0x40));

	private static readonly Dictionary<string, Color> LegacyDefaultTintOverrides = new(StringComparer.OrdinalIgnoreCase)
	{
		["leather_helmet"] = DefaultLeatherArmorColor,
		["leather_chestplate"] = DefaultLeatherArmorColor,
		["leather_leggings"] = DefaultLeatherArmorColor,
		["leather_boots"] = DefaultLeatherArmorColor,
		["leather_horse_armor"] = DefaultLeatherArmorColor,
		["wolf_armor_dyed"] = DefaultLeatherArmorColor
	};

	private static readonly Dictionary<string, int[]> LegacyDefaultTintLayerOverrides =
		new(StringComparer.OrdinalIgnoreCase)
		{
			["wolf_armor_dyed"] = new[] { 1 }
		};

	private Image<Rgba32> RenderGuiItemInternal(string itemName, BlockRenderOptions options)
	{
		options = options with { Padding = 0f };
		EnsureNotDisposed();

		var normalizedItemKey = NormalizeItemTextureKey(itemName);
		var alignToBottom = ShouldAlignGuiItemToBottom(normalizedItemKey);
		float? postScale = null;

		Image<Rgba32> FinalizeGuiResult(Image<Rgba32> image)
		{
			if (postScale.HasValue)
			{
				ApplyCenteredScale(image, postScale.Value);
			}

			if (alignToBottom)
			{
				AlignImageToBottom(image);
			}

			return image;
		}

		ItemRegistry.ItemInfo? itemInfo = null;
		if (_itemRegistry is not null)
		{
			_itemRegistry.TryGetInfo(itemName, out itemInfo);
		}

		var (model, modelCandidates, _) = ResolveItemModel(itemName, itemInfo, options);
		if (options.OverrideGuiTransform is null && options.UseGuiTransform && model is not null)
		{
			var guiOverride = model.GetDisplayTransform("gui");
			if (guiOverride is not null)
			{
				options = options with { OverrideGuiTransform = guiOverride };
			}
		}

		if (options.UseGuiTransform && IsBannerItem(normalizedItemKey))
		{
			options = options with { OverrideGuiTransform = AdjustBannerGuiTransform(options.OverrideGuiTransform) };
		}

		postScale = GetPostRenderScale(normalizedItemKey);

		if (TryRenderGuiTextureLayers(itemName, itemInfo, model, options, out var flatRender))
		{
			if (ShouldPreferPlayerHeadRenderer(itemName, model, modelCandidates, options) &&
			    TryRenderPlayerHead(itemName, model, modelCandidates, options, out var preferredHead))
			{
				flatRender.Dispose();
				return FinalizeGuiResult(preferredHead);
			}

			return FinalizeGuiResult(flatRender);
		}

		if (TryRenderBedItem(itemName, model, options, out var bedComposite))
		{
			return FinalizeGuiResult(bedComposite);
		}

		if (!HasExplicitFlatHeadOverride(model, modelCandidates) &&
		    TryRenderPlayerHead(itemName, model, modelCandidates, options, out var headComposite))
		{
			return FinalizeGuiResult(headComposite);
		}

		if (model is not null && IsBillboardModel(model))
		{
			var billboardTextures = CollectBillboardTextures(model, itemInfo);
			if (TryRenderFlatItemFromIdentifiers(billboardTextures, model, options, itemName, out flatRender))
			{
				return FinalizeGuiResult(flatRender);
			}
		}

		if (model is not null && model.Elements.Count > 0)
		{
			return FinalizeGuiResult(RenderModel(model, options, itemName));
		}

		if (TryRenderBlockEntityFallback(itemName, itemInfo, model, modelCandidates, options, out var blockRender))
		{
			return FinalizeGuiResult(blockRender);
		}

		return FinalizeGuiResult(RenderFallbackTexture(itemName, itemInfo, model, options));
	}

	private (BlockModelInstance? Model, IReadOnlyList<string> Candidates, string? ResolvedModelName) ResolveItemModel(string itemName,
		ItemRegistry.ItemInfo? itemInfo, BlockRenderOptions options)
	{
		var displayContext = DetermineDisplayContext(options);
		string? dynamicModel = null;
		if (itemInfo?.Selector is not null)
		{
			var selectorContext = new ItemModelContext(options.ItemData, displayContext);
			dynamicModel = itemInfo.Selector.Resolve(selectorContext);
		}

		var primaryModel = itemInfo?.Model;
		string fallbackModel;
		if (!string.IsNullOrWhiteSpace(dynamicModel))
		{
			fallbackModel = dynamicModel!;
		}
		else if (!string.IsNullOrWhiteSpace(primaryModel))
		{
			fallbackModel = primaryModel!;
		}
		else if (_blockRegistry.TryGetModel(itemName, out var blockModel) && !string.IsNullOrWhiteSpace(blockModel))
		{
			fallbackModel = blockModel;
		}
		else
		{
			fallbackModel = itemName;
		}

		var candidates = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		void AppendCandidates(string? primary)
		{
			if (string.IsNullOrWhiteSpace(primary))
			{
				return;
			}

			foreach (var candidate in BuildModelCandidates(primary, itemName))
			{
				if (seen.Add(candidate))
				{
					candidates.Add(candidate);
				}
			}
		}

		AppendCandidates(dynamicModel);
		AppendCandidates(primaryModel);
		AppendCandidates(fallbackModel);
		AppendCandidates(itemName);

		if (candidates.Count == 0)
		{
			candidates.Add(itemName);
		}

		BlockModelInstance? model = null;
		string? resolvedModelName = null;
		foreach (var candidate in candidates)
		{
			try
			{
				model = _modelResolver.Resolve(candidate);
				resolvedModelName = candidate;
				var normalizedName = NormalizeModelIdentifier(resolvedModelName);
				if (!string.Equals(model.Name, normalizedName, StringComparison.OrdinalIgnoreCase))
				{
					model = new BlockModelInstance(
						normalizedName,
						model.ParentChain,
						model.Textures,
						model.Display,
						model.Elements);
				}
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

		return (model, candidates, resolvedModelName);
	}

	private static string DetermineDisplayContext(BlockRenderOptions options)
		=> options.UseGuiTransform ? "gui" : "none";

	private bool TryRenderGuiTextureLayers(string itemName, ItemRegistry.ItemInfo? itemInfo, BlockModelInstance? model,
		BlockRenderOptions options, out Image<Rgba32> rendered)
	{
		var candidates = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var isBillboardModel = model is not null && IsBillboardModel(model);
		var hasModelLayer = false;

		void TryAdd(string? candidate, bool allowNonGuiTexture = false, bool markModelLayer = false)
		{
			if (string.IsNullOrWhiteSpace(candidate))
			{
				return;
			}

			if (!allowNonGuiTexture && !IsGuiTexture(candidate))
			{
				return;
			}

			if (seen.Add(candidate))
			{
				candidates.Add(candidate);
				if (markModelLayer)
				{
					hasModelLayer = true;
				}
			}
		}

		if (model is not null)
		{
			var orderedLayers = model.Textures
				.Where(static kvp => kvp.Key.StartsWith("layer", StringComparison.OrdinalIgnoreCase))
				.OrderBy(static kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);

			foreach (var layer in orderedLayers)
			{
				TryAdd(layer.Value, allowNonGuiTexture: isBillboardModel, markModelLayer: true);
			}
		}

		if (!hasModelLayer && itemInfo is not null && !string.IsNullOrWhiteSpace(itemInfo.Texture))
		{
			TryAdd(itemInfo.Texture, allowNonGuiTexture: isBillboardModel);
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

		return TryRenderFlatItemFromIdentifiers(candidates, model, options, itemName, out rendered);
	}

	private bool TryRenderFlatItemFromIdentifiers(IEnumerable<string> identifiers, BlockModelInstance? model,
		BlockRenderOptions options, string? tintContext, out Image<Rgba32> rendered)
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

		rendered = RenderFlatItem(available, options, tintContext);
		return true;
	}

	private bool TryRenderBlockEntityFallback(string itemName, ItemRegistry.ItemInfo? itemInfo,
		BlockModelInstance? model, IReadOnlyList<string> modelCandidates, BlockRenderOptions options,
		out Image<Rgba32> rendered)
	{
		var blockOptions = options;
		if (options.OverrideGuiTransform is not null && model is not null && model.Elements.Count == 0)
		{
			var itemGuiTransform = model.GetDisplayTransform("gui");
			if (ReferenceEquals(itemGuiTransform, options.OverrideGuiTransform))
			{
				blockOptions = options with { OverrideGuiTransform = null };
			}
		}

		foreach (var candidate in EnumerateBlockFallbackNames(itemName, itemInfo, model, modelCandidates))
		{
			if (TryRenderBlockItem(candidate, blockOptions, out rendered))
			{
				return true;
			}
		}

		rendered = null!;
		return false;
	}

	private static IReadOnlyList<string> EnumerateBlockFallbackNames(string itemName, ItemRegistry.ItemInfo? itemInfo,
		BlockModelInstance? model, IReadOnlyList<string> modelCandidates)
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

		if (normalized.StartsWith("item/", StringComparison.OrdinalIgnoreCase) ||
		    normalized.StartsWith("items/", StringComparison.OrdinalIgnoreCase))
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

	private Image<Rgba32> RenderFallbackTexture(string itemName, ItemRegistry.ItemInfo? itemInfo,
		BlockModelInstance? model, BlockRenderOptions options)
	{
		if (TryRenderFlatItemFromIdentifiers(CollectItemLayerTextures(model, itemInfo), model, options, itemName,
			    out var rendered))
		{
			return rendered;
		}

		if (itemInfo is not null && !string.IsNullOrWhiteSpace(itemInfo.Texture) &&
		    TryRenderEmbeddedTexture(itemInfo.Texture, options, itemName, out rendered))
		{
			return rendered;
		}

		foreach (var candidate in EnumerateTextureFallbackCandidates(itemName))
		{
			if (TryRenderEmbeddedTexture(candidate, options, itemName, out rendered))
			{
				return rendered;
			}
		}

		return RenderFlatItem(new[] { "minecraft:missingno" }, options, itemName);
	}

		private static bool ShouldPreferPlayerHeadRenderer(string itemName, BlockModelInstance? model,
			IReadOnlyList<string> modelCandidates, BlockRenderOptions options)
		{
			var itemData = options.ItemData;
			if (itemData is null)
			{
				return false;
			}

			if (!string.Equals(NormalizeItemTextureKey(itemName), "player_head", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			var hasSkinSource = itemData.Profile is not null
			                  || (itemData.CustomData is not null &&
			                      TryGetHeadTextureOverride(itemData.CustomData, out _));
			if (!hasSkinSource)
			{
				return false;
			}

			if (HasExplicitFlatHeadOverride(model, modelCandidates))
			{
				return false;
			}

			return true;
		}

		private static bool HasExplicitFlatHeadOverride(BlockModelInstance? model,
			IReadOnlyList<string> modelCandidates)
		{
			var hasNonDefaultCandidate = HasNonDefaultPlayerHeadModelCandidate(modelCandidates);

			if (ModelChainContainsTemplateSkull(model))
			{
				return hasNonDefaultCandidate;
			}

			if (!hasNonDefaultCandidate &&
			    modelCandidates.Any(static candidate => ContainsTemplateSkullToken(candidate)))
			{
				return false;
			}

			if (model is not null)
			{
				return true;
			}

			return hasNonDefaultCandidate;
		}

	private bool TryRenderPlayerHead(string itemName, BlockModelInstance? model,
		IReadOnlyList<string> modelCandidates, BlockRenderOptions options, out Image<Rgba32> rendered)
	{
		rendered = null!;
		if (!IsPlayerHeadCandidate(itemName, model, modelCandidates))
		{
			return false;
		}

		if (!TryResolveHeadSkin(options, out var skinSource))
		{
			return false;
		}

		using var skin = skinSource.Clone();

		var rotation = options.OverrideGuiTransform?.Rotation ?? model?.GetDisplayTransform("gui")?.Rotation;
		var pitch = options.PitchInDegrees;
		var yaw = options.YawInDegrees;
		var roll = options.RollInDegrees;

		if (rotation is not null)
		{
			if (rotation.Length > 0)
			{
				pitch += rotation[0];
			}

			if (rotation.Length > 1)
			{
				yaw += rotation[1];
			}

			if (rotation.Length > 2)
			{
				roll += rotation[2];
			}
		}

		yaw -= 180f;

		var headOptions = new MinecraftHeadRenderer.RenderOptions(
			options.Size,
			yaw,
			pitch,
			roll,
			options.PerspectiveAmount,
			ShowOverlay: true);

		rendered = MinecraftHeadRenderer.RenderHead(headOptions, skin);
		return true;
	}

	private static bool IsPlayerHeadCandidate(string itemName, BlockModelInstance? model,
		IReadOnlyList<string> modelCandidates)
	{
		var normalized = NormalizeItemTextureKey(itemName);
		if (!string.Equals(normalized, "player_head", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		if (ModelChainContainsTemplateSkull(model) ||
		    modelCandidates.Any(static candidate => ContainsTemplateSkullToken(candidate)))
		{
			return true;
		}

		// Some aggregated asset bundles omit the explicit template_skull parent chain. Fall back to treating all
		// player_head items as candidates so profile-based skins can still render correctly.
		return true;
	}

	private static bool ModelChainContainsTemplateSkull(BlockModelInstance? model)
	{
		if (model is null)
		{
			return false;
		}

		if (ContainsTemplateSkullToken(model.Name))
		{
			return true;
		}

		foreach (var parent in model.ParentChain)
		{
			if (ContainsTemplateSkullToken(parent))
			{
				return true;
			}
		}

		return false;
	}

	private static bool ContainsTemplateSkullToken(string? candidate)
	{
		if (string.IsNullOrWhiteSpace(candidate))
		{
			return false;
		}

		return candidate.IndexOf("template_skull", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static bool HasNonDefaultPlayerHeadModelCandidate(IReadOnlyList<string> modelCandidates)
	{
		if (modelCandidates.Count == 0)
		{
			return false;
		}

		foreach (var candidate in modelCandidates)
		{
			if (IsDefaultPlayerHeadModelCandidate(candidate))
			{
				continue;
			}

			if (!ContainsTemplateSkullToken(candidate))
			{
				return true;
			}
		}

		return false;
	}

	private static bool IsDefaultPlayerHeadModelCandidate(string? candidate)
	{
		if (string.IsNullOrWhiteSpace(candidate))
		{
			return false;
		}

		var normalized = NormalizeItemTextureKey(candidate);
		return string.Equals(normalized, "player_head", StringComparison.OrdinalIgnoreCase)
		       || string.Equals(normalized, "item/player_head", StringComparison.OrdinalIgnoreCase)
		       || string.Equals(normalized, "player_head_inventory", StringComparison.OrdinalIgnoreCase)
		       || string.Equals(normalized, "item/player_head_inventory", StringComparison.OrdinalIgnoreCase)
		       || string.Equals(normalized, "player_head#inventory", StringComparison.OrdinalIgnoreCase)
		       || string.Equals(normalized, "item/player_head#inventory", StringComparison.OrdinalIgnoreCase);
	}

	private bool TryResolveHeadSkin(BlockRenderOptions options, out Image<Rgba32> skin)
	{
		skin = null!;
		var itemData = options.ItemData;

		if (itemData?.CustomData is not null &&
		    TryGetHeadTextureOverride(itemData.CustomData, out var textureId) &&
		    _textureRepository.TryGetTexture(textureId, out var textureOverride))
		{
			skin = textureOverride;
			return true;
		}

		if (itemData?.Profile is not null && TryGetProfileSkin(itemData.Profile, out var profileSkin))
		{
			skin = profileSkin;
			return true;
		}

		return TryGetDefaultPlayerSkin(out skin);
	}

	private static bool TryGetHeadTextureOverride(NbtCompound customData, out string textureId)
	{
		if (TryGetString(customData, "texture", out var value) && !string.IsNullOrWhiteSpace(value))
		{
			textureId = value!;
			return true;
		}

		if (TryGetString(customData, "skin", out value) && !string.IsNullOrWhiteSpace(value))
		{
			textureId = value!;
			return true;
		}

		if (TryGetString(customData, "skin_texture", out value) && !string.IsNullOrWhiteSpace(value))
		{
			textureId = value!;
			return true;
		}

		textureId = string.Empty;
		return false;
	}

	private bool TryGetProfileSkin(NbtCompound profile, out Image<Rgba32> skin)
	{
		skin = null!;
		if (!TryExtractSkinUrl(profile, out var url))
		{
			return false;
		}

		return TryLoadSkinFromUrl(url, out skin);
	}

	private static bool TryExtractSkinUrl(NbtCompound profile, out string url)
	{
		url = string.Empty;
		if (!profile.TryGetValue("properties", out var propertiesTag) || propertiesTag is not NbtList properties)
		{
			return false;
		}

		foreach (var entry in properties)
		{
			if (entry is not NbtCompound propertyCompound)
			{
				continue;
			}

			if (!TryGetString(propertyCompound, "name", out var name) ||
			    !string.Equals(name, "textures", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (!TryGetString(propertyCompound, "value", out var encoded) || string.IsNullOrWhiteSpace(encoded))
			{
				continue;
			}

			if (TryDecodeSkinPayload(encoded!, out var decodedUrl))
			{
				url = decodedUrl;
				return true;
			}
		}

		return false;
	}

	private static bool TryDecodeSkinPayload(string encodedPayload, out string url)
	{
		url = string.Empty;
		try
		{
			var payloadBytes = Convert.FromBase64String(encodedPayload);
			using var document = JsonDocument.Parse(payloadBytes);
			if (document.RootElement.TryGetProperty("textures", out var texturesElement) &&
			    texturesElement.TryGetProperty("SKIN", out var skinElement) &&
			    skinElement.TryGetProperty("url", out var urlElement) &&
			    urlElement.ValueKind == JsonValueKind.String)
			{
				var candidate = urlElement.GetString();
				if (!string.IsNullOrWhiteSpace(candidate))
				{
					url = candidate!;
					return true;
				}
			}
		}
		catch (FormatException)
		{
			return false;
		}
		catch (JsonException)
		{
			return false;
		}

		return false;
	}

	private bool TryLoadSkinFromUrl(string url, out Image<Rgba32> skin)
	{
		skin = null!;
		if (!TryNormalizeSkinUrl(url, out var normalized))
		{
			return false;
		}

		try
		{
			var cached = _playerSkinCache.GetOrAdd(normalized,
				key => new Lazy<Image<Rgba32>>(() => LoadOrDownloadPlayerSkin(key), LazyThreadSafetyMode.ExecutionAndPublication));

			skin = cached.Value;
			return true;
		}
		catch
		{
			_playerSkinCache.TryRemove(normalized, out _);
			return false;
		}
	}

	private static bool TryNormalizeSkinUrl(string url, out string normalized)
	{
		normalized = string.Empty;
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
		{
			return false;
		}

		if (!string.Equals(uri.Host, "textures.minecraft.net", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		var builder = new UriBuilder(uri)
		{
			Scheme = Uri.UriSchemeHttps,
			Port = -1
		};

		normalized = builder.Uri.ToString();
		return true;
	}

	private Image<Rgba32> LoadOrDownloadPlayerSkin(string normalizedUrl)
	{
		if (TryLoadSkinFromDisk(normalizedUrl, out var skin))
		{
			return skin;
		}

		return DownloadPlayerSkin(normalizedUrl);
	}

	private bool TryLoadSkinFromDisk(string normalizedUrl, out Image<Rgba32> skin)
	{
		skin = null!;
		var path = GetSkinCachePath(normalizedUrl);
		if (path is null || !File.Exists(path))
		{
			return false;
		}

		try
		{
			skin = Image.Load<Rgba32>(path);
			return true;
		}
		catch
		{
			try
			{
				File.Delete(path);
			}
			catch
			{
				// Ignore deletion failures; cache entry may get retried later.
			}

			return false;
		}
	}

	private Image<Rgba32> DownloadPlayerSkin(string normalizedUrl)
	{
		using var response = PlayerSkinClient.GetAsync(normalizedUrl).GetAwaiter().GetResult();
		response.EnsureSuccessStatusCode();
		using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
		var image = Image.Load<Rgba32>(stream);
		TryPersistSkin(normalizedUrl, image);
		return image;
	}

	private void TryPersistSkin(string normalizedUrl, Image<Rgba32> image)
	{
		var path = GetSkinCachePath(normalizedUrl);
		if (path is null)
		{
			return;
		}

		try
		{
			var directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			image.SaveAsPng(path);
		}
		catch
		{
			// Ignore persistence failures to avoid interrupting rendering.
		}
	}

	private string? GetSkinCachePath(string normalizedUrl)
	{
		if (string.IsNullOrWhiteSpace(_playerSkinCacheDirectory))
		{
			return null;
		}

		var fileName = GetSkinCacheFileName(normalizedUrl);
		return Path.Combine(_playerSkinCacheDirectory, fileName);
	}

	private static string GetSkinCacheFileName(string normalizedUrl)
	{
		try
		{
			var uri = new Uri(normalizedUrl, UriKind.Absolute);
			var lastSegment = uri.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
			if (!string.IsNullOrWhiteSpace(lastSegment))
			{
				return lastSegment.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
					? lastSegment
					: lastSegment + ".png";
			}
		}
		catch (UriFormatException)
		{
			// Fall back to hashing logic.
		}

		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUrl));
		return Convert.ToHexString(hash) + ".png";
	}

	private bool TryGetDefaultPlayerSkin(out Image<Rgba32> skin)
	{
		var candidates = new[]
		{
			"minecraft:entity/player/wide/steve",
			"minecraft:entity/steve",
			"minecraft:entity/player/wide/alex",
			"minecraft:entity/alex"
		};

		foreach (var candidate in candidates)
		{
			if (_textureRepository.TryGetTexture(candidate, out skin))
			{
				return true;
			}
		}

		skin = null!;
		return false;
	}

	private static bool TryGetString(NbtCompound compound, string key, out string? value)
	{
		value = null;
		if (!compound.TryGetValue(key, out var tag))
		{
			return false;
		}

		if (tag is NbtString nbtString)
		{
			value = nbtString.Value;
			return true;
		}

		return false;
	}

	private static HttpClient CreatePlayerSkinClient()
	{
		var client = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(10)
		};
		client.DefaultRequestHeaders.UserAgent.ParseAdd("MinecraftRenderer/1.0");
		return client;
	}

	private bool TryRenderBedItem(string itemName, BlockModelInstance? itemModel, BlockRenderOptions options,
		out Image<Rgba32> rendered)
	{
		rendered = null!;

		var normalizedName = NormalizeItemTextureKey(itemName);
		if (!normalizedName.EndsWith("_bed", StringComparison.OrdinalIgnoreCase) &&
		    !string.Equals(normalizedName, "bed", StringComparison.OrdinalIgnoreCase))
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
		elements.AddRange(CloneAndTranslateElements(headModel, new Vector3(0f, 0f, -16f), flipBottomFaces: false,
			flipNorthSouthFaces: false));
		elements.AddRange(CloneAndTranslateElements(footModel, Vector3.Zero, flipBottomFaces: true,
			flipNorthSouthFaces: true));

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
		var renderOptions = options;
		if (display.TryGetValue("gui", out var adjustedGui))
		{
			renderOptions = renderOptions with { OverrideGuiTransform = adjustedGui };
		}
		else
		{
			renderOptions = renderOptions with { OverrideGuiTransform = null };
		}

		var composite = new BlockModelInstance(
			"minecraft:generated/bed_composite",
			parentChain,
			textures,
			display,
			elements);

		rendered = RenderModel(composite, renderOptions);
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

	private static List<ModelElement> CloneAndTranslateElements(BlockModelInstance source, Vector3 translation,
		bool flipBottomFaces, bool flipNorthSouthFaces)
	{
		var result = new List<ModelElement>(source.Elements.Count);
		for (var i = 0; i < source.Elements.Count; i++)
		{
			result.Add(CloneAndTranslateElement(source.Elements[i], translation, flipBottomFaces, flipNorthSouthFaces));
		}

		return result;
	}

	private static ModelElement CloneAndTranslateElement(ModelElement element, Vector3 translation,
		bool flipBottomFaces, bool flipNorthSouthFaces)
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
			else if (flipNorthSouthFaces && shouldFlipLargeFaces &&
			         (direction == BlockFaceDirection.North || direction == BlockFaceDirection.South))
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
		const float RotationAdjustment = -50f;
		const float ScaleMultiplier = 0.9f;
		var defaultScale = new[] { 0.48f, 0.48f, 0.48f };
		var defaultTranslation = new[] { 2f, 2.5f, 0f };

		if (!display.TryGetValue("gui", out var gui))
		{
			display["gui"] = new TransformDefinition
			{
				Rotation = new[] { 30f, 160f + RotationAdjustment, 0f },
				Translation = (float[])defaultTranslation.Clone(),
				Scale = (float[])defaultScale.Clone()
			};
			return;
		}

		var rotationArray = gui.Rotation is null ? new float[3] : CloneVector(gui.Rotation, 3);
		EnsureLength(ref rotationArray, 3);
		rotationArray[1] = NormalizeRotation(rotationArray[1] + RotationAdjustment);

		float[] translationArray;
		if (gui.Translation is null || gui.Translation.Length == 0)
		{
			translationArray = (float[])defaultTranslation.Clone();
		}
		else
		{
			translationArray = CloneVector(gui.Translation, gui.Translation.Length);
		}

		EnsureLength(ref translationArray, 3);

		float[] scaleArray;
		if (gui.Scale is null || gui.Scale.Length == 0)
		{
			scaleArray = (float[])defaultScale.Clone();
		}
		else
		{
			scaleArray = CloneVector(gui.Scale, gui.Scale.Length);
			EnsureLength(ref scaleArray, 3);
			for (var i = 0; i < scaleArray.Length; i++)
			{
				scaleArray[i] *= ScaleMultiplier;
			}
		}

		display["gui"] = new TransformDefinition
		{
			Rotation = rotationArray,
			Translation = translationArray,
			Scale = scaleArray
		};
	}

	private static TransformDefinition AdjustBannerGuiTransform(TransformDefinition? original)
	{
		const float YawAdjustment = 215f;

		var rotation = original?.Rotation is { Length: > 0 } rotationSource
			? CloneVector(rotationSource, Math.Max(3, rotationSource.Length))
			: new float[3];
		EnsureLength(ref rotation, 3);
		rotation[1] = NormalizeRotation(rotation[1] + YawAdjustment);

		float[]? translation = null;
		if (original?.Translation is { } translationSource)
		{
			translation = CloneVector(translationSource, Math.Max(3, translationSource.Length));
		}

		float[]? scale = null;
		if (original?.Scale is { } scaleSource)
		{
			scale = CloneVector(scaleSource, Math.Max(3, scaleSource.Length));
		}

		return new TransformDefinition
		{
			Rotation = rotation,
			Translation = translation,
			Scale = scale
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
		var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var candidate in EnumerateTextureNameVariants(normalized))
		{
			if (yielded.Add(candidate))
			{
				yield return candidate;
			}
		}

		if (AnimatedDialItems.Contains(normalized))
		{
			foreach (var candidate in EnumerateTextureNameVariants(normalized + "_00"))
			{
				if (yielded.Add(candidate))
				{
					yield return candidate;
				}
			}
		}
	}

	private static IEnumerable<string> EnumerateTextureNameVariants(string textureKey)
	{
		yield return textureKey;
		yield return $"minecraft:item/{textureKey}";
		yield return $"item/{textureKey}";
		yield return $"textures/item/{textureKey}";
		yield return $"minecraft:block/{textureKey}";
		yield return $"block/{textureKey}";
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
			   || value.Contains("seagrass", StringComparison.OrdinalIgnoreCase)
			   || value.Contains("item/generated", StringComparison.OrdinalIgnoreCase);
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
			if (string.IsNullOrWhiteSpace(textureId) ||
			    textureId.Equals("minecraft:missingno", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			var canonical = NormalizeResourceKey(textureId);
			if (string.IsNullOrWhiteSpace(canonical))
			{
				canonical = textureId;
			}

			if (seen.Add(canonical))
			{
				resolved.Add(textureId);
			}
		}

		return resolved;
	}

	private bool TryRenderEmbeddedTexture(string textureId, BlockRenderOptions options, string? tintContext,
		out Image<Rgba32> rendered)
	{
		if (_textureRepository.TryGetTexture(textureId, out _))
		{
			rendered = RenderFlatItem(new[] { textureId }, options, tintContext);
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

	private Image<Rgba32> RenderFlatItem(IReadOnlyList<string> layerTextureIds, BlockRenderOptions options,
		string? tintContext)
	{
		var canvas = new Image<Rgba32>(options.Size, options.Size, Color.Transparent);
		ItemRegistry.ItemInfo? itemInfo = null;
		string? normalizedItemKey = null;

		if (!string.IsNullOrWhiteSpace(tintContext))
		{
			normalizedItemKey = NormalizeItemTextureKey(tintContext);

			if (_itemRegistry is not null)
			{
				if (!_itemRegistry.TryGetInfo(tintContext, out itemInfo) && !string.Equals(normalizedItemKey,
					    tintContext, StringComparison.OrdinalIgnoreCase))
				{
					_itemRegistry.TryGetInfo(normalizedItemKey, out itemInfo);
				}
			}
		}

		var primaryTintLayerIndex = DeterminePrimaryTintLayerIndex(normalizedItemKey, itemInfo);
		var explicitItemData = options.ItemData;
		var disablePrimaryDefault = explicitItemData?.DisableDefaultLayer0Tint ?? false;

		for (var layerIndex = 0; layerIndex < layerTextureIds.Count; layerIndex++)
		{
			var textureId = layerTextureIds[layerIndex];
			var explicitLayerTint = GetExplicitLayerTint(explicitItemData, layerIndex, primaryTintLayerIndex);
			Color? layerTint = explicitLayerTint;
			if (!layerTint.HasValue)
			{
				var defaultTint = ResolveDefaultLayerTint(normalizedItemKey, itemInfo, layerIndex,
					layerIndex == primaryTintLayerIndex, disablePrimaryDefault);
				if (defaultTint.HasValue)
				{
					layerTint = defaultTint.Value;
				}
			}

			var hasMetadataTint = itemInfo?.LayerTints.ContainsKey(layerIndex) == true;
			var hasLegacyTint = false;
			if (!string.IsNullOrWhiteSpace(normalizedItemKey) &&
			    LegacyDefaultTintOverrides.ContainsKey(normalizedItemKey))
			{
				if (LegacyDefaultTintLayerOverrides.TryGetValue(normalizedItemKey, out var constrainedLayers))
				{
					hasLegacyTint = constrainedLayers.Contains(layerIndex);
				}
				else
				{
					hasLegacyTint = layerIndex == 0;
				}
			}

			var hasExplicitPerLayerTint = explicitItemData?.AdditionalLayerTints is not null
			                              && explicitItemData.AdditionalLayerTints.ContainsKey(layerIndex);
			var hasPrimaryExplicitTint =
				layerIndex == primaryTintLayerIndex && explicitItemData?.Layer0Tint.HasValue == true;
			var skipContextTint = hasMetadataTint || hasLegacyTint || hasExplicitPerLayerTint || hasPrimaryExplicitTint;
			var texture = ResolveItemLayerTexture(textureId, tintContext, skipContextTint);
			var scale = MathF.Min(options.Size / (float)texture.Width, options.Size / (float)texture.Height);
			var targetWidth = Math.Max(1, (int)MathF.Round(texture.Width * scale));
			var targetHeight = Math.Max(1, (int)MathF.Round(texture.Height * scale));

			using var resized = texture.Clone(ctx => ctx.Resize(new ResizeOptions
			{
				Size = new Size(targetWidth, targetHeight),
				Sampler = KnownResamplers.NearestNeighbor,
				Mode = ResizeMode.Stretch
			}));
			if (layerTint.HasValue)
			{
				ApplyLayerTint(resized, layerTint.Value);
			}

			var offset = new Point((canvas.Width - targetWidth) / 2, (canvas.Height - targetHeight) / 2);
			canvas.Mutate(ctx => ctx.DrawImage(resized, offset, 1f));
		}

		return canvas;
	}

	private static Color? GetExplicitLayerTint(ItemRenderData? itemData, int layerIndex, int primaryTintLayerIndex)
	{
		if (itemData is null)
		{
			return null;
		}

		if (itemData.AdditionalLayerTints is not null &&
		    itemData.AdditionalLayerTints.TryGetValue(layerIndex, out var explicitTint))
		{
			return explicitTint;
		}

		if (layerIndex == primaryTintLayerIndex && itemData.Layer0Tint.HasValue)
		{
			return itemData.Layer0Tint.Value;
		}

		return null;
	}

	private static int DeterminePrimaryTintLayerIndex(string? normalizedItemKey, ItemRegistry.ItemInfo? itemInfo)
	{
		if (itemInfo is not null && itemInfo.LayerTints.Count > 0)
		{
			var dyeLayers = itemInfo.LayerTints
				.Where(static kvp => kvp.Value.Kind == ItemRegistry.ItemTintKind.Dye)
				.Select(static kvp => kvp.Key)
				.OrderBy(static index => index)
				.ToList();

			if (dyeLayers.Count > 0)
			{
				return dyeLayers[0];
			}

			var firstTintLayer = itemInfo.LayerTints.Keys.OrderBy(static index => index).FirstOrDefault();
			return firstTintLayer;
		}

		if (!string.IsNullOrWhiteSpace(normalizedItemKey) &&
		    LegacyDefaultTintLayerOverrides.TryGetValue(normalizedItemKey, out var overrides) && overrides.Length > 0)
		{
			return overrides.Min();
		}

		return 0;
	}

	private static Color? ResolveDefaultLayerTint(string? normalizedItemKey, ItemRegistry.ItemInfo? itemInfo,
		int layerIndex, bool isPrimaryDyeLayer, bool disablePrimaryDefault)
	{
		if (itemInfo is not null && itemInfo.LayerTints.Count > 0 &&
		    itemInfo.LayerTints.TryGetValue(layerIndex, out var tintInfo))
		{
			switch (tintInfo.Kind)
			{
				case ItemRegistry.ItemTintKind.Dye:
					if (disablePrimaryDefault && isPrimaryDyeLayer)
					{
						return null;
					}

					if (tintInfo.DefaultColor.HasValue)
					{
						return tintInfo.DefaultColor.Value;
					}

					break;
				case ItemRegistry.ItemTintKind.Constant:
					if (tintInfo.DefaultColor.HasValue)
					{
						return tintInfo.DefaultColor.Value;
					}

					break;
				default:
					if (tintInfo.DefaultColor.HasValue && !(disablePrimaryDefault && isPrimaryDyeLayer))
					{
						return tintInfo.DefaultColor.Value;
					}

					break;
			}
		}

		if (string.IsNullOrWhiteSpace(normalizedItemKey))
		{
			return null;
		}

		if (LegacyDefaultTintLayerOverrides.TryGetValue(normalizedItemKey, out var overrides) &&
		    overrides.Contains(layerIndex))
		{
			if (disablePrimaryDefault && isPrimaryDyeLayer)
			{
				return null;
			}

			if (LegacyDefaultTintOverrides.TryGetValue(normalizedItemKey, out var overrideColor))
			{
				return overrideColor;
			}
		}

		if (layerIndex == 0
		    && LegacyDefaultTintOverrides.TryGetValue(normalizedItemKey, out var legacyColor)
		    && (!LegacyDefaultTintLayerOverrides.TryGetValue(normalizedItemKey, out var constrainedLayers) ||
		        constrainedLayers.Contains(layerIndex)))
		{
			if (disablePrimaryDefault && isPrimaryDyeLayer)
			{
				return null;
			}

			return legacyColor;
		}

		if (!string.IsNullOrWhiteSpace(normalizedItemKey)
		    && normalizedItemKey.StartsWith("leather_", StringComparison.OrdinalIgnoreCase)
		    && layerIndex == 0
		    && !(disablePrimaryDefault && isPrimaryDyeLayer))
		{
			return DefaultLeatherArmorColor;
		}

		return null;
	}

	private static void ApplyLayerTint(Image<Rgba32> image, Color tint)
	{
		var tintVector = tint.ToPixel<Rgba32>().ToVector4();
		tintVector.W = 1f;

		image.ProcessPixelRows(accessor =>
		{
			for (var y = 0; y < accessor.Height; y++)
			{
				var row = accessor.GetRowSpan(y);
				for (var x = 0; x < row.Length; x++)
				{
					var pixelVector = row[x].ToVector4();
					var alpha = pixelVector.W;
					pixelVector.X = MathF.Min(pixelVector.X * tintVector.X, 1f);
					pixelVector.Y = MathF.Min(pixelVector.Y * tintVector.Y, 1f);
					pixelVector.Z = MathF.Min(pixelVector.Z * tintVector.Z, 1f);
					pixelVector.W = alpha;
					row[x].FromVector4(pixelVector);
				}
			}
		});
	}

	private static bool ShouldAlignGuiItemToBottom(string? normalizedItemKey)
	{
		if (string.IsNullOrWhiteSpace(normalizedItemKey))
		{
			return false;
		}

		foreach (var suffix in BottomAlignedItemSuffixes)
		{
			if (normalizedItemKey.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return string.Equals(normalizedItemKey, "carpet", StringComparison.OrdinalIgnoreCase)
		       || string.Equals(normalizedItemKey, "trapdoor", StringComparison.OrdinalIgnoreCase)
		       || string.Equals(normalizedItemKey, "pressure_plate", StringComparison.OrdinalIgnoreCase);
	}

	private static float? GetPostRenderScale(string? normalizedItemKey)
	{
		if (string.IsNullOrWhiteSpace(normalizedItemKey))
		{
			return null;
		}

		if (IsBedItem(normalizedItemKey))
		{
			return 0.92f;
		}

		if (IsBannerItem(normalizedItemKey))
		{
			return 0.9f;
		}

		return null;
	}

	private static bool IsBedItem(string normalizedItemKey)
		=> normalizedItemKey.EndsWith("_bed", StringComparison.OrdinalIgnoreCase)
		   || string.Equals(normalizedItemKey, "bed", StringComparison.OrdinalIgnoreCase);

	private static bool IsBannerItem(string normalizedItemKey)
	{
		foreach (var suffix in BannerSuffixes)
		{
			if (normalizedItemKey.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return string.Equals(normalizedItemKey, "banner", StringComparison.OrdinalIgnoreCase);
	}

	private static void AlignImageToBottom(Image<Rgba32> image)
	{
		var bounds = FindOpaqueBounds(image);
		if (bounds.Height <= 0)
		{
			return;
		}

		var desiredTop = image.Height - bounds.Height;
		var deltaY = desiredTop - bounds.Y;
		if (deltaY == 0)
		{
			return;
		}

		using var clone = image.Clone();
		ClearImage(image);
		image.Mutate(ctx => ctx.DrawImage(clone, new Point(0, deltaY), 1f));
	}

	private static void ApplyCenteredScale(Image<Rgba32> image, float scaleFactor)
	{
		if (scaleFactor <= 0f || MathF.Abs(scaleFactor - 1f) < 1e-3f)
		{
			return;
		}

		var targetWidth = Math.Max(1, (int)MathF.Round(image.Width * scaleFactor));
		var targetHeight = Math.Max(1, (int)MathF.Round(image.Height * scaleFactor));

		using var clone = image.Clone();
		using var resized = clone.Clone(ctx => ctx.Resize(new ResizeOptions
		{
			Size = new Size(targetWidth, targetHeight),
			Sampler = KnownResamplers.NearestNeighbor,
			Mode = ResizeMode.Stretch
		}));

		ClearImage(image);
		var offset = new Point((image.Width - targetWidth) / 2, (image.Height - targetHeight) / 2);
		image.Mutate(ctx => ctx.DrawImage(resized, offset, 1f));
	}

	private static void ClearImage(Image<Rgba32> image)
	{
		image.ProcessPixelRows(accessor =>
		{
			for (var y = 0; y < accessor.Height; y++)
			{
				accessor.GetRowSpan(y).Clear();
			}
		});
	}

	private static Rectangle FindOpaqueBounds(Image<Rgba32> image)
	{
		var minX = image.Width;
		var minY = image.Height;
		var maxX = -1;
		var maxY = -1;

		image.ProcessPixelRows(accessor =>
		{
			for (var y = 0; y < accessor.Height; y++)
			{
				var row = accessor.GetRowSpan(y);
				for (var x = 0; x < row.Length; x++)
				{
					if (row[x].A == 0)
					{
						continue;
					}

					if (x < minX)
					{
						minX = x;
					}

					if (y < minY)
					{
						minY = y;
					}

					if (x > maxX)
					{
						maxX = x;
					}

					if (y > maxY)
					{
						maxY = y;
					}
				}
			}
		});

		if (maxX < 0 || maxY < 0)
		{
			return Rectangle.Empty;
		}

		return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
	}

	private Image<Rgba32> ResolveItemLayerTexture(string textureId, string? tintContext, bool skipContextTint)
	{
		if (skipContextTint)
		{
			return _textureRepository.GetTexture(textureId);
		}

		var constantTint = TryGetConstantTint(textureId, tintContext);
		if (constantTint.HasValue)
		{
			return _textureRepository.GetTintedTexture(textureId, constantTint.Value, ConstantTintStrength);
		}

		if (TryGetBiomeTintKind(textureId, tintContext, out var biomeKind))
		{
			return GetBiomeTintedTexture(textureId, biomeKind);
		}

		if (ShouldApplyItemColorTint(textureId, tintContext))
		{
			var fallbackTint = GetColorFromBlockName(tintContext) ?? GetColorFromBlockName(textureId);
			if (fallbackTint.HasValue)
			{
				return _textureRepository.GetTintedTexture(textureId, fallbackTint.Value, 1f, ColorTintBlend);
			}
		}

		return _textureRepository.GetTexture(textureId);
	}

	private bool ShouldApplyItemColorTint(string textureId, string? tintContext)
	{
		if (string.IsNullOrWhiteSpace(tintContext))
		{
			return false;
		}

		var normalizedContext = NormalizeResourceKey(tintContext);
		if (string.IsNullOrEmpty(normalizedContext))
		{
			return false;
		}

		var textureKey = NormalizeResourceKey(textureId);

		if (_itemRegistry is not null && _itemRegistry.TryGetInfo(normalizedContext, out var itemInfo))
		{
			BlockModelInstance? model = null;

			if (!string.IsNullOrWhiteSpace(itemInfo.Model))
			{
				model = ResolveModelOrNull(itemInfo.Model!);
			}

			model ??= ResolveModelOrNull(normalizedContext);

			if (model is not null)
			{
				if (ModelChainIndicatesDyeTint(model))
				{
					return true;
				}

				return ShouldApplyColorByHeuristic(textureKey, normalizedContext);
			}
		}

		return ShouldApplyColorByHeuristic(textureKey, normalizedContext);
	}

	private static bool ModelChainIndicatesDyeTint(BlockModelInstance model)
	{
		if (IsDyeTintTemplate(model.Name))
		{
			return true;
		}

		for (var i = 0; i < model.ParentChain.Count; i++)
		{
			if (IsDyeTintTemplate(model.ParentChain[i]))
			{
				return true;
			}
		}

		return false;
	}

	private static bool IsDyeTintTemplate(string? candidate)
	{
		if (string.IsNullOrWhiteSpace(candidate))
		{
			return false;
		}

		if (candidate.Contains("template_shulker_box", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (candidate.Contains("template_banner", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		return false;
	}

	private static bool ShouldApplyColorByHeuristic(string textureKey, string contextKey)
	{
		if (!ContainsColorToken(contextKey))
		{
			return false;
		}

		if (string.IsNullOrEmpty(textureKey))
		{
			return true;
		}

		return !ContainsColorToken(textureKey);
	}

	private static bool ContainsColorToken(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return false;
		}

		foreach (var colorName in ColorMap.Keys)
		{
			if (ContainsColorToken(value, colorName))
			{
				return true;
			}
		}

		return false;
	}

	private static bool ContainsColorToken(string source, string token)
	{
		var index = source.IndexOf(token, StringComparison.OrdinalIgnoreCase);
		while (index >= 0)
		{
			var beforeIndex = index - 1;
			var afterIndex = index + token.Length;
			var hasLetterBefore = beforeIndex >= 0 && char.IsLetter(source[beforeIndex]);
			var hasLetterAfter = afterIndex < source.Length && char.IsLetter(source[afterIndex]);

			if (!hasLetterBefore && !hasLetterAfter)
			{
				return true;
			}

			index = source.IndexOf(token, index + 1, StringComparison.OrdinalIgnoreCase);
		}

		return false;
	}
}