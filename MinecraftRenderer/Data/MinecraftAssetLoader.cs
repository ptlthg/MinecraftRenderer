namespace MinecraftRenderer;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using MinecraftRenderer.Assets;

internal static class MinecraftAssetLoader
{
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		MaxDepth = 8192,
		AllowTrailingCommas = true
	};

	private static readonly string[] PreferredVariantKeys =
	[
		string.Empty,
		"inventory",
		"normal",
		"facing=north",
		"north=true",
		"axis=y",
		"half=lower",
		"type=bottom",
		"part=base"
	];

	private static readonly string[] TexturePreferenceOrder =
	[
		"all",
		"layer0",
		"texture",
		"side",
		"top",
		"bottom",
		"front",
		"back",
		"north",
		"south",
		"east",
		"west",
		"up",
		"down",
		"particle"
	];

	private sealed record ItemDefinitionEntry(
		string Name,
		string? ModelReference,
		Dictionary<int, ItemRegistry.ItemTintInfo> LayerTints,
		ItemModelSelector? Selector);

	public static Dictionary<string, BlockModelDefinition> LoadModelDefinitions(string assetsRoot,
		IEnumerable<string>? overlayRoots = null, AssetNamespaceRegistry? assetNamespaces = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(assetsRoot);

		var namespaceRoots = BuildNamespaceRootList(assetsRoot, overlayRoots, assetNamespaces,
			includeAllNamespaces: true);
		var definitions = new Dictionary<string, BlockModelDefinition>(StringComparer.OrdinalIgnoreCase);

		var hasAnyModels = false;
		foreach (var root in namespaceRoots)
		{
			foreach (var directory in EnumerateModelDirectories(root.Path))
			{
				hasAnyModels = true;
				foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
				{
					var relativePath = Path.GetRelativePath(directory, file);
					var key = NormalizeModelKey(relativePath, root.Namespace);
					if (string.IsNullOrWhiteSpace(key))
					{
						continue;
					}

					var json = File.ReadAllText(file);
					var definition = JsonSerializer.Deserialize<BlockModelDefinition>(json, SerializerOptions) ??
					                 new BlockModelDefinition();
					definitions[key] = definition;
				}
			}
		}

		if (!hasAnyModels)
		{
			var modelsRoot = Path.Combine(assetsRoot, "models");
			throw new DirectoryNotFoundException($"Models directory not found at '{modelsRoot}'.");
		}

		foreach (var (key, definition) in GetBuiltinModelDefinitions())
		{
			definitions.TryAdd(key, definition);
		}

		return definitions;
	}

	public static List<BlockRegistry.BlockInfo> LoadBlockInfos(string assetsRoot,
		IReadOnlyDictionary<string, BlockModelDefinition> models, IEnumerable<string>? overlayRoots = null,
		AssetNamespaceRegistry? assetNamespaces = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(assetsRoot);
		ArgumentNullException.ThrowIfNull(models);

		var roots = BuildRootList(assetsRoot, overlayRoots, assetNamespaces);
		var entries = new Dictionary<string, BlockRegistry.BlockInfo>(StringComparer.OrdinalIgnoreCase);
		var hasAnyBlockstates = false;
		foreach (var root in roots)
		{
			foreach (var directory in EnumerateBlockstateDirectories(root))
			{
				hasAnyBlockstates = true;
				foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
				{
					var relativePath = Path.GetRelativePath(directory, file);
					var blockName = NormalizeBlockStateName(relativePath);

					using var stream = File.OpenRead(file);
					using var document =
						JsonDocument.Parse(stream,
							new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 8192 });
					var modelReference = ResolveDefaultModel(blockName, document.RootElement, models);
					var textureReference = ResolveRepresentativeTexture(modelReference, models);

					entries[blockName] = new BlockRegistry.BlockInfo
					{
						Name = blockName,
						BlockState = blockName,
						Model = modelReference,
						Texture = textureReference
					};
				}
			}
		}

		if (!hasAnyBlockstates)
		{
			var blockstatesRoot = Path.Combine(assetsRoot, "blockstates");
			throw new DirectoryNotFoundException($"Blockstates directory not found at '{blockstatesRoot}'.");
		}

		return entries.Values.ToList();
	}

	public static List<ItemRegistry.ItemInfo> LoadItemInfos(string assetsRoot,
		IReadOnlyDictionary<string, BlockModelDefinition> models, IEnumerable<string>? overlayRoots = null,
		AssetNamespaceRegistry? assetNamespaces = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(assetsRoot);
		ArgumentNullException.ThrowIfNull(models);

		var entries = new Dictionary<string, ItemRegistry.ItemInfo>(StringComparer.OrdinalIgnoreCase);

		foreach (var (key, definition) in models)
		{
			if (!key.StartsWith("item/", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			var itemName = key[5..];
			if (string.IsNullOrWhiteSpace(itemName) || IsTemplateItem(itemName))
			{
				continue;
			}

			var texture = ResolvePrimaryTexture(definition, models);

			if (!entries.TryGetValue(itemName, out var info))
			{
				info = new ItemRegistry.ItemInfo { Name = itemName };
				entries[itemName] = info;
			}

			info.Model = key;
			if (string.IsNullOrWhiteSpace(info.Texture) && !string.IsNullOrWhiteSpace(texture))
			{
				info.Texture = texture;
			}
		}

		foreach (var entry in EnumerateItemDefinitions(assetsRoot, overlayRoots, assetNamespaces))
		{
			var itemName = entry.Name;
			var modelReference = entry.ModelReference;
			if (string.IsNullOrWhiteSpace(itemName) || IsTemplateItem(itemName))
			{
				continue;
			}

			if (!entries.TryGetValue(itemName, out var info))
			{
				info = new ItemRegistry.ItemInfo { Name = itemName };
				entries[itemName] = info;
			}

			if (!string.IsNullOrWhiteSpace(modelReference))
			{
				info.Model = modelReference;

				if (string.IsNullOrWhiteSpace(info.Texture))
				{
					var normalized = NormalizeModelReference(modelReference);
					if (!string.IsNullOrWhiteSpace(normalized) && models.TryGetValue(normalized, out var definition))
					{
						var texture = ResolvePrimaryTexture(definition, models);
						if (!string.IsNullOrWhiteSpace(texture))
						{
							info.Texture = texture;
						}
					}
				}
			}

			if (entry.Selector is not null)
			{
				info.Selector = entry.Selector;
			}

			if (entry.LayerTints.Count > 0)
			{
				foreach (var (layerIndex, tintInfo) in entry.LayerTints)
				{
					info.LayerTints[layerIndex] = tintInfo;
				}
			}
		}

		return entries.Values.ToList();
	}

	private static string NormalizeModelKey(string relativePath, string? namespaceName = null)
	{
		if (string.IsNullOrWhiteSpace(relativePath))
		{
			return string.Empty;
		}

		var normalized = relativePath.Replace('\\', '/').Trim();

		if (normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[..^5];
		}

		normalized = normalized.TrimStart('.', '/');

		if (normalized.StartsWith("block/", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[6..];
		}
		else if (normalized.StartsWith("blocks/", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[7..];
		}

		normalized = normalized.TrimStart('/');

		if (!string.IsNullOrWhiteSpace(namespaceName) &&
		    !namespaceName.Equals("minecraft", StringComparison.OrdinalIgnoreCase) &&
		    !normalized.StartsWith(namespaceName + ":", StringComparison.OrdinalIgnoreCase))
		{
			normalized = $"{namespaceName}:{normalized}";
		}

		return normalized;
	}

	private static IEnumerable<(string Key, BlockModelDefinition Definition)> GetBuiltinModelDefinitions()
	{
		yield return ("builtin/generated", new BlockModelDefinition());
		yield return ("builtin/entity", new BlockModelDefinition());
		yield return ("builtin/missing", new BlockModelDefinition
		{
			Textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["particle"] = "minecraft:block/missingno"
			}
		});
	}

	private static IReadOnlyList<string> BuildRootList(string primaryRoot, IEnumerable<string>? overlayRoots,
		AssetNamespaceRegistry? assetNamespaces, string namespaceName = "minecraft", bool includeAllNamespaces = false)
	{
		var namespaceRoots = BuildNamespaceRootList(primaryRoot, overlayRoots, assetNamespaces, namespaceName,
			includeAllNamespaces);
		return namespaceRoots
			.Select(static root => root.Path)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private static IReadOnlyList<AssetNamespaceRoot> BuildNamespaceRootList(string primaryRoot,
		IEnumerable<string>? overlayRoots, AssetNamespaceRegistry? assetNamespaces, string namespaceName = "minecraft",
		bool includeAllNamespaces = false)
	{
		if (assetNamespaces is not null)
		{
			IEnumerable<AssetNamespaceRoot> resolvedNamespaces;
			if (includeAllNamespaces)
			{
				resolvedNamespaces = assetNamespaces.Roots;
			}
			else
			{
				resolvedNamespaces = assetNamespaces.ResolveRoots(namespaceName);
			}

			var resolvedList = DeduplicateNamespaceRoots(resolvedNamespaces);
			if (resolvedList.Count > 0)
			{
				return resolvedList;
			}
		}

		var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var results = new List<AssetNamespaceRoot>();
		var effectiveNamespace = string.IsNullOrWhiteSpace(namespaceName) ? "minecraft" : namespaceName;

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

			if (!dedupe.Add(fullPath))
			{
				return;
			}

			results.Add(new AssetNamespaceRoot(effectiveNamespace, fullPath, "external", false));
		}

		TryAdd(primaryRoot);
		if (overlayRoots is not null)
		{
			foreach (var overlay in overlayRoots)
			{
				TryAdd(overlay);
			}
		}

		return results;
	}

	private static IReadOnlyList<AssetNamespaceRoot> DeduplicateNamespaceRoots(IEnumerable<AssetNamespaceRoot> roots)
	{
		var results = new List<AssetNamespaceRoot>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var root in roots)
		{
			if (root is null)
			{
				continue;
			}

			var path = root.Path;
			if (string.IsNullOrWhiteSpace(path))
			{
				continue;
			}

			var fullPath = Path.GetFullPath(path);
			if (!Directory.Exists(fullPath))
			{
				continue;
			}

			var namespaceKey = string.IsNullOrWhiteSpace(root.Namespace) ? "minecraft" : root.Namespace;
			var identity = $"{namespaceKey.ToLowerInvariant()}|{fullPath.ToLowerInvariant()}";
			if (!seen.Add(identity))
			{
				continue;
			}

			results.Add(new AssetNamespaceRoot(namespaceKey, fullPath, root.SourceId, root.IsVanilla));
		}

		return results;
	}

	private static IEnumerable<string> EnumerateModelDirectories(string root)
	{
		var modelsRoot = Path.Combine(root, "models");
		if (Directory.Exists(modelsRoot))
		{
			yield return modelsRoot;
		}

		var blockEntityModels = Path.Combine(root, "blockentities", "blockModels");
		if (Directory.Exists(blockEntityModels))
		{
			yield return blockEntityModels;
		}
	}

	private static IEnumerable<string> EnumerateBlockstateDirectories(string root)
	{
		var blockstatesRoot = Path.Combine(root, "blockstates");
		if (Directory.Exists(blockstatesRoot))
		{
			yield return blockstatesRoot;
		}

		var blockEntityStates = Path.Combine(root, "blockentities", "blockStates");
		if (Directory.Exists(blockEntityStates))
		{
			yield return blockEntityStates;
		}
	}

	private static IEnumerable<ItemDefinitionEntry> EnumerateItemDefinitions(string assetsRoot,
		IEnumerable<string>? overlayRoots, AssetNamespaceRegistry? assetNamespaces)
	{
		var namespaceRoots = BuildNamespaceRootList(assetsRoot, overlayRoots, assetNamespaces,
			includeAllNamespaces: true);

		foreach (var nsRoot in namespaceRoots)
		{
			var itemsRoot = Path.Combine(nsRoot.Path, "items");
			if (!Directory.Exists(itemsRoot))
			{
				continue;
			}

			foreach (var file in Directory.EnumerateFiles(itemsRoot, "*.json", SearchOption.AllDirectories))
			{
				var relativePath = Path.GetRelativePath(itemsRoot, file);
				var itemName = NormalizeItemName(relativePath);
				if (string.IsNullOrWhiteSpace(itemName))
				{
					continue;
				}

				ItemDefinitionEntry? entry = null;
				try
				{
					using var stream = File.OpenRead(file);
					using var document =
						JsonDocument.Parse(stream,
							new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 8192 });
					var tintMap = new Dictionary<int, ItemRegistry.ItemTintInfo>();
					ExtractTintInfoFromDefinition(document.RootElement, tintMap);
					var selector = ItemModelSelectorParser.ParseFromRoot(document.RootElement);
					var modelReference = ResolveModelReferenceFromItemDefinition(document.RootElement);
					entry = new ItemDefinitionEntry(itemName, modelReference, tintMap, selector);
				}
				catch (JsonException)
				{
					continue;
				}
				catch (Exception)
				{
					continue;
				}

				yield return entry;
			}
		}
	}

	private static string NormalizeBlockStateName(string relativePath)
	{
		var normalized = relativePath.Replace('\\', '/');
		if (normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[..^5];
		}

		return normalized.Trim('/');
	}

	private static string NormalizeItemName(string relativePath)
	{
		var normalized = relativePath.Replace('\\', '/');
		if (normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[..^5];
		}

		return normalized.Trim('/');
	}

	private static string? ResolveDefaultModel(string blockName, JsonElement root,
		IReadOnlyDictionary<string, BlockModelDefinition> models)
	{
		if (root.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Object)
		{
			var resolved = ResolveFromVariants(variants, models);
			if (!string.IsNullOrWhiteSpace(resolved))
			{
				return resolved;
			}
		}

		if (root.TryGetProperty("multipart", out var multipart) && multipart.ValueKind == JsonValueKind.Array)
		{
			var resolved = ResolveFromMultipart(blockName, multipart, models);
			if (!string.IsNullOrWhiteSpace(resolved))
			{
				return resolved;
			}
		}

		if (ModelExists($"minecraft:block/{blockName}", models, out var normalized))
		{
			return FormatModelReference($"minecraft:block/{blockName}", normalized);
		}

		if (ModelExists(blockName, models, out normalized))
		{
			return FormatModelReference(null, normalized);
		}

		return null;
	}

	private static string? ResolveFromVariants(JsonElement variants,
		IReadOnlyDictionary<string, BlockModelDefinition> models)
	{
		foreach (var key in PreferredVariantKeys)
		{
			if (variants.TryGetProperty(key, out var variantElement))
			{
				var modelRef = ExtractModelReference(variantElement);
				if (ModelExists(modelRef, models, out var normalized))
				{
					return FormatModelReference(modelRef, normalized);
				}
			}
		}

		foreach (var property in variants.EnumerateObject())
		{
			var modelRef = ExtractModelReference(property.Value);
			if (ModelExists(modelRef, models, out var normalized))
			{
				return FormatModelReference(modelRef, normalized);
			}
		}

		return null;
	}

	private static string? ResolveFromMultipart(string blockName, JsonElement multipart,
		IReadOnlyDictionary<string, BlockModelDefinition> models)
	{
		foreach (var candidate in EnumerateMultipartCandidates(blockName))
		{
			if (ModelExists(candidate, models, out var normalized))
			{
				return FormatModelReference(candidate, normalized);
			}
		}

		foreach (var part in multipart.EnumerateArray())
		{
			if (!part.TryGetProperty("apply", out var applyElement))
			{
				continue;
			}

			var modelRef = ExtractModelReference(applyElement);
			if (ModelExists(modelRef, models, out var normalized))
			{
				return FormatModelReference(modelRef, normalized);
			}
		}

		return null;
	}

	private static IEnumerable<string> EnumerateMultipartCandidates(string blockName)
	{
		yield return $"minecraft:block/{blockName}_inventory";
		yield return $"minecraft:block/{blockName}_item";
		yield return $"minecraft:block/{blockName}";
		yield return $"minecraft:block/{blockName}_post";
		yield return $"minecraft:block/{blockName}_center";
		yield return $"minecraft:block/{blockName}_side";
		yield return $"minecraft:block/{blockName}_floor";
		yield return $"minecraft:block/{blockName}_top";
		yield return $"minecraft:block/{blockName}_bottom";
	}

	private static bool ModelExists(string? reference, IReadOnlyDictionary<string, BlockModelDefinition> models,
		out string normalized)
	{
		normalized = NormalizeModelReference(reference);
		return !string.IsNullOrWhiteSpace(normalized) && models.ContainsKey(normalized);
	}

	private static string FormatModelReference(string? originalReference, string normalized)
	{
		if (!string.IsNullOrWhiteSpace(originalReference))
		{
			return originalReference;
		}

		if (normalized.StartsWith("item/", StringComparison.OrdinalIgnoreCase))
		{
			return "minecraft:" + normalized;
		}

		if (normalized.StartsWith("builtin/", StringComparison.OrdinalIgnoreCase))
		{
			return normalized;
		}

		return $"minecraft:block/{normalized}";
	}

	private static string NormalizeModelReference(string? reference)
	{
		if (string.IsNullOrWhiteSpace(reference))
		{
			return string.Empty;
		}

		var normalized = reference.Trim();
		if (normalized.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[10..];
		}

		normalized = normalized.Replace('\\', '/').TrimStart('/');

		if (normalized.StartsWith("block/", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[6..];
		}
		else if (normalized.StartsWith("blocks/", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[7..];
		}

		return normalized;
	}

	private static string? ExtractModelReference(JsonElement element)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				if (element.TryGetProperty("model", out var modelProperty))
				{
					if (modelProperty.ValueKind == JsonValueKind.String)
					{
						return modelProperty.GetString();
					}

					var nested = ExtractModelReference(modelProperty);
					if (!string.IsNullOrWhiteSpace(nested))
					{
						return nested;
					}
				}

				if (element.TryGetProperty("base", out var baseProperty) &&
				    baseProperty.ValueKind == JsonValueKind.String)
				{
					return baseProperty.GetString();
				}

				break;
			case JsonValueKind.Array:
				foreach (var entry in element.EnumerateArray())
				{
					var candidate = ExtractModelReference(entry);
					if (!string.IsNullOrWhiteSpace(candidate))
					{
						return candidate;
					}
				}

				break;
		}

		return null;
	}

	private static void ExtractTintInfoFromDefinition(JsonElement element,
		Dictionary<int, ItemRegistry.ItemTintInfo> target)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
			{
				if (element.TryGetProperty("tints", out var tintsProperty) &&
				    tintsProperty.ValueKind == JsonValueKind.Array)
				{
					var index = 0;
					foreach (var tintElement in tintsProperty.EnumerateArray())
					{
						if (!target.ContainsKey(index))
						{
							var tintInfo = CreateTintInfo(tintElement);
							if (tintInfo is not null)
							{
								target[index] = tintInfo;
							}
						}

						index++;
					}
				}

				foreach (var property in element.EnumerateObject())
				{
					if (string.Equals(property.Name, "tints", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					ExtractTintInfoFromDefinition(property.Value, target);
				}

				break;
			}
			case JsonValueKind.Array:
				foreach (var entry in element.EnumerateArray())
				{
					ExtractTintInfoFromDefinition(entry, target);
				}

				break;
		}
	}

	private static ItemRegistry.ItemTintInfo? CreateTintInfo(JsonElement tintElement)
	{
		if (tintElement.ValueKind != JsonValueKind.Object)
		{
			return null;
		}

		var tintInfo = new ItemRegistry.ItemTintInfo();
		if (tintElement.TryGetProperty("type", out var typeProperty) && typeProperty.ValueKind == JsonValueKind.String)
		{
			switch (typeProperty.GetString())
			{
				case "minecraft:dye":
					tintInfo.Kind = ItemRegistry.ItemTintKind.Dye;
					break;
				case "minecraft:constant":
					tintInfo.Kind = ItemRegistry.ItemTintKind.Constant;
					break;
				case null:
					tintInfo.Kind = ItemRegistry.ItemTintKind.Unspecified;
					break;
				default:
					tintInfo.Kind = ItemRegistry.ItemTintKind.Unknown;
					break;
			}
		}

		tintInfo.DefaultColor = tintInfo.Kind switch
		{
			ItemRegistry.ItemTintKind.Dye => TryReadColor(tintElement, "default"),
			ItemRegistry.ItemTintKind.Constant => TryReadColor(tintElement, "value"),
			_ => TryReadColor(tintElement, "default") ?? TryReadColor(tintElement, "value")
		};

		return tintInfo;
	}

	private static Color? TryReadColor(JsonElement element, string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out var property))
		{
			return null;
		}

		return property.ValueKind switch
		{
			JsonValueKind.Number => ConvertIntToColor(property.GetInt32()),
			JsonValueKind.String => ParseColorString(property.GetString()),
			_ => null
		};
	}

	private static Color? ParseColorString(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		var text = value.Trim();
		if (text.StartsWith("#", StringComparison.Ordinal))
		{
			text = text[1..];
		}
		else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			text = text[2..];
		}

		if (uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
			    System.Globalization.CultureInfo.InvariantCulture, out var hex))
		{
			var intValue = unchecked((int)hex);
			return ConvertIntToColor(intValue);
		}

		return null;
	}

	private static Color ConvertIntToColor(int argb)
	{
		var raw = unchecked((uint)argb);
		var a = (byte)((raw >> 24) & 0xFF);
		var r = (byte)((raw >> 16) & 0xFF);
		var g = (byte)((raw >> 8) & 0xFF);
		var b = (byte)(raw & 0xFF);

		if (a == 0 && raw != 0)
		{
			a = 0xFF;
		}

		return new Color(new Rgba32(r, g, b, a == 0 ? (byte)0xFF : a));
	}

	private static string? ResolveModelReferenceFromItemDefinition(JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object)
		{
			return null;
		}

		if (root.TryGetProperty("model", out var modelElement))
		{
			var reference = ExtractModelReference(modelElement);
			if (!string.IsNullOrWhiteSpace(reference))
			{
				return reference;
			}
		}

		if (root.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Object)
		{
			if (components.TryGetProperty("minecraft:model", out var componentModel))
			{
				var reference = ExtractModelReference(componentModel);
				if (!string.IsNullOrWhiteSpace(reference))
				{
					return reference;
				}
			}
		}

		return null;
	}

	private static string? ResolveRepresentativeTexture(string? modelReference,
		IReadOnlyDictionary<string, BlockModelDefinition> models)
	{
		if (!ModelExists(modelReference, models, out var normalized))
		{
			return null;
		}

		if (!models.TryGetValue(normalized, out var definition))
		{
			return null;
		}

		return ResolvePrimaryTexture(definition, models);
	}

	private static string? ResolvePrimaryTexture(BlockModelDefinition definition,
		IReadOnlyDictionary<string, BlockModelDefinition> models)
		=> ResolvePrimaryTexture(definition, models, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

	private static string? ResolvePrimaryTexture(BlockModelDefinition definition,
		IReadOnlyDictionary<string, BlockModelDefinition> models, HashSet<string> visited)
	{
		if (definition.Textures is { Count: > 0 })
		{
			foreach (var key in TexturePreferenceOrder)
			{
				if (definition.Textures.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
				{
					return value;
				}
			}

			foreach (var value in definition.Textures.Values)
			{
				if (!string.IsNullOrWhiteSpace(value))
				{
					return value;
				}
			}
		}

		if (!string.IsNullOrWhiteSpace(definition.Parent))
		{
			var parentKey = NormalizeModelReference(definition.Parent);
			if (!string.IsNullOrWhiteSpace(parentKey) && visited.Add(parentKey) &&
			    models.TryGetValue(parentKey, out var parentDefinition))
			{
				return ResolvePrimaryTexture(parentDefinition, models, visited);
			}
		}

		return null;
	}

	private static bool IsTemplateItem(string itemName)
	{
		if (itemName.StartsWith("template_", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		return itemName.Equals("generated", StringComparison.OrdinalIgnoreCase)
		       || itemName.Equals("handheld", StringComparison.OrdinalIgnoreCase)
		       || itemName.Equals("handheld_rod", StringComparison.OrdinalIgnoreCase)
		       || itemName.Equals("handheld_mace", StringComparison.OrdinalIgnoreCase);
	}
}