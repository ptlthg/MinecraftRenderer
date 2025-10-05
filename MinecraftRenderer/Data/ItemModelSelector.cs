namespace MinecraftRenderer;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using MinecraftRenderer.Nbt;

internal readonly record struct ItemModelContext(
	MinecraftBlockRenderer.ItemRenderData? ItemData,
	string DisplayContext);

internal abstract class ItemModelSelector
{
	public abstract string? Resolve(ItemModelContext context);
}

internal sealed class ItemModelSelectorModel(string? model, string? baseModel) : ItemModelSelector
{
	public string? Model { get; } = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
	public string? BaseModel { get; } = string.IsNullOrWhiteSpace(baseModel) ? null : baseModel.Trim();

	public override string? Resolve(ItemModelContext context)
		=> Model ?? BaseModel;
}

internal sealed class ItemModelSelectorSpecial(string? baseModel, ItemModelSelector? nested) : ItemModelSelector
{
	public string? BaseModel { get; } = string.IsNullOrWhiteSpace(baseModel) ? null : baseModel.Trim();
	public ItemModelSelector? Nested { get; } = nested;

	public override string? Resolve(ItemModelContext context)
		=> Nested?.Resolve(context) ?? BaseModel;
}

internal sealed class ItemModelSelectorCondition(
	string property,
	string? predicate,
	string? component,
	IReadOnlyDictionary<string, string>? valueProperties,
	string? valueLiteral,
	ItemModelSelector? onTrue,
	ItemModelSelector? onFalse) : ItemModelSelector
{
	public string Property { get; } = property;
	public string? Predicate { get; } = predicate;
	public string? Component { get; } = component;
	public IReadOnlyDictionary<string, string>? ValueProperties { get; } = valueProperties;
	public string? ValueLiteral { get; } = valueLiteral;
	public ItemModelSelector? OnTrue { get; } = onTrue;
	public ItemModelSelector? OnFalse { get; } = onFalse;

	public override string? Resolve(ItemModelContext context)
		=> EvaluateCondition(context) ? OnTrue?.Resolve(context) : OnFalse?.Resolve(context);

	private bool EvaluateCondition(ItemModelContext context)
	{
		if (string.Equals(Property, "component", StringComparison.OrdinalIgnoreCase))
		{
			return EvaluateComponentCondition(context);
		}

		if (string.Equals(Property, "display_context", StringComparison.OrdinalIgnoreCase))
		{
			if (ValueProperties is not null && ValueProperties.Count > 0)
			{
				if (ValueProperties.TryGetValue("value", out var expected))
				{
					return string.Equals(expected, context.DisplayContext, StringComparison.OrdinalIgnoreCase);
				}

				if (ValueProperties.TryGetValue("equals", out expected))
				{
					return string.Equals(expected, context.DisplayContext, StringComparison.OrdinalIgnoreCase);
				}
			}

			if (!string.IsNullOrWhiteSpace(ValueLiteral))
			{
				return string.Equals(ValueLiteral, context.DisplayContext, StringComparison.OrdinalIgnoreCase);
			}

			return false;
		}

		if (string.Equals(Property, "selected", StringComparison.OrdinalIgnoreCase))
		{
			return false; //ValueLiteral is not null && string.Equals(ValueLiteral, "true", StringComparison.OrdinalIgnoreCase);
		}

		return false;
	}

	private bool EvaluateComponentCondition(ItemModelContext context)
	{
		var predicate = Predicate ?? string.Empty;
		if (string.Equals(predicate, "custom_data", StringComparison.OrdinalIgnoreCase))
		{
			return EvaluateCustomData(context);
		}

		return false;
	}

	private bool EvaluateCustomData(ItemModelContext context)
	{
		var customData = context.ItemData?.CustomData;
		if (customData is null)
		{
			return false;
		}

		if (ValueProperties is not null && ValueProperties.Count > 0)
		{
			foreach (var (key, expected) in ValueProperties)
			{
				if (!TryMatchCustomDataValue(customData, key, expected))
				{
					return false;
				}
			}

			return true;
		}

		if (!string.IsNullOrWhiteSpace(ValueLiteral))
		{
			var id = TryGetString(customData, "id");
			if (!string.IsNullOrWhiteSpace(id))
			{
				return string.Equals(id, ValueLiteral, StringComparison.OrdinalIgnoreCase);
			}
		}

		return false;
	}

	private static bool TryMatchCustomDataValue(NbtCompound compound, string key, string expected)
	{
		if (!compound.TryGetValue(key, out var tag))
		{
			return false;
		}

		if (IsJsonStructure(expected))
		{
			return TryMatchJsonStructure(tag, expected);
		}

		return MatchesPrimitiveValue(tag, expected);
	}

	private static string? TryGetString(NbtCompound compound, string key)
		=> compound.TryGetValue(key, out var tag) && tag is NbtString str && !string.IsNullOrWhiteSpace(str.Value)
			? str.Value
			: null;

	private static bool MatchesPrimitiveValue(NbtTag tag, string expected)
	{
		if (TryParseBoolean(expected, out var expectedBool))
		{
			return tag switch
			{
				NbtByte b => (b.Value != 0) == expectedBool,
				NbtShort s => (s.Value != 0) == expectedBool,
				NbtInt i => (i.Value != 0) == expectedBool,
				NbtLong l => (l.Value != 0) == expectedBool,
				NbtString s => bool.TryParse(s.Value, out var actual) && actual == expectedBool,
				_ => false
			};
		}

		return tag switch
		{
			NbtString s => string.Equals(s.Value, expected, StringComparison.Ordinal),
			NbtInt i => string.Equals(i.Value.ToString(CultureInfo.InvariantCulture), expected,
				StringComparison.Ordinal),
			NbtLong l => string.Equals(l.Value.ToString(CultureInfo.InvariantCulture), expected,
				StringComparison.Ordinal),
			NbtShort s16 => string.Equals(s16.Value.ToString(CultureInfo.InvariantCulture), expected,
				StringComparison.Ordinal),
			NbtByte b => string.Equals(b.Value.ToString(CultureInfo.InvariantCulture), expected,
				StringComparison.Ordinal),
			NbtDouble d => string.Equals(d.Value.ToString(CultureInfo.InvariantCulture), expected,
				StringComparison.Ordinal),
			NbtFloat f => string.Equals(f.Value.ToString(CultureInfo.InvariantCulture), expected,
				StringComparison.Ordinal),
			_ => false
		};
	}

	private static bool IsJsonStructure(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}

		var trimmed = value.TrimStart();
		return trimmed.StartsWith('{') || trimmed.StartsWith('[');
	}

	private static bool TryMatchJsonStructure(NbtTag tag, string json)
	{
		try
		{
			using var document = JsonDocument.Parse(json);
			return MatchTagWithJson(tag, document.RootElement);
		}
		catch (JsonException)
		{
			return false;
		}
	}

	private static bool MatchTagWithJson(NbtTag tag, JsonElement expected)
		=> expected.ValueKind switch
		{
			JsonValueKind.Object => tag is NbtCompound compound && MatchCompound(compound, expected),
			JsonValueKind.Array => MatchArray(tag, expected),
			JsonValueKind.String => tag switch
			{
				NbtString s => string.Equals(s.Value, expected.GetString(), StringComparison.Ordinal),
				_ => false
			},
			JsonValueKind.Number => MatchNumeric(tag, expected),
			JsonValueKind.True => MatchesPrimitiveValue(tag, "true"),
			JsonValueKind.False => MatchesPrimitiveValue(tag, "false"),
			JsonValueKind.Null => false,
			_ => false
		};

	private static bool MatchCompound(NbtCompound compound, JsonElement expected)
	{
		foreach (var property in expected.EnumerateObject())
		{
			if (!compound.TryGetValue(property.Name, out var child))
			{
				return false;
			}

			if (!MatchTagWithJson(child, property.Value))
			{
				return false;
			}
		}

		return true;
	}

	private static bool MatchArray(NbtTag tag, JsonElement expected)
	{
		return tag switch
		{
			NbtList list => MatchList(list, expected),
			NbtByteArray byteArray => MatchPrimitiveArray(byteArray.Values.Select(value => (double)value), expected),
			NbtIntArray intArray => MatchPrimitiveArray(intArray.Values.Select(value => (double)value), expected),
			NbtLongArray longArray => MatchPrimitiveArray(longArray.Values.Select(value => (double)value), expected),
			_ => false
		};
	}

	private static bool MatchList(NbtList list, JsonElement expected)
	{
		if (list.Count != expected.GetArrayLength())
		{
			return false;
		}

		var index = 0;
		foreach (var element in expected.EnumerateArray())
		{
			if (!MatchTagWithJson(list[index++], element))
			{
				return false;
			}
		}

		return true;
	}

	private static bool MatchPrimitiveArray(IEnumerable<double> actualValues, JsonElement expected)
	{
		var actualList = actualValues.ToList();
		if (actualList.Count != expected.GetArrayLength())
		{
			return false;
		}

		var index = 0;
		foreach (var element in expected.EnumerateArray())
		{
			if (!element.TryGetDouble(out var expectedValue))
			{
				return false;
			}

			if (!NumericEquals(actualList[index++], expectedValue))
			{
				return false;
			}
		}

		return true;
	}

	private static bool MatchNumeric(NbtTag tag, JsonElement expected)
		=> tag switch
		{
			NbtByte b => TryGetInt(expected, out var intValue) && intValue >= sbyte.MinValue &&
			             intValue <= sbyte.MaxValue
				? b.Value == intValue
				: TryGetDouble(expected, out var doubleValue) && NumericEquals(b.Value, doubleValue),
			NbtShort s => TryGetInt(expected, out var shortValue) && shortValue >= short.MinValue &&
			              shortValue <= short.MaxValue
				? s.Value == shortValue
				: TryGetDouble(expected, out var shortDouble) && NumericEquals(s.Value, shortDouble),
			NbtInt i => TryGetInt(expected, out var expectedInt)
				? i.Value == expectedInt
				: TryGetDouble(expected, out var intDouble) && NumericEquals(i.Value, intDouble),
			NbtLong l => TryGetLong(expected, out var expectedLong)
				? l.Value == expectedLong
				: TryGetDouble(expected, out var longDouble) && NumericEquals(l.Value, longDouble),
			NbtFloat f => TryGetDouble(expected, out var floatDouble) && NumericEquals(f.Value, floatDouble),
			NbtDouble d => TryGetDouble(expected, out var doubleValue) && NumericEquals(d.Value, doubleValue),
			_ => false
		};

	private static bool NumericEquals(double actual, double expected)
		=> Math.Abs(actual - expected) < 1e-6;

	private static bool TryParseBoolean(string value, out bool result)
	{
		if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
		{
			result = true;
			return true;
		}

		if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
		{
			result = false;
			return true;
		}

		result = default;
		return false;
	}

	private static bool TryGetInt(JsonElement element, out int value)
	{
		if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
		{
			return true;
		}

		if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var longValue) &&
		    longValue is >= int.MinValue and <= int.MaxValue)
		{
			value = (int)longValue;
			return true;
		}

		value = default;
		return false;
	}

	private static bool TryGetLong(JsonElement element, out long value)
	{
		if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value))
		{
			return true;
		}

		value = default;
		return false;
	}

	private static bool TryGetDouble(JsonElement element, out double value)
	{
		if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value))
		{
			return true;
		}

		value = default;
		return false;
	}
}

