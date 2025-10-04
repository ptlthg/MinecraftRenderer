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
                    itemKey = MapNumericIdToItem(item.NumericId.Value);
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

                Console.WriteLine($"  RenderOptions: UseGuiTransform={renderOptions.UseGuiTransform}, PackIds={string.Join(",", renderOptions.PackIds ?? Array.Empty<string>())}");

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

    /// <summary>
    /// Map 1.8.9 numeric item IDs to modern namespaced item names.
    /// Reference: https://minecraft.wiki/w/Java_Edition_data_values/Pre-flattening/Item_IDs
    /// </summary>
    private static string MapNumericIdToItem(short numericId)
    {
        // Common items used in Hypixel Skyblock
        return numericId switch
        {
            // Tools & Weapons
            256 => "minecraft:iron_shovel",
            257 => "minecraft:iron_pickaxe",
            258 => "minecraft:iron_axe",
            259 => "minecraft:flint_and_steel",
            261 => "minecraft:bow",
            267 => "minecraft:iron_sword",
            268 => "minecraft:wooden_sword",
            269 => "minecraft:wooden_shovel",
            270 => "minecraft:wooden_pickaxe",
            271 => "minecraft:wooden_axe",
            272 => "minecraft:stone_sword",
            273 => "minecraft:stone_shovel",
            274 => "minecraft:stone_pickaxe",
            275 => "minecraft:stone_axe",
            276 => "minecraft:diamond_sword",
            277 => "minecraft:diamond_shovel",
            278 => "minecraft:diamond_pickaxe",
            279 => "minecraft:diamond_axe",
            283 => "minecraft:golden_sword",
            284 => "minecraft:golden_shovel",
            285 => "minecraft:golden_pickaxe",
            286 => "minecraft:golden_axe",
            290 => "minecraft:wooden_hoe",
            291 => "minecraft:stone_hoe",
            292 => "minecraft:iron_hoe",
            293 => "minecraft:diamond_hoe",
            294 => "minecraft:golden_hoe",
            346 => "minecraft:fishing_rod",
            359 => "minecraft:shears",
            
            // Armor
            298 => "minecraft:leather_helmet",
            299 => "minecraft:leather_chestplate",
            300 => "minecraft:leather_leggings",
            301 => "minecraft:leather_boots",
            302 => "minecraft:chainmail_helmet",
            303 => "minecraft:chainmail_chestplate",
            304 => "minecraft:chainmail_leggings",
            305 => "minecraft:chainmail_boots",
            306 => "minecraft:iron_helmet",
            307 => "minecraft:iron_chestplate",
            308 => "minecraft:iron_leggings",
            309 => "minecraft:iron_boots",
            310 => "minecraft:diamond_helmet",
            311 => "minecraft:diamond_chestplate",
            312 => "minecraft:diamond_leggings",
            313 => "minecraft:diamond_boots",
            314 => "minecraft:golden_helmet",
            315 => "minecraft:golden_chestplate",
            316 => "minecraft:golden_leggings",
            317 => "minecraft:golden_boots",
            
            // Food & Consumables
            282 => "minecraft:mushroom_stew",
            297 => "minecraft:bread",
            319 => "minecraft:porkchop",
            320 => "minecraft:cooked_porkchop",
            322 => "minecraft:golden_apple",
            349 => "minecraft:cod",
            350 => "minecraft:cooked_cod",
            357 => "minecraft:cookie",
            360 => "minecraft:melon_slice",
            363 => "minecraft:beef",
            364 => "minecraft:cooked_beef",
            365 => "minecraft:chicken",
            366 => "minecraft:cooked_chicken",
            367 => "minecraft:rotten_flesh",
            391 => "minecraft:carrot",
            392 => "minecraft:potato",
            393 => "minecraft:baked_potato",
            396 => "minecraft:golden_carrot",
            400 => "minecraft:pumpkin_pie",
            
            // Blocks
            1 => "minecraft:stone",
            2 => "minecraft:grass_block",
            3 => "minecraft:dirt",
            4 => "minecraft:cobblestone",
            5 => "minecraft:oak_planks",
            12 => "minecraft:sand",
            13 => "minecraft:gravel",
            17 => "minecraft:oak_log",
            24 => "minecraft:sandstone",
            35 => "minecraft:white_wool",
            41 => "minecraft:gold_block",
            42 => "minecraft:iron_block",
            45 => "minecraft:bricks",
            46 => "minecraft:tnt",
            47 => "minecraft:bookshelf",
            48 => "minecraft:mossy_cobblestone",
            49 => "minecraft:obsidian",
            53 => "minecraft:oak_stairs",
            54 => "minecraft:chest",
            57 => "minecraft:diamond_block",
            58 => "minecraft:crafting_table",
            61 => "minecraft:furnace",
            65 => "minecraft:ladder",
            80 => "minecraft:snow_block",
            82 => "minecraft:clay",
            86 => "minecraft:pumpkin",
            87 => "minecraft:netherrack",
            88 => "minecraft:soul_sand",
            89 => "minecraft:glowstone",
            91 => "minecraft:jack_o_lantern",
            95 => "minecraft:white_stained_glass",
            98 => "minecraft:stone_bricks",
            103 => "minecraft:melon",
            121 => "minecraft:end_stone",
            155 => "minecraft:quartz_block",
            159 => "minecraft:white_terracotta",
            172 => "minecraft:terracotta",
            174 => "minecraft:packed_ice",
            
            // Materials
            263 => "minecraft:coal",
            264 => "minecraft:diamond",
            265 => "minecraft:iron_ingot",
            266 => "minecraft:gold_ingot",
            280 => "minecraft:stick",
            287 => "minecraft:string",
            289 => "minecraft:gunpowder",
            318 => "minecraft:flint",
            325 => "minecraft:bucket",
            331 => "minecraft:redstone",
            337 => "minecraft:clay_ball",
            339 => "minecraft:paper",
            341 => "minecraft:slime_ball",
            344 => "minecraft:egg",
            348 => "minecraft:glowstone_dust",
            351 => "minecraft:bone_meal", // Note: dyes have damage values
            352 => "minecraft:bone",
            353 => "minecraft:sugar",
            354 => "minecraft:cake",
            371 => "minecraft:gold_nugget",
            372 => "minecraft:nether_wart",
            373 => "minecraft:potion",
            375 => "minecraft:spider_eye",
            376 => "minecraft:fermented_spider_eye",
            377 => "minecraft:blaze_powder",
            378 => "minecraft:magma_cream",
            379 => "minecraft:brewing_stand",
            381 => "minecraft:ender_eye",
            382 => "minecraft:glistering_melon_slice",
            383 => "minecraft:spawn_egg",
            384 => "minecraft:experience_bottle",
            385 => "minecraft:fire_charge",
            388 => "minecraft:emerald",
            399 => "minecraft:nether_star",
            
            // Misc
            262 => "minecraft:arrow",
            296 => "minecraft:wheat",
            323 => "minecraft:oak_sign",
            324 => "minecraft:oak_door",
            327 => "minecraft:lava_bucket",
            328 => "minecraft:minecart",
            329 => "minecraft:saddle",
            330 => "minecraft:iron_door",
            332 => "minecraft:snowball",
            333 => "minecraft:boat",
            334 => "minecraft:leather",
            335 => "minecraft:milk_bucket",
            336 => "minecraft:brick",
            338 => "minecraft:reeds", // sugar_cane
            340 => "minecraft:book",
            342 => "minecraft:minecart", // storage_minecart (chest minecart)
            343 => "minecraft:minecart", // powered_minecart (furnace minecart)
            345 => "minecraft:compass",
            347 => "minecraft:clock",
            355 => "minecraft:bed",
            356 => "minecraft:repeater",
            358 => "minecraft:map",
            361 => "minecraft:pumpkin_seeds",
            362 => "minecraft:melon_seeds",
            368 => "minecraft:ender_pearl",
            369 => "minecraft:blaze_rod",
            370 => "minecraft:ghast_tear",
            374 => "minecraft:glass_bottle",
            380 => "minecraft:cauldron",
            386 => "minecraft:writable_book",
            387 => "minecraft:written_book",
            389 => "minecraft:item_frame",
            390 => "minecraft:flower_pot",
            394 => "minecraft:poisonous_potato",
            395 => "minecraft:map", // filled_map
            397 => "minecraft:player_head", // skull
            398 => "minecraft:carrot_on_a_stick",
            401 => "minecraft:firework_rocket",
            402 => "minecraft:firework_star",
            403 => "minecraft:enchanted_book",
            404 => "minecraft:comparator",
            405 => "minecraft:nether_brick",
            406 => "minecraft:quartz",
            407 => "minecraft:tnt_minecart",
            408 => "minecraft:hopper_minecart",
            409 => "minecraft:prismarine_shard",
            410 => "minecraft:prismarine_crystals",
            411 => "minecraft:rabbit",
            412 => "minecraft:cooked_rabbit",
            413 => "minecraft:rabbit_stew",
            414 => "minecraft:rabbit_foot",
            415 => "minecraft:rabbit_hide",
            416 => "minecraft:armor_stand",
            417 => "minecraft:iron_horse_armor",
            418 => "minecraft:golden_horse_armor",
            419 => "minecraft:diamond_horse_armor",
            420 => "minecraft:lead",
            421 => "minecraft:name_tag",
            422 => "minecraft:command_block_minecart",
            423 => "minecraft:mutton",
            424 => "minecraft:cooked_mutton",
            425 => "minecraft:banner", // white_banner
            426 => "minecraft:end_crystal",
            427 => "minecraft:spruce_door",
            428 => "minecraft:birch_door",
            429 => "minecraft:jungle_door",
            430 => "minecraft:acacia_door",
            431 => "minecraft:dark_oak_door",
            432 => "minecraft:chorus_fruit",
            433 => "minecraft:chorus_fruit_popped",
            434 => "minecraft:beetroot",
            435 => "minecraft:beetroot_seeds",
            436 => "minecraft:beetroot_soup",
            437 => "minecraft:dragon_breath",
            438 => "minecraft:splash_potion",
            439 => "minecraft:spectral_arrow",
            440 => "minecraft:tipped_arrow",
            441 => "minecraft:lingering_potion",
            442 => "minecraft:shield",
            443 => "minecraft:elytra",
            444 => "minecraft:spruce_boat",
            445 => "minecraft:birch_boat",
            446 => "minecraft:jungle_boat",
            447 => "minecraft:acacia_boat",
            448 => "minecraft:dark_oak_boat",
            
            // Default fallback
            _ => $"minecraft:diamond_sword" // Fallback to diamond sword for unknown IDs
        };
    }
}
