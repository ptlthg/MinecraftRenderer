namespace MinecraftRenderer;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using MinecraftRenderer.Nbt;
using SixLabors.ImageSharp.PixelFormats;

internal readonly record struct ItemModelContext(
	MinecraftBlockRenderer.ItemRenderData? ItemData,
	string DisplayContext,
	string? ItemName)
{
	public ItemModelContext(MinecraftBlockRenderer.ItemRenderData? itemData, string displayContext)
		: this(itemData, displayContext, null) {
	}
}

internal abstract class ItemModelSelector
{
	public abstract string? Resolve(ItemModelContext context);

	public virtual IReadOnlyList<string> ResolveAll(ItemModelContext context) {
		var resolved = Resolve(context);
		return string.IsNullOrWhiteSpace(resolved) ? [] : [resolved];
	}
}

internal static class CatharsisDataTypeResolver
{
	private static readonly HashSet<string> KnownSelectDataTypes = new(StringComparer.OrdinalIgnoreCase)
	{
		"rarity",
		"modifier",
		"selected_arrow",
		"fungi_cutter_mode"
	};

	private static readonly HashSet<string> KnownNumericDataTypes = new(StringComparer.OrdinalIgnoreCase)
	{
		"dungeonbreaker_charges",
		"fuel",
		"midas_weapon_paid",
		"pelts_earned",
		"snowballs",
		"thunder_charge",
		"water_level"
	};

	public static bool SupportsSelectValue(string? dataType) {
		if (string.IsNullOrWhiteSpace(dataType)) {
			return false;
		}

		return KnownSelectDataTypes.Contains(NormalizeDataType(dataType));
	}

	public static bool SupportsNumericValue(string? dataType)
		=> !string.IsNullOrWhiteSpace(dataType) && KnownNumericDataTypes.Contains(NormalizeDataType(dataType));

	public static bool EvaluateCondition(string? dataType, ItemModelContext context) {
		if (string.IsNullOrWhiteSpace(dataType)) {
			return false;
		}

		var normalized = NormalizeDataType(dataType);
		return normalized switch {
			"has_skin_fallback" => HasSkinFallback(context),
			"has_dye_fallback" => HasDyeFallback(context),
			_ => TryGetDataTypeTag(context.ItemData?.CustomData, normalized, out var tag) &&
			     TryGetBooleanValue(tag, out var value) && value
		};
	}

	public static bool IsPresent(string? dataType, ItemModelContext context) {
		if (string.IsNullOrWhiteSpace(dataType)) {
			return false;
		}

		var normalized = NormalizeDataType(dataType);
		return normalized switch {
			"has_skin_fallback" => HasSkinFallback(context),
			"helmet_skin" => HasSkinFallback(context),
			"has_dye_fallback" => HasDyeFallback(context),
			"applied_dye" => HasDyeFallback(context),
			"rarity" => GetSelectValue(dataType, context) is not null,
			"modifier" => GetSelectValue(dataType, context) is not null,
			_ => TryGetDataTypeTag(context.ItemData?.CustomData, normalized, out _)
		};
	}

	public static string? GetSelectValue(string? dataType, ItemModelContext context) {
		if (context.ItemData?.CustomData is not { } customData || string.IsNullOrWhiteSpace(dataType) ||
		    !SupportsSelectValue(dataType)) {
			return null;
		}

		var normalized = NormalizeDataType(dataType);
		return normalized switch {
			"rarity" => NormalizeStringValue(GetFirstString(customData, "upgradedRarity", "rarity", "tier")),
			"modifier" => NormalizeStringValue(GetFirstString(customData, "modifier", "reforge", "prefix")),
			_ => TryGetDataTypeTag(customData, normalized, out var tag)
				? NormalizeStringValue(GetStringValue(tag))
				: null
		};
	}

	public static double? GetNumericValue(string? dataType, ItemModelContext context) {
		if (!SupportsNumericValue(dataType)) {
			return null;
		}

		if (context.ItemData?.CustomData is not { } customData) {
			return 0.0;
		}

		return TryGetDataTypeTag(customData, NormalizeDataType(dataType!), out var tag) && TryGetNumericValue(tag, out var value)
			? value
			: 0.0;
	}

	public static bool HasGemstones(ItemModelContext context, int amount, string? slot, string? quality) {
		if (context.ItemData?.CustomData is not { } customData) {
			return false;
		}

		if (!TryGetDataTypeTag(customData, "gems", out var gemsTag) || gemsTag is not NbtCompound gems) {
			return false;
		}

		var required = Math.Max(1, amount);
		var matched = 0;
		foreach (var (key, value) in gems) {
			if (string.Equals(key, "unlocked_slots", StringComparison.OrdinalIgnoreCase)) {
				continue;
			}

			if (!HasSocketedGemstone(value)) {
				continue;
			}

			if (!MatchesGemstoneSlot(key, value, slot)) {
				continue;
			}

			if (!MatchesGemstoneQuality(value, quality)) {
				continue;
			}

			matched++;
			if (matched >= required) {
				return true;
			}
		}

		return false;
	}

	private static bool HasSkinFallback(ItemModelContext context) {
		if (context.ItemData?.Profile is not null) {
			return true;
		}

		var customData = context.ItemData?.CustomData;
		if (customData is null) {
			return false;
		}

		return HasNonEmptyString(customData, "helmet_skin", "skin", "skin_texture", "texture", "textures");
	}

	private static bool HasDyeFallback(ItemModelContext context)
		=> context.ItemData?.Layer0Tint is not null
		   || (context.ItemData?.AdditionalLayerTints is not null && context.ItemData.AdditionalLayerTints.Count > 0);

	private static bool HasSocketedGemstone(NbtTag tag)
		=> tag switch {
			NbtString str => !string.IsNullOrWhiteSpace(str.Value),
			NbtCompound compound => !string.IsNullOrWhiteSpace(GetFirstString(compound, "quality", "gemstone", "type")),
			_ => false
		};

	private static bool MatchesGemstoneSlot(string key, NbtTag tag, string? expectedSlot) {
		if (string.IsNullOrWhiteSpace(expectedSlot)) {
			return true;
		}

		var expected = NormalizeGemstoneToken(expectedSlot);
		if (string.IsNullOrWhiteSpace(expected)) {
			return true;
		}

		if (string.Equals(expected, "UNIVERSAL", StringComparison.OrdinalIgnoreCase)) {
			return true;
		}

		var normalizedKey = NormalizeGemstoneToken(key);
		if (normalizedKey == expected ||
		    normalizedKey.StartsWith(expected, StringComparison.OrdinalIgnoreCase) ||
		    normalizedKey.Contains(expected, StringComparison.OrdinalIgnoreCase)) {
			return true;
		}

		if (tag is NbtCompound compound) {
			var compoundSlot = NormalizeGemstoneToken(GetFirstString(compound, "slot", "gemstone", "type"));
			return compoundSlot == expected ||
			       compoundSlot.Contains(expected, StringComparison.OrdinalIgnoreCase);
		}

		return false;
	}