internal sealed record ItemModelSelectorSelectCase(IReadOnlyList<string> When, ItemModelSelector? Selector);

internal sealed class ItemModelSelectorSelect(
	string property,
	IReadOnlyList<ItemModelSelectorSelectCase> cases,
	ItemModelSelector? fallback) : ItemModelSelector
{
	public string Property { get; } = property;
	public IReadOnlyList<ItemModelSelectorSelectCase> Cases { get; } = cases;
	public ItemModelSelector? Fallback { get; } = fallback;

	public override string? Resolve(ItemModelContext context)
	{
		foreach (var selectCase in Cases)
		{
			if (Matches(selectCase.When, context))
			{
				var resolved = selectCase.Selector?.Resolve(context);
				if (!string.IsNullOrWhiteSpace(resolved))
				{
					return resolved;
				}
			}
		}

		return Fallback?.Resolve(context);
	}

	private bool Matches(IReadOnlyList<string> when, ItemModelContext context)
	{
		if (when.Count == 0)
		{
			return false;
		}

		if (string.Equals(Property, "display_context", StringComparison.OrdinalIgnoreCase))
		{
			return when.Any(value =>
				string.Equals(value, context.DisplayContext, StringComparison.OrdinalIgnoreCase));
		}

		return false;
	}
}

