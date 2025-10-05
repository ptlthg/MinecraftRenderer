using MinecraftRenderer.Hypixel;
using MinecraftRenderer.Nbt;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CreateAtlases;

/// <summary>
/// Generates debug atlases from Hypixel inventory data (base64 gzipped NBT).
/// </summary>
public static class HypixelInventoryAtlasGenerator
{
    public static void GenerateInventoryAtlas(
        MinecraftRenderer.MinecraftBlockRenderer renderer,
        string inventoryDataPath,
        string outputDirectory,
        string? texturePackId = null,
        int tileSize = 128,
        int columns = 8)
    {
        Console.WriteLine($"[HypixelInventoryAtlas] Reading inventory data from: {inventoryDataPath}");
        
        if (!File.Exists(inventoryDataPath))
        {
            throw new FileNotFoundException($"Inventory data file not found: {inventoryDataPath}");
        }

        var base64Data = File.ReadAllText(inventoryDataPath).Trim();
        var items = InventoryParser.ParseInventory(base64Data);
        
        Console.WriteLine($"[HypixelInventoryAtlas] Parsed {items.Count} items from inventory");

        if (items.Count == 0)
        {
            Console.WriteLine("[HypixelInventoryAtlas] No items to render");
            return;
        }

        // Calculate rows needed
        var rows = (items.Count + columns - 1) / columns;
        
        // Create atlas
        var atlasWidth = columns * tileSize;
        var atlasHeight = rows * tileSize;
        
        using var atlas = new Image<Rgba32>(atlasWidth, atlasHeight);
        atlas.Mutate(ctx => ctx.BackgroundColor(Color.Transparent));

        var manifest = new List<object>();
        var errorCount = 0;

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var col = i % columns;
            var row = i / columns;
            var x = col * tileSize;
            var y = row * tileSize;

            var textureId = TextureResolver.GetTextureId(item);
            
            Console.WriteLine($"[{i + 1}/{items.Count}] Rendering {item.SkyblockId ?? item.ItemId} (texture: {textureId})");

            try
            {
                // For 1.8.9 Hypixel items, we need to convert ExtraAttributes to custom_data
                // ExtraAttributes.id (Skyblock ID) -> custom_data.id
                MinecraftRenderer.Nbt.NbtCompound? customData = null;
                if (item.Tag?.GetCompound("ExtraAttributes") is { } extraAttribs)
                {
                    var skyblockId = extraAttribs.GetString("id");
                    if (!string.IsNullOrEmpty(skyblockId))
                    {
                        // Create a custom_data compound with the Skyblock ID
                        customData = new MinecraftRenderer.Nbt.NbtCompound(new[]
                        {
                            new KeyValuePair<string, MinecraftRenderer.Nbt.NbtTag>("id", new MinecraftRenderer.Nbt.NbtString(skyblockId))
                        });
                        Console.WriteLine($"  Created custom_data with id='{skyblockId}'");
                    }
                }
                
                // Try to render the item
                var itemRenderData = new MinecraftRenderer.MinecraftBlockRenderer.ItemRenderData(
                    CustomData: customData,
                    Layer0Tint: null,
                    AdditionalLayerTints: null,
                    DisableDefaultLayer0Tint: false,
                    Profile: null
                );

                // For Hypixel Skyblock items (1.8.9 format), we need to:
                // 1. Use the numeric ID to determine the base Minecraft item
                // 2. Let the texture pack handle the custom model via customData
                string? itemKey = null;
                
                if (item.NumericId.HasValue)
                {
                    // Map 1.8.9 numeric ID to modern item name
                    if (LegacyItemMappings.TryMapNumericId(item.NumericId.Value, out var mappedId))
                    {
                        itemKey = mappedId;
                    }
                    else
                    {
                        itemKey = "minecraft:diamond_sword";
                    }

                    Console.WriteLine($"  Using base item {itemKey} (numeric ID: {item.NumericId})");
                }
                else if (item.ItemId.StartsWith("minecraft:"))
                {
                    itemKey = item.ItemId;
                }
                else
                {
                    // Fallback for unknown format
                    itemKey = "minecraft:diamond_sword";
                    Console.WriteLine($"  WARNING: Unknown item format, using fallback");
                }

                // We need to explicitly pass PackIds in the render options for the texture pack to be used
                var renderOptions = new MinecraftRenderer.MinecraftBlockRenderer.BlockRenderOptions(
                    Size: tileSize,
                    UseGuiTransform: true,
                    PackIds: texturePackId != null ? new[] { texturePackId } : null,
                    ItemData: itemRenderData
                );

                Console.WriteLine($"  RenderOptions: UseGuiTransform={renderOptions.UseGuiTransform}, PackIds={string.Join(",", renderOptions.PackIds ?? [])}");

                // Debug: Check what model is being resolved
                try
                {
                    var resourceInfo = renderer.ComputeResourceId(itemKey, renderOptions);
                    Console.WriteLine($"  Resolved model: {resourceInfo.Model ?? "(null)"} from pack: {resourceInfo.SourcePackId}");
                    if (resourceInfo.Textures.Count > 0)
                    {
                        Console.WriteLine($"    Textures: {string.Join(", ", resourceInfo.Textures.Take(3))}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Failed to compute resource ID: {ex.Message}");
                }

                using var itemImage = renderer.RenderItem(
                    itemKey,
                    itemRenderData,
                    renderOptions
                );

                // Draw item onto atlas
                atlas.Mutate(ctx => ctx.DrawImage(itemImage, new Point(x, y), 1f));

                manifest.Add(new
                {
                    index = i,
                    skyblock_id = item.SkyblockId,
                    item_id = item.ItemId,
                    texture_id = textureId,
                    display_name = item.DisplayName,
                    count = item.Count,
                    damage = item.Damage,
                    has_enchantments = item.Enchantments != null,
                    has_gems = item.Gems != null,
                    has_attributes = item.Attributes != null,
                    position = new { x = col, y = row }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERROR: {ex.Message}");
                errorCount++;
                
                manifest.Add(new
                {
                    index = i,
                    skyblock_id = item.SkyblockId,
                    item_id = item.ItemId,
                    texture_id = textureId,
                    display_name = item.DisplayName,
                    error = ex.Message,
                    position = new { x = col, y = row }
                });
            }
        }

        // Save atlas
        Directory.CreateDirectory(outputDirectory);
        var atlasFileName = $"hypixel_inventory_{Path.GetFileNameWithoutExtension(inventoryDataPath)}.png";
        var atlasPath = Path.Combine(outputDirectory, atlasFileName);
        atlas.SaveAsPng(atlasPath);
        
        // Save manifest
        var manifestFileName = $"hypixel_inventory_{Path.GetFileNameWithoutExtension(inventoryDataPath)}.json";
        var manifestPath = Path.Combine(outputDirectory, manifestFileName);
        var manifestJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            source = inventoryDataPath,
            texture_pack = texturePackId,
            tile_size = tileSize,
            columns = columns,
            rows = rows,
            total_items = items.Count,
            errors = errorCount,
            items = manifest
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(manifestPath, manifestJson);

        Console.WriteLine($"[HypixelInventoryAtlas] Saved atlas: {atlasPath}");
        Console.WriteLine($"[HypixelInventoryAtlas] Saved manifest: {manifestPath}");
        Console.WriteLine($"[HypixelInventoryAtlas] Rendered {items.Count - errorCount}/{items.Count} items successfully");
    }
}
