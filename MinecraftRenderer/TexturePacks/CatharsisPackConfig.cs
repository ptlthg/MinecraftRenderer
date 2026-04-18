namespace MinecraftRenderer.TexturePacks;

using System;
using System.Collections.Generic;
using System.Text.Json;

/// <summary>
/// Parses catharsis pack configuration and evaluates fabric overlay conditions
/// from a <c>pack.mcmeta</c> to determine which overlay directories should be enabled.
/// </summary>
public static class CatharsisPackConfig
{
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

	private static void ApplyOverrides(Dictionary<string, string> defaults,
		IReadOnlyDictionary<string, string>? overrides) {
		if (overrides is null || overrides.Count == 0) {
			return;
		}

		foreach (var (id, value) in overrides) {
			if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(value)) {
				continue;
			}

			defaults[id] = value;
		}
	}

	/// <summary>
	/// Parses the <c>catharsis:pack/v1.config</c> section to build a map of option id → default value.
	/// Boolean options map to "true"/"false", dropdown options map to the default option's value string.
	/// </summary>
	private static Dictionary<string, string> ParseConfigDefaults(JsonElement root, string? configJson) {
		var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

	private static bool TryParseConfigOptionsJson(string configJson, Dictionary<string, string> defaults) {
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

	private static void ParseOptionsArray(JsonElement optionsArray, Dictionary<string, string> defaults) {
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

	private static void ParseSingleOption(JsonElement option, Dictionary<string, string> defaults) {
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

			defaults[id] = defaultValue ? "true" : "false";
		}
		else if (string.Equals(type, "dropdown", StringComparison.OrdinalIgnoreCase)) {
			// Find the option with default: true
			if (option.TryGetProperty("options", out var dropdownOptions) &&
			    dropdownOptions.ValueKind == JsonValueKind.Array) {
				string? defaultValue = null;
				string? firstValue = null;

				foreach (var dropdownOption in dropdownOptions.EnumerateArray()) {
					var value = dropdownOption.TryGetProperty("value", out var valElement)
						? valElement.GetString()
						: null;

					firstValue ??= value;

					if (dropdownOption.TryGetProperty("default", out var defElement) &&
					    defElement.ValueKind == JsonValueKind.True) {
						defaultValue = value;
						break;
					}
				}

				defaults[id] = defaultValue ?? firstValue ?? "off";
			}
		}
	}

	/// <summary>
	/// Evaluates the <c>fabric:overlays.entries</c> array and returns directory names
	/// whose conditions evaluate to true given the config defaults.
	/// </summary>
	private static IReadOnlyList<string> EvaluateOverlayEntries(JsonElement root,
		Dictionary<string, string> defaults, bool enableAll = false) {
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

			if (EvaluateCondition(condition, defaults)) {
				enabled.Add(directory);
			}
		}

		return enabled;
	}

	/// <summary>
	/// Evaluates a single overlay condition against config defaults.
	/// </summary>
	private static bool EvaluateCondition(JsonElement condition, Dictionary<string, string> defaults) {
		if (!condition.TryGetProperty("condition", out var conditionType)) {
			return false;
		}

		var type = conditionType.GetString();

		if (string.Equals(type, "catharsis:config", StringComparison.OrdinalIgnoreCase)) {
			return EvaluateCatharsisConfigCondition(condition, defaults);
		}

		if (string.Equals(type, "fabric:not", StringComparison.OrdinalIgnoreCase)) {
			if (condition.TryGetProperty("value", out var inner)) {
				return !EvaluateCondition(inner, defaults);
			}

			return false;
		}

		if (string.Equals(type, "fabric:all_of", StringComparison.OrdinalIgnoreCase)) {
			if (condition.TryGetProperty("values", out var valuesArray) &&
			    valuesArray.ValueKind == JsonValueKind.Array) {
				foreach (var inner in valuesArray.EnumerateArray()) {
					if (!EvaluateCondition(inner, defaults)) {
						return false;
					}
				}

				return true;
			}

			return false;
		}

		if (string.Equals(type, "fabric:any_of", StringComparison.OrdinalIgnoreCase)) {
			if (condition.TryGetProperty("values", out var valuesArray) &&
			    valuesArray.ValueKind == JsonValueKind.Array) {
				foreach (var inner in valuesArray.EnumerateArray()) {
					if (EvaluateCondition(inner, defaults)) {
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
		Dictionary<string, string> defaults) {
		if (!condition.TryGetProperty("id", out var idElement)) {
			return false;
		}

		var id = idElement.GetString();
		if (string.IsNullOrWhiteSpace(id)) {
			return false;
		}

		// Check if a specific value match is required
		if (condition.TryGetProperty("value", out var valueElement)) {
			var requiredValue = valueElement.GetString();
			if (requiredValue is null) {
				return false;
			}

			if (!defaults.TryGetValue(id, out var currentValue)) {
				return false;
			}

			return string.Equals(currentValue, requiredValue, StringComparison.OrdinalIgnoreCase);
		}

		// Boolean check: config option must be "true"
		if (!defaults.TryGetValue(id, out var boolValue)) {
			return false; // Option not found → treated as disabled
		}

		return string.Equals(boolValue, "true", StringComparison.OrdinalIgnoreCase);
	}
}