	private static bool MatchesGemstoneQuality(NbtTag tag, string? expectedQuality) {
		if (string.IsNullOrWhiteSpace(expectedQuality)) {
			return true;
		}

		var expected = NormalizeGemstoneToken(expectedQuality);
		var actual = tag switch {
			NbtString str => str.Value,
			NbtCompound compound => GetFirstString(compound, "quality"),
			_ => null
		};

		return NormalizeGemstoneToken(actual) == expected;
	}

	private static bool TryGetDataTypeTag(NbtCompound? compound, string normalizedDataType, out NbtTag tag) {
		tag = null!;
		if (compound is null || string.IsNullOrWhiteSpace(normalizedDataType)) {
			return false;
		}

		foreach (var (key, value) in compound) {
			if (string.Equals(NormalizeDataType(key), normalizedDataType, StringComparison.OrdinalIgnoreCase)) {
				tag = value;
				return true;
			}
		}

		return false;
	}

	private static bool HasNonEmptyString(NbtCompound compound, params string[] keys)
		=> keys.Any(key => !string.IsNullOrWhiteSpace(GetFirstString(compound, key)));

	private static string? GetStringValue(NbtTag tag)
		=> tag switch {
			NbtString str => str.Value,
			NbtInt i => i.Value.ToString(CultureInfo.InvariantCulture),
			NbtLong l => l.Value.ToString(CultureInfo.InvariantCulture),
			NbtShort s => s.Value.ToString(CultureInfo.InvariantCulture),
			NbtByte b => b.Value.ToString(CultureInfo.InvariantCulture),
			NbtDouble d => d.Value.ToString(CultureInfo.InvariantCulture),
			NbtFloat f => f.Value.ToString(CultureInfo.InvariantCulture),
			_ => null
		};

	private static bool TryGetNumericValue(NbtTag tag, out double value) {
		switch (tag) {
			case NbtInt i:
				value = i.Value;
				return true;
			case NbtLong l:
				value = l.Value;
				return true;
			case NbtShort s:
				value = s.Value;
				return true;
			case NbtByte b:
				value = b.Value;
				return true;
			case NbtFloat f:
				value = f.Value;
				return true;
			case NbtDouble d:
				value = d.Value;
				return true;
			case NbtString str when double.TryParse(str.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value):
				return true;
			default:
				value = 0;
				return false;
		}
	}

	private static bool TryGetBooleanValue(NbtTag tag, out bool value) {
		switch (tag) {
			case NbtByte b:
				value = b.Value != 0;
				return true;
			case NbtShort s:
				value = s.Value != 0;
				return true;
			case NbtInt i:
				value = i.Value != 0;
				return true;
			case NbtLong l:
				value = l.Value != 0;
				return true;
			case NbtFloat f:
				value = Math.Abs(f.Value) > float.Epsilon;
				return true;
			case NbtDouble d:
				value = Math.Abs(d.Value) > double.Epsilon;
				return true;
			case NbtString str:
				var text = str.Value.Trim();
				if (bool.TryParse(text, out value)) {
					return true;
				}

				if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue)) {
					value = longValue != 0;
					return true;
				}

				break;
		}

		value = false;
		return false;
	}

	private static string NormalizeDataType(string dataType)
		=> dataType.Trim().ToLowerInvariant();

	private static string NormalizeGemstoneToken(string? value) {
		if (string.IsNullOrWhiteSpace(value)) {
			return string.Empty;
		}

		var normalized = value.Trim().ToUpperInvariant();
		var builder = new System.Text.StringBuilder(normalized.Length);
		foreach (var c in normalized) {
			if (c is >= 'A' and <= 'Z') {
				builder.Append(c);
			}
		}

		return builder.ToString();
	}

	private static string? NormalizeStringValue(string? value)
		=> string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

	private static string? GetFirstString(NbtCompound compound, params string[] keys) {
		foreach (var key in keys) {
			if (compound.TryGetValue(key, out var tag) && tag is NbtString str && !string.IsNullOrWhiteSpace(str.Value)) {
				return str.Value;
			}
		}

		return null;
	}
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

	public override IReadOnlyList<string> ResolveAll(ItemModelContext context) {
		var nested = Nested?.ResolveAll(context);
		return nested is { Count: > 0 }
			? nested
			: string.IsNullOrWhiteSpace(BaseModel) ? [] : [BaseModel];
	}
}

internal sealed class ItemModelSelectorFallthrough : ItemModelSelector
{
	public override string? Resolve(ItemModelContext context) => null;

	public override IReadOnlyList<string> ResolveAll(ItemModelContext context) => [];
}

internal sealed class ItemModelSelectorComposite(IReadOnlyList<ItemModelSelector> selectors) : ItemModelSelector
{
	public IReadOnlyList<ItemModelSelector> Selectors { get; } = selectors;

	public override string? Resolve(ItemModelContext context) {
		var resolved = ResolveAll(context);
		return resolved.Count > 0 ? resolved[0] : null;
	}

	public override IReadOnlyList<string> ResolveAll(ItemModelContext context) {
		if (Selectors.Count == 0) {
			return [];
		}

		var models = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var selector in Selectors) {
			foreach (var resolved in selector.ResolveAll(context)) {
				if (!string.IsNullOrWhiteSpace(resolved) && seen.Add(resolved)) {
					models.Add(resolved);
				}
			}
		}

		return models;
	}
}

