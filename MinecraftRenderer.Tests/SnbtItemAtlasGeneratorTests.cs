using System;
using System.Collections.Generic;
using System.IO;
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

        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory, registry, new[] { packId });

        var directOptions = MinecraftBlockRenderer.BlockRenderOptions.Default with
        {
            PackIds = new[] { packId },
            Size = 64
        };
        var customData = new NbtCompound(new[]
        {
            new KeyValuePair<string, NbtTag>("id", new NbtString("custom_head_test"))
        });
        var itemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: customData);
        using (var directRender = renderer.RenderItem("player_head", itemData, directOptions))
        {
            var directPixel = SampleOpaquePixel(directRender);
            Assert.Equal(0xD4, directPixel.R);
            Assert.Equal(0x34, directPixel.G);
            Assert.Equal(0x2C, directPixel.B);
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