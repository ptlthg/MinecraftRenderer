namespace MinecraftRenderer.TexturePacks;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

/// <summary>
/// Parses catharsis pack configuration and evaluates fabric overlay conditions
/// from a <c>pack.mcmeta</c> to determine which overlay directories should be enabled.
/// </summary>
public static class CatharsisPackConfig
{
	private readonly record struct ConfigValue(IReadOnlyList<string> Values, bool? BooleanValue = null)
	{
		public static ConfigValue Boolean(bool value) => new([value ? "true" : "false"], value);

		public static ConfigValue Single(string value) {
			var normalized = value.Trim();
			if (bool.TryParse(normalized, out var boolValue)) {
				return Boolean(boolValue);
			}

			return new ConfigValue([normalized]);
		}

		public static ConfigValue Many(IEnumerable<string> values)
			=> new(values.Where(static value => !string.IsNullOrWhiteSpace(value))
				.Select(static value => value.Trim())
				.ToArray());

		public bool Matches(string? requiredValue) {
			if (requiredValue is null) {
				return BooleanValue == true;
			}

			return Values.Any(value => string.Equals(value, requiredValue, StringComparison.OrdinalIgnoreCase));
		}
	}

	private readonly record struct IntRange(int MinInclusive, int MaxInclusive)
	{
		public bool Intersects(IntRange other)
			=> MinInclusive <= other.MaxInclusive && other.MinInclusive <= MaxInclusive;
	}

	/// <summary>
	/// Parses the <c>pack.mcmeta</c> JSON from a catharsis pack and returns the list of
	/// overlay directory names that should be enabled based on their default config values.
	/// </summary>
	/// <param name="packMcmetaJson">The raw JSON string of <c>pack.mcmeta</c>.</param>
	/// <param name="configJson">Optional raw JSON string of <c>config.catharsis.json</c>.</param>
	/// <param name="enableAll">When <c>true</c>, returns all overlay directories regardless of conditions.</param>
	/// <returns>A list of overlay directory names to enable, or an empty list if parsing fails.</returns>
	public static IReadOnlyList<string> ResolveEnabledOverlays(string packMcmetaJson,
		string? configJson, bool enableAll = false)
		=> ResolveEnabledOverlays(packMcmetaJson, configJson, overrides: null, enableAll);

	/// <summary>
	/// Parses the <c>pack.mcmeta</c> JSON from a catharsis pack and returns the list of
	/// overlay directory names that should be enabled based on their default config values.
	/// </summary>
	/// <param name="packMcmetaJson">The raw JSON string of <c>pack.mcmeta</c>.</param>
	/// <param name="enableAll">When <c>true</c>, returns all overlay directories regardless of conditions.</param>
	/// <returns>A list of overlay directory names to enable, or an empty list if parsing fails.</returns>
	public static IReadOnlyList<string> ResolveEnabledOverlays(string packMcmetaJson, bool enableAll = false)
		=> ResolveEnabledOverlays(packMcmetaJson, configJson: null, overrides: null, enableAll);

	/// <summary>
	/// Parses the <c>pack.mcmeta</c> JSON from a catharsis pack and returns the list of
	/// overlay directory names that should be enabled based on merged default and override values.
	/// </summary>
	/// <param name="packMcmetaJson">The raw JSON string of <c>pack.mcmeta</c>.</param>
	/// <param name="overrides">Optional config overrides that replace the pack defaults before evaluating overlays.</param>
	/// <param name="enableAll">When <c>true</c>, returns all overlay directories regardless of conditions.</param>
	/// <returns>A list of overlay directory names to enable, or an empty list if parsing fails.</returns>
	public static IReadOnlyList<string> ResolveEnabledOverlays(string packMcmetaJson,
		IReadOnlyDictionary<string, string>? overrides, bool enableAll = false)
		=> ResolveEnabledOverlays(packMcmetaJson, configJson: null, overrides, enableAll);

