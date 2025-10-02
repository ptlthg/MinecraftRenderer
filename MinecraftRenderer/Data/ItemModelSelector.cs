namespace MinecraftRenderer;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using MinecraftRenderer.Nbt;

internal readonly record struct ItemModelContext(MinecraftBlockRenderer.ItemRenderData? ItemData, string DisplayContext);

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
			return ValueLiteral is not null && string.Equals(ValueLiteral, "true", StringComparison.OrdinalIgnoreCase);
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

	internal static class ItemModelSelectorParser
	{
		public static ItemModelSelector? ParseFromRoot(JsonElement root)
		{
			if (root.ValueKind != JsonValueKind.Object)
			{
				return null;
			}

			if (root.TryGetProperty("model", out var modelElement))
			{
				var selector = Parse(modelElement);
				if (selector is not null)
				{
					return selector;
				}
			}

			if (root.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Object)
			{
				if (components.TryGetProperty("minecraft:model", out var componentModel))
				{
					var selector = Parse(componentModel);
					if (selector is not null)
					{
						return selector;
					}
				}
			}

			if (root.TryGetProperty("type", out var typeProperty) && typeProperty.ValueKind == JsonValueKind.String)
			{
				var selector = Parse(root);
				if (selector is not null)
				{
					return selector;
				}
			}

			if (root.TryGetProperty("cases", out _) || root.TryGetProperty("on_true", out _) ||
			    root.TryGetProperty("on_false", out _))
			{
				var selector = Parse(root);
				if (selector is not null)
				{
					return selector;
				}
			}

			return null;
		}

		public static ItemModelSelector? Parse(JsonElement element)
		{
			if (element.ValueKind == JsonValueKind.String)
			{
				return new ItemModelSelectorModel(element.GetString(), null);
			}

			if (element.ValueKind != JsonValueKind.Object)
			{
				return null;
			}

			var type = NormalizeType(element.TryGetProperty("type", out var typeProperty)
				? typeProperty.GetString()
				: null);

			return type switch
			{
				"model" => new ItemModelSelectorModel(GetString(element, "model"), GetString(element, "base")),
				"special" => new ItemModelSelectorSpecial(GetString(element, "base"),
					Parse(element.TryGetProperty("model", out var nested) ? nested : default)),
				"condition" => ParseCondition(element),
				"select" => ParseSelect(element),
				_ => CreateFallbackSelector(element)
			};
		}

		private static ItemModelSelector? ParseCondition(JsonElement element)
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

			var onTrue = element.TryGetProperty("on_true", out var onTrueElement) ? Parse(onTrueElement) : null;
			var onFalse = element.TryGetProperty("on_false", out var onFalseElement) ? Parse(onFalseElement) : null;
			return new ItemModelSelectorCondition(property, predicate, component, valueProperties, valueLiteral, onTrue,
				onFalse);
		}

		private static ItemModelSelector? CreateFallbackSelector(JsonElement element)
		{
			var directModel = GetString(element, "model") ?? GetString(element, "base");
			return string.IsNullOrWhiteSpace(directModel) ? null : new ItemModelSelectorModel(directModel, null);
		}

		private static ItemModelSelector? ParseSelect(JsonElement element)
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
						? Parse(modelElement)
						: null;
					cases.Add(new ItemModelSelectorSelectCase(whenValues, selector));
				}
			}

			var fallback = element.TryGetProperty("fallback", out var fallbackElement) ? Parse(fallbackElement) : null;
			return new ItemModelSelectorSelect(property, cases, fallback);
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

			return Array.Empty<string>();
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