internal sealed class ItemModelSelectorEmpty : ItemModelSelector
{
	public override string? Resolve(ItemModelContext context) => null;
}

internal sealed class ItemModelSelectorRangeDispatch(
	string property,
	bool normalize,
	IReadOnlyList<RangeDispatchEntry> entries,
	ItemModelSelector? fallback) : ItemModelSelector
{
	public string Property { get; } = property;
	public bool Normalize { get; } = normalize;
	public IReadOnlyList<RangeDispatchEntry> Entries { get; } = entries;
	public ItemModelSelector? Fallback { get; } = fallback;

	public override string? Resolve(ItemModelContext context)
	{
		var value = GetPropertyValue(context);
		if (value is null)
		{
			return Fallback?.Resolve(context);
		}

		// Find the highest threshold that's <= value
		RangeDispatchEntry? matchedEntry = null;
		foreach (var entry in Entries)
		{
			if (value >= entry.Threshold)
			{
				if (matchedEntry is null || entry.Threshold > matchedEntry.Value.Threshold)
				{
					matchedEntry = entry;
				}
			}
		}

		if (matchedEntry is not null)
		{
			var resolved = matchedEntry.Value.Selector?.Resolve(context);
			if (!string.IsNullOrWhiteSpace(resolved))
			{
				return resolved;
			}
		}

		return Fallback?.Resolve(context);
	}

	private double? GetPropertyValue(ItemModelContext context)
	{
		// Currently only supporting "count" property
		if (string.Equals(Property, "count", StringComparison.OrdinalIgnoreCase))
		{
			// For now, return 1 since we don't have stack count in ItemRenderData
			// This is a limitation but allows the selector to work with fallback
			return 1.0;
		}

		return null;
	}
}