	/// <summary>
	/// Parses the <c>pack.mcmeta</c> JSON and optional <c>config.catharsis.json</c> from a catharsis pack
	/// and returns the list of overlay directory names that should be enabled based on merged default and override values.
	/// </summary>
	/// <param name="packMcmetaJson">The raw JSON string of <c>pack.mcmeta</c>.</param>
	/// <param name="configJson">Optional raw JSON string of <c>config.catharsis.json</c>.</param>
	/// <param name="overrides">Optional config overrides that replace the pack defaults before evaluating overlays.</param>
	/// <param name="enableAll">When <c>true</c>, returns all overlay directories regardless of conditions.</param>
	/// <returns>A list of overlay directory names to enable, or an empty list if parsing fails.</returns>
	public static IReadOnlyList<string> ResolveEnabledOverlays(string packMcmetaJson,
		string? configJson, IReadOnlyDictionary<string, string>? overrides, bool enableAll = false) {
		if (string.IsNullOrWhiteSpace(packMcmetaJson)) {
			return [];
		}

		try {
			using var document = JsonDocument.Parse(packMcmetaJson, new JsonDocumentOptions {
				CommentHandling = JsonCommentHandling.Skip
			});

			var root = document.RootElement;

			// Catharsis prefers config.catharsis.json when present, and falls back to catharsis:pack/v1.config.
			var defaults = ParseConfigDefaults(root, configJson);
			ApplyOverrides(defaults, overrides);

			// Evaluate fabric:overlays entries
			return EvaluateOverlayEntries(root, defaults, enableAll);
		}
		catch (JsonException) {
			return [];
		}
	}

