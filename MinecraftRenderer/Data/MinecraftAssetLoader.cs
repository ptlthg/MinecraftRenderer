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

	public static Dictionary<string, BlockModelDefinition> LoadModelDefinitions(string assetsRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(assetsRoot);

		var modelsRoot = Path.Combine(assetsRoot, "models");
		if (!Directory.Exists(modelsRoot))
		{
			throw new DirectoryNotFoundException($"Models directory not found at '{modelsRoot}'.");
		}

		var definitions = new Dictionary<string, BlockModelDefinition>(StringComparer.OrdinalIgnoreCase);

		foreach (var file in Directory.EnumerateFiles(modelsRoot, "*.json", SearchOption.AllDirectories))
		{
			var relativePath = Path.GetRelativePath(modelsRoot, file);
			var key = NormalizeModelKey(relativePath);
			if (string.IsNullOrWhiteSpace(key))
			{
				continue;
			}

			var json = File.ReadAllText(file);
			var definition = JsonSerializer.Deserialize<BlockModelDefinition>(json, SerializerOptions) ?? new BlockModelDefinition();
			definitions[key] = definition;
		}

		foreach (var (key, definition) in GetBuiltinModelDefinitions())
		{
			definitions.TryAdd(key, definition);
		}

		return definitions;
	}

	public static List<BlockRegistry.BlockInfo> LoadBlockInfos(string assetsRoot, IReadOnlyDictionary<string, BlockModelDefinition> models)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(assetsRoot);
		ArgumentNullException.ThrowIfNull(models);

		var blockstatesRoot = Path.Combine(assetsRoot, "blockstates");
		if (!Directory.Exists(blockstatesRoot))
		{
			throw new DirectoryNotFoundException($"Blockstates directory not found at '{blockstatesRoot}'.");
		}

		var entries = new List<BlockRegistry.BlockInfo>();
		foreach (var file in Directory.EnumerateFiles(blockstatesRoot, "*.json", SearchOption.AllDirectories))
		{
			var relativePath = Path.GetRelativePath(blockstatesRoot, file);
			var blockName = NormalizeBlockStateName(relativePath);

			using var stream = File.OpenRead(file);
			using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true });

			var modelReference = ResolveDefaultModel(blockName, document.RootElement, models);
			var textureReference = ResolveRepresentativeTexture(modelReference, models);

			entries.Add(new BlockRegistry.BlockInfo
			{
				Name = blockName,
				BlockState = blockName,
				Model = modelReference,
				Texture = textureReference
			});
		}

		return entries;
	}

	public static List<ItemRegistry.ItemInfo> LoadItemInfos(string assetsRoot, IReadOnlyDictionary<string, BlockModelDefinition> models)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(assetsRoot);
		ArgumentNullException.ThrowIfNull(models);

		var results = new List<ItemRegistry.ItemInfo>();

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

			results.Add(new ItemRegistry.ItemInfo
			{
				Name = itemName,
				Model = key,
				Texture = texture
			});
		}

		return results;
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

	private static string NormalizeBlockStateName(string relativePath)
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
			return originalReference!;
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
			case JsonValueKind.Object when element.TryGetProperty("model", out var modelProperty) && modelProperty.ValueKind == JsonValueKind.String:
				return modelProperty.GetString();
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