internal readonly record struct RangeDispatchEntry(double Threshold, ItemModelSelector? Selector);

/// <summary>
/// Optimized selector for deeply nested conditional trees (e.g., Hypixel+ player_head.json).
/// Pre-builds a lookup table for custom_data.id → model mappings to avoid stack overflow
/// and provide O(1) resolution for custom items.
/// </summary>
internal sealed class ItemModelSelectorOptimized : ItemModelSelector
{
	private readonly Dictionary<string, string> _customDataIdToModel;
	private readonly Dictionary<string, ItemModelSelector> _customDataIdToSelector;
	private readonly ItemModelSelector? _fallbackSelector;

	public ItemModelSelectorOptimized(
		Dictionary<string, string> customDataIdToModel,
		Dictionary<string, ItemModelSelector> customDataIdToSelector,
		ItemModelSelector? fallbackSelector)
	{
		_customDataIdToModel = customDataIdToModel;
		_customDataIdToSelector = customDataIdToSelector;
		_fallbackSelector = fallbackSelector;
	}

	public override string? Resolve(ItemModelContext context)
	{
		// Fast path: Check if we have a direct custom_data match
		if (context.ItemData?.CustomData is { } customData)
		{
			string? customDataKey = null;

			// Try "id" field first
			if (customData.TryGetValue("id", out var idTag) &&
			    idTag is NbtString idString &&
			    !string.IsNullOrWhiteSpace(idString.Value))
			{
				customDataKey = idString.Value;
			}
			// Try "model" field as fallback
			else if (customData.TryGetValue("model", out var modelTag) &&
			         modelTag is NbtString modelString &&
			         !string.IsNullOrWhiteSpace(modelString.Value))
			{
				customDataKey = modelString.Value;
			}

			if (customDataKey != null)
			{
				// Check simple model mapping first
				if (_customDataIdToModel.TryGetValue(customDataKey, out var model))
				{
					return model;
				}

				// Check complex selector mapping
				if (_customDataIdToSelector.TryGetValue(customDataKey, out var selector))
				{
					return selector.Resolve(context);
				}
			}
		}

		// Fallback to the original selector tree (for non-custom-data conditions)
		return _fallbackSelector?.Resolve(context);
	}

	public int CustomDataMappingCount => _customDataIdToModel.Count + _customDataIdToSelector.Count;
}