internal sealed class ItemModelSelectorCondition(
	string property,
	string? dataType,
	string? predicate,
	string? component,
	IReadOnlyDictionary<string, string>? valueProperties,
	string? valueLiteral,
	int? amount,
	string? gemstoneSlot,
	string? gemstoneQuality,
	ItemModelSelector? onTrue,
	ItemModelSelector? onFalse) : ItemModelSelector
{
	public string Property { get; } = property;
	public string? DataType { get; } = dataType;
	public string? Predicate { get; } = predicate;
	public string? Component { get; } = component;
	public IReadOnlyDictionary<string, string>? ValueProperties { get; } = valueProperties;
	public string? ValueLiteral { get; } = valueLiteral;
	public int? Amount { get; } = amount;
	public string? GemstoneSlot { get; } = gemstoneSlot;
	public string? GemstoneQuality { get; } = gemstoneQuality;
	public ItemModelSelector? OnTrue { get; } = onTrue;
	public ItemModelSelector? OnFalse { get; } = onFalse;

	public override string? Resolve(ItemModelContext context)
		=> EvaluateCondition(context) ? OnTrue?.Resolve(context) : OnFalse?.Resolve(context);

	public override IReadOnlyList<string> ResolveAll(ItemModelContext context)
		=> EvaluateCondition(context)
			? OnTrue?.ResolveAll(context) ?? []
			: OnFalse?.ResolveAll(context) ?? [];

	private bool EvaluateCondition(ItemModelContext context) {
		if (string.Equals(Property, "catharsis:has_gemstones", StringComparison.OrdinalIgnoreCase)) {
			return CatharsisDataTypeResolver.HasGemstones(context, Amount ?? 1, GemstoneSlot, GemstoneQuality);
		}

		if (IsCatharsisDataTypeProperty(Property)) {
			return CatharsisDataTypeResolver.EvaluateCondition(DataType, context);
		}

		if (IsCatharsisDataTypePresentProperty(Property)) {
			return CatharsisDataTypeResolver.IsPresent(DataType, context);
		}

		if (string.Equals(Property, "has_component", StringComparison.OrdinalIgnoreCase) ||
		    string.Equals(Property, "minecraft:has_component", StringComparison.OrdinalIgnoreCase)) {
			return HasComponent(Component, context);
		}

		if (string.Equals(Property, "component", StringComparison.OrdinalIgnoreCase)) {
			return EvaluateComponentCondition(context);
		}

		if (string.Equals(Property, "display_context", StringComparison.OrdinalIgnoreCase)) {
			if (ValueProperties is not null && ValueProperties.Count > 0) {
				if (ValueProperties.TryGetValue("value", out var expected)) {
					return string.Equals(expected, context.DisplayContext, StringComparison.OrdinalIgnoreCase);
				}

				if (ValueProperties.TryGetValue("equals", out expected)) {
					return string.Equals(expected, context.DisplayContext, StringComparison.OrdinalIgnoreCase);
				}
			}

			if (!string.IsNullOrWhiteSpace(ValueLiteral)) {
				return string.Equals(ValueLiteral, context.DisplayContext, StringComparison.OrdinalIgnoreCase);
			}

			return false;
		}

		if (string.Equals(Property, "selected", StringComparison.OrdinalIgnoreCase)) {
			return
				false; //ValueLiteral is not null && string.Equals(ValueLiteral, "true", StringComparison.OrdinalIgnoreCase);
		}

		return false;
	}

	private bool EvaluateComponentCondition(ItemModelContext context) {
		var predicate = Predicate ?? string.Empty;
		if (string.Equals(predicate, "custom_data", StringComparison.OrdinalIgnoreCase)) {
			return EvaluateCustomData(context);
		}

		if (string.IsNullOrWhiteSpace(predicate) && !string.IsNullOrWhiteSpace(Component)) {
			return HasComponent(Component, context);
		}

		return false;
	}

	private static bool IsCatharsisDataTypeProperty(string property)
		=> string.Equals(property, "catharsis:data_type", StringComparison.OrdinalIgnoreCase);

	private static bool IsCatharsisDataTypePresentProperty(string property)
		=> string.Equals(property, "catharsis:is_data_type_present", StringComparison.OrdinalIgnoreCase) ||
		   string.Equals(property, "catharsis:has_data_type", StringComparison.OrdinalIgnoreCase);

	internal static bool HasComponent(string? component, ItemModelContext context) {
		if (string.IsNullOrWhiteSpace(component)) {
			return false;
		}

		var normalized = NormalizeComponentName(component);
		return normalized switch {
			"custom_data" => context.ItemData?.CustomData is not null,
			"profile" => context.ItemData?.Profile is not null,
			"dyed_color" => context.ItemData?.Layer0Tint is not null
			                 || (context.ItemData?.AdditionalLayerTints is not null &&
			                     context.ItemData.AdditionalLayerTints.Count > 0)
			                 || context.ItemData?.DisableDefaultLayer0Tint == true,
			"item_model" => !string.IsNullOrWhiteSpace(context.ItemName),
			_ => false
		};
	}

	internal static string NormalizeComponentName(string component) {
		var normalized = component.Trim();
		var colon = normalized.IndexOf(':');
		if (colon >= 0 && colon + 1 < normalized.Length) {
			normalized = normalized[(colon + 1)..];
		}

		return normalized.ToLowerInvariant();
	}

	private bool EvaluateCustomData(ItemModelContext context) {
		var customData = context.ItemData?.CustomData;
		if (customData is null) {
			return false;
		}

		if (ValueProperties is not null && ValueProperties.Count > 0) {
			foreach (var (key, expected) in ValueProperties) {
				if (!TryMatchCustomDataValue(customData, key, expected)) {
					return false;
				}
			}

			return true;
		}

		if (!string.IsNullOrWhiteSpace(ValueLiteral)) {
			var id = TryGetString(customData, "id");
			if (!string.IsNullOrWhiteSpace(id)) {
				return string.Equals(id, ValueLiteral, StringComparison.OrdinalIgnoreCase);
			}
		}

		return false;
	}

	internal static bool TryMatchCustomDataValue(NbtCompound compound, string key, string expected) {
		if (!compound.TryGetValue(key, out var tag)) {
			return false;
		}

		if (IsJsonStructure(expected)) {
			return TryMatchJsonStructure(tag, expected);
		}

		return MatchesPrimitiveValue(tag, expected);
	}

	private static string? TryGetString(NbtCompound compound, string key)
		=> compound.TryGetValue(key, out var tag) && tag is NbtString str && !string.IsNullOrWhiteSpace(str.Value)
			? str.Value
			: null;

	private static bool MatchesPrimitiveValue(NbtTag tag, string expected) {
		if (TryParseBoolean(expected, out var expectedBool)) {
			return tag switch {
				NbtByte b => (b.Value != 0) == expectedBool,
				NbtShort s => (s.Value != 0) == expectedBool,
				NbtInt i => (i.Value != 0) == expectedBool,
				NbtLong l => (l.Value != 0) == expectedBool,
				NbtString s => bool.TryParse(s.Value, out var actual) && actual == expectedBool,
				_ => false
			};
		}

		return tag switch {
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

	private static bool IsJsonStructure(string value) {
		if (string.IsNullOrWhiteSpace(value)) {
			return false;
		}

		var trimmed = value.TrimStart();
		return trimmed.StartsWith('{') || trimmed.StartsWith('[');
	}

	private static bool TryMatchJsonStructure(NbtTag tag, string json) {
		try {
			using var document = JsonDocument.Parse(json);
			return MatchTagWithJson(tag, document.RootElement);
		}
		catch (JsonException) {
			return false;
		}
	}

	private static bool MatchTagWithJson(NbtTag tag, JsonElement expected)
		=> expected.ValueKind switch {
			JsonValueKind.Object => tag is NbtCompound compound && MatchCompound(compound, expected),
			JsonValueKind.Array => MatchArray(tag, expected),
			JsonValueKind.String => tag switch {
				NbtString s => string.Equals(s.Value, expected.GetString(), StringComparison.Ordinal),
				_ => false
			},
			JsonValueKind.Number => MatchNumeric(tag, expected),
			JsonValueKind.True => MatchesPrimitiveValue(tag, "true"),
			JsonValueKind.False => MatchesPrimitiveValue(tag, "false"),
			JsonValueKind.Null => false,
			_ => false
		};

	private static bool MatchCompound(NbtCompound compound, JsonElement expected) {
		foreach (var property in expected.EnumerateObject()) {
			if (!compound.TryGetValue(property.Name, out var child)) {
				return false;
			}

			if (!MatchTagWithJson(child, property.Value)) {
				return false;
			}
		}

		return true;
	}

	private static bool MatchArray(NbtTag tag, JsonElement expected) {
		return tag switch {
			NbtList list => MatchList(list, expected),
			NbtByteArray byteArray => MatchPrimitiveArray(byteArray.Values.Select(value => (double)value), expected),
			NbtIntArray intArray => MatchPrimitiveArray(intArray.Values.Select(value => (double)value), expected),
			NbtLongArray longArray => MatchPrimitiveArray(longArray.Values.Select(value => (double)value), expected),
			_ => false
		};
	}

	private static bool MatchList(NbtList list, JsonElement expected) {
		if (list.Count != expected.GetArrayLength()) {
			return false;
		}

		var index = 0;
		foreach (var element in expected.EnumerateArray()) {
			if (!MatchTagWithJson(list[index++], element)) {
				return false;
			}
		}

		return true;
	}

	private static bool MatchPrimitiveArray(IEnumerable<double> actualValues, JsonElement expected) {
		var actualList = actualValues.ToList();
		if (actualList.Count != expected.GetArrayLength()) {
			return false;
		}

		var index = 0;
		foreach (var element in expected.EnumerateArray()) {
			if (!element.TryGetDouble(out var expectedValue)) {
				return false;
			}

			if (!NumericEquals(actualList[index++], expectedValue)) {
				return false;
			}
		}

		return true;
	}

	private static bool MatchNumeric(NbtTag tag, JsonElement expected)
		=> tag switch {
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

	private static bool TryParseBoolean(string value, out bool result) {
		if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) {
			result = true;
			return true;
		}

		if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) {
			result = false;
			return true;
		}

		result = default;
		return false;
	}

	private static bool TryGetInt(JsonElement element, out int value) {
		if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value)) {
			return true;
		}

		if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var longValue) &&
		    longValue is >= int.MinValue and <= int.MaxValue) {
			value = (int)longValue;
			return true;
		}

		value = default;
		return false;
	}

	private static bool TryGetLong(JsonElement element, out long value) {
		if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value)) {
			return true;
		}

		value = default;
		return false;
	}

	private static bool TryGetDouble(JsonElement element, out double value) {
		if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value)) {
			return true;
		}

		value = default;
		return false;
	}
}

