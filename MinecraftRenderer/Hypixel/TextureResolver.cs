using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MinecraftRenderer.Nbt;
using SixLabors.ImageSharp;

namespace MinecraftRenderer.Hypixel;

/// <summary>
/// Resolves Hypixel Skyblock items to texture identifiers for rendering.
/// </summary>
public static class TextureResolver
{
	/// <summary>
	/// Get a deterministic texture ID for a Hypixel item that can be used for texture pack lookups.
	/// </summary>
	/// <param name="item">The parsed Hypixel item data.</param>
	/// <returns>A string identifier that can be used to look up or cache textures.</returns>
	public static string GetTextureId(HypixelItemData item)
	{
		// Priority:
		// 1. CustomData override (for Firmament texture packs)
		// 2. Skyblock ID + attributes (for Kuudra armor, etc.)
		// 3. Skyblock ID alone
		// 4. Minecraft ID + damage value
		var id = RawString();
		
		// Base64 encode to shorten length and ensure filesystem safety
		var bytes = System.Text.Encoding.UTF8.GetBytes(id);
		return Convert.ToBase64String(bytes)
			.Replace('/', '_') // URL and filesystem safe
			.Replace('+', '-') // URL and filesystem safe
			.TrimEnd('=');     // Remove padding for brevity

		string RawString()
		{
			if (item.CustomData != null)
			{
				// Check for Firmament's texture override
				var customTexture = item.CustomData.GetString("texture");
				if (!string.IsNullOrEmpty(customTexture))
				{
					return $"custom:{customTexture}";
				}
			}

			if (item.SkyblockId != null)
			{
				var descriptor = $"{HypixelPrefixes.Skyblock}{item.SkyblockId.ToLowerInvariant()}";
				var query = BuildSkyblockQuery(item);
				return string.IsNullOrEmpty(query) ? descriptor : $"{descriptor}?{query}";
			}

			// Fallback to Minecraft ID with damage for 1.8.9 items
			return item.Damage != 0 ? $"{item.ItemId}:{item.Damage}" : item.ItemId;
		}
	}

	private static string BuildSkyblockQuery(HypixelItemData item)
	{
		var parameters = new List<string>();
		if (item.Attributes is { Count: > 0 })
		{
			var attrs = string.Join(",", item.Attributes.Keys.OrderBy(static key => key));
			if (!string.IsNullOrEmpty(attrs))
			{
				parameters.Add($"attrs={attrs}");
			}
		}

		var fallbackBase = DetermineSkyblockFallbackBase(item);
		if (!string.IsNullOrWhiteSpace(fallbackBase))
		{
			parameters.Add($"base={fallbackBase}");
		}

		if (item.NumericId.HasValue)
		{
			parameters.Add($"numeric={item.NumericId.Value.ToString(CultureInfo.InvariantCulture)}");
		}

		return string.Join("&", parameters);
	}

	private static string? DetermineSkyblockFallbackBase(HypixelItemData item)
	{
		if (item.NumericId.HasValue && LegacyItemMappings.TryMapNumericId(item.NumericId.Value, out var mapped) &&
		    !string.IsNullOrWhiteSpace(mapped))
		{
			return mapped;
		}

		if (!string.IsNullOrWhiteSpace(item.ItemId))
		{
			if (item.ItemId.StartsWith(HypixelPrefixes.Numeric, StringComparison.OrdinalIgnoreCase))
			{
				var numericSpan = item.ItemId.AsSpan(HypixelPrefixes.Numeric.Length);
				if (short.TryParse(numericSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericBase) &&
				    LegacyItemMappings.TryMapNumericId(numericBase, out var mapped1) &&
				    !string.IsNullOrWhiteSpace(mapped1))
				{
					return mapped1;
				}
			}
			
			return item.ItemId;
		}

		return null;
	}

	/// <summary>
	/// Decode a texture identifier produced by <see cref="GetTextureId"/> back to its raw descriptor string.
	/// </summary>
	/// <param name="textureId">The encoded texture identifier.</param>
	/// <returns>The decoded descriptor string.</returns>
	/// <exception cref="FormatException">Thrown when the texture id is not a valid Base64 string.</exception>
	public static string DecodeTextureId(string textureId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(textureId);
		if (!TryDecodeTextureId(textureId, out var decoded))
		{
			throw new FormatException($"Texture id '{textureId}' is not a valid encoded identifier.");
		}

		return decoded;
	}

	/// <summary>
	/// Try to decode an encoded texture identifier.
	/// </summary>
	/// <param name="textureId">The encoded texture identifier.</param>
	/// <param name="decoded">Outputs the decoded descriptor when the method returns true.</param>
	/// <returns>True when the identifier could be decoded successfully; otherwise false.</returns>
	public static bool TryDecodeTextureId(string textureId, out string decoded)
	{
		decoded = string.Empty;
		if (string.IsNullOrWhiteSpace(textureId))
		{
			return false;
		}

		var base64 = textureId
			.Replace('-', '+')
			.Replace('_', '/');

		var padding = (4 - base64.Length % 4) % 4;
		if (padding > 0)
		{
			base64 = base64.PadRight(base64.Length + padding, '=');
		}

		try
		{
			var bytes = Convert.FromBase64String(base64);
			decoded = Encoding.UTF8.GetString(bytes);
			return true;
		}
		catch (FormatException)
		{
			return false;
		}
	}

	// TODO: Move this to a separate integration class to avoid circular dependencies
	// /// <summary>
	// /// Convert a Hypixel item to ItemRenderData for rendering.
	// /// </summary>
	// /// <param name="item">The parsed Hypixel item data.</param>
	// /// <returns>ItemRenderData that can be passed to the renderer.</returns>
	// public static global::MinecraftRenderer.ItemRenderData ToItemRenderData(HypixelItemData item)
	// {
	//     return new global::MinecraftRenderer.ItemRenderData(
	//         CustomData: item.CustomData,
	//         // Could add tint extraction here based on leather armor dyes, etc.
	//         Layer0Tint: null,
	//         AdditionalLayerTints: null,
	//         DisableDefaultLayer0Tint: false,
	//         Profile: null
	//     );
	// }

	/// <summary>
	/// Extract a potential texture path from CustomData if present (Firmament format).
	/// </summary>
	/// <param name="item">The parsed Hypixel item data.</param>
	/// <returns>The texture path string, or null if not present.</returns>
	public static string? GetCustomTexturePath(HypixelItemData item)
	{
		return item.CustomData?.GetString("texture");
	}
}