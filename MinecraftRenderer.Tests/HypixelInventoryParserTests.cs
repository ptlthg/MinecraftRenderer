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
        var base64Data = File.ReadAllText("../../../inventory_data.txt").Trim();
        
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
}
