using System.Buffers.Binary;
using System.Text.Json;
using MinecraftRenderer.Nbt;
using MinecraftRenderer.Schematics;
using Xunit;

namespace MinecraftRenderer.Tests;

public sealed class LitematicReaderTests
{
    private static readonly string AssetsDirectory =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "minecraft", "assets", "minecraft"));

    [Fact]
    public void ReadsPackedPaletteAndPreservesYLayers()
    {
        var document = CreateDocument(new SchematicPosition(10, 20, 30), new SchematicPosition(2, 2, 1), 0b01_01_00_01);

        var schematic = LitematicReader.Read(document);

        Assert.Equal("Layer test", schematic.Name);
        Assert.Equal(3, schematic.Blocks.Count);
        Assert.Equal(20, schematic.Min.Y);
        Assert.Equal(21, schematic.Max.Y);
        Assert.Contains(schematic.Blocks, block => block.Position == new SchematicPosition(10, 20, 30));
        Assert.Contains(schematic.Blocks, block => block.Position == new SchematicPosition(10, 21, 30));
    }

    [Theory]
    [InlineData(-2, 1, 1, 4, 7, 9)]
    [InlineData(1, -2, 1, 5, 6, 9)]
    [InlineData(1, 1, -2, 5, 7, 8)]
    public void NegativeRegionSizeStartsPackedBlocksAtTheMinimumCorner(
        int sizeX, int sizeY, int sizeZ, int expectedX, int expectedY, int expectedZ)
    {
        var document = CreateDocument(
            new SchematicPosition(5, 7, 9),
            new SchematicPosition(sizeX, sizeY, sizeZ),
            0b00_01);

        var schematic = LitematicReader.Read(document);

        var block = Assert.Single(schematic.Blocks);
        Assert.Equal(new SchematicPosition(expectedX, expectedY, expectedZ), block.Position);
        Assert.Equal("stone", block.State.Name);
    }

    [Fact]
    public void NegativeRegionBlockEntityUsesSignedLocalPosition()
    {
        var entities = new NbtList(NbtTagType.Compound, [
            Compound(("Pos", new NbtIntArray([-1, 0, 0])))
        ]);
        var document = CreateDocument(
            new SchematicPosition(5, 7, 9),
            new SchematicPosition(-2, 1, 1),
            0b00_01,
            entities);

        var block = Assert.Single(LitematicReader.Read(document).Blocks);
        Assert.Equal(new SchematicPosition(4, 7, 9), block.Position);
        Assert.NotNull(block.BlockEntity);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(-1, -1, 0)]
    public void NegativeRegionBlockEntityUsesScalarCoordinates(int x, int y, int z)
    {
        var entities = new NbtList(NbtTagType.Compound, [
            Compound(("x", new NbtInt(x)), ("y", new NbtInt(y)), ("z", new NbtInt(z)))
        ]);
        var document = CreateDocument(
            new SchematicPosition(5, 7, 9),
            new SchematicPosition(-2, -2, 1),
            0b00_01,
            entities);

        var block = Assert.Single(LitematicReader.Read(document).Blocks);
        Assert.Equal(new SchematicPosition(4, 6, 9), block.Position);
        Assert.NotNull(block.BlockEntity);
    }

    [Fact]
    public void AllAirSchematicPreservesRegionBounds()
    {
        var document = CreateDocument(new SchematicPosition(-3, 8, 5), new SchematicPosition(2, 3, -4), 0);

        var schematic = LitematicReader.Read(document);

        Assert.Empty(schematic.Blocks);
        Assert.Equal(new SchematicPosition(-3, 8, 2), schematic.Min);
        Assert.Equal(new SchematicPosition(-2, 10, 5), schematic.Max);
        Assert.Equal(2, schematic.Width);
        Assert.Equal(3, schematic.Height);
        Assert.Equal(4, schematic.Length);
    }

    [Fact]
    public void ExportsLayerAddressableBinaryGltf()
    {
        var schematic = LitematicReader.Read(CreateDocument(
            new SchematicPosition(0, 0, 0),
            new SchematicPosition(2, 2, 1),
            0b01_01_00_01));
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);

        var result = renderer.ExportSchematicGlb(schematic);

        Assert.Equal((byte)'g', result.Glb[0]);
        Assert.Equal((byte)'l', result.Glb[1]);
        Assert.Equal((byte)'T', result.Glb[2]);
        Assert.Equal((byte)'F', result.Glb[3]);
        Assert.Equal(2, result.LayerCount);
        Assert.Equal(SchematicExportFormat.CurrentVersion, result.FormatVersion);
        Assert.Equal(SchematicExportFormat.CurrentVersion, ReadRootExtras(result.Glb).GetProperty("formatVersion").GetInt32());
        Assert.Equal(3, result.BlockCount);
        Assert.True(result.TriangleCount > 0, string.Join(Environment.NewLine, result.Warnings));
    }

    [Fact]
    public void ExportsSavedSignTextAsTexturedGeometry()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var withoutText = renderer.ExportSchematicGlb(LitematicReader.Read(CreateSignDocument(includeText: false)));
        var withText = renderer.ExportSchematicGlb(LitematicReader.Read(CreateSignDocument(includeText: true)));

        Assert.Equal(withoutText.TriangleCount + 2, withText.TriangleCount);
    }

    [Fact]
    public void ExportsStructuredSignTextComponents()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var withoutText = renderer.ExportSchematicGlb(LitematicReader.Read(CreateSignDocument(includeText: false)));
        var withText = renderer.ExportSchematicGlb(LitematicReader.Read(CreateSignDocument(includeText: true, structuredMessages: true)));

        Assert.Equal(withoutText.TriangleCount + 2, withText.TriangleCount);
    }

    [Fact]
    public void EnforcesExportResourceBudgets()
    {
        var schematic = LitematicReader.Read(CreateDocument(
            new SchematicPosition(0, 0, 0),
            new SchematicPosition(2, 2, 1),
            0b01_01_00_01));
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);

        var blockLimit = Assert.Throws<SchematicExportLimitException>(() => renderer.ExportSchematicGlb(
            schematic,
            new SchematicExportOptions(MaximumBlockCount: 2)));
        var triangleLimit = Assert.Throws<SchematicExportLimitException>(() => renderer.ExportSchematicGlb(
            schematic,
            new SchematicExportOptions(CullOccludedFullCubeFaces: false, MaximumTriangleCount: 1)));
        var outputLimit = Assert.Throws<SchematicExportLimitException>(() => renderer.ExportSchematicGlb(
            schematic,
            new SchematicExportOptions(MaximumOutputBytes: 128)));
        var atlasLimit = Assert.Throws<SchematicExportLimitException>(() => renderer.ExportSchematicGlb(
            schematic,
            new SchematicExportOptions(MaximumTextureAtlasPixels: 128)));

        Assert.Equal("block count", blockLimit.LimitName);
        Assert.Equal("triangle count", triangleLimit.LimitName);
        Assert.Equal("GLB output size", outputLimit.LimitName);
        Assert.Equal("texture atlas pixels", atlasLimit.LimitName);
    }

    private static NbtDocument CreateDocument(
        SchematicPosition position,
        SchematicPosition size,
        long packed,
        NbtList? blockEntities = null)
    {
        var palette = new NbtList(NbtTagType.Compound, [
            Compound(("Name", new NbtString("minecraft:air"))),
            Compound(("Name", new NbtString("minecraft:stone")))
        ]);
        var regionEntries = new List<(string Key, NbtTag Value)> {
            ("Position", Vector(position)),
            ("Size", Vector(size)),
            ("BlockStatePalette", palette),
            ("BlockStates", new NbtLongArray([packed]))
        };
        if (blockEntities is not null) regionEntries.Add(("TileEntities", blockEntities));
        var region = Compound(regionEntries.ToArray());
        return new NbtDocument(Compound(
            ("Version", new NbtInt(6)),
            ("MinecraftDataVersion", new NbtInt(3955)),
            ("Metadata", Compound(
                ("Name", new NbtString("Layer test")),
                ("Author", new NbtString("Elite")))),
            ("Regions", new NbtCompound(new Dictionary<string, NbtTag> { ["Main"] = region }))));
    }

    private static JsonElement ReadRootExtras(byte[] glb)
    {
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4)));
        using var document = JsonDocument.Parse(glb.AsMemory(20, jsonLength));
        var root = document.RootElement;
        var sceneRootNodeIndex = root.GetProperty("scenes")[0].GetProperty("nodes")[0].GetInt32();
        return root.GetProperty("nodes")[sceneRootNodeIndex].GetProperty("extras").Clone();
    }

    private static NbtDocument CreateSignDocument(bool includeText, bool structuredMessages = false)
    {
        var palette = new NbtList(NbtTagType.Compound, [
            Compound(("Name", new NbtString("minecraft:air"))),
            Compound(
                ("Name", new NbtString("minecraft:oak_sign")),
                ("Properties", Compound(("rotation", new NbtString("0")))))
        ]);
        var regionEntries = new List<(string Key, NbtTag Value)> {
            ("Position", Vector(new SchematicPosition(0, 0, 0))),
            ("Size", Vector(new SchematicPosition(1, 1, 1))),
            ("BlockStatePalette", palette),
            ("BlockStates", new NbtLongArray([1]))
        };
        if (includeText)
        {
            var messages = structuredMessages
                ? new NbtList(NbtTagType.Compound, [
                    Compound(
                        ("text", new NbtString("Elite")),
                        ("extra", new NbtList(NbtTagType.Compound, [
                            Compound(("text", new NbtString(" SkyBlock")))
                        ]))),
                    Compound(("text", new NbtString(string.Empty))),
                    Compound(("text", new NbtString(string.Empty))),
                    Compound(("text", new NbtString(string.Empty)))
                ])
                : new NbtList(NbtTagType.String, [
                    new NbtString("{\"text\":\"Elite\"}"),
                    new NbtString("\"SkyBlock\""),
                    new NbtString(string.Empty),
                    new NbtString(string.Empty)
                ]);
            var entity = Compound(
                ("Pos", new NbtIntArray([0, 0, 0])),
                ("front_text", Compound(("messages", messages), ("color", new NbtString("black")))));
            regionEntries.Add(("TileEntities", new NbtList(NbtTagType.Compound, [entity])));
        }

        var region = Compound(regionEntries.ToArray());
        return new NbtDocument(Compound(
            ("Version", new NbtInt(6)),
            ("MinecraftDataVersion", new NbtInt(3955)),
            ("Metadata", Compound(("Name", new NbtString("Sign test")))),
            ("Regions", new NbtCompound(new Dictionary<string, NbtTag> { ["Main"] = region }))));
    }

    private static NbtCompound Vector(SchematicPosition value) => Compound(
        ("x", new NbtInt(value.X)),
        ("y", new NbtInt(value.Y)),
        ("z", new NbtInt(value.Z)));

    private static NbtCompound Compound(params (string Key, NbtTag Value)[] values) =>
        new(values.Select(static value => new KeyValuePair<string, NbtTag>(value.Key, value.Value)));
}
