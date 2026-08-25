using System.Buffers.Binary;
using System.Text.Json;
using MinecraftRenderer.Schematics;
using Xunit;

namespace MinecraftRenderer.Tests;

public sealed class SchematicFluidRenderingTests
{
    private static readonly string AssetsDirectory =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "minecraft", "assets", "minecraft"));

    [Fact]
    public void SourceWaterUsesProceduralTranslucentGeometryInsteadOfMissingModel()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var result = renderer.ExportSchematicGlb(CreateSchematic(
            Block(0, 4, 0, "water", ("level", "0"))));

        Assert.Equal(12, result.TriangleCount);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("Missing block model", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, warning => warning.StartsWith("Missing schematic fluid texture", StringComparison.Ordinal));
        Assert.Contains("Animated fluid textures use their first frame in schematic GLB exports.", result.Warnings);
        Assert.Equal([2], ReadPrimitiveMaterialIndices(result.Glb).Distinct().ToArray());
    }

    [Fact]
    public void LavaUsesStillAndFlowTexturesWithOpaqueGeometry()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var result = renderer.ExportSchematicGlb(CreateSchematic(
            Block(0, 0, 0, "lava", ("level", "0"))));

        Assert.Equal(12, result.TriangleCount);
        Assert.DoesNotContain(result.Warnings, warning => warning.StartsWith("Missing schematic fluid texture", StringComparison.Ordinal));
        Assert.Equal([0], ReadPrimitiveMaterialIndices(result.Glb).Distinct().ToArray());
    }

    [Fact]
    public void AdjacentAndStackedWaterCullsInternalFacesAndKeepsSeparateSliceCaps()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var adjacent = renderer.ExportSchematicGlb(CreateSchematic(
            Block(0, 0, 0, "water", ("level", "0")),
            Block(1, 0, 0, "water", ("level", "0"))));
        var stacked = renderer.ExportSchematicGlb(CreateSchematic(
            Block(0, 8, 0, "water", ("level", "0")),
            Block(0, 9, 0, "water", ("level", "0"))));

        Assert.Equal(20, adjacent.TriangleCount);
        Assert.Equal(24, stacked.TriangleCount);
        Assert.Equal(2, stacked.LayerCount);
        Assert.Equal([8, 9], ReadLayerYs(stacked.Glb));
        Assert.Equal([8, 9], ReadSliceCapYs(stacked.Glb));
    }

    [Fact]
    public void LevelAndFallingStatesProduceDifferentSurfaceHeights()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var source = renderer.ExportSchematicGlb(CreateSchematic(
            Block(0, 0, 0, "water", ("level", "0"))));
        var lowFlow = renderer.ExportSchematicGlb(CreateSchematic(
            Block(0, 0, 0, "water", ("level", "7"))));
        var falling = renderer.ExportSchematicGlb(CreateSchematic(
            Block(0, 0, 0, "water", ("level", "8"))));

        var sourceTop = ReadMaximumY(source.Glb);
        var lowFlowTop = ReadMaximumY(lowFlow.Glb);
        var fallingTop = ReadMaximumY(falling.Glb);
        Assert.True(sourceTop > lowFlowTop + 0.4f, $"Expected source ({sourceTop}) to be visibly higher than level 7 ({lowFlowTop}).");
        Assert.True(fallingTop > lowFlowTop + 0.4f, $"Expected falling water ({fallingTop}) to use a full falling column height, not level 8 as a low flow ({lowFlowTop}).");
    }

    [Fact]
    public void NeighborLevelsCreateAContinuousSlopedSurface()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var result = renderer.ExportSchematicGlb(CreateSchematic(
            Block(0, 0, 0, "water", ("level", "0")),
            Block(1, 0, 0, "water", ("level", "7"))));

        var upperHeights = ReadPositionYs(result.Glb)
            .Where(static y => y > -0.49f)
            .Select(static y => MathF.Round(y, 4))
            .Distinct()
            .Order()
            .ToArray();
        Assert.True(upperHeights.Length >= 3, $"Expected multiple corner heights for a slope, got {string.Join(", ", upperHeights)}.");
    }

    [Fact]
    public void WaterloggedAndNaturallySubmergedBlocksKeepTheirModelAndAddWater()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var drySlab = renderer.ExportSchematicGlb(CreateSchematic(
            Block(0, 0, 0, "stone_slab", ("type", "bottom"), ("waterlogged", "false"))));
        var wetSlab = renderer.ExportSchematicGlb(CreateSchematic(
            Block(0, 0, 0, "stone_slab", ("type", "bottom"), ("waterlogged", "true"))));
        var wetBlockWithOwnLevel = renderer.ExportSchematicGlb(CreateSchematic(
            Block(0, 0, 0, "stone_slab", ("type", "bottom"), ("waterlogged", "true"), ("level", "7"))));
        var lavaLoggedSlab = renderer.ExportSchematicGlb(CreateSchematic(
            Block(0, 0, 0, "stone_slab", ("type", "bottom"), ("waterlogged", "false"), ("lava_logged", "true"))));
        var kelp = renderer.ExportSchematicGlb(CreateSchematic(Block(0, 0, 0, "kelp", ("age", "0"))));
        var bubbleColumn = renderer.ExportSchematicGlb(CreateSchematic(Block(0, 0, 0, "bubble_column", ("drag", "false"))));

        Assert.Equal(drySlab.TriangleCount + 12, wetSlab.TriangleCount);
        Assert.Equal(ReadMaximumY(wetSlab.Glb), ReadMaximumY(wetBlockWithOwnLevel.Glb));
        Assert.Equal(drySlab.TriangleCount + 12, lavaLoggedSlab.TriangleCount);
        Assert.True(kelp.TriangleCount > 12, "Kelp should retain its plant model in addition to the water volume.");
        Assert.Equal(12, bubbleColumn.TriangleCount);
        Assert.DoesNotContain(bubbleColumn.Warnings, warning => warning.Contains("Missing block model", StringComparison.Ordinal));
    }

    private static SchematicBlock Block(
        int x,
        int y,
        int z,
        string name,
        params (string Name, string Value)[] properties) =>
        new(
            new SchematicPosition(x, y, z),
            new SchematicBlockState(name, properties.ToDictionary(static pair => pair.Name, static pair => pair.Value)));

    private static LitematicSchematic CreateSchematic(params SchematicBlock[] blocks)
    {
        var minimum = new SchematicPosition(
            blocks.Min(static block => block.Position.X),
            blocks.Min(static block => block.Position.Y),
            blocks.Min(static block => block.Position.Z));
        var maximum = new SchematicPosition(
            blocks.Max(static block => block.Position.X),
            blocks.Max(static block => block.Position.Y),
            blocks.Max(static block => block.Position.Z));
        return new LitematicSchematic
        {
            Name = "Fluid test",
            Regions = [],
            Blocks = blocks,
            Min = minimum,
            Max = maximum
        };
    }

    private static int[] ReadPrimitiveMaterialIndices(byte[] glb)
    {
        using var document = ReadGlbJson(glb, out _);
        return document.RootElement.GetProperty("meshes")
            .EnumerateArray()
            .SelectMany(static mesh => mesh.GetProperty("primitives").EnumerateArray())
            .Select(static primitive => primitive.GetProperty("material").GetInt32())
            .ToArray();
    }

    private static int[] ReadLayerYs(byte[] glb)
    {
        using var document = ReadGlbJson(glb, out _);
        return document.RootElement.GetProperty("nodes")
            .EnumerateArray()
            .Where(static node => node.TryGetProperty("extras", out var extras) && extras.TryGetProperty("eliteY", out _))
            .Select(static node => node.GetProperty("extras").GetProperty("eliteY").GetInt32())
            .Order()
            .ToArray();
    }

    private static int[] ReadSliceCapYs(byte[] glb)
    {
        using var document = ReadGlbJson(glb, out _);
        return document.RootElement.GetProperty("nodes")
            .EnumerateArray()
            .Where(static node => node.TryGetProperty("extras", out var extras) && extras.TryGetProperty("eliteSliceCapY", out _))
            .Select(static node => node.GetProperty("extras").GetProperty("eliteSliceCapY").GetInt32())
            .Order()
            .ToArray();
    }

    private static float ReadMaximumY(byte[] glb)
    {
        return ReadPositionYs(glb).Max();
    }

    private static float[] ReadPositionYs(byte[] glb)
    {
        using var document = ReadGlbJson(glb, out var binaryOffset);
        var root = document.RootElement;
        var accessors = root.GetProperty("accessors");
        var bufferViews = root.GetProperty("bufferViews");
        var values = new List<float>();
        foreach (var mesh in root.GetProperty("meshes").EnumerateArray())
        {
            foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
            {
                var accessor = accessors[primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32()];
                var view = bufferViews[accessor.GetProperty("bufferView").GetInt32()];
                var offset = binaryOffset + GetOptionalInt(view, "byteOffset") + GetOptionalInt(accessor, "byteOffset");
                var count = accessor.GetProperty("count").GetInt32();
                for (var index = 0; index < count; index++)
                {
                    var yOffset = offset + index * 12 + 4;
                    values.Add(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(yOffset, 4))));
                }
            }
        }

        return values.ToArray();
    }

    private static JsonDocument ReadGlbJson(byte[] glb, out int binaryOffset)
    {
        Assert.Equal(0x46546C67u, BinaryPrimitives.ReadUInt32LittleEndian(glb));
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4)));
        var binaryHeaderOffset = 20 + jsonLength;
        Assert.Equal(0x004E4942u, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(binaryHeaderOffset + 4, 4)));
        binaryOffset = binaryHeaderOffset + 8;
        return JsonDocument.Parse(glb.AsMemory(20, jsonLength));
    }

    private static int GetOptionalInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) ? property.GetInt32() : 0;
}
