using System;
using System.Collections.Generic;
using System.IO;
using MinecraftRenderer.Nbt;

namespace MinecraftRenderer.Hypixel;

/// <summary>
/// Parses Hypixel inventory data from base64-encoded, gzipped NBT format.
/// </summary>
public static class InventoryParser
{
	/// <summary>
	/// Parse base64-encoded, gzipped NBT inventory data into structured items.
	/// </summary>
	/// <param name="base64Data">Base64 string from Hypixel API.</param>
	/// <returns>List of parsed items.</returns>
	public static List<HypixelItemData> ParseInventory(string base64Data)
	{
		var bytes = Convert.FromBase64String(base64Data);
		using var stream = new MemoryStream(bytes);
		var doc = NbtParser.ParseBinary(stream);

		return ExtractItems(doc.Root);
	}

	/// <summary>
	/// Parse already-decoded NBT data into structured items.
	/// </summary>
	/// <param name="root">Root NBT tag (compound or list).</param>
	/// <returns>List of parsed items.</returns>
	public static List<HypixelItemData> ParseInventory(NbtTag root)
	{
		return ExtractItems(root);
	}

	private static List<HypixelItemData> ExtractItems(NbtTag root)
	{
		var items = new List<HypixelItemData>();

		// Try to find the item list - Hypixel uses various structures
		NbtList? itemList = null;

		if (root is NbtCompound compound)
		{
			// Common patterns: nested under "i", "items", "inventory", or "data" key
			itemList = compound.GetList("i")
			           ?? compound.GetList("items")
			           ?? compound.GetList("inventory")
			           ?? compound.GetList("data");
		}
		else if (root is NbtList list)
		{
			// Root is already a list
			itemList = list;
		}

		if (itemList == null)
		{
			return items;
		}

		foreach (var element in itemList)
		{
			if (element is not NbtCompound itemCompound)
				continue;

			var item = ParseItem(itemCompound);
			if (item != null)
			{
				items.Add(item);
			}
		}

		return items;
	}

	private static HypixelItemData? ParseItem(NbtCompound itemCompound)
	{
		// Skip empty slots
		if (!itemCompound.ContainsKey("id") && !itemCompound.ContainsKey("ID"))
		{
			return null;
		}

		// Extract item ID - can be string (modern) or short (1.8.9 numeric ID)
		string? itemIdTag = itemCompound.GetString("id") ?? itemCompound.GetString("ID");
		short? numericId = null;

		if (itemIdTag == null)
		{
			// Try short for 1.8.9 numeric IDs
			numericId = itemCompound.GetShort("id") ?? itemCompound.GetShort("ID");
			if (numericId == null)
			{
				return null;
			}

			itemIdTag = numericId.Value.ToString();
		}

		// Normalize item ID (1.8.9 uses numeric IDs, newer uses namespaced)
		var itemId = NormalizeItemId(itemIdTag, itemCompound);

		// Extract count
		var count = itemCompound.GetByte("Count") ?? itemCompound.GetByte("count") ?? 1;

		// Extract damage/data value (important for 1.8.9 item variants)
		var damage = itemCompound.GetShort("Damage") ?? itemCompound.GetShort("damage") ?? 0;

		// Extract tag compound (contains all the rich data)
		var tag = itemCompound.GetCompound("tag") ?? itemCompound.GetCompound("Tag");

		return new HypixelItemData(
			ItemId: itemId,
			Count: count,
			Damage: damage,
			Tag: tag,
			NumericId: numericId
		);
	}

	private static string NormalizeItemId(string rawId, NbtCompound itemCompound)
	{
		// If it's already a namespaced ID, use it
		if (rawId.Contains(':'))
		{
			return rawId.ToLowerInvariant();
		}

		// For numeric IDs (1.8.9), we need to map to modern namespaced IDs
		// This is a complex mapping - for now, treat as a Minecraft numeric ID
		// The renderer will need to handle this mapping separately
		if (short.TryParse(rawId, out var numericId))
		{
			// Common Skyblock items often have the actual ID in ExtraAttributes
			var skyblockId = itemCompound.GetCompound("tag")
				?.GetCompound("ExtraAttributes")
				?.GetString("id");

			if (!string.IsNullOrEmpty(skyblockId))
			{
				// Return the Skyblock ID prefixed for later resolution
				return $"skyblock:{skyblockId.ToLowerInvariant()}";
			}

			// Fallback: return numeric ID with prefix for later mapping
			return $"minecraft.numeric:{numericId}";
		}

		// Unknown format, return as-is with minecraft namespace
		return $"minecraft:{rawId.ToLowerInvariant()}";
	}
}