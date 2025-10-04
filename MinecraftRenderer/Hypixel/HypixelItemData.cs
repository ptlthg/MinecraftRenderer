using MinecraftRenderer.Nbt;

namespace MinecraftRenderer.Hypixel;

/// <summary>
/// Represents parsed item data from Hypixel inventories (1.8.9 format).
/// Designed to be versatile and map cleanly to texture lookups.
/// </summary>
public sealed record HypixelItemData(
	string ItemId,
	int Count = 1,
	short Damage = 0,
	NbtCompound? Tag = null,
	short? NumericId = null)
{
	/// <summary>
	/// Helper to extract Skyblock ID from ExtraAttributes.id if present.
	/// </summary>
	public string? SkyblockId => Tag
		?.GetCompound("ExtraAttributes")
		?.GetString("id");

	/// <summary>
	/// Helper to extract custom texture data (e.g., Firmament overrides).
	/// </summary>
	public NbtCompound? CustomData => Tag
		?.GetCompound("ExtraAttributes")
		?.GetCompound("customData");

	/// <summary>
	/// Helper to extract gem data.
	/// </summary>
	public NbtCompound? Gems => Tag
		?.GetCompound("ExtraAttributes")
		?.GetCompound("gems");

	/// <summary>
	/// Helper to extract enchantments.
	/// </summary>
	public NbtCompound? Enchantments => Tag
		?.GetCompound("ExtraAttributes")
		?.GetCompound("enchantments");

	/// <summary>
	/// Helper to extract attributes.
	/// </summary>
	public NbtCompound? Attributes => Tag
		?.GetCompound("ExtraAttributes")
		?.GetCompound("attributes");

	/// <summary>
	/// Display name from the item (may include formatting codes).
	/// </summary>
	public string? DisplayName => Tag
		?.GetCompound("display")
		?.GetString("Name");
}