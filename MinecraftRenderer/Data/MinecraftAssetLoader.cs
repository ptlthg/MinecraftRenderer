namespace MinecraftRenderer;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

internal static class MinecraftAssetLoader
{
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
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

	public static Dictionary<string, BlockModelDefinition> LoadModelDefinitions(string assetsRoot, IEnumerable<string>? overlayRoots = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(assetsRoot);

		var roots = BuildRootList(assetsRoot, overlayRoots);
		var definitions = new Dictionary<string, BlockModelDefinition>(StringComparer.OrdinalIgnoreCase);

		var hasAnyModels = false;
		foreach (var root in roots)
		{
			foreach (var directory in EnumerateModelDirectories(root))
			{
				hasAnyModels = true;
				foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
				{
					var relativePath = Path.GetRelativePath(directory, file);
					var key = NormalizeModelKey(relativePath);
					if (string.IsNullOrWhiteSpace(key))
					{
						continue;
					}

					var json = File.ReadAllText(file);
					var definition = JsonSerializer.Deserialize<BlockModelDefinition>(json, SerializerOptions) ?? new BlockModelDefinition();
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

	public static List<BlockRegistry.BlockInfo> LoadBlockInfos(string assetsRoot, IReadOnlyDictionary<string, BlockModelDefinition> models, IEnumerable<string>? overlayRoots = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(assetsRoot);
		ArgumentNullException.ThrowIfNull(models);

		var roots = BuildRootList(assetsRoot, overlayRoots);
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
					using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true });

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

	public static List<ItemRegistry.ItemInfo> LoadItemInfos(string assetsRoot, IReadOnlyDictionary<string, BlockModelDefinition> models, IEnumerable<string>? overlayRoots = null)
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

		foreach (var (itemName, modelReference) in EnumerateItemDefinitions(assetsRoot, overlayRoots))
		{
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
		}

		return entries.Values.ToList();
	}

	private static string NormalizeModelKey(string relativePath)
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

		return normalized.TrimStart('/');
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

	private static IReadOnlyList<string> BuildRootList(string primaryRoot, IEnumerable<string>? overlayRoots)
	{
		var ordered = new List<string>();
		void TryAdd(string? candidate)
		{
			if (string.IsNullOrWhiteSpace(candidate))
			{
				return;
			}

			var full = Path.GetFullPath(candidate);
			if (!ordered.Contains(full, StringComparer.OrdinalIgnoreCase))
			{
				ordered.Add(full);
			}
		}

		TryAdd(primaryRoot);
		if (overlayRoots is not null)
		{
			foreach (var overlay in overlayRoots)
			{
				TryAdd(overlay);
			}
		}

		return ordered;
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

	private static IEnumerable<(string Name, string? ModelReference)> EnumerateItemDefinitions(string assetsRoot, IEnumerable<string>? overlayRoots)
	{
		var roots = BuildRootList(assetsRoot, overlayRoots);

		foreach (var root in roots)
		{
			var itemsRoot = Path.Combine(root, "items");
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

				string? modelReference;

				try
				{
					using var stream = File.OpenRead(file);
					using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true });
					modelReference = ResolveModelReferenceFromItemDefinition(document.RootElement);
				}
				catch (JsonException)
				{
					continue;
				}

				yield return (itemName, modelReference);
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

	private static string? ResolveDefaultModel(string blockName, JsonElement root, IReadOnlyDictionary<string, BlockModelDefinition> models)
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

	private static string? ResolveFromVariants(JsonElement variants, IReadOnlyDictionary<string, BlockModelDefinition> models)
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

	private static string? ResolveFromMultipart(string blockName, JsonElement multipart, IReadOnlyDictionary<string, BlockModelDefinition> models)
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

	private static bool ModelExists(string? reference, IReadOnlyDictionary<string, BlockModelDefinition> models, out string normalized)
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

				if (element.TryGetProperty("base", out var baseProperty) && baseProperty.ValueKind == JsonValueKind.String)
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

	private static string? ResolveRepresentativeTexture(string? modelReference, IReadOnlyDictionary<string, BlockModelDefinition> models)
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

	private static string? ResolvePrimaryTexture(BlockModelDefinition definition, IReadOnlyDictionary<string, BlockModelDefinition> models)
		=> ResolvePrimaryTexture(definition, models, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

	private static string? ResolvePrimaryTexture(BlockModelDefinition definition, IReadOnlyDictionary<string, BlockModelDefinition> models, HashSet<string> visited)
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
			if (!string.IsNullOrWhiteSpace(parentKey) && visited.Add(parentKey) && models.TryGetValue(parentKey, out var parentDefinition))
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
