using MinecraftRenderer.Nbt;

namespace MinecraftRenderer.Hypixel;

/// <summary>
/// Represents parsed item data from Hypixel inventories (1.8.9 format).
/// Designed to be versatile and map cleanly to texture lookups.
/// </summary>
public sealed record HypixelItemData(
    /// <summary>The Minecraft item ID (e.g., "minecraft:diamond_sword").</summary>
    string ItemId,
    
    /// <summary>Stack count.</summary>
    int Count = 1,
    
    /// <summary>Damage/data value (relevant for 1.8.9 items).</summary>
    short Damage = 0,
    
    /// <summary>Full NBT tag compound from the item, if present.</summary>
    NbtCompound? Tag = null,
    
    /// <summary>Raw numeric item ID from 1.8.9 format, if present.</summary>
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
