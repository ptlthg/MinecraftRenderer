#!/usr/bin/env dotnet-script
#r "nuget: SixLabors.ImageSharp, 3.1.5"
#r "nuget: fNbt, 0.7.0"
#r "g:\Programming\MinecraftRenderer\MinecraftRenderer\bin\Debug\net9.0\MinecraftRenderer.dll"

using MinecraftRenderer;
using MinecraftRenderer.TexturePacks;
using MinecraftRenderer.Nbt;
using System;
using System.IO;
using System.Linq;

var assetsDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "minecraft"));
var registry = TexturePackRegistry.Create();
var hypixelPackPath = @"g:\Programming\MinecraftRenderer\texturepacks\Hypixel+ 0.23.4 for 1.21.8";
registry.RegisterPack(hypixelPackPath);

using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(assetsDir, registry, new[] { "hypixelplus" });

// Check if the item registry has a selector for player_head
var itemRegistryField = typeof(MinecraftBlockRenderer).GetField("_itemRegistry", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
var itemRegistry = (ItemRegistry)itemRegistryField.GetValue(renderer);

if (itemRegistry.TryGetInfo("player_head", out var itemInfo))
{
    Console.WriteLine($"player_head found in registry");
    Console.WriteLine($"  Model: {itemInfo.Model}");
    
    var selectorProp = typeof(ItemRegistry.ItemInfo).GetProperty("Selector", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    var selector = selectorProp?.GetValue(itemInfo);
    Console.WriteLine($"  Has Selector: {selector != null}");
    
    if (selector != null)
    {
        // Test with AATROX_BATPHONE custom data
        var customData = new NbtCompound(new[] { new KeyValuePair<string, NbtTag>("id", new NbtString("AATROX_BATPHONE")) });
        var itemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: customData);
        
        var contextType = typeof(MinecraftBlockRenderer).Assembly.GetType("MinecraftRenderer.ItemModelContext");
        var context = Activator.CreateInstance(contextType, itemData, "gui");
        
        var resolveMethod = selector.GetType().GetMethod("Resolve");
        var resolvedModel = (string)resolveMethod.Invoke(selector, new[] { context });
        
        Console.WriteLine($"  Resolved model for AATROX_BATPHONE: {resolvedModel ?? "<null>"}");
    }
}
else
{
    Console.WriteLine("player_head NOT found in registry!");
}

// Check if hplus models are loaded
var modelResolverField = typeof(MinecraftBlockRenderer).GetField("_modelResolver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
var modelResolver = modelResolverField.GetValue(renderer);
var definitionsProperty = modelResolver.GetType().GetProperty("Definitions");
var definitions = (System.Collections.Generic.IReadOnlyDictionary<string, object>)definitionsProperty.GetValue(modelResolver);

var hplusModels = definitions.Keys.Where(k => k.StartsWith("hplus:", StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
Console.WriteLine($"\nFound {hplusModels.Count} hplus: models (showing first 10):");
foreach (var model in hplusModels)
{
    Console.WriteLine($"  {model}");
}

var targetModel = "hplus:skyblock/tools/abiphones/aatrox_batphone";
Console.WriteLine($"\nLooking for specific model: {targetModel}");
Console.WriteLine($"  Exists: {definitions.ContainsKey(targetModel)}");
