using System.Numerics;
using System.Buffers.Binary;
using MinecraftRenderer.Schematics;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace MinecraftRenderer.Tests;

public sealed class ModernMaterialTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MinecraftRenderer_Materials", Guid.NewGuid().ToString());
    public ModernMaterialTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void LegacyAndObjectTexturesRoundTripWithTranslucency()
    {
        var model = JsonSerializer.Deserialize<BlockModelDefinition>("""
        {"parent":"block/cube","textures":{"side":"minecraft:block/stone","pane":{"sprite":"minecraft:block/glass","force_translucent":true}}}
        """)!;
        Assert.Equal("minecraft:block/stone", model.Textures!["side"]);
        Assert.Equal("minecraft:block/glass", model.Textures["pane"]);
        Assert.True(model.ForceTranslucentTextures["pane"]);
        var copy = JsonSerializer.Deserialize<BlockModelDefinition>(JsonSerializer.Serialize(model))!;
        Assert.Equal(model.Parent, copy.Parent);
        Assert.Equal(model.Textures["pane"], copy.Textures!["pane"]);
        Assert.True(copy.ForceTranslucentTextures["pane"]);
    }

    [Theory]
    [InlineData("""{"textures":{"pane":{"sprite":42}}}""")]
    [InlineData("""{"textures":{"pane":{"sprite":"block/glass","force_translucent":"yes"}}}""")]
    public void MalformedMaterialObjectsFail(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<BlockModelDefinition>(json));

    [Fact]
    public void InheritedFlagsFollowReferencesAndAreClearedByChildOverride()
    {
        var path = Path.Combine(root, "models.json");
        File.WriteAllText(path, """
        {"base":{"textures":{"pane":{"sprite":"block/glass","force_translucent":true},"alias":"#pane"}},
         "child":{"parent":"base"},"override":{"parent":"base","textures":{"pane":"block/stone"}}}
        """);
        var resolver = BlockModelResolver.LoadFromFile(path);
        Assert.True(resolver.Resolve("child").IsTextureForcedTranslucent("#alias"));
        Assert.False(resolver.Resolve("override").IsTextureForcedTranslucent("#alias"));
        Assert.Equal("block/stone", resolver.Resolve("override").Textures["pane"]);
    }

    [Fact]
    public void ForcedTranslucentFacesBlendOverOpaqueGeometryWithoutDoubleBlendingDiagonal()
    {
        WriteTexture("block/back", new Rgba32(0, 0, 255, 255));
        WriteTexture("block/front", new Rgba32(255, 0, 0, 128));
        using var renderer = CreateRenderer();
        var elements = new[] { Plane(0, "#back"), Plane(2, "#front") };
        var model = new BlockModelInstance("layers", [],
            new Dictionary<string, string> { ["back"] = "block/back", ["front"] = "block/front" },
            new Dictionary<string, TransformDefinition>(), elements,
            new Dictionary<string, bool> { ["front"] = true });
        using var image = renderer.RenderModel(model, Options());
        foreach (var x in new[] { 28, 32, 36 })
        {
            var pixel = image[x, 32];
            Assert.Equal(255, pixel.A);
            Assert.InRange((int)pixel.R, 126, 130);
            Assert.InRange((int)pixel.B, 125, 129);
        }
    }

    [Fact]
    public void ForcedTranslucentMaterialKeepsLowAlphaPixels()
    {
        WriteTexture("block/front", new Rgba32(255, 0, 0, 5));
        using var renderer = CreateRenderer();
        var model = new BlockModelInstance("low_alpha", [],
            new Dictionary<string, string> { ["front"] = "block/front" },
            new Dictionary<string, TransformDefinition>(), new[] { Plane(0, "#front") },
            new Dictionary<string, bool> { ["front"] = true });
        using var image = renderer.RenderModel(model, Options());
        Assert.Equal(5, image[32, 32].A);
    }

    [Theory]
    [InlineData("entity/shield_base_nopattern", "entity/shield/shield_base_nopattern")]
    [InlineData("entity/banner_base", "entity/banner/banner_base")]
    [InlineData("entity/copper_golem/exposed_copper_golem", "entity/copper_golem/copper_golem_exposed")]
    [InlineData("entity/copper_golem/weathered_copper_golem", "entity/copper_golem/copper_golem_weathered")]
    [InlineData("entity/copper_golem/oxidized_copper_golem", "entity/copper_golem/copper_golem_oxidized")]
    public void MovedVanillaTexturesResolveInBothDirections(string legacy, string current)
    {
        WriteTexture(current, new Rgba32(10, 20, 30));
        using (var repository = new TextureRepository(root))
        {
            Assert.True(repository.TryGetTexture("minecraft:" + legacy, out var image));
            Assert.Equal(new Rgba32(10, 20, 30), image[0, 0]);
        }
        File.Delete(Path.Combine(root, current + ".png"));
        WriteTexture(legacy, new Rgba32(40, 50, 60));
        using var older = new TextureRepository(root);
        Assert.True(older.TryGetTexture("minecraft:" + current, out var oldImage));
        Assert.Equal(new Rgba32(40, 50, 60), oldImage[0, 0]);
    }

    [Fact]
    public void ExplicitLegacyTextureWinsAndNonVanillaNamespacesDoNotAlias()
    {
        WriteTexture("entity/shield_base_nopattern", new Rgba32(255, 0, 0));
        WriteTexture("entity/shield/shield_base_nopattern", new Rgba32(0, 0, 255));
        using var repository = new TextureRepository(root);
        Assert.Equal(new Rgba32(255, 0, 0), repository.GetTexture("entity/shield_base_nopattern")[0, 0]);
        // Directory sources already allow exact-path fallback for other namespaces.
        // Remove the exact path so this assertion checks only our new alias behavior.
        File.Delete(Path.Combine(root, "entity/shield_base_nopattern.png"));
        Assert.False(repository.TryGetTexture("example:entity/shield_base_nopattern", out _));
    }

    [Fact]
    public void ModernOverrideTextureKeepsSourcePriorityOverLegacyBaseTexture()
    {
        WriteTexture("entity/shield_base_nopattern", new Rgba32(255, 0, 0));
        var overlay = Path.Combine(root, "overlay");
        WriteTexture("entity/shield/shield_base_nopattern", new Rgba32(0, 255, 0), overlay);
        using var repository = new TextureRepository(root, overlayRoots: new[] { overlay });
        Assert.Equal(new Rgba32(0, 255, 0), repository.GetTexture("entity/shield_base_nopattern")[0, 0]);
    }

    [Fact]
    public void ForcedOpaqueSpriteExportsAsBlendMaterial()
    {
        WriteTexture("block/front", new Rgba32(60, 80, 100));
        var assets = Path.Combine(root, "assets");
        Directory.CreateDirectory(Path.Combine(assets, "models/block"));
        Directory.CreateDirectory(Path.Combine(assets, "blockstates"));
        File.WriteAllText(Path.Combine(assets, "models/block/forced.json"), """
        {"textures":{"pane":{"sprite":"minecraft:block/front","force_translucent":true}},
         "elements":[{"from":[0,0,0],"to":[16,16,16],"faces":{"south":{"texture":"#pane"}}}]}
        """);
        File.WriteAllText(Path.Combine(assets, "blockstates/forced.json"), """
        {"variants":{"":{"model":"minecraft:block/forced"}}}
        """);
        using var renderer = CreateRenderer();
        var position = new SchematicPosition(0, 0, 0);
        var schematic = new LitematicSchematic {
            Name = "Forced material", Regions = [], Min = position, Max = position,
            Blocks = new[] { new SchematicBlock(position, new SchematicBlockState("forced")) }
        };
        var result = renderer.ExportSchematicGlb(schematic, new SchematicExportOptions(CullOccludedFullCubeFaces: false));
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(result.Glb.AsSpan(12, 4));
        using var document = JsonDocument.Parse(result.Glb.AsMemory(20, length));
        Assert.Contains(document.RootElement.GetProperty("materials").EnumerateArray(),
            material => material.TryGetProperty("alphaMode", out var mode) && mode.GetString() == "BLEND");
    }

    private void WriteTexture(string path, Rgba32 color, string? directory = null)
    {
        var file = Path.Combine(directory ?? root, path + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var image = new Image<Rgba32>(2, 2, color);
        image.SaveAsPng(file);
    }

    private MinecraftBlockRenderer CreateRenderer()
    {
        var assets = Path.Combine(root, "assets");
        Directory.CreateDirectory(Path.Combine(assets, "models/block"));
        Directory.CreateDirectory(Path.Combine(assets, "blockstates"));
        Directory.CreateDirectory(Path.Combine(assets, "textures/block"));
        File.WriteAllText(Path.Combine(assets, "models/block/dummy.json"), "{}");
        File.Copy(Path.Combine(root, "block/front.png"), Path.Combine(assets, "textures/block/front.png"));
        var back = Path.Combine(root, "block/back.png");
        if (File.Exists(back)) File.Copy(back, Path.Combine(assets, "textures/block/back.png"));
        return MinecraftBlockRenderer.CreateFromMinecraftAssets(assets);
    }

    private static ModelElement Plane(float z, string texture) => new(new Vector3(0, 0, z),
        new Vector3(16, 16, z + 1), null,
        new Dictionary<BlockFaceDirection, ModelFace> {
            [BlockFaceDirection.South] = new(texture, new Vector4(0, 0, 16, 16), null, null, null)
        }, false);

    private static MinecraftBlockRenderer.BlockRenderOptions Options() => new(Size: 64, Padding: 0,
        UseGuiTransform: false, EnableAntiAliasing: false,
        OverrideGuiTransform: new TransformDefinition { Rotation = [0, 0, 0], Scale = [1, 1, 1], Translation = [0, 0, 0] });
}
