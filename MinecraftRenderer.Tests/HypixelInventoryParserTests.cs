using MinecraftRenderer.Hypixel;
using MinecraftRenderer.Nbt;
using Xunit;

namespace MinecraftRenderer.Tests;

public class HypixelInventoryParserTests
{
    [Fact]
    public void CanParseInventoryData()
    {
        // Sample from inventory_data.txt
        const string base64Data = "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";
        
        // Parse the NBT and inspect structure
        var bytes = Convert.FromBase64String(base64Data);
        using var stream = new MemoryStream(bytes);
        var doc = Nbt.NbtParser.ParseBinary(stream);
        
        Console.WriteLine($"Root type: {doc.Root.Type}");
        if (doc.Root is Nbt.NbtCompound compound)
        {
            Console.WriteLine($"Root compound keys: {string.Join(", ", compound.Keys)}");
            var iList = compound.GetList("i");
            if (iList != null)
            {
                Console.WriteLine($"'i' list found with {iList.Count} elements, element type: {iList.ElementType}");
                if (iList.Count > 0)
                {
                    Console.WriteLine($"First element type: {iList[0].Type}");
                    if (iList[0] is Nbt.NbtCompound firstItem)
                    {
                        Console.WriteLine($"First item keys: {string.Join(", ", firstItem.Keys)}");
                    }
                }
            }
        }
        else if (doc.Root is Nbt.NbtList list)
        {
            Console.WriteLine($"Root list count: {list.Count}");
        }
        
        var items = InventoryParser.ParseInventory(base64Data);
        
        Console.WriteLine($"Parsed {items.Count} items");
        Assert.NotEmpty(items);
        
        // Should have parsed some items
        foreach (var item in items.Take(5))
        {
            Assert.NotNull(item.ItemId);
            // Output for inspection
            Console.WriteLine($"Item: {item.ItemId}, Count: {item.Count}, Damage: {item.Damage}");
            if (item.SkyblockId != null)
            {
                Console.WriteLine($"  Skyblock ID: {item.SkyblockId}");
            }
            if (item.DisplayName != null)
            {
                Console.WriteLine($"  Display Name: {item.DisplayName}");
            }
        }
    }

    [Fact]
    public void ExtractsItemMetadata()
    {
        var base64Data = File.ReadAllText("../../../inventory_data.txt").Trim();
        var items = InventoryParser.ParseInventory(base64Data);
        
        // Should have at least one item with Skyblock ID
        var skyblockItems = items.Where(i => i.SkyblockId != null).ToList();
        Assert.NotEmpty(skyblockItems);
        
        foreach (var item in skyblockItems.Take(3))
        {
            Console.WriteLine($"\nItem: {item.SkyblockId}");
            Console.WriteLine($"  Minecraft ID: {item.ItemId}");
            Console.WriteLine($"  Display: {item.DisplayName}");
            Console.WriteLine($"  Texture ID: {TextureResolver.GetTextureId(item)}");
            
            if (item.Enchantments != null)
            {
                Console.WriteLine($"  Enchantments: {item.Enchantments.Count} found");
            }
            
            if (item.Gems != null)
            {
                Console.WriteLine($"  Gems: {item.Gems.Count} found");
            }
            
            if (item.Attributes != null)
            {
                Console.WriteLine($"  Attributes: {item.Attributes.Count} found");
            }
        }
    }

    [Fact]
    public void GeneratesConsistentTextureIds()
    {
        var base64Data = File.ReadAllText("../../../inventory_data.txt").Trim();
        var items = InventoryParser.ParseInventory(base64Data);
        
        // Texture IDs should be deterministic
        var textureIds = items.Select(TextureResolver.GetTextureId).ToList();
        Assert.NotEmpty(textureIds);
        
        // Re-parse and verify IDs match
        var items2 = InventoryParser.ParseInventory(base64Data);
        var textureIds2 = items2.Select(TextureResolver.GetTextureId).ToList();
        
        Assert.Equal(textureIds, textureIds2);
        
        Console.WriteLine($"Generated {textureIds.Count} unique texture IDs");
        foreach (var id in textureIds.Distinct())
        {
            Console.WriteLine($"  {id}");
        }
    }

    [Fact]
    public void SkyblockTextureIdIncludesFallbackMetadata()
    {
        var extraAttributes = new NbtCompound(new[]
        {
            new KeyValuePair<string, NbtTag>("id", new NbtString("FUNGI_CUTTER"))
        });
        var tag = new NbtCompound(new[]
        {
            new KeyValuePair<string, NbtTag>("ExtraAttributes", extraAttributes)
        });

        var item = new HypixelItemData("minecraft:golden_hoe", NumericId: 294, Tag: tag);
        var textureId = TextureResolver.GetTextureId(item);

        Assert.True(TextureResolver.TryDecodeTextureId(textureId, out var descriptor));
    Assert.StartsWith($"{HypixelPrefixes.Skyblock}fungi_cutter", descriptor, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("base=minecraft:golden_hoe", descriptor, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("numeric=294", descriptor, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SkyblockTextureIdNormalizesNumericItemId()
    {
        var extraAttributes = new NbtCompound(new[]
        {
            new KeyValuePair<string, NbtTag>("id", new NbtString("FUNGI_CUTTER"))
        });
        var tag = new NbtCompound(new[]
        {
            new KeyValuePair<string, NbtTag>("ExtraAttributes", extraAttributes)
        });

    var item = new HypixelItemData($"{HypixelPrefixes.Numeric}293", Tag: tag);
        var textureId = TextureResolver.GetTextureId(item);

        Assert.True(TextureResolver.TryDecodeTextureId(textureId, out var descriptor));
        Assert.Contains("base=minecraft:diamond_hoe", descriptor, StringComparison.OrdinalIgnoreCase);
    }
}
