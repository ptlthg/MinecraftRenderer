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
			// For items with attributes (Kuudra armor, etc.), include them in the ID
			if (item.Attributes != null && item.Attributes.Count > 0)
			{
				// Build a deterministic string from attributes
				var attrs = string.Join(",", item.Attributes.Keys.OrderBy(k => k));
				return $"skyblock:{item.SkyblockId.ToLowerInvariant()}?attrs={attrs}";
			}

			// For items with gems, include gem types
			if (item.Gems != null && item.Gems.Count > 0)
			{
				var gems = string.Join(",", item.Gems.Keys.OrderBy(k => k));
				return $"skyblock:{item.SkyblockId.ToLowerInvariant()}?gems={gems}";
			}

			// Plain Skyblock ID
			return $"skyblock:{item.SkyblockId.ToLowerInvariant()}";
		}

		// Fallback to Minecraft ID with damage for 1.8.9 items
		if (item.Damage != 0)
		{
			return $"{item.ItemId}:{item.Damage}";
		}

		return item.ItemId;
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