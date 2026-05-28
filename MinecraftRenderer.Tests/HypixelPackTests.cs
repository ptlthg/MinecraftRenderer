using System;
using System.IO;
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

    private static readonly string TexturePacksDirectory =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "texturepacks"));

    private static readonly string HypixelPackPath =
        Path.Combine(TexturePacksDirectory, "Hypixel+ 0.23.4 for 1.21.8");

    private static readonly string HypixelPlusCatsPackPath =
        Path.Combine(TexturePacksDirectory, "hplus");

    private static readonly string FurfSkyPackPath =
        Path.Combine(TexturePacksDirectory, "fursky");

    [Fact]
    public void HypixelPlusCatsPackRegistersEnabledCatharsisOverlays()
    {
        if (!Directory.Exists(HypixelPlusCatsPackPath)) return;

        var registry = TexturePackRegistry.Create();
        registry.RegisterPack(HypixelPlusCatsPackPath);

        Assert.True(registry.TryGetPack("hypixelplus", out var pack));
        Assert.True(pack.IsCatharsisPack);
        Assert.Equal(69, pack.Meta.PackFormat);
        Assert.NotNull(pack.CatharsisOverlays);
        Assert.Contains("hplus_weapons_swords", pack.CatharsisOverlays!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("hplus_items_farming", pack.CatharsisOverlays!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("hplus_ui", pack.CatharsisOverlays!, StringComparer.OrdinalIgnoreCase);

        Assert.NotNull(pack.OverlayNamespaceProviders);
        Assert.Contains(pack.OverlayNamespaceProviders!, overlay =>
            overlay.Namespace.Equals("skyblock", StringComparison.OrdinalIgnoreCase) &&
            overlay.DisplayPath.Contains("hplus_weapons_swords", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HypixelPlayerHeadSelectorLoadsAndResolvesCorrectly()
    {
        if (!Directory.Exists(HypixelPackPath)) return;

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

    [Fact]
    public void FurfSkyAdvancedGardeningAxeResolvesEnabledOverlayModel()
    {
        if (!Directory.Exists(FurfSkyPackPath)) return;

        var registry = TexturePackRegistry.Create();
        registry.RegisterPack(FurfSkyPackPath);

        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory, registry, new[] { "fursky" });

        var itemData = new MinecraftBlockRenderer.ItemRenderData(
            CustomData: new NbtCompound(new[]
            {
                new KeyValuePair<string, NbtTag>("id", new NbtString("ADVANCED_GARDENING_AXE"))
            }));

        var options = MinecraftBlockRenderer.BlockRenderOptions.Default with { PackIds = new[] { "fursky" }, ItemData = itemData };
        var result = renderer.RenderGuiItemWithResourceId("minecraft:diamond_axe", options);

        Assert.Equal("fursky", result.ResourceId.SourcePackId);
        Assert.Equal("item_tool:item/advanced_gardening_axe", result.ResourceId.Model);
    }

    [Fact]
    public void FurfSkySkyblockOverrideCanResolveMinecraftNamespaceModel()
    {
        if (!Directory.Exists(FurfSkyPackPath)) return;

        var registry = TexturePackRegistry.Create();
        registry.RegisterPack(FurfSkyPackPath);

        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory, registry, new[] { "fursky" });

        var suspiciousStewOptions = MinecraftBlockRenderer.BlockRenderOptions.Default with {
            PackIds = new[] { "fursky" },
            ItemData = new MinecraftBlockRenderer.ItemRenderData(
                CustomData: new NbtCompound(new[] {
                    new KeyValuePair<string, NbtTag>("id", new NbtString("SUSPICIOUS_STEW"))
                }))
        };

        var suspiciousStew = renderer.RenderGuiItemWithResourceId("minecraft:player_head", suspiciousStewOptions);
        Assert.Equal("minecraft:item/suspicious_stew", suspiciousStew.ResourceId.Model);

        var enchantedBoneBlockOptions = MinecraftBlockRenderer.BlockRenderOptions.Default with {
            PackIds = new[] { "fursky" },
            ItemData = new MinecraftBlockRenderer.ItemRenderData(
                CustomData: new NbtCompound(new[] {
                    new KeyValuePair<string, NbtTag>("id", new NbtString("ENCHANTED_BONE_BLOCK"))
                }))
        };

        var enchantedBoneBlock = renderer.RenderGuiItemWithResourceId("minecraft:chiseled_quartz_block", enchantedBoneBlockOptions);
        Assert.Equal("minecraft:block/bone_block", enchantedBoneBlock.ResourceId.Model);
    }

    [Fact]
    public void FurfSkyBookOfProgressionUsesRarityDataType()
    {
        if (!Directory.Exists(FurfSkyPackPath)) return;

        var registry = TexturePackRegistry.Create();
        registry.RegisterPack(FurfSkyPackPath);

        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory, registry, new[] { "fursky" });

        var itemData = new MinecraftBlockRenderer.ItemRenderData(
            CustomData: new NbtCompound(new[]
            {
                new KeyValuePair<string, NbtTag>("id", new NbtString("BOOK_OF_PROGRESSION")),
                new KeyValuePair<string, NbtTag>("upgradedRarity", new NbtString("LEGENDARY"))
            }));

        var options = MinecraftBlockRenderer.BlockRenderOptions.Default with { PackIds = new[] { "fursky" }, ItemData = itemData };
        var result = renderer.RenderGuiItemWithResourceId("minecraft:book", options);

        Assert.Equal("fursky", result.ResourceId.SourcePackId);
        Assert.Equal("item_accessory:item/book_of_progression_legendary", result.ResourceId.Model);
    }

    [Fact]
    public void FurfSkyMidasSwordFallsBackToBasePaidVariant()
    {
        if (!Directory.Exists(FurfSkyPackPath)) return;

        var registry = TexturePackRegistry.Create();
        registry.RegisterPack(FurfSkyPackPath);

        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory, registry, new[] { "fursky" });

        var itemData = new MinecraftBlockRenderer.ItemRenderData(
            CustomData: new NbtCompound(new[]
            {
                new KeyValuePair<string, NbtTag>("id", new NbtString("MIDAS_SWORD"))
            }));

        var options = MinecraftBlockRenderer.BlockRenderOptions.Default with { PackIds = new[] { "fursky" }, ItemData = itemData };
        var result = renderer.RenderGuiItemWithResourceId("minecraft:golden_sword", options);

        Assert.Equal("fursky", result.ResourceId.SourcePackId);
        Assert.Equal("item_melee:item/midas_sword_0", result.ResourceId.Model);
    }

    [Fact]
    public void FurfSkyMidasSwordUsesModifierDataTypeWhenPresent()
    {
        if (!Directory.Exists(FurfSkyPackPath)) return;

        var registry = TexturePackRegistry.Create();
        registry.RegisterPack(FurfSkyPackPath);

        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory, registry, new[] { "fursky" });

        var itemData = new MinecraftBlockRenderer.ItemRenderData(
            CustomData: new NbtCompound(new[]
            {
                new KeyValuePair<string, NbtTag>("id", new NbtString("MIDAS_SWORD")),
                new KeyValuePair<string, NbtTag>("modifier", new NbtString("GILDED"))
            }));

        var options = MinecraftBlockRenderer.BlockRenderOptions.Default with { PackIds = new[] { "fursky" }, ItemData = itemData };
        var result = renderer.RenderGuiItemWithResourceId("minecraft:golden_sword", options);

        Assert.Equal("fursky", result.ResourceId.SourcePackId);
        Assert.Equal("item_melee:item/midas_sword_gilded_0", result.ResourceId.Model);
    }
}