	private static void ApplyOverrides(Dictionary<string, ConfigValue> defaults,
		IReadOnlyDictionary<string, string>? overrides) {
		if (overrides is null || overrides.Count == 0) {
			return;
		}

		foreach (var (id, value) in overrides) {
			if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(value)) {
				continue;
			}

			defaults[id] = ConfigValue.Single(value);
		}
	}

	/// <summary>
	/// Parses the Catharsis config section to build a map of option id to default value.
	/// Boolean options keep boolean semantics, dropdown/color options keep one scalar value, and select options keep all selected values.
	/// </summary>
	private static Dictionary<string, ConfigValue> ParseConfigDefaults(JsonElement root, string? configJson) {
		var defaults = new Dictionary<string, ConfigValue>(StringComparer.OrdinalIgnoreCase);

		if (!string.IsNullOrWhiteSpace(configJson)) {
			if (TryParseConfigOptionsJson(configJson, defaults)) {
				return defaults;
			}
		}

		if (!root.TryGetProperty("catharsis:pack/v1", out var catharsisSection)) {
			return defaults;
		}

		if (!catharsisSection.TryGetProperty("config", out var configArray) ||
		    configArray.ValueKind != JsonValueKind.Array) {
			return defaults;
		}

		foreach (var entry in configArray.EnumerateArray()) {
			if (!entry.TryGetProperty("type", out var typeElement)) {
				continue;
			}

			var type = typeElement.GetString();

			if (string.Equals(type, "tab", StringComparison.OrdinalIgnoreCase)) {
				// Tabs contain nested options
				if (entry.TryGetProperty("options", out var optionsArray) &&
				    optionsArray.ValueKind == JsonValueKind.Array) {
					ParseOptionsArray(optionsArray, defaults);
				}
			}
			else {
				// Top-level option (outside a tab)
				ParseSingleOption(entry, defaults);
			}
		}

		return defaults;
	}

	private static bool TryParseConfigOptionsJson(string configJson, Dictionary<string, ConfigValue> defaults) {
		try {
			using var document = JsonDocument.Parse(configJson, new JsonDocumentOptions {
				CommentHandling = JsonCommentHandling.Skip
			});

			if (document.RootElement.ValueKind != JsonValueKind.Array) {
				return false;
			}

			ParseOptionsArray(document.RootElement, defaults);
			return true;
		}
		catch (JsonException) {
			return false;
		}
	}

	private static void ParseOptionsArray(JsonElement optionsArray, Dictionary<string, ConfigValue> defaults) {
		foreach (var option in optionsArray.EnumerateArray()) {
			if (!option.TryGetProperty("type", out var typeElement)) {
				continue;
			}

			var type = typeElement.GetString();
			if (string.Equals(type, "tab", StringComparison.OrdinalIgnoreCase)) {
				if (option.TryGetProperty("options", out var nestedOptions) &&
				    nestedOptions.ValueKind == JsonValueKind.Array) {
					ParseOptionsArray(nestedOptions, defaults);
				}

				continue;
			}

			ParseSingleOption(option, defaults);
		}
	}

	private static void ParseSingleOption(JsonElement option, Dictionary<string, ConfigValue> defaults) {
		if (!option.TryGetProperty("type", out var typeElement)) {
			return;
		}

		var type = typeElement.GetString();
		if (type is null) {
			return;
		}

		if (!option.TryGetProperty("id", out var idElement)) {
			return; // separators and other non-value types have no id
		}

		var id = idElement.GetString();
		if (string.IsNullOrWhiteSpace(id)) {
			return;
		}

		if (string.Equals(type, "boolean", StringComparison.OrdinalIgnoreCase)) {
			var defaultValue = false;
			if (option.TryGetProperty("default", out var defaultElement) &&
			    defaultElement.ValueKind == JsonValueKind.True) {
				defaultValue = true;
			}

			defaults[id] = ConfigValue.Boolean(defaultValue);
		}
		else if (string.Equals(type, "dropdown", StringComparison.OrdinalIgnoreCase)) {
			// Find the option with default: true
			if (option.TryGetProperty("options", out var dropdownOptions) &&
			    dropdownOptions.ValueKind == JsonValueKind.Array) {
				string? defaultValue = null;
				string? firstValue = null;

				foreach (var dropdownOption in dropdownOptions.EnumerateArray()) {
					var value = dropdownOption.TryGetProperty("value", out var valElement) &&
					            TryReadScalar(valElement, out var scalarValue)
						? scalarValue
						: null;

					firstValue ??= value;

					if (dropdownOption.TryGetProperty("default", out var defElement) &&
					    defElement.ValueKind == JsonValueKind.True) {
						defaultValue = value;
						break;
					}
				}

				defaults[id] = ConfigValue.Single(defaultValue ?? firstValue ?? "off");
			}
		}
		else if (string.Equals(type, "select", StringComparison.OrdinalIgnoreCase)) {
			var selected = new List<string>();
			if (option.TryGetProperty("options", out var selectOptions) &&
			    selectOptions.ValueKind == JsonValueKind.Array) {
				foreach (var selectOption in selectOptions.EnumerateArray()) {
					if (!selectOption.TryGetProperty("selected", out var selectedElement) ||
					    selectedElement.ValueKind != JsonValueKind.True) {
						continue;
					}

					if (selectOption.TryGetProperty("value", out var valueElement) &&
					    TryReadScalar(valueElement, out var scalarValue)) {
						selected.Add(scalarValue);
					}
				}
			}

			defaults[id] = ConfigValue.Many(selected);
		}
		else if (string.Equals(type, "color", StringComparison.OrdinalIgnoreCase)) {
			if (option.TryGetProperty("default", out var defaultElement) &&
			    TryReadScalar(defaultElement, out var defaultValue)) {
				defaults[id] = ConfigValue.Single(defaultValue);
			}
		}
	}

	/// <summary>
	/// Evaluates the <c>fabric:overlays.entries</c> array and returns directory names
	/// whose conditions evaluate to true given the config defaults.
	/// </summary>
	private static IReadOnlyList<string> EvaluateOverlayEntries(JsonElement root,
		Dictionary<string, ConfigValue> defaults, bool enableAll = false) {
		if (!root.TryGetProperty("fabric:overlays", out var overlaysSection)) {
			return [];
		}

		if (!overlaysSection.TryGetProperty("entries", out var entriesArray) ||
		    entriesArray.ValueKind != JsonValueKind.Array) {
			return [];
		}

		var enabled = new List<string>();

		foreach (var entry in entriesArray.EnumerateArray()) {
			if (!entry.TryGetProperty("directory", out var dirElement)) {
				continue;
			}

			var directory = dirElement.GetString();
			if (string.IsNullOrWhiteSpace(directory)) {
				continue;
			}

			if (enableAll) {
				enabled.Add(directory);
				continue;
			}

			if (!entry.TryGetProperty("condition", out var condition)) {
				// No condition → always enabled
				enabled.Add(directory);
				continue;
			}

			if (EvaluateCondition(condition, defaults, root)) {
				enabled.Add(directory);
			}
		}

		return enabled;
	}

	/// <summary>
	/// Evaluates a single overlay condition against config defaults.
	/// </summary>
	private static bool EvaluateCondition(JsonElement condition, Dictionary<string, ConfigValue> defaults,
		JsonElement root) {
		if (!condition.TryGetProperty("condition", out var conditionType)) {
			return false;
		}

		var type = conditionType.GetString();

		if (string.Equals(type, "catharsis:config", StringComparison.OrdinalIgnoreCase)) {
			return EvaluateCatharsisConfigCondition(condition, defaults);
		}

		if (string.Equals(type, "catharsis:version", StringComparison.OrdinalIgnoreCase)) {
			return EvaluateCatharsisVersionCondition(condition, root);
		}

		if (string.Equals(type, "fabric:not", StringComparison.OrdinalIgnoreCase)) {
			if (condition.TryGetProperty("value", out var inner)) {
				return !EvaluateCondition(inner, defaults, root);
			}

			return false;
		}

		if (string.Equals(type, "fabric:all_of", StringComparison.OrdinalIgnoreCase) ||
		    string.Equals(type, "fabric:and", StringComparison.OrdinalIgnoreCase)) {
			if (condition.TryGetProperty("values", out var valuesArray) &&
			    valuesArray.ValueKind == JsonValueKind.Array) {
				foreach (var inner in valuesArray.EnumerateArray()) {
					if (!EvaluateCondition(inner, defaults, root)) {
						return false;
					}
				}

				return true;
			}

			return false;
		}

		if (string.Equals(type, "fabric:any_of", StringComparison.OrdinalIgnoreCase) ||
		    string.Equals(type, "fabric:or", StringComparison.OrdinalIgnoreCase)) {
			if (condition.TryGetProperty("values", out var valuesArray) &&
			    valuesArray.ValueKind == JsonValueKind.Array) {
				foreach (var inner in valuesArray.EnumerateArray()) {
					if (EvaluateCondition(inner, defaults, root)) {
						return true;
					}
				}
			}

			return false;
		}

		// Unknown condition type — conservatively return false
		return false;
	}

	/// <summary>
	/// Evaluates a <c>catharsis:config</c> condition.
	/// If <c>value</c> is specified, checks if the config option equals that value.
	/// Otherwise, checks if the boolean config option is true.
	/// </summary>
	private static bool EvaluateCatharsisConfigCondition(JsonElement condition,
		Dictionary<string, ConfigValue> defaults) {
		if (!condition.TryGetProperty("id", out var idElement)) {
			return false;
		}

		var id = idElement.GetString();
		if (string.IsNullOrWhiteSpace(id)) {
			return false;
		}

		// Check if a specific value match is required
		if (condition.TryGetProperty("value", out var valueElement)) {
			if (!TryReadScalar(valueElement, out var requiredValue)) {
				return false;
			}

			if (!defaults.TryGetValue(id, out var currentValue)) {
				return false;
			}

			return currentValue.Matches(requiredValue);
		}

		if (!defaults.TryGetValue(id, out var value)) {
			return false; // Option not found → treated as disabled
		}

		return value.Matches(requiredValue: null);
	}

	private static bool EvaluateCatharsisVersionCondition(JsonElement condition, JsonElement root) {
		var type = condition.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
			? NormalizeVersionType(typeElement.GetString())
			: "minecraft";

		if (!string.Equals(type, "packformat", StringComparison.OrdinalIgnoreCase)) {
			// The renderer does not know the active Minecraft runtime version; avoid enabling
			// Minecraft-version-specific overlays unless they also use a pack format condition.
			return false;
		}

		if (!condition.TryGetProperty("packFormatRange", out var rangeElement) ||
		    !TryParseRange(rangeElement, out var requiredRange)) {
			return true;
		}

		var declaredRange = ParseDeclaredPackFormatRange(root);
		return declaredRange is not null && declaredRange.Value.Intersects(requiredRange);
	}

	private static IntRange? ParseDeclaredPackFormatRange(JsonElement root) {
		if (!root.TryGetProperty("pack", out var packElement) || packElement.ValueKind != JsonValueKind.Object) {
			return null;
		}

		if (packElement.TryGetProperty("pack_format", out var packFormatElement) &&
		    TryReadInt(packFormatElement, out var packFormat)) {
			return new IntRange(packFormat, packFormat);
		}

		var minFormat = 0;
		var maxFormat = 0;
		var hasMin = packElement.TryGetProperty("min_format", out var minElement) &&
		             TryReadInt(minElement, out minFormat);
		var hasMax = packElement.TryGetProperty("max_format", out var maxElement) &&
		             TryReadInt(maxElement, out maxFormat);

		return (hasMin, hasMax) switch {
			(true, true) => new IntRange(Math.Min(minFormat, maxFormat), Math.Max(minFormat, maxFormat)),
			(true, false) => new IntRange(minFormat, minFormat),
			(false, true) => new IntRange(maxFormat, maxFormat),
			_ => null
		};
	}

	private static bool TryParseRange(JsonElement element, out IntRange range) {
		range = default;
		if (element.ValueKind != JsonValueKind.Object) {
			return false;
		}

		var minInclusive = 0;
		var maxInclusive = 0;
		var hasMin = element.TryGetProperty("min_inclusive", out var minElement) &&
		             TryReadInt(minElement, out minInclusive);
		var hasMax = element.TryGetProperty("max_inclusive", out var maxElement) &&
		             TryReadInt(maxElement, out maxInclusive);
		if (!hasMin && !hasMax) {
			return false;
		}

		var min = hasMin ? minInclusive : int.MinValue;
		var max = hasMax ? maxInclusive : int.MaxValue;
		range = new IntRange(Math.Min(min, max), Math.Max(min, max));
		return true;
	}

	private static bool TryReadScalar(JsonElement element, out string value) {
		switch (element.ValueKind) {
			case JsonValueKind.String:
				value = element.GetString() ?? string.Empty;
				return !string.IsNullOrWhiteSpace(value);
			case JsonValueKind.Number:
				if (element.TryGetInt64(out var longValue)) {
					value = longValue.ToString(CultureInfo.InvariantCulture);
					return true;
				}

				if (element.TryGetDouble(out var doubleValue)) {
					value = doubleValue.ToString(CultureInfo.InvariantCulture);
					return true;
				}

				break;
			case JsonValueKind.True:
				value = "true";
				return true;
			case JsonValueKind.False:
				value = "false";
				return true;
		}

		value = string.Empty;
		return false;
	}

	private static bool TryReadInt(JsonElement element, out int value) {
		if (element.ValueKind == JsonValueKind.Number) {
			if (element.TryGetInt32(out value)) {
				return true;
			}

			if (element.TryGetDouble(out var doubleValue) &&
			    doubleValue >= int.MinValue &&
			    doubleValue <= int.MaxValue &&
			    Math.Abs(doubleValue - Math.Round(doubleValue)) < 1e-6) {
				value = (int)Math.Round(doubleValue);
				return true;
			}
		}

		if (element.ValueKind == JsonValueKind.String &&
		    int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) {
			return true;
		}

		value = default;
		return false;
	}

	private static string NormalizeVersionType(string? value)
		=> string.IsNullOrWhiteSpace(value)
			? string.Empty
			: value.Replace("_", string.Empty, StringComparison.Ordinal)
				.Replace("-", string.Empty, StringComparison.Ordinal)
				.Trim();
}