internal static class ItemModelSelectorParser
{
	private const int MaxRecursionDepth = 10000; // Increased to handle Hypixel+ player_head.json with 8000+ levels

	public static ItemModelSelector? ParseFromRoot(JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object)
		{
			return null;
		}

		if (root.TryGetProperty("model", out var modelElement))
		{
			// Try to optimize deeply nested custom_data conditionals
			var optimized = TryOptimizeCustomDataSelector(modelElement);
			if (optimized is not null)
			{
				return optimized;
			}

			var selector = Parse(modelElement, 0);
			if (selector is not null)
			{
				return selector;
			}
		}

		if (root.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Object)
		{
			if (components.TryGetProperty("minecraft:model", out var componentModel))
			{
				var selector = Parse(componentModel, 0);
				if (selector is not null)
				{
					return selector;
				}
			}
		}

		if (root.TryGetProperty("type", out var typeProperty) && typeProperty.ValueKind == JsonValueKind.String)
		{
			var selector = Parse(root, 0);
			if (selector is not null)
			{
				return selector;
			}
		}

		if (root.TryGetProperty("cases", out _) || root.TryGetProperty("on_true", out _) ||
		    root.TryGetProperty("on_false", out _))
		{
			var selector = Parse(root, 0);
			if (selector is not null)
			{
				return selector;
			}
		}

