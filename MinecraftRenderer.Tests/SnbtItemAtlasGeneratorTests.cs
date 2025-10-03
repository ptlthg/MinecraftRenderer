using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using MinecraftRenderer;
using MinecraftRenderer.Snbt;
using MinecraftRenderer.TexturePacks;
using MinecraftRenderer.Nbt;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace MinecraftRenderer.Tests;

public sealed class SnbtItemAtlasGeneratorTests : IDisposable
{
    private static readonly string AssetsDirectory =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "minecraft"));

    private readonly string _tempRoot;

    public SnbtItemAtlasGeneratorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "MinecraftRenderer_SnbtAtlas", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void RenderedAtlasUsesTexturePackForCustomData()
    {
        var packId = "snbt_custom_head_pack";
        var packRoot = CreateCustomHeadPack(packId, new Rgba32(0xD4, 0x34, 0x2C, 0xFF));

        var registry = TexturePackRegistry.Create();
        registry.RegisterPack(packRoot);
        var packStack = registry.BuildPackStack(new[] { packId });
        Assert.Contains(Path.Combine(packRoot, "assets", "minecraft"),
            packStack.OverlayRoots.Select(static overlay => overlay.Path), StringComparer.OrdinalIgnoreCase);

        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory, registry, new[] { packId });

        var packContextField = typeof(MinecraftBlockRenderer).GetField("_packContext", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(packContextField);
        var packContext = packContextField!.GetValue(renderer);
        Assert.NotNull(packContext);
        var searchRootsProperty = packContext.GetType().GetProperty("SearchRoots", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(searchRootsProperty);
        var searchRoots = (IEnumerable<object>)searchRootsProperty!.GetValue(packContext)!;
        Assert.Contains(searchRoots, root =>
        {
            var pathProperty = root.GetType().GetProperty("Path", BindingFlags.Public | BindingFlags.Instance);
            var sourceIdProperty = root.GetType().GetProperty("SourceId", BindingFlags.Public | BindingFlags.Instance);
            var kindProperty = root.GetType().GetProperty("Kind", BindingFlags.Public | BindingFlags.Instance);
            var path = pathProperty?.GetValue(root) as string;
            var sourceId = sourceIdProperty?.GetValue(root) as string;
            var kind = kindProperty?.GetValue(root)?.ToString();
            return string.Equals(sourceId, packId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(kind, "ResourcePack", StringComparison.Ordinal)
                   && string.Equals(path, Path.Combine(packRoot, "assets", "minecraft"), StringComparison.OrdinalIgnoreCase);
        });

        var assetNamespacesProperty = packContext.GetType().GetProperty("AssetNamespaces", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(assetNamespacesProperty);
        var assetNamespaces = assetNamespacesProperty!.GetValue(packContext);
        Assert.NotNull(assetNamespaces);
        var rootsProperty = assetNamespaces.GetType().GetProperty("Roots", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(rootsProperty);
        var rootsCollection = (System.Collections.IEnumerable)rootsProperty!.GetValue(assetNamespaces)!;
        Assert.Contains(rootsCollection.Cast<object>(), root =>
        {
            var namespaceProperty = root.GetType().GetProperty("Namespace", BindingFlags.Public | BindingFlags.Instance);
            var pathProperty = root.GetType().GetProperty("Path", BindingFlags.Public | BindingFlags.Instance);
            var sourceIdProperty = root.GetType().GetProperty("SourceId", BindingFlags.Public | BindingFlags.Instance);
            var isVanillaProperty = root.GetType().GetProperty("IsVanilla", BindingFlags.Public | BindingFlags.Instance);
            var namespaceName = namespaceProperty?.GetValue(root) as string;
            var rootPath = pathProperty?.GetValue(root) as string;
            var sourceId = sourceIdProperty?.GetValue(root) as string;
            var isVanilla = isVanillaProperty?.GetValue(root) as bool?;
            return string.Equals(namespaceName, "minecraft", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(sourceId, packId, StringComparison.OrdinalIgnoreCase)
                   && isVanilla is false
                   && string.Equals(rootPath, Path.Combine(packRoot, "assets", "minecraft"), StringComparison.OrdinalIgnoreCase);
        });

        var customData = new NbtCompound(new[]
        {
            new KeyValuePair<string, NbtTag>("id", new NbtString("custom_head_test"))
        });
        var itemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: customData);

        var itemRegistryField = typeof(MinecraftBlockRenderer).GetField("_itemRegistry", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(itemRegistryField);
        var itemRegistry = (ItemRegistry?)itemRegistryField!.GetValue(renderer);
        Assert.NotNull(itemRegistry);
        Assert.True(itemRegistry!.TryGetInfo("player_head", out var itemInfo));
        var selectorProperty = typeof(ItemRegistry.ItemInfo).GetProperty("Selector", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(selectorProperty);
        var selector = selectorProperty!.GetValue(itemInfo);
        Assert.NotNull(selector);
        var contextType = typeof(MinecraftBlockRenderer).Assembly.GetType("MinecraftRenderer.ItemModelContext");
        Assert.NotNull(contextType);

        var directOptions = MinecraftBlockRenderer.BlockRenderOptions.Default with
        {
            PackIds = new[] { packId },
            Size = 64
        };

        var context = Activator.CreateInstance(contextType!, itemData, "gui");
        var resolveMethod = selector!.GetType().GetMethod("Resolve", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(resolveMethod);
        var resolvedModel = resolveMethod!.Invoke(selector, new object?[] { context });
        Assert.IsType<string>(resolvedModel);
        Assert.Equal("minecraft:item/custom_player_head", (string)resolvedModel!);
        var resolveItemModelMethod = typeof(MinecraftBlockRenderer).GetMethod("ResolveItemModel", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(resolveItemModelMethod);
        var optionsWithItemData = directOptions with { ItemData = itemData };
        var forwardedOptions = optionsWithItemData with { PackIds = null };
        var resolveResult = resolveItemModelMethod!.Invoke(renderer, new object?[] { "player_head", itemInfo, optionsWithItemData });
        Assert.NotNull(resolveResult);
        var resultType = resolveResult.GetType();
    var tupleModel = resultType.GetField("Item1")?.GetValue(resolveResult);
    var tupleCandidates = resultType.GetField("Item2")?.GetValue(resolveResult) as System.Collections.IEnumerable;
        Assert.NotNull(tupleCandidates);
        var candidateList = tupleCandidates!.Cast<object>().Select(obj => obj?.ToString()).ToList();
        Assert.Contains("minecraft:item/custom_player_head", candidateList, StringComparer.OrdinalIgnoreCase);
        var tupleModelName = tupleModel?.GetType().GetProperty("Name")?.GetValue(tupleModel) as string;
        var forwardedResolve = resolveItemModelMethod!.Invoke(renderer, new object?[] { "player_head", itemInfo, forwardedOptions });
        Assert.NotNull(forwardedResolve);
        var forwardedType = forwardedResolve.GetType();
        var forwardedModel = forwardedType.GetField("Item1")?.GetValue(forwardedResolve);
        var forwardedCandidates = forwardedType.GetField("Item2")?.GetValue(forwardedResolve) as System.Collections.IEnumerable;
        Assert.NotNull(forwardedCandidates);
        var forwardedModelName = forwardedModel?.GetType().GetProperty("Name")?.GetValue(forwardedModel) as string;
        Assert.Equal(tupleModelName, forwardedModelName);
        using (var directRender = renderer.RenderItem("player_head", itemData, directOptions))
        {
            var directPixel = SampleOpaquePixel(directRender);
            Assert.Equal(0xD4, directPixel.R);
            Assert.Equal(0x34, directPixel.G);
            Assert.Equal(0x2C, directPixel.B);
        }

    Assert.Same(itemData, optionsWithItemData.ItemData);
    Assert.Same(itemData, forwardedOptions.ItemData);
    Assert.Null(forwardedOptions.PackIds);

    var buildKeyMethod = typeof(MinecraftBlockRenderer).GetMethod("BuildItemRenderDataKey", BindingFlags.NonPublic | BindingFlags.Static);
    Assert.NotNull(buildKeyMethod);
    var itemDataKeyValue = (string)buildKeyMethod!.Invoke(null, new object?[] { itemData })!;
    Assert.Contains("custom_head_test", itemDataKeyValue);

    var baselineResourceInfo = renderer.ComputeResourceId("player_head", directOptions);
    var directResourceInfo = renderer.ComputeResourceId("player_head", optionsWithItemData);
        Console.WriteLine($"Direct resource model: {directResourceInfo.Model}");
        Assert.NotNull(directResourceInfo.Model);
              Assert.Equal("minecraft:item/custom_player_head", tupleModelName);
              Assert.Equal("minecraft:item/custom_player_head", directResourceInfo.Model);
        Assert.True(string.Equals(packId, directResourceInfo.SourcePackId, StringComparison.OrdinalIgnoreCase),
            $"Expected pack '{packId}' but found '{directResourceInfo.SourcePackId}'. Model='{directResourceInfo.Model ?? "<null>"}', ResolvedModel='{tupleModelName ?? "<null>"}', Candidates='{string.Join(", ", candidateList)}', Textures='{string.Join(", ", directResourceInfo.Textures)}'.");
        Assert.Contains(directResourceInfo.Textures, texture => texture.Contains("custom_player_head", StringComparison.OrdinalIgnoreCase));

        if (directResourceInfo.Model is { } modelPath)
        {
            var normalized = NormalizeModelPathForTest(modelPath);
            var candidate = Path.Combine(packRoot, "assets", "minecraft", "models", normalized + ".json");
            Assert.True(File.Exists(candidate), $"Model path '{candidate}' was not found.");
        }

        var views = new List<MinecraftAtlasGenerator.AtlasView>
        {
            new("front", MinecraftBlockRenderer.BlockRenderOptions.Default with
            {
                PackIds = new[] { packId },
                Size = 64
            })
        };

        const string snbtPayload = """
        {
            components: {
                "minecraft:custom_data": {
                    id: "custom_head_test"
                }
            },
            id: "minecraft:player_head"
        }
        """;

        var document = NbtParser.ParseSnbt(snbtPayload);
        var entry = new SnbtItemAtlasGenerator.SnbtItemEntry(
            "custom_head",
            Path.Combine(_tempRoot, "custom_head.snbt"),
            document,
            null);

        var outputDirectory = Path.Combine(_tempRoot, "output");
        var results = SnbtItemAtlasGenerator.GenerateAtlases(
            renderer,
            outputDirectory,
            views,
            tileSize: 32,
            columns: 1,
            rows: 1,
            items: new[] { entry });

        Assert.Single(results);
        var atlas = results[0];
        using var image = Image.Load<Rgba32>(atlas.ImagePath);
        var pixel = SampleOpaquePixel(image);

        Assert.Equal(0xD4, pixel.R);
        Assert.Equal(0x34, pixel.G);
        Assert.Equal(0x2C, pixel.B);

        using var manifest = JsonDocument.Parse(File.ReadAllText(atlas.ManifestPath));
        var manifestRoot = manifest.RootElement;
        Assert.Equal(JsonValueKind.Array, manifestRoot.ValueKind);
        Assert.Equal(1, manifestRoot.GetArrayLength());
        var entryElement = manifestRoot[0];

        Assert.True(entryElement.TryGetProperty("texturePack", out var packProperty));
        Assert.Equal(packId, packProperty.GetString());

        Assert.True(entryElement.TryGetProperty("model", out var modelProperty));
        var modelValue = modelProperty.GetString();
        Assert.False(string.IsNullOrWhiteSpace(modelValue));
        Assert.EndsWith("custom_player_head", modelValue, StringComparison.OrdinalIgnoreCase);

        Assert.True(entryElement.TryGetProperty("textures", out var texturesProperty));
        Assert.Equal(JsonValueKind.Array, texturesProperty.ValueKind);
        Assert.Contains(texturesProperty.EnumerateArray(), element =>
            element.GetString() is { } texture && texture.Contains("custom_player_head", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeModelPathForTest(string modelPath)
    {
        var normalized = modelPath.Replace('\\', '/').Trim();
        if (normalized.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[10..];
        }

        normalized = normalized.TrimStart('/');
        if (normalized.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[7..];
        }

        return normalized;
    }

    private string CreateCustomHeadPack(string id, Rgba32 color)
    {
        var packRoot = Path.Combine(_tempRoot, id);
        Directory.CreateDirectory(packRoot);

        File.WriteAllText(Path.Combine(packRoot, "meta.json"),
            $"{{\n  \"id\": \"{id}\",\n  \"name\": \"{id}\",\n  \"version\": \"1.0.0\",\n  \"description\": \"Test pack\",\n  \"authors\": [\"tests\"]\n}}\n");
        File.WriteAllText(Path.Combine(packRoot, "pack.mcmeta"),
            "{\"pack\": {\"pack_format\": 32, \"description\": \"Test\"}}\n");

        var itemsDir = Path.Combine(packRoot, "assets", "minecraft", "items");
        Directory.CreateDirectory(itemsDir);
        File.WriteAllText(Path.Combine(itemsDir, "player_head.json"),
            "{\n  \"model\": {\n    \"type\": \"condition\",\n    \"property\": \"component\",\n    \"predicate\": \"custom_data\",\n    \"value\": { \"id\": \"custom_head_test\" },\n    \"on_true\": {\n      \"type\": \"model\",\n      \"model\": \"minecraft:item/custom_player_head\"\n    },\n    \"on_false\": {\n      \"type\": \"model\",\n      \"model\": \"minecraft:item/player_head\"\n    }\n  }\n}\n");

        var modelsDir = Path.Combine(packRoot, "assets", "minecraft", "models", "item");
        Directory.CreateDirectory(modelsDir);
        File.WriteAllText(Path.Combine(modelsDir, "custom_player_head.json"),
            "{\n  \"parent\": \"minecraft:item/generated\",\n  \"textures\": {\n    \"layer0\": \"minecraft:item/custom_player_head\"\n  }\n}\n");

        var texturesDir = Path.Combine(packRoot, "assets", "minecraft", "textures", "item");
        Directory.CreateDirectory(texturesDir);
        using var image = new Image<Rgba32>(16, 16, color);
        image.Save(Path.Combine(texturesDir, "custom_player_head.png"));

        return packRoot;
    }

    private static Rgba32 SampleOpaquePixel(Image<Rgba32> image)
    {
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var pixel = image[x, y];
                if (pixel.A > 200)
                {
                    return pixel;
                }
            }
        }

        throw new InvalidOperationException("No opaque pixel found in rendered image.");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup issues in tests.
        }
    }
}