internal sealed record ItemModelSelectorSelectCase(IReadOnlyList<string> When, ItemModelSelector? Selector);

internal sealed class ItemModelSelectorSelect(
	string property,
	string? dataType,
	string? component,
	IReadOnlyList<ItemModelSelectorSelectCase> cases,
	ItemModelSelector? fallback) : ItemModelSelector
{
	public string Property { get; } = property;
	public string? DataType { get; } = dataType;
	public string? Component { get; } = component;
	public IReadOnlyList<ItemModelSelectorSelectCase> Cases { get; } = cases;
	public ItemModelSelector? Fallback { get; } = fallback;

	public override string? Resolve(ItemModelContext context) {
		var resolved = ResolveAll(context);
		return resolved.Count > 0 ? resolved[0] : null;
	}

	public override IReadOnlyList<string> ResolveAll(ItemModelContext context) {
		foreach (var selectCase in Cases) {
			if (Matches(selectCase.When, context)) {
				var resolved = selectCase.Selector?.ResolveAll(context);
				if (resolved is { Count: > 0 }) {
					return resolved;
				}
			}
		}

		if (ShouldResolveFirstCaseOnUnsupportedSelector()) {
			var firstResolved = ResolveFirstCase(context);
			if (firstResolved.Count > 0) {
				return firstResolved;
			}
		}

		return Fallback?.ResolveAll(context) ?? [];
	}

	private bool ShouldResolveFirstCaseOnUnsupportedSelector() {
		if (IsCatharsisDataTypeProperty(Property)) {
			return !CatharsisDataTypeResolver.SupportsSelectValue(DataType);
		}

		return false;
	}

	private IReadOnlyList<string> ResolveFirstCase(ItemModelContext context) {
		if (Cases.Count == 0) {
			return [];
		}

		return Cases[0].Selector?.ResolveAll(context) ?? [];
	}

	private bool Matches(IReadOnlyList<string> when, ItemModelContext context) {
		if (when.Count == 0) {
			return false;
		}

		if (IsCatharsisDataTypeProperty(Property)) {
			var value = CatharsisDataTypeResolver.GetSelectValue(DataType, context);
			return !string.IsNullOrWhiteSpace(value)
			       && when.Any(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));
		}

		if (string.Equals(Property, "display_context", StringComparison.OrdinalIgnoreCase)) {
			return when.Any(value =>
				string.Equals(value, context.DisplayContext, StringComparison.OrdinalIgnoreCase));
		}

		if (string.Equals(Property, "component", StringComparison.OrdinalIgnoreCase)) {
			foreach (var value in when) {
				if (MatchesComponentValue(Component, value, context)) {
					return true;
				}
			}

			return false;
		}

		return false;
	}

	private static bool MatchesComponentValue(string? component, string? value, ItemModelContext context) {
		if (string.IsNullOrWhiteSpace(component) && string.IsNullOrWhiteSpace(value)) {
			return false;
		}

		var itemData = context.ItemData;

		if (string.Equals(component, "item_model", StringComparison.OrdinalIgnoreCase) ||
		    string.Equals(component, "minecraft:item_model", StringComparison.OrdinalIgnoreCase)) {
			if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(context.ItemName)) {
				return false;
			}

			return string.Equals(value, context.ItemName, StringComparison.OrdinalIgnoreCase) ||
			       string.Equals(value, "minecraft:" + context.ItemName, StringComparison.OrdinalIgnoreCase);
		}

		if (itemData is null) {
			return false;
		}

		if (string.Equals(component, "dyed_color", StringComparison.OrdinalIgnoreCase) ||
		    string.Equals(component, "minecraft:dyed_color", StringComparison.OrdinalIgnoreCase)) {
			return MatchesDyedColor(value, itemData);
		}

		if (string.Equals(value, "minecraft:custom_data", StringComparison.OrdinalIgnoreCase) ||
		    string.Equals(value, "custom_data", StringComparison.OrdinalIgnoreCase)) {
			return itemData.CustomData is not null;
		}

		if (string.Equals(value, "minecraft:profile", StringComparison.OrdinalIgnoreCase) ||
		    string.Equals(value, "profile", StringComparison.OrdinalIgnoreCase)) {
			return itemData.Profile is not null;
		}

		if (string.Equals(value, "minecraft:dyed_color", StringComparison.OrdinalIgnoreCase) ||
		    string.Equals(value, "dyed_color", StringComparison.OrdinalIgnoreCase)) {
			return itemData.Layer0Tint is not null
			       || (itemData.AdditionalLayerTints is not null && itemData.AdditionalLayerTints.Count > 0)
			       || itemData.DisableDefaultLayer0Tint;
		}

		return false;
	}

	private static bool MatchesDyedColor(string? value, MinecraftBlockRenderer.ItemRenderData itemData) {
		if (string.IsNullOrWhiteSpace(value) || itemData.Layer0Tint is not { } tint) {
			return false;
		}

		var pixel = tint.ToPixel<Rgba32>();
		var rgb = (pixel.R << 16) | (pixel.G << 8) | pixel.B;
		return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expected) &&
		       rgb == expected;
	}

	private static bool IsCatharsisDataTypeProperty(string property)
		=> string.Equals(property, "catharsis:data_type", StringComparison.OrdinalIgnoreCase);
}