		return null;
	}

	/// <summary>
	/// Optimizes deeply nested custom_data conditional selectors by building a lookup table.
	/// This prevents stack overflow on files like Hypixel+ player_head.json (8000+ nesting).
	/// </summary>
	private static ItemModelSelector? TryOptimizeCustomDataSelector(JsonElement element)
	{
		// Check if this is a deeply nested custom_data conditional structure
		if (!IsDeepCustomDataConditional(element, out var estimatedDepth))
		{
			return null;
		}

		Console.WriteLine(
			$"[TryOptimizeCustomDataSelector] Detected deeply nested custom_data selector (est. depth: {estimatedDepth}). Building lookup table...");

		var modelMappings = new Dictionary<string, string>(StringComparer.Ordinal);
		var selectorMappings = new Dictionary<string, ItemModelSelector>(StringComparer.Ordinal);
		var fallbackModel = ExtractCustomDataMappings(element, modelMappings, selectorMappings, 0, 100000);

		if (modelMappings.Count > 0 || selectorMappings.Count > 0)
		{
			Console.WriteLine(
				$"[TryOptimizeCustomDataSelector] Built lookup table with {modelMappings.Count} model mappings + {selectorMappings.Count} selector mappings");

			// Parse the fallback model if we found one
			ItemModelSelector? fallbackSelector = null;
			if (fallbackModel.HasValue && fallbackModel.Value.ValueKind == JsonValueKind.String)
			{
				var modelStr = fallbackModel.Value.GetString();
				if (!string.IsNullOrWhiteSpace(modelStr))
				{
					fallbackSelector = new ItemModelSelectorModel(modelStr, null);
				}
			}

			return new ItemModelSelectorOptimized(modelMappings, selectorMappings, fallbackSelector);
		}

		return null;
	}

	/// <summary>
	/// Checks if a selector is a deeply nested custom_data conditional tree.
	/// </summary>
	private static bool IsDeepCustomDataConditional(JsonElement element, out int estimatedDepth)
	{
		estimatedDepth = 0;

		if (element.ValueKind != JsonValueKind.Object)
		{
			return false;
		}

		// Handle "fallback" property wrapper
		var current = element;
		if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty("fallback", out var fallbackEl))
		{
			current = fallbackEl;
		}

		// Check first few levels to see if it's custom_data conditionals
		var customDataCount = 0;
		var depth = 0;

		for (var i = 0; i < 20; i++) // Sample first 20 levels
		{
			if (current.ValueKind != JsonValueKind.Object)
			{
				break;
			}

			if (current.TryGetProperty("type", out var typeEl) &&
			    typeEl.ValueKind == JsonValueKind.String &&
			    typeEl.GetString() == "condition")
			{
				if (current.TryGetProperty("property", out var propEl) &&
				    propEl.ValueKind == JsonValueKind.String &&
				    propEl.GetString() == "component" &&
				    current.TryGetProperty("predicate", out var predEl) &&
				    predEl.ValueKind == JsonValueKind.String &&
				    predEl.GetString() == "custom_data")
				{
					customDataCount++;
				}

				depth++;

				// Follow on_false branch (where the nesting usually continues)
				if (current.TryGetProperty("on_false", out var onFalseEl))
				{
					current = onFalseEl;
					continue;
				}
			}

			break;
		}

		// If we found many custom_data conditions in the first 20 levels, estimate full depth
		if (customDataCount >= 15)
		{
			estimatedDepth = depth * 400; // Rough estimate
			return true;
		}

		return false;
	}

	/// <summary>
	/// Iteratively extracts custom_data.id → model/selector mappings from a nested conditional tree.
	/// Uses a work queue to avoid stack overflow.
	/// </summary>
	private static JsonElement? ExtractCustomDataMappings(
		JsonElement root,
		Dictionary<string, string> modelMappings,
		Dictionary<string, ItemModelSelector> selectorMappings,
		int startDepth,
		int maxDepth)
	{
		// Handle "fallback" property wrapper
		var startElement = root;
		if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("fallback", out var fallbackEl))
		{
			startElement = fallbackEl;
		}

		var queue = new Queue<(JsonElement element, int depth)>();
		queue.Enqueue((startElement, startDepth));
		JsonElement? fallbackModel = null;

		while (queue.Count > 0)
		{
			var (current, depth) = queue.Dequeue();

			if (depth > maxDepth)
			{
				continue;
			}

			if (current.ValueKind == JsonValueKind.String)
			{
				// Reached a leaf model
				fallbackModel = current;
				continue;
			}

			if (current.ValueKind != JsonValueKind.Object)
			{
				continue;
			}

			// Check if this is a custom_data condition
			if (current.TryGetProperty("type", out var typeEl) &&
			    typeEl.ValueKind == JsonValueKind.String &&
			    typeEl.GetString() == "condition" &&
			    current.TryGetProperty("property", out var propEl) &&
			    propEl.ValueKind == JsonValueKind.String &&
			    propEl.GetString() == "component" &&
			    current.TryGetProperty("predicate", out var predEl) &&
			    predEl.ValueKind == JsonValueKind.String &&
			    predEl.GetString() == "custom_data")
			{
				// Extract the custom_data.id or custom_data.model value
				string? customDataId = null;

				if (current.TryGetProperty("value", out var valueEl))
				{
					if (valueEl.ValueKind == JsonValueKind.String)
					{
						customDataId = valueEl.GetString();
					}
					else if (valueEl.ValueKind == JsonValueKind.Object)
					{
						// Try "id" field first, then "model" field
						if (valueEl.TryGetProperty("id", out var idEl) &&
						    idEl.ValueKind == JsonValueKind.String)
						{
							customDataId = idEl.GetString();
						}
						else if (valueEl.TryGetProperty("model", out var modelEl) &&
						         modelEl.ValueKind == JsonValueKind.String)
						{
							customDataId = modelEl.GetString();
						}
					}
				}

				// If we found a custom_data.id, process the on_true branch
				if (!string.IsNullOrWhiteSpace(customDataId) &&
				    current.TryGetProperty("on_true", out var onTrueEl))
				{
					// Try to extract a simple model first
					var model = ExtractModelFromElement(onTrueEl);
					if (!string.IsNullOrWhiteSpace(model))
					{
						modelMappings[customDataId] = model;
					}
					else
					{
						// on_true is a complex selector - parse it (it should be shallow)
						var selector = Parse(onTrueEl, 0);
						if (selector != null)
						{
							selectorMappings[customDataId] = selector;
						}
					}
				}

				// Continue traversing on_false branch
				if (current.TryGetProperty("on_false", out var onFalseEl))
				{
					queue.Enqueue((onFalseEl, depth + 1));
				}
			}
			else
			{
				// Not a custom_data condition - this might be the fallback
				fallbackModel = current;
			}
		}

		return fallbackModel;
	}

	/// <summary>
	/// Extracts a model string from various selector structures.
	/// </summary>
	private static string? ExtractModelFromElement(JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.String)
		{
			return element.GetString();
		}

		if (element.ValueKind == JsonValueKind.Object)
		{
			// Check for direct model property
			if (element.TryGetProperty("model", out var modelEl) &&
			    modelEl.ValueKind == JsonValueKind.String)
			{
				return modelEl.GetString();
			}

			// Check for nested structure
			if (element.TryGetProperty("type", out var typeEl) &&
			    typeEl.ValueKind == JsonValueKind.String)
			{
				var type = typeEl.GetString();
				if (type == "model" && element.TryGetProperty("model", out modelEl) &&
				    modelEl.ValueKind == JsonValueKind.String)
				{
					return modelEl.GetString();
				}
			}
		}

		return null;
	}

	public static ItemModelSelector? Parse(JsonElement element, int depth)
	{
		// Prevent stack overflow on extremely deeply nested JSON
		if (depth > MaxRecursionDepth)
		{
			// Return null to allow fallback to on_false or other graceful degradation
			return null;
		}

		// Use iterative parsing with explicit stack to avoid stack overflow on deeply nested JSON
		// (e.g., Hypixel+ player_head.json with 8000+ nesting levels)
		while (true)
		{
			if (element.ValueKind == JsonValueKind.String)
			{
				return new ItemModelSelectorModel(element.GetString(), null);
			}

			if (element.ValueKind != JsonValueKind.Object)
			{
				return null;
			}

			// Check for fallback property first (e.g., Hypixel+ pack structure)
			if (element.TryGetProperty("fallback", out var fallbackElement))
			{
				element = fallbackElement;
				// Don't increment depth for tail recursion optimization
				continue; // Tail recursion optimization: loop instead of recursive call
			}

			var type = NormalizeType(element.TryGetProperty("type", out var typeProperty)
				? typeProperty.GetString()
				: null);

			return type switch
			{
				"model" => new ItemModelSelectorModel(GetString(element, "model"), GetString(element, "base")),
				"special" => new ItemModelSelectorSpecial(GetString(element, "base"),
					Parse(element.TryGetProperty("model", out var nested) ? nested : default, depth + 1)),
				"condition" => ParseCondition(element, depth + 1),
				"select" => ParseSelect(element, depth + 1),
				"range_dispatch" => ParseRangeDispatch(element, depth + 1),
				"composite" => ParseComposite(element, depth + 1),
				"empty" => new ItemModelSelectorEmpty(),
				_ => CreateFallbackSelector(element)
			};
		}
	}

	private static ItemModelSelector? ParseCondition(JsonElement element, int depth)
	{
		var property = GetString(element, "property") ?? string.Empty;
		var predicate = GetString(element, "predicate");
		var component = GetString(element, "component");

		IReadOnlyDictionary<string, string>? valueProperties = null;
		string? valueLiteral = null;
		if (element.TryGetProperty("value", out var valueElement))
		{
			valueProperties = ParseStringMap(valueElement);
			if (valueProperties is null && valueElement.ValueKind == JsonValueKind.String)
			{
				valueLiteral = valueElement.GetString();
			}
			else if (valueProperties is null)
			{
				valueLiteral = valueElement.GetRawText();
			}
		}

		var onTrue = element.TryGetProperty("on_true", out var onTrueElement) ? Parse(onTrueElement, depth + 1) : null;
		var onFalse = element.TryGetProperty("on_false", out var onFalseElement)
			? Parse(onFalseElement, depth + 1)
			: null;

		// If parsing on_true failed due to depth limit or unsupported selector, fall back to on_false
		if (onTrue is null && onFalse is not null)
		{
			return onFalse;
		}

		return new ItemModelSelectorCondition(property, predicate, component, valueProperties, valueLiteral, onTrue,
			onFalse);
	}

	private static ItemModelSelector? CreateFallbackSelector(JsonElement element)
	{
		var directModel = GetString(element, "model") ?? GetString(element, "base");
		return string.IsNullOrWhiteSpace(directModel) ? null : new ItemModelSelectorModel(directModel, null);
	}

	private static ItemModelSelector? ParseComposite(JsonElement element, int depth)
	{
		// Composite type has a "models" array - for static rendering, we just use the first model
		// Hopefully the first model is consistently a good one to use
		// (keybind/animation effects are ignored in static atlas generation)
		if (!element.TryGetProperty("models", out var modelsArray) || modelsArray.ValueKind != JsonValueKind.Array)
		{
			return null;
		}

		foreach (var modelElement in modelsArray.EnumerateArray())
		{
			var parsed = Parse(modelElement, depth);
			if (parsed is not null)
			{
				return parsed; // Return first valid model
			}
		}

		return null;
	}

	private static ItemModelSelector? ParseSelect(JsonElement element, int depth)
	{
		var property = GetString(element, "property") ?? string.Empty;
		var cases = new List<ItemModelSelectorSelectCase>();
		if (element.TryGetProperty("cases", out var casesElement) && casesElement.ValueKind == JsonValueKind.Array)
		{
			foreach (var caseElement in casesElement.EnumerateArray())
			{
				var whenValues = ParseWhen(caseElement.TryGetProperty("when", out var whenElement)
					? whenElement
					: default);
				var selector = caseElement.TryGetProperty("model", out var modelElement)
					? Parse(modelElement, depth + 1)
					: null;
				cases.Add(new ItemModelSelectorSelectCase(whenValues, selector));
			}
		}

		var fallback = element.TryGetProperty("fallback", out var fallbackElement)
			? Parse(fallbackElement, depth + 1)
			: null;
		return new ItemModelSelectorSelect(property, cases, fallback);
	}

	private static ItemModelSelector? ParseRangeDispatch(JsonElement element, int depth)
	{
		var property = GetString(element, "property") ?? string.Empty;
		var normalize = element.TryGetProperty("normalize", out var normalizeElement) &&
		                normalizeElement.ValueKind == JsonValueKind.True;

		var entries = new List<RangeDispatchEntry>();
		if (element.TryGetProperty("entries", out var entriesElement) &&
		    entriesElement.ValueKind == JsonValueKind.Array)
		{
			foreach (var entryElement in entriesElement.EnumerateArray())
			{
				if (entryElement.TryGetProperty("threshold", out var thresholdElement) &&
				    thresholdElement.TryGetDouble(out var threshold))
				{
					var selector = entryElement.TryGetProperty("model", out var modelElement)
						? Parse(modelElement, depth + 1)
						: null;
					entries.Add(new RangeDispatchEntry(threshold, selector));
				}
			}
		}

		var fallback = element.TryGetProperty("fallback", out var fallbackElement)
			? Parse(fallbackElement, depth + 1)
			: null;
		return new ItemModelSelectorRangeDispatch(property, normalize, entries, fallback);
	}

	private static IReadOnlyDictionary<string, string>? ParseStringMap(JsonElement element)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			return null;
		}

		var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var property in element.EnumerateObject())
		{
			var value = property.Value.ValueKind switch
			{
				JsonValueKind.String => property.Value.GetString() ?? string.Empty,
				JsonValueKind.Number => property.Value.TryGetInt64(out var longValue)
					? longValue.ToString(CultureInfo.InvariantCulture)
					: property.Value.GetDouble().ToString(CultureInfo.InvariantCulture),
				JsonValueKind.True => "true",
				JsonValueKind.False => "false",
				JsonValueKind.Null => "null",
				_ => property.Value.GetRawText()
			};

			if (!string.IsNullOrWhiteSpace(property.Name) && !string.IsNullOrWhiteSpace(value))
			{
				map[property.Name] = value;
			}
		}

		return map.Count > 0 ? map : null;
	}

	private static IReadOnlyList<string> ParseWhen(JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.String)
		{
			var value = element.GetString();
			return string.IsNullOrWhiteSpace(value)
				? Array.Empty<string>()
				: new[] { value };
		}

		if (element.ValueKind == JsonValueKind.Array)
		{
			var values = new List<string>();
			foreach (var entry in element.EnumerateArray())
			{
				if (entry.ValueKind == JsonValueKind.String)
				{
					var value = entry.GetString();
					if (!string.IsNullOrWhiteSpace(value))
					{
						values.Add(value);
					}
				}
			}

			return values;
		}

		return [];
	}

	private static string? GetString(JsonElement element, string propertyName)
		=> element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
			? property.GetString()
			: null;

	private static string NormalizeType(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "model";
		}

		var type = value.Trim();
		if (type.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase))
		{
			type = type[10..];
		}

		return type.ToLowerInvariant();
	}
}