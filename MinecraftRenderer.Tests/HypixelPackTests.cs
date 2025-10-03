using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MinecraftRenderer;
using MinecraftRenderer.Nbt;
using MinecraftRenderer.TexturePacks;
using Xunit;

namespace MinecraftRenderer.Tests;

public sealed class HypixelPackTests
{
    private static readonly string AssetsDirectory =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "minecraft"));

    private static readonly string HypixelPackPath =
        @"g:\Programming\MinecraftRenderer\texturepacks\Hypixel+ 0.23.4 for 1.21.8";

    [Fact]
    public void HypixelPlayerHeadSelectorLoadsAndResolvesCorrectly()
    {
        // Arrange
        var registry = TexturePackRegistry.Create();
        registry.RegisterPack(HypixelPackPath);
        
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory, registry, new[] { "hypixelplus" });
        
        // Get item registry
        var itemRegistryField = typeof(MinecraftBlockRenderer).GetField("_itemRegistry", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(itemRegistryField);
        var itemRegistry = (ItemRegistry?)itemRegistryField!.GetValue(renderer);
        Assert.NotNull(itemRegistry);

        // Check that player_head has a selector
        Assert.True(itemRegistry!.TryGetInfo("player_head", out var itemInfo));
        Console.WriteLine($"player_head item info - Model: {itemInfo.Model}");
        
        var selectorProperty = typeof(ItemRegistry.ItemInfo).GetProperty("Selector", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(selectorProperty);
        var selector = selectorProperty!.GetValue(itemInfo);
        Console.WriteLine($"Selector is null: {selector == null}");
        Console.WriteLine($"Selector type: {selector?.GetType().Name}");
        
        if (selector?.GetType().Name == "ItemModelSelectorSpecial")
        {
            var baseModelProp = selector.GetType().GetProperty("BaseModel");
            var nestedProp = selector.GetType().GetProperty("Nested");
            Console.WriteLine($"Special.BaseModel: {baseModelProp?.GetValue(selector)}");
            Console.WriteLine($"Special.Nested: {nestedProp?.GetValue(selector)?.GetType().Name}");
        }
        
        Assert.NotNull(selector);

        // Create item data with AATROX_BATPHONE custom data
        var customData = new NbtCompound(new[]
        {
            new KeyValuePair<string, NbtTag>("id", new NbtString("AATROX_BATPHONE"))
        });
        var itemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: customData);

        // Resolve the model
        var contextType = typeof(MinecraftBlockRenderer).Assembly.GetType("MinecraftRenderer.ItemModelContext");
        Assert.NotNull(contextType);
        var context = Activator.CreateInstance(contextType!, itemData, "gui");
        Assert.NotNull(context);

        var resolveMethod = selector!.GetType().GetMethod("Resolve");
        Assert.NotNull(resolveMethod);
        var resolvedModel = (string?)resolveMethod!.Invoke(selector, new[] { context });

        // Assert that it resolved to the hplus model
        Assert.NotNull(resolvedModel);
        Assert.StartsWith("hplus:", resolvedModel);
        Assert.Contains("aatrox_batphone", resolvedModel, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"✅ AATROX_BATPHONE resolved to: {resolvedModel}");
        
        // Test succeeds if we can resolve the model - model loading is a separate concern
    }
}