internal sealed class ItemModelSelectorEmpty : ItemModelSelector
{
	public override string? Resolve(ItemModelContext context) => null;

	public override IReadOnlyList<string> ResolveAll(ItemModelContext context) => [];
}

internal sealed class ItemModelSelectorRangeDispatch(
	string property,
	string? dataType,
	bool normalize,
	IReadOnlyList<RangeDispatchEntry> entries,
	ItemModelSelector? fallback) : ItemModelSelector
{
	public string Property { get; } = property;
	public string? DataType { get; } = dataType;
	public bool Normalize { get; } = normalize;
	public IReadOnlyList<RangeDispatchEntry> Entries { get; } = entries;
	public ItemModelSelector? Fallback { get; } = fallback;

	public override string? Resolve(ItemModelContext context) {
		var resolved = ResolveAll(context);
		return resolved.Count > 0 ? resolved[0] : null;
	}

	public override IReadOnlyList<string> ResolveAll(ItemModelContext context) {
		var value = GetPropertyValue(context);
		if (value is null) {
			if (ShouldResolveFirstEntryOnUnsupportedSelector()) {
				var firstResolved = ResolveFirstEntry(context);
				if (firstResolved.Count > 0) {
					return firstResolved;
				}
			}

			return Fallback?.ResolveAll(context) ?? [];
		}

		// Find the highest threshold that's <= value
		RangeDispatchEntry? matchedEntry = null;
		foreach (var entry in Entries) {
			if (value >= entry.Threshold) {
				if (matchedEntry is null || entry.Threshold > matchedEntry.Value.Threshold) {
					matchedEntry = entry;
				}
			}
		}

		if (matchedEntry is not null) {
			var resolved = matchedEntry.Value.Selector?.ResolveAll(context);
			if (resolved is { Count: > 0 }) {
				return resolved;
			}
		}

		return Fallback?.ResolveAll(context) ?? [];
	}

	private bool ShouldResolveFirstEntryOnUnsupportedSelector() {
		if (string.Equals(Property, "catharsis:data_type", StringComparison.OrdinalIgnoreCase)) {
			return !CatharsisDataTypeResolver.SupportsNumericValue(DataType);
		}

		return false;
	}

	private IReadOnlyList<string> ResolveFirstEntry(ItemModelContext context) {
		if (Entries.Count == 0) {
			return [];
		}

		return Entries[0].Selector?.ResolveAll(context) ?? [];
	}

	private double? GetPropertyValue(ItemModelContext context) {
		if (string.Equals(Property, "catharsis:data_type", StringComparison.OrdinalIgnoreCase)) {
			return CatharsisDataTypeResolver.GetNumericValue(DataType, context);
		}

		// Currently only supporting "count" property
		if (string.Equals(Property, "count", StringComparison.OrdinalIgnoreCase)) {
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
	private readonly IReadOnlyList<CustomDataCompositeMapping> _compositeMappings;

	internal readonly record struct CustomDataCompositeMapping(
		IReadOnlyDictionary<string, string> ExpectedValues,
		string? Model,
		ItemModelSelector? Selector);

	public ItemModelSelectorOptimized(
		Dictionary<string, string> customDataIdToModel,
		Dictionary<string, ItemModelSelector> customDataIdToSelector,
		IReadOnlyList<CustomDataCompositeMapping> compositeMappings,
		ItemModelSelector? fallbackSelector) {
		_customDataIdToModel = customDataIdToModel;
		_customDataIdToSelector = customDataIdToSelector;
		_compositeMappings = compositeMappings;
		_fallbackSelector = fallbackSelector;
	}

	public override string? Resolve(ItemModelContext context) {
		var resolved = ResolveAll(context);
		return resolved.Count > 0 ? resolved[0] : null;
	}

	public override IReadOnlyList<string> ResolveAll(ItemModelContext context) {
		// Fast path: Check if we have a direct custom_data.id or custom_data.model match
		if (context.ItemData?.CustomData is { } customData) {
			string? customDataKey = null;

			// Try "id" field first
			if (customData.TryGetValue("id", out var idTag) &&
			    idTag is NbtString idString &&
			    !string.IsNullOrWhiteSpace(idString.Value)) {
				customDataKey = idString.Value;
			}
			// Try "model" field as fallback
			else if (customData.TryGetValue("model", out var modelTag) &&
			         modelTag is NbtString modelString &&
			         !string.IsNullOrWhiteSpace(modelString.Value)) {
				customDataKey = modelString.Value;
			}

			if (customDataKey != null) {
				// Check simple model mapping first
				if (_customDataIdToModel.TryGetValue(customDataKey, out var model)) {
					return [model];
				}

				// Check complex selector mapping
				if (_customDataIdToSelector.TryGetValue(customDataKey, out var selector)) {
					return selector.ResolveAll(context);
				}

				// If id/model didn't match, DON'T short-circuit - fall through to check
				// other properties via the fallback selector (e.g., potion + potion_type)
			}
		}

		// Fallback to the original selector tree (for non-id/model conditions like potion properties)
		if (context.ItemData?.CustomData is { } compositeCustomData && _compositeMappings.Count > 0) {
			foreach (var mapping in _compositeMappings) {
				if (!MatchesComposite(compositeCustomData, mapping.ExpectedValues)) {
					continue;
				}

				if (mapping.Selector is not null) {
					return mapping.Selector.ResolveAll(context);
				}

				if (!string.IsNullOrWhiteSpace(mapping.Model)) {
					return [mapping.Model];
				}
			}
		}

		if (_fallbackSelector is null) {
			return [];
		}

		return _fallbackSelector.ResolveAll(context);
	}

	private static bool MatchesComposite(NbtCompound customData, IReadOnlyDictionary<string, string> expected) {
		foreach (var (key, value) in expected) {
			if (!ItemModelSelectorCondition.TryMatchCustomDataValue(customData, key, value)) {
				return false;
			}
		}

		return true;
	}

	public int CustomDataMappingCount
		=> _customDataIdToModel.Count + _customDataIdToSelector.Count + _compositeMappings.Count;
}

internal static class ItemModelSelectorParser
{
	private const int MaxRecursionDepth = 10000; // Increased to handle Hypixel+ player_head.json with 8000+ levels

	public static ItemModelSelector? ParseFromRoot(JsonElement root) {
		if (root.ValueKind != JsonValueKind.Object) {
			return null;
		}

		if (root.TryGetProperty("model", out var modelElement)) {
			// Try to optimize deeply nested custom_data conditionals
			var optimized = TryOptimizeCustomDataSelector(modelElement);
			if (optimized is not null) {
				return optimized;
			}

			var selector = Parse(modelElement, 0);
			if (selector is not null) {
				return selector;
			}
		}

		if (root.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Object) {
			if (components.TryGetProperty("minecraft:model", out var componentModel)) {
				var selector = Parse(componentModel, 0);
				if (selector is not null) {
					return selector;
				}
			}
		}

		if (root.TryGetProperty("type", out var typeProperty) && typeProperty.ValueKind == JsonValueKind.String) {
			var selector = Parse(root, 0);
			if (selector is not null) {
				return selector;
			}
		}

		if (root.TryGetProperty("cases", out _) || root.TryGetProperty("on_true", out _) ||
		    root.TryGetProperty("on_false", out _)) {
			var selector = Parse(root, 0);
			if (selector is not null) {
				return selector;
			}
		}

		return null;
	}

	/// <summary>
	/// Optimizes deeply nested custom_data conditional selectors by building a lookup table.
	/// This prevents stack overflow on files like Hypixel+ player_head.json (8000+ nesting).
	/// </summary>
	private static ItemModelSelector? TryOptimizeCustomDataSelector(JsonElement element) {
		// Check if this is a deeply nested custom_data conditional structure
		if (!IsDeepCustomDataConditional(element, out var estimatedDepth)) {
			return null;
		}

		var modelMappings = new Dictionary<string, string>(StringComparer.Ordinal);
		var selectorMappings = new Dictionary<string, ItemModelSelector>(StringComparer.Ordinal);
		var compositeMappings = new List<ItemModelSelectorOptimized.CustomDataCompositeMapping>();
		var extractionResult =
			ExtractCustomDataMappings(element, modelMappings, selectorMappings, compositeMappings, 0, 100000);
		var fallbackModel = extractionResult.FallbackModel;

		if (extractionResult.EncounteredUnsupportedCondition ||
		    (modelMappings.Count == 0 && selectorMappings.Count == 0)) {
			return null;
		}

		// Parse the fallback model if we found one
		ItemModelSelector? fallbackSelector = null;
		if (fallbackModel.HasValue) {
			// If it's a string, create a simple model selector
			if (fallbackModel.Value.ValueKind == JsonValueKind.String) {
				var modelStr = fallbackModel.Value.GetString();
				if (!string.IsNullOrWhiteSpace(modelStr)) {
					fallbackSelector = new ItemModelSelectorModel(modelStr, null);
				}
			}
			// Otherwise, parse the complex selector tree (e.g., potion multi-property conditions)
			else if (fallbackModel.Value.ValueKind == JsonValueKind.Object) {
				fallbackSelector = Parse(fallbackModel.Value, 0);
			}
		}

		return new ItemModelSelectorOptimized(modelMappings, selectorMappings, compositeMappings, fallbackSelector);
	}

	private readonly record struct CustomDataExtractionResult(
		JsonElement? FallbackModel,
		bool EncounteredUnsupportedCondition);

	/// <summary>
	/// Checks if a selector is a deeply nested custom_data conditional tree.
	/// </summary>
	private static bool IsDeepCustomDataConditional(JsonElement element, out int estimatedDepth) {
		estimatedDepth = 0;

		if (element.ValueKind != JsonValueKind.Object) {
			return false;
		}

		// Handle "fallback" property wrapper
		var current = element;
		if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty("fallback", out var fallbackEl)) {
			current = fallbackEl;
		}

		// Check first few levels to see if it's custom_data conditionals
		var customDataCount = 0;
		var depth = 0;

		for (var i = 0; i < 20; i++) // Sample first 20 levels
		{
			if (current.ValueKind != JsonValueKind.Object) {
				break;
			}

			if (current.TryGetProperty("type", out var typeEl) &&
			    typeEl.ValueKind == JsonValueKind.String &&
			    typeEl.GetString() == "condition") {
				if (current.TryGetProperty("property", out var propEl) &&
				    propEl.ValueKind == JsonValueKind.String &&
				    propEl.GetString() == "component" &&
				    current.TryGetProperty("predicate", out var predEl) &&
				    predEl.ValueKind == JsonValueKind.String &&
				    predEl.GetString() == "custom_data") {
					customDataCount++;
				}

				depth++;

				// Follow on_false branch (where the nesting usually continues)
				if (current.TryGetProperty("on_false", out var onFalseEl)) {
					current = onFalseEl;
					continue;
				}
			}

			break;
		}

		// If we found many custom_data conditions in the first 20 levels, estimate full depth
		if (customDataCount >= 15) {
			estimatedDepth = depth * 400; // Rough estimate
			return true;
		}

		return false;
	}

	/// <summary>
	/// Iteratively extracts custom_data.id → model/selector mappings from a nested conditional tree.
	/// Uses a work queue to avoid stack overflow.
	/// </summary>
	private static CustomDataExtractionResult ExtractCustomDataMappings(
		JsonElement root,
		Dictionary<string, string> modelMappings,
		Dictionary<string, ItemModelSelector> selectorMappings,
		List<ItemModelSelectorOptimized.CustomDataCompositeMapping> compositeMappings,
		int startDepth,
		int maxDepth) {
		// Handle "fallback" property wrapper
		var startElement = root;
		if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("fallback", out var fallbackEl)) {
			startElement = fallbackEl;
		}

		var queue = new Queue<(JsonElement element, int depth)>();
		queue.Enqueue((startElement, startDepth));
		JsonElement? fallbackModel = null;
		var encounteredUnsupportedCondition = false;

		while (queue.Count > 0) {
			var (current, depth) = queue.Dequeue();

			if (depth > maxDepth) {
				continue;
			}

			if (current.ValueKind == JsonValueKind.String) {
				// Reached a leaf model
				fallbackModel = current;
				continue;
			}

			if (current.ValueKind != JsonValueKind.Object) {
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
			    predEl.GetString() == "custom_data") {
				// Extract the custom_data.id or composite value requirements
				string? customDataId = null;
				Dictionary<string, string>? compositeExpectedValues = null;
				var supportedKeyFound = false;

				if (current.TryGetProperty("value", out var valueEl)) {
					if (valueEl.ValueKind == JsonValueKind.String) {
						customDataId = valueEl.GetString();
						supportedKeyFound = !string.IsNullOrWhiteSpace(customDataId);
					}
					else if (valueEl.ValueKind == JsonValueKind.Object) {
						if (valueEl.TryGetProperty("id", out var idEl) &&
						    idEl.ValueKind == JsonValueKind.String) {
							customDataId = idEl.GetString();
							supportedKeyFound = true;
						}
						else if (valueEl.TryGetProperty("model", out var modelEl) &&
						         modelEl.ValueKind == JsonValueKind.String) {
							customDataId = modelEl.GetString();
							supportedKeyFound = true;
						}

						Dictionary<string, string>? extracted = null;
						foreach (var property in valueEl.EnumerateObject()) {
							if (property.Value.ValueKind == JsonValueKind.String) {
								extracted ??= new Dictionary<string, string>(StringComparer.Ordinal);
								extracted[property.Name] = property.Value.GetString()!;
							}
						}

						if (extracted is not null) {
							if (!string.IsNullOrWhiteSpace(customDataId)) {
								extracted.Remove("id");
								extracted.Remove("model");
							}

							if (extracted.Count > 0) {
								compositeExpectedValues = extracted;
								supportedKeyFound = true;
							}
						}
					}
				}

				if (!supportedKeyFound) {
					encounteredUnsupportedCondition = true;
				}

				ItemModelSelector? selector = null;
				string? model = null;

				if (current.TryGetProperty("on_true", out var onTrueEl)) {
					model = ExtractModelFromElement(onTrueEl);
					if (string.IsNullOrWhiteSpace(model)) {
						selector = Parse(onTrueEl, 0);
					}
				}

				if (!string.IsNullOrWhiteSpace(customDataId)) {
					if (!string.IsNullOrWhiteSpace(model)) {
						modelMappings[customDataId] = model!;
					}
					else if (selector is not null) {
						selectorMappings[customDataId] = selector;
					}
				}

				if (compositeExpectedValues is not null &&
				    (!string.IsNullOrWhiteSpace(model) || selector is not null)) {
					compositeMappings.Add(new ItemModelSelectorOptimized.CustomDataCompositeMapping(
						compositeExpectedValues,
						string.IsNullOrWhiteSpace(model) ? null : model,
						selector));
				}

				// Continue traversing on_false branch
				if (current.TryGetProperty("on_false", out var onFalseEl)) {
					queue.Enqueue((onFalseEl, depth + 1));
				}
			}
			else {
				// Not a custom_data condition - this might be the fallback
				fallbackModel = current;
			}
		}

		return new CustomDataExtractionResult(fallbackModel, encounteredUnsupportedCondition);
	}

	/// <summary>
	/// Extracts a model string from various selector structures.
	/// </summary>
	private static string? ExtractModelFromElement(JsonElement element) {
		if (element.ValueKind == JsonValueKind.String) {
			return element.GetString();
		}

		if (element.ValueKind == JsonValueKind.Object) {
			// Check for direct model property
			if (element.TryGetProperty("model", out var modelEl) &&
			    modelEl.ValueKind == JsonValueKind.String) {
				return modelEl.GetString();
			}

			// Check for nested structure
			if (element.TryGetProperty("type", out var typeEl) &&
			    typeEl.ValueKind == JsonValueKind.String) {
				var type = typeEl.GetString();
				if (type == "model" && element.TryGetProperty("model", out modelEl) &&
				    modelEl.ValueKind == JsonValueKind.String) {
					return modelEl.GetString();
				}
			}
		}

		return null;
	}

	public static ItemModelSelector? Parse(JsonElement element, int depth) {
		// Prevent stack overflow on extremely deeply nested JSON
		if (depth > MaxRecursionDepth) {
			// Return null to allow fallback to on_false or other graceful degradation
			return null;
		}

		// Use iterative parsing with explicit stack to avoid stack overflow on deeply nested JSON
		// (e.g., Hypixel+ player_head.json with 8000+ nesting levels)
		while (true) {
			if (element.ValueKind == JsonValueKind.String) {
				return new ItemModelSelectorModel(element.GetString(), null);
			}

			if (element.ValueKind != JsonValueKind.Object) {
				return null;
			}

			// Unwrap pass-through nodes that only forward to on_false/on_true without specifying a type
			if (!element.TryGetProperty("type", out _)
			    && !element.TryGetProperty("cases", out _)
			    && !element.TryGetProperty("entries", out _)
			    && !element.TryGetProperty("model", out _)) {
				if (element.TryGetProperty("on_false", out var wrapperOnFalse)) {
					element = wrapperOnFalse;
					depth++;
					continue;
				}

				if (element.TryGetProperty("on_true", out var wrapperOnTrue)) {
					element = wrapperOnTrue;
					depth++;
					continue;
				}
			}

			var type = DetermineSelectorType(element);
			return type switch {
				"model" => new ItemModelSelectorModel(GetString(element, "model"), GetString(element, "base")),
				"special" => new ItemModelSelectorSpecial(GetString(element, "base"),
					Parse(element.TryGetProperty("model", out var nested) ? nested : default, depth + 1)),
				"catharsis:fallthrough" => new ItemModelSelectorFallthrough(),
				"condition" => ParseCondition(element, depth + 1),
				"select" => ParseSelect(element, depth + 1),
				"range_dispatch" => ParseRangeDispatch(element, depth + 1),
				"composite" => ParseComposite(element, depth + 1),
				"empty" => new ItemModelSelectorEmpty(),
				_ => CreateFallbackSelector(element)
			};
		}
	}

	private static string DetermineSelectorType(JsonElement element) {
		if (element.ValueKind != JsonValueKind.Object) {
			return "model";
		}

		if (element.TryGetProperty("type", out var typeProperty) && typeProperty.ValueKind == JsonValueKind.String) {
			return NormalizeType(typeProperty.GetString());
		}

		if (element.TryGetProperty("cases", out var casesElement) && casesElement.ValueKind == JsonValueKind.Array) {
			return "select";
		}

		if (element.TryGetProperty("entries", out var entriesElement) &&
		    entriesElement.ValueKind == JsonValueKind.Array) {
			return "range_dispatch";
		}

		if (element.TryGetProperty("models", out var modelsElement) && modelsElement.ValueKind == JsonValueKind.Array) {
			return "composite";
		}

		if ((element.TryGetProperty("on_true", out _) || element.TryGetProperty("on_false", out _)) &&
		    element.TryGetProperty("property", out _)) {
			return "condition";
		}

		if (element.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.Object) {
			return DetermineSelectorType(modelElement);
		}

		return "model";
	}

	private static ItemModelSelector? ParseCondition(JsonElement element, int depth) {
		var property = GetString(element, "property") ?? string.Empty;
		var dataType = GetString(element, "data_type");
		var predicate = GetString(element, "predicate");
		var component = GetString(element, "component");
		var amount = GetInt(element, "amount");
		var gemstoneSlot = GetString(element, "slot");
		var gemstoneQuality = GetString(element, "quality");

		IReadOnlyDictionary<string, string>? valueProperties = null;
		string? valueLiteral = null;
		if (element.TryGetProperty("value", out var valueElement)) {
			valueProperties = ParseStringMap(valueElement);
			if (valueProperties is null && valueElement.ValueKind == JsonValueKind.String) {
				valueLiteral = valueElement.GetString();
			}
			else if (valueProperties is null) {
				valueLiteral = valueElement.GetRawText();
			}
		}

		var onTrue = element.TryGetProperty("on_true", out var onTrueElement) ? Parse(onTrueElement, depth + 1) : null;
		var onFalse = element.TryGetProperty("on_false", out var onFalseElement)
			? Parse(onFalseElement, depth + 1)
			: null;

		// If parsing on_true failed due to depth limit or unsupported selector, fall back to on_false
		if (onTrue is null && onFalse is not null) {
			return onFalse;
		}

		return new ItemModelSelectorCondition(property, dataType, predicate, component, valueProperties, valueLiteral, amount,
			gemstoneSlot, gemstoneQuality, onTrue, onFalse);
	}

	private static ItemModelSelector? CreateFallbackSelector(JsonElement element) {
		var directModel = GetString(element, "model") ?? GetString(element, "base");
		return string.IsNullOrWhiteSpace(directModel) ? null : new ItemModelSelectorModel(directModel, null);
	}

	private static ItemModelSelector? ParseComposite(JsonElement element, int depth) {
		if (!element.TryGetProperty("models", out var modelsArray) || modelsArray.ValueKind != JsonValueKind.Array) {
			return null;
		}

		var selectors = new List<ItemModelSelector>();
		foreach (var modelElement in modelsArray.EnumerateArray()) {
			var parsed = Parse(modelElement, depth);
			if (parsed is not null) {
				selectors.Add(parsed);
			}
		}

		return selectors.Count == 0 ? null : new ItemModelSelectorComposite(selectors);
	}

	private static ItemModelSelector? ParseSelect(JsonElement element, int depth) {
		var property = GetString(element, "property") ?? string.Empty;
		var dataType = GetString(element, "data_type");
		var component = GetString(element, "component");
		var cases = new List<ItemModelSelectorSelectCase>();
		if (element.TryGetProperty("cases", out var casesElement) && casesElement.ValueKind == JsonValueKind.Array) {
			foreach (var caseElement in casesElement.EnumerateArray()) {
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
		return new ItemModelSelectorSelect(property, dataType, component, cases, fallback);
	}

	private static ItemModelSelector? ParseRangeDispatch(JsonElement element, int depth) {
		var property = GetString(element, "property") ?? string.Empty;
		var dataType = GetString(element, "data_type");
		var normalize = element.TryGetProperty("normalize", out var normalizeElement) &&
		                normalizeElement.ValueKind == JsonValueKind.True;

		var entries = new List<RangeDispatchEntry>();
		if (element.TryGetProperty("entries", out var entriesElement) &&
		    entriesElement.ValueKind == JsonValueKind.Array) {
			foreach (var entryElement in entriesElement.EnumerateArray()) {
				if (entryElement.TryGetProperty("threshold", out var thresholdElement) &&
				    thresholdElement.TryGetDouble(out var threshold)) {
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
		return new ItemModelSelectorRangeDispatch(property, dataType, normalize, entries, fallback);
	}

	private static IReadOnlyDictionary<string, string>? ParseStringMap(JsonElement element) {
		if (element.ValueKind != JsonValueKind.Object) {
			return null;
		}

		var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var property in element.EnumerateObject()) {
			var value = property.Value.ValueKind switch {
				JsonValueKind.String => property.Value.GetString() ?? string.Empty,
				JsonValueKind.Number => property.Value.TryGetInt64(out var longValue)
					? longValue.ToString(CultureInfo.InvariantCulture)
					: property.Value.GetDouble().ToString(CultureInfo.InvariantCulture),
				JsonValueKind.True => "true",
				JsonValueKind.False => "false",
				JsonValueKind.Null => "null",
				_ => property.Value.GetRawText()
			};

			if (!string.IsNullOrWhiteSpace(property.Name) && !string.IsNullOrWhiteSpace(value)) {
				map[property.Name] = value;
			}
		}

		return map.Count > 0 ? map : null;
	}

	private static IReadOnlyList<string> ParseWhen(JsonElement element) {
		if (TryReadCaseValue(element, out var value)) {
			return string.IsNullOrWhiteSpace(value)
				? Array.Empty<string>()
				: new[] { value };
		}

		if (element.ValueKind == JsonValueKind.Array) {
			var values = new List<string>();
			foreach (var entry in element.EnumerateArray()) {
				if (TryReadCaseValue(entry, out value) && !string.IsNullOrWhiteSpace(value)) {
					values.Add(value);
				}
			}

			return values;
		}

		return [];
	}

	private static bool TryReadCaseValue(JsonElement element, out string? value) {
		value = element.ValueKind switch {
			JsonValueKind.String => element.GetString(),
			JsonValueKind.Number => element.TryGetInt64(out var longValue)
				? longValue.ToString(CultureInfo.InvariantCulture)
				: element.GetDouble().ToString(CultureInfo.InvariantCulture),
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			_ => null
		};

		return value is not null;
	}

	private static string? GetString(JsonElement element, string propertyName)
		=> element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
			? property.GetString()
			: null;

	private static int? GetInt(JsonElement element, string propertyName)
		=> element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
			? value
			: null;

	private static string NormalizeType(string? value) {
		if (string.IsNullOrWhiteSpace(value)) {
			return "model";
		}

		var type = value.Trim();
		if (type.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase)) {
			type = type[10..];
		}

		return type.ToLowerInvariant();
	}
}
