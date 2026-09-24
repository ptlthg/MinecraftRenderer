using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using MinecraftRenderer.Nbt;
using MinecraftRenderer.Schematics;
using Xunit;

namespace MinecraftRenderer.Tests;

public sealed class SchematicBlockStateRenderingTests
{
    private static readonly string AssetsDirectory =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "minecraft", "assets", "minecraft"));

    [Fact]
    public void RejectsUnsafeBlockStateResourceNames()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var result = renderer.ExportSchematicGlb(CreateSchematic([
            new SchematicBlock(new SchematicPosition(0, 0, 0), State("../models/block/stone"))
        ]));

        Assert.Contains(
            "A block with an unsupported resource name was replaced by the missing-model texture.",
            result.Warnings);
        Assert.DoesNotContain(result.Warnings, warning => warning.StartsWith("Invalid blockstate definition", StringComparison.Ordinal));
    }

    [Fact]
    public void SlabTypeSelectsBottomTopAndDoubleGeometry()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);

        var bottom = Export(renderer, State("stone_slab", ("type", "bottom")));
        var top = Export(renderer, State("stone_slab", ("type", "top")));
        var full = Export(renderer, State("stone_slab", ("type", "double")));

        AssertBounds(bottom, -0.5f, 0f);
        AssertBounds(top, 0f, 0.5f);
        AssertBounds(full, -0.5f, 0.5f);
    }

    [Fact]
    public void StairFacingHalfAndShapeSelectAndRotateGeometry()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var eastBottom = Export(renderer, Stair("east", "bottom", "straight"));
        var northBottom = Export(renderer, Stair("north", "bottom", "straight"));
        var northTop = Export(renderer, Stair("north", "top", "straight"));
        var inner = Export(renderer, Stair("north", "bottom", "inner_left"));
        var outer = Export(renderer, Stair("north", "bottom", "outer_left"));

        Assert.Equal(22, eastBottom.Result.TriangleCount);
        Assert.Equal(22, northBottom.Result.TriangleCount);
        Assert.Equal(22, northTop.Result.TriangleCount);
        Assert.Equal(30, inner.Result.TriangleCount);
        Assert.Equal(22, outer.Result.TriangleCount);

        Assert.True(eastBottom.Vertices.Where(vertex => vertex.Position.Y > 0.25f).Average(vertex => vertex.Position.X) > 0.1f);
        Assert.True(northBottom.Vertices.Where(vertex => vertex.Position.Y > 0.25f).Average(vertex => vertex.Position.Z) < -0.1f);
        Assert.True(northTop.Vertices.Where(vertex => vertex.Position.Y < -0.25f).Average(vertex => vertex.Position.Z) < -0.1f);
    }

    [Fact]
    public void OpenTrapdoorsFromLitematicStatesUseExpectedHingeEdges()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var north = Export(renderer, State("oak_trapdoor",
            ("facing", "north"), ("half", "top"), ("open", "true"),
            ("powered", "false"), ("waterlogged", "false")));
        var east = Export(renderer, State("oak_trapdoor",
            ("facing", "east"), ("half", "top"), ("open", "true"),
            ("powered", "false"), ("waterlogged", "false")));

        Assert.Equal(12, north.Result.TriangleCount);
        Assert.Equal(12, east.Result.TriangleCount);
        Assert.Equal(0.3125f, north.Vertices.Min(vertex => vertex.Position.Z), 4);
        Assert.Equal(0.5f, north.Vertices.Max(vertex => vertex.Position.Z), 4);
        Assert.Equal(-0.5f, east.Vertices.Min(vertex => vertex.Position.X), 4);
        Assert.Equal(-0.3125f, east.Vertices.Max(vertex => vertex.Position.X), 4);
    }

    [Fact]
    public void SkeletonSkullsRenderAsHeadsOnFloorAndWall()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var floor = Export(renderer, State("skeleton_skull", ("rotation", "0"), ("powered", "false")));
        var northWall = Export(renderer, State("skeleton_wall_skull", ("facing", "north"), ("powered", "true")));

        AssertBounds(floor, -0.5f, 0f);
        AssertBounds(northWall, -0.25f, 0.25f);
        Assert.Equal(-0.5f, northWall.Vertices.Min(vertex => vertex.Position.Z), 4);
        Assert.Equal(0f, northWall.Vertices.Max(vertex => vertex.Position.Z), 4);
        Assert.Empty(floor.Result.Warnings);
        Assert.Empty(northWall.Result.Warnings);
    }

    [Fact]
    public void PlayerHeadsRenderAsHeadsOnFloorAndWall()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var floor = Export(renderer, State("player_head", ("rotation", "4"), ("powered", "false")));
        var westWall = Export(renderer, State("player_wall_head", ("facing", "west"), ("powered", "true")));

        Assert.Equal(24, floor.Result.TriangleCount);
        Assert.Equal(24, westWall.Result.TriangleCount);
        Assert.Equal(-0.53125f, floor.Vertices.Min(vertex => vertex.Position.Y), 4);
        Assert.Equal(0.03125f, floor.Vertices.Max(vertex => vertex.Position.Y), 4);
        Assert.Equal(-0.28125f, westWall.Vertices.Min(vertex => vertex.Position.Y), 4);
        Assert.Equal(0.28125f, westWall.Vertices.Max(vertex => vertex.Position.Y), 4);
        Assert.Equal(-0.53125f, westWall.Vertices.Min(vertex => vertex.Position.X), 4);
        Assert.Equal(0.03125f, westWall.Vertices.Max(vertex => vertex.Position.X), 4);
        Assert.Empty(floor.Result.Warnings);
        Assert.Empty(westWall.Result.Warnings);
    }

    [Fact]
    public void OakSignRendersBoardAndPostWithoutText()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var sign = Export(renderer, State("oak_sign", ("rotation", "12"), ("waterlogged", "false")));

        Assert.Equal(24, sign.Result.TriangleCount);
        Assert.Equal(-0.5f, sign.Vertices.Min(vertex => vertex.Position.Y), 4);
        Assert.Equal(0.45f, sign.Vertices.Max(vertex => vertex.Position.Y), 4);
        Assert.Empty(sign.Result.Warnings);
    }

    [Fact]
    public void AllSignWoodsRenderStandingWallAndHangingVariants()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        string[] woods = [
            "oak", "spruce", "birch", "jungle", "acacia", "dark_oak", "mangrove", "cherry",
            "bamboo", "crimson", "warped", "pale_oak"
        ];
        var blocks = woods
            .SelectMany(wood => new[] {
                State($"{wood}_sign", ("rotation", "4")),
                State($"{wood}_wall_sign", ("facing", "south")),
                State($"{wood}_hanging_sign", ("rotation", "4"), ("attached", "false")),
                State($"{wood}_wall_hanging_sign", ("facing", "south"))
            })
            .Select((state, index) => new SchematicBlock(new SchematicPosition(index, 0, 0), state))
            .ToArray();

        var export = Export(renderer, CreateSchematic(blocks), cull: false);

        Assert.Equal(woods.Length * (24 + 12 + 36 + 36), export.Result.TriangleCount);
        Assert.Empty(export.Result.Warnings);
    }

    [Fact]
    public void WallSignBoardSitsAgainstTheSupportingFace()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var wall = Export(renderer, State("spruce_wall_sign", ("facing", "south")));
        var northWall = Export(renderer, State("spruce_wall_sign", ("facing", "north")));
        var eastWall = Export(renderer, State("spruce_wall_sign", ("facing", "east")));
        var hanging = Export(renderer, State("bamboo_hanging_sign", ("rotation", "0"), ("attached", "true")));

        Assert.Equal(12, wall.Result.TriangleCount);
        Assert.Equal(-0.5f, wall.Vertices.Min(vertex => vertex.Position.Z), 4);
        Assert.Equal(-0.375f, wall.Vertices.Max(vertex => vertex.Position.Z), 4);
        Assert.Equal(0.375f, northWall.Vertices.Min(vertex => vertex.Position.Z), 4);
        Assert.Equal(0.5f, northWall.Vertices.Max(vertex => vertex.Position.Z), 4);
        Assert.Equal(-0.5f, eastWall.Vertices.Min(vertex => vertex.Position.X), 4);
        Assert.Equal(-0.375f, eastWall.Vertices.Max(vertex => vertex.Position.X), 4);
        Assert.Equal(24, hanging.Result.TriangleCount);
        Assert.Equal(0.5f, hanging.Vertices.Max(vertex => vertex.Position.Y), 4);
    }

    [Fact]
    public void WallSignTextRestsOnTheBoard()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var state = State("spruce_wall_sign", ("facing", "south"));
        var entity = new NbtCompound(new Dictionary<string, NbtTag> {
            ["Text1"] = new NbtString("Hello")
        });
        var export = Export(renderer, CreateSchematic([
            new SchematicBlock(new SchematicPosition(0, 0, 0), state, entity)
        ]), cull: false);

        Assert.Equal(14, export.Result.TriangleCount);
        Assert.Equal(-0.3695f, export.Vertices.Max(vertex => vertex.Position.Z), 4);
    }

    [Fact]
    public void LegacySignBlockNamesUseOakGeometry()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var standing = Export(renderer, State("standing_sign", ("rotation", "0")));
        var wall = Export(renderer, State("wall_sign", ("facing", "south")));

        Assert.Equal(24, standing.Result.TriangleCount);
        Assert.Equal(12, wall.Result.TriangleCount);
        Assert.Empty(standing.Result.Warnings);
        Assert.Empty(wall.Result.Warnings);
    }

    [Fact]
    public void MultipartStateAddsOnlyMatchingFenceConnections()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var disconnected = Export(renderer, State("oak_fence",
            ("north", "false"), ("east", "false"), ("south", "false"), ("west", "false")));
        var connected = Export(renderer, State("oak_fence",
            ("north", "true"), ("east", "true"), ("south", "false"), ("west", "false")));

        Assert.Equal(12, disconnected.Result.TriangleCount);
        Assert.Equal(52, connected.Result.TriangleCount);
    }

    [Fact]
    public void UvLockKeepsRotatedStairTopTextureAlignedToWorld()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var unrotated = Export(renderer, Stair("east", "bottom", "straight"));
        var rotated = Export(renderer, Stair("north", "bottom", "straight"));

        var unrotatedUvs = ReadBaseTopUvs(unrotated.Vertices);
        var rotatedUvs = ReadBaseTopUvs(rotated.Vertices);
        Assert.Equal(unrotatedUvs.Keys.Order(), rotatedUvs.Keys.Order());
        foreach (var position in unrotatedUvs.Keys)
        {
            AssertVectorClose(unrotatedUvs[position], rotatedUvs[position]);
        }
    }

    [Fact]
    public void RotatedFullCubeCullsThePhysicalNeighborFace()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var furnace = State("furnace", ("facing", "east"), ("lit", "false"));
        var blocks = new[] {
            new SchematicBlock(new SchematicPosition(0, 0, 0), furnace),
            new SchematicBlock(new SchematicPosition(1, 0, 0), State("stone"))
        };

        var export = Export(renderer, CreateSchematic(blocks), cull: true);
        var trianglesOnSharedPlane = export.Vertices
            .Chunk(3)
            .Count(triangle => triangle.All(vertex => MathF.Abs(vertex.Position.X) < 0.0001f));

        Assert.Equal(20, export.Result.TriangleCount);
        Assert.Equal(0, trianglesOnSharedPlane);
    }

    [Fact]
    public void AdjacentFullCubesCullTheirSharedFacesByDefault()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var schematic = CreateSchematic([
            new SchematicBlock(new SchematicPosition(0, 0, 0), State("stone")),
            new SchematicBlock(new SchematicPosition(1, 0, 0), State("stone"))
        ]);

        var export = Export(renderer, schematic, cull: true);
        var triangles = export.Vertices.Chunk(3).ToArray();

        Assert.Equal(20, export.Result.TriangleCount);
        Assert.Equal(0, CountTrianglesOnPlane(triangles, static vertex => vertex.Position.X, 0));
        Assert.Equal(2, CountTrianglesOnPlane(triangles, static vertex => vertex.Position.X, -1));
        Assert.Equal(2, CountTrianglesOnPlane(triangles, static vertex => vertex.Position.X, 1));
    }

    [Fact]
    public void FarmlandCullsSharedSidesAcrossMoistureStates()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var schematic = CreateSchematic([
            new SchematicBlock(new SchematicPosition(0, 0, 0), State("farmland", ("moisture", "0"))),
            new SchematicBlock(new SchematicPosition(1, 0, 0), State("farmland", ("moisture", "7")))
        ]);

        var export = Export(renderer, schematic, cull: true);
        Assert.Equal(20, export.Result.TriangleCount);
        Assert.Equal(0, CountTrianglesOnPlane(export.Vertices.Chunk(3).ToArray(), static vertex => vertex.Position.X, 0));
    }

    [Theory]
    [InlineData("dirt_path", null)]
    [InlineData("stone_slab", "bottom")]
    public void MatchingPartialBlockSidesCullWhenFullyCovered(string block, string? slabType)
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var state = slabType is null ? State(block) : State(block, ("type", slabType));
        var export = Export(renderer, CreateSchematic([
            new SchematicBlock(new SchematicPosition(0, 0, 0), state),
            new SchematicBlock(new SchematicPosition(1, 0, 0), state)
        ]), cull: true);

        Assert.Equal(20, export.Result.TriangleCount);
    }

    [Fact]
    public void PartialNeighborLeavesTheUncoveredFullCubeFaceVisible()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var export = Export(renderer, CreateSchematic([
            new SchematicBlock(new SchematicPosition(0, 0, 0), State("stone")),
            new SchematicBlock(new SchematicPosition(1, 0, 0), State("farmland", ("moisture", "7")))
        ]), cull: true);

        Assert.Equal(22, export.Result.TriangleCount);
        Assert.Equal(2, CountTrianglesOnPlane(export.Vertices.Chunk(3).ToArray(), static vertex => vertex.Position.X, 0));
    }

    [Fact]
    public void OppositeSlabHalvesDoNotCullEachOther()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var export = Export(renderer, CreateSchematic([
            new SchematicBlock(new SchematicPosition(0, 0, 0), State("stone_slab", ("type", "bottom"))),
            new SchematicBlock(new SchematicPosition(1, 0, 0), State("stone_slab", ("type", "top")))
        ]), cull: true);

        Assert.Equal(24, export.Result.TriangleCount);
        Assert.Equal(4, CountTrianglesOnPlane(export.Vertices.Chunk(3).ToArray(), static vertex => vertex.Position.X, 0));
    }

    [Fact]
    public void RotatedStairElementsTogetherCoverTheAdjacentFullCubeFace()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var export = Export(renderer, CreateSchematic([
            new SchematicBlock(new SchematicPosition(0, 0, 0), Stair("east", "bottom", "straight")),
            new SchematicBlock(new SchematicPosition(1, 0, 0), State("stone"))
        ]), cull: true);

        Assert.Equal(28, export.Result.TriangleCount);
        Assert.Equal(0, CountTrianglesOnPlane(export.Vertices.Chunk(3).ToArray(), static vertex => vertex.Position.X, 0));
    }

    [Theory]
    [InlineData("glass")]
    [InlineData("oak_leaves")]
    public void TransparentFullCubesCullTheirHiddenFaceButRetainTheAdjacentSolidFace(string transparentBlock)
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var schematic = CreateSchematic([
            new SchematicBlock(new SchematicPosition(0, 0, 0), State("stone")),
            new SchematicBlock(new SchematicPosition(1, 0, 0), State(transparentBlock))
        ]);

        var export = Export(renderer, schematic, cull: true);
        var triangles = export.Vertices.Chunk(3).ToArray();

        Assert.Equal(22, export.Result.TriangleCount);
        Assert.Equal(2, CountTrianglesOnPlane(triangles, static vertex => vertex.Position.X, 0));
    }

    [Theory]
    [InlineData("glass")]
    [InlineData("oak_leaves")]
    public void MatchingTransparentFullCubesCullTheirSharedFaces(string transparentBlock)
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var state = State(transparentBlock);
        var schematic = CreateSchematic([
            new SchematicBlock(new SchematicPosition(0, 0, 0), state),
            new SchematicBlock(new SchematicPosition(1, 0, 0), state)
        ]);

        var export = Export(renderer, schematic, cull: true);
        var triangles = export.Vertices.Chunk(3).ToArray();

        Assert.Equal(20, export.Result.TriangleCount);
        Assert.Equal(0, CountTrianglesOnPlane(triangles, static vertex => vertex.Position.X, 0));
    }

    [Fact]
    public void StackedFullCubesRetainCapsForIndependentLayerVisibility()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var schematic = CreateSchematic([
            new SchematicBlock(new SchematicPosition(0, 0, 0), State("stone")),
            new SchematicBlock(new SchematicPosition(0, 1, 0), State("stone"))
        ]);

        var export = Export(renderer, schematic, cull: true);
        var triangles = export.Vertices.Chunk(3).ToArray();

        Assert.Equal(24, export.Result.TriangleCount);
        Assert.Equal(4, CountTrianglesOnPlane(triangles, static vertex => vertex.Position.Y, 0.5f));
        Assert.Equal(20, CountLayerTriangles(export.Result.Glb));
        Assert.Equal([0, 1], ReadSliceCapYs(export.Result.Glb));
    }

    [Fact]
    public void DenseFullCubeVolumeCullsFacesWithinEachLayer()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var stone = State("stone");
        var blocks = (
            from x in Enumerable.Range(0, 4)
            from y in Enumerable.Range(0, 4)
            from z in Enumerable.Range(0, 4)
            select new SchematicBlock(new SchematicPosition(x, y, z), stone)
        ).ToArray();

        var export = renderer.ExportSchematicGlb(CreateSchematic(blocks));

        Assert.Equal(384, export.TriangleCount);
        Assert.Equal(192, CountLayerTriangles(export.Glb));
    }

    [Fact]
    public void ExportUsesSingleSidedSolidsAndFilteredTextureMinification()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var export = renderer.ExportSchematicGlb(CreateSchematic([
            new SchematicBlock(new SchematicPosition(0, 0, 0), State("stone"))
        ]));
        using var document = ReadGlbJson(export.Glb);
        var root = document.RootElement;
        var materials = root.GetProperty("materials").EnumerateArray().ToArray();
        var sampler = root.GetProperty("samplers")[0];

        Assert.False(materials[0].GetProperty("doubleSided").GetBoolean());
        Assert.False(materials[1].GetProperty("doubleSided").GetBoolean());
        Assert.True(materials[2].GetProperty("doubleSided").GetBoolean());
        Assert.Equal(9728, sampler.GetProperty("magFilter").GetInt32());
        Assert.Equal(9729, sampler.GetProperty("minFilter").GetInt32());

        var primitive = root.GetProperty("meshes")[0].GetProperty("primitives")[0];
        var attributes = primitive.GetProperty("attributes");
        var accessors = root.GetProperty("accessors");
        var normalAccessor = accessors[attributes.GetProperty("NORMAL").GetInt32()];
        var colorAccessor = accessors[attributes.GetProperty("COLOR_0").GetInt32()];
        Assert.False(primitive.TryGetProperty("indices", out _));
        Assert.Equal(5120, normalAccessor.GetProperty("componentType").GetInt32());
        Assert.True(normalAccessor.GetProperty("normalized").GetBoolean());
        Assert.Equal(5121, colorAccessor.GetProperty("componentType").GetInt32());
        Assert.True(colorAccessor.GetProperty("normalized").GetBoolean());
    }

    [Fact]
    public void FullBlockTextureUvsReachTileEdgesWithoutEnteringTheAtlasGutter()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var export = Export(renderer, State("stone"));
        using var document = ReadGlbJson(export.Result.Glb);
        var root = document.RootElement;
        var imageViewIndex = root.GetProperty("images")[0].GetProperty("bufferView").GetInt32();
        var imageView = root.GetProperty("bufferViews")[imageViewIndex];
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(export.Result.Glb.AsSpan(12, 4)));
        var imageOffset = 20 + jsonLength + 8 + imageView.GetProperty("byteOffset").GetInt32();
        var atlasWidth = BinaryPrimitives.ReadInt32BigEndian(export.Result.Glb.AsSpan(imageOffset + 16, 4));
        var atlasHeight = BinaryPrimitives.ReadInt32BigEndian(export.Result.Glb.AsSpan(imageOffset + 20, 4));
        var minU = export.Vertices.Min(vertex => vertex.Uv.X) * atlasWidth;
        var maxU = export.Vertices.Max(vertex => vertex.Uv.X) * atlasWidth;
        var minV = export.Vertices.Min(vertex => vertex.Uv.Y) * atlasHeight;
        var maxV = export.Vertices.Max(vertex => vertex.Uv.Y) * atlasHeight;

        Assert.Equal(16f, maxU - minU, 3);
        Assert.Equal(16f, maxV - minV, 3);
        Assert.Equal(MathF.Round(minU), minU, 3);
        Assert.Equal(MathF.Round(maxU), maxU, 3);
        Assert.Equal(MathF.Round(minV), minV, 3);
        Assert.Equal(MathF.Round(maxV), maxV, 3);
    }

    [Fact]
    public void GpuInstancingReusesFaceMeshesWithinLayerNodes()
    {
        using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
        var export = renderer.ExportSchematicGlb(
            CreateSchematic([
                new SchematicBlock(new SchematicPosition(0, 0, 0), State("stone")),
                new SchematicBlock(new SchematicPosition(2, 0, 0), State("stone"))
            ]),
            new SchematicExportOptions(CullOccludedFullCubeFaces: false, UseGpuInstancing: true));
        using var document = ReadGlbJson(export.Glb);
        var root = document.RootElement;

        Assert.Contains("EXT_mesh_gpu_instancing", root.GetProperty("extensionsUsed").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("EXT_mesh_gpu_instancing", root.GetProperty("extensionsRequired").EnumerateArray().Select(value => value.GetString()));
        Assert.True(root.GetProperty("meshes").GetArrayLength() < export.TriangleCount / 2);

        var accessors = root.GetProperty("accessors");
        var nodes = root.GetProperty("nodes");
        var layer = nodes.EnumerateArray().Single(node =>
            node.TryGetProperty("extras", out var extras) && extras.TryGetProperty("eliteY", out _));
        var instanceCount = 0;
        foreach (var childIndex in layer.GetProperty("children").EnumerateArray().Select(value => value.GetInt32()))
        {
            var child = nodes[childIndex];
            var translationAccessor = child
                .GetProperty("extensions")
                .GetProperty("EXT_mesh_gpu_instancing")
                .GetProperty("attributes")
                .GetProperty("TRANSLATION")
                .GetInt32();
            instanceCount += accessors[translationAccessor].GetProperty("count").GetInt32();
        }

        Assert.Equal(12, instanceCount);
        Assert.Equal(24, export.TriangleCount);
    }

    [Fact]
    public void WeightedVariantsAreStableButVaryByBlockPosition()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "minecraft-renderer-weighted-state-tests", Guid.NewGuid().ToString("N"));
        try
        {
            CreateWeightedStateFixture(fixture);
            using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(fixture);
            var weighted = State("weighted_test");
            var blocks = Enumerable.Range(0, 64)
                .Select(x => new SchematicBlock(new SchematicPosition(x, 0, 0), weighted))
                .ToArray();
            var schematic = CreateSchematic(blocks);

            var first = renderer.ExportSchematicGlb(schematic, new SchematicExportOptions(CullOccludedFullCubeFaces: false));
            var second = renderer.ExportSchematicGlb(schematic, new SchematicExportOptions(CullOccludedFullCubeFaces: false));

            Assert.InRange(first.TriangleCount, 129, 255);
            Assert.Equal(first.TriangleCount, second.TriangleCount);
            Assert.Equal(first.Glb, second.Glb);
        }
        finally
        {
            if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true);
        }
    }

    private static SchematicBlockState Stair(string facing, string half, string shape) =>
        State("oak_stairs", ("facing", facing), ("half", half), ("shape", shape), ("waterlogged", "false"));

    private static SchematicBlockState State(string name, params (string Name, string Value)[] properties) =>
        new(name, properties.ToDictionary(static pair => pair.Name, static pair => pair.Value));

    private static ExportedSchematic Export(MinecraftBlockRenderer renderer, SchematicBlockState state) =>
        Export(renderer, CreateSchematic([new SchematicBlock(new SchematicPosition(0, 0, 0), state)]), cull: false);

    private static ExportedSchematic Export(MinecraftBlockRenderer renderer, LitematicSchematic schematic, bool cull)
    {
        var result = renderer.ExportSchematicGlb(schematic, new SchematicExportOptions(CullOccludedFullCubeFaces: cull));
        return new ExportedSchematic(result, ReadVertices(result.Glb));
    }

    private static LitematicSchematic CreateSchematic(IReadOnlyList<SchematicBlock> blocks)
    {
        var min = new SchematicPosition(blocks.Min(static block => block.Position.X), blocks.Min(static block => block.Position.Y), blocks.Min(static block => block.Position.Z));
        var max = new SchematicPosition(blocks.Max(static block => block.Position.X), blocks.Max(static block => block.Position.Y), blocks.Max(static block => block.Position.Z));
        return new LitematicSchematic
        {
            Name = "Blockstate test",
            Regions = [],
            Blocks = blocks,
            Min = min,
            Max = max
        };
    }

    private static void AssertBounds(ExportedSchematic export, float minimumY, float maximumY)
    {
        Assert.Equal(12, export.Result.TriangleCount);
        Assert.Equal(minimumY, export.Vertices.Min(static vertex => vertex.Position.Y), 4);
        Assert.Equal(maximumY, export.Vertices.Max(static vertex => vertex.Position.Y), 4);
    }

    private static Dictionary<(float X, float Z), Vector2> ReadBaseTopUvs(IReadOnlyList<GlbVertex> vertices)
    {
        var top = vertices
            .Where(vertex => MathF.Abs(vertex.Position.Y) < 0.0001f && vertex.Normal.Y > 0.99f)
            .ToArray();
        Assert.Equal(6, top.Length);
        var minU = top.Min(static vertex => vertex.Uv.X);
        var maxU = top.Max(static vertex => vertex.Uv.X);
        var minV = top.Min(static vertex => vertex.Uv.Y);
        var maxV = top.Max(static vertex => vertex.Uv.Y);
        return top
            .GroupBy(static vertex => (MathF.Round(vertex.Position.X, 4), MathF.Round(vertex.Position.Z, 4)))
            .ToDictionary(
                static group => group.Key,
                group => new Vector2(
                    (group.First().Uv.X - minU) / (maxU - minU),
                    (group.First().Uv.Y - minV) / (maxV - minV)));
    }

    private static void AssertVectorClose(Vector2 expected, Vector2 actual)
    {
        Assert.InRange(actual.X, expected.X - 0.0001f, expected.X + 0.0001f);
        Assert.InRange(actual.Y, expected.Y - 0.0001f, expected.Y + 0.0001f);
    }

    private static int CountTrianglesOnPlane(
        IEnumerable<GlbVertex[]> triangles,
        Func<GlbVertex, float> coordinate,
        float plane) => triangles.Count(triangle =>
        triangle.All(vertex => MathF.Abs(coordinate(vertex) - plane) < 0.0001f));

    private static JsonDocument ReadGlbJson(byte[] glb)
    {
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4)));
        return JsonDocument.Parse(glb.AsMemory(20, jsonLength));
    }

    private static int CountLayerTriangles(byte[] glb)
    {
        using var document = ReadGlbJson(glb);
        var root = document.RootElement;
        var meshes = root.GetProperty("meshes");
        var accessors = root.GetProperty("accessors");
        var triangleCount = 0;
        foreach (var node in root.GetProperty("nodes").EnumerateArray())
        {
            if (!node.TryGetProperty("extras", out var extras) || !extras.TryGetProperty("eliteY", out _)) continue;
            var mesh = meshes[node.GetProperty("mesh").GetInt32()];
            foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
            {
                var vertexAccessor = accessors[
                    primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32()];
                triangleCount += vertexAccessor.GetProperty("count").GetInt32() / 3;
            }
        }
        return triangleCount;
    }

    private static int[] ReadSliceCapYs(byte[] glb)
    {
        using var document = ReadGlbJson(glb);
        return document.RootElement.GetProperty("nodes")
            .EnumerateArray()
            .Where(static node => node.TryGetProperty("extras", out var extras) && extras.TryGetProperty("eliteSliceCapY", out _))
            .Select(static node => node.GetProperty("extras").GetProperty("eliteSliceCapY").GetInt32())
            .Order()
            .ToArray();
    }

    private static IReadOnlyList<GlbVertex> ReadVertices(byte[] glb)
    {
        using var document = ReadGlbJson(glb);
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4)));
        var binaryHeader = 20 + jsonLength;
        var binaryLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(binaryHeader, 4)));
        var binaryOffset = binaryHeader + 8;
        var root = document.RootElement;
        var accessors = root.GetProperty("accessors");
        var bufferViews = root.GetProperty("bufferViews");
        var vertices = new List<GlbVertex>();
        foreach (var mesh in root.GetProperty("meshes").EnumerateArray())
        {
            foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
            {
                var attributes = primitive.GetProperty("attributes");
                var positions = ReadFloatAccessor(attributes.GetProperty("POSITION").GetInt32(), 3);
                var normals = ReadNormalizedAccessor(attributes.GetProperty("NORMAL").GetInt32(), 3);
                var uvs = ReadFloatAccessor(attributes.GetProperty("TEXCOORD_0").GetInt32(), 2);
                for (var index = 0; index < positions.Length / 3; index++)
                {
                    vertices.Add(new GlbVertex(
                        new Vector3(positions[index * 3], positions[index * 3 + 1], positions[index * 3 + 2]),
                        new Vector3(normals[index * 3], normals[index * 3 + 1], normals[index * 3 + 2]),
                        new Vector2(uvs[index * 2], uvs[index * 2 + 1])));
                }
            }
        }
        return vertices;

        float[] ReadFloatAccessor(int accessorIndex, int componentCount)
        {
            var accessor = accessors[accessorIndex];
            var count = accessor.GetProperty("count").GetInt32();
            var view = bufferViews[accessor.GetProperty("bufferView").GetInt32()];
            var offset = view.GetProperty("byteOffset").GetInt32()
                + (accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0);
            var result = new float[count * componentCount];
            for (var index = 0; index < result.Length; index++)
            {
                result[index] = BinaryPrimitives.ReadSingleLittleEndian(
                    glb.AsSpan(binaryOffset + offset + index * sizeof(float), sizeof(float)));
            }
            return result;
        }

        float[] ReadNormalizedAccessor(int accessorIndex, int componentCount)
        {
            var accessor = accessors[accessorIndex];
            var count = accessor.GetProperty("count").GetInt32();
            var view = bufferViews[accessor.GetProperty("bufferView").GetInt32()];
            var offset = view.GetProperty("byteOffset").GetInt32()
                + (accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0);
            var result = new float[count * componentCount];
            for (var index = 0; index < result.Length; index++)
            {
                result[index] = Math.Max((sbyte)glb[binaryOffset + offset + index] / 127f, -1f);
            }
            return result;
        }
    }

    private static void CreateWeightedStateFixture(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "blockstates"));
        Directory.CreateDirectory(Path.Combine(root, "models", "block"));
        Directory.CreateDirectory(Path.Combine(root, "textures", "block"));
        File.WriteAllText(Path.Combine(root, "blockstates", "weighted_test.json"), """
			{"variants":{"": [{"model":"minecraft:block/weighted_one","weight":1},{"model":"minecraft:block/weighted_two","weight":3}]}}
			""");
        File.WriteAllText(Path.Combine(root, "models", "block", "weighted_one.json"), ModelWithFaces("up"));
        File.WriteAllText(Path.Combine(root, "models", "block", "weighted_two.json"), ModelWithFaces("up", "north"));
        File.Copy(
            Path.Combine(AssetsDirectory, "textures", "block", "stone.png"),
            Path.Combine(root, "textures", "block", "weighted_test.png"));
    }

    private static string ModelWithFaces(params string[] faces)
    {
        var faceJson = string.Join(',', faces.Select(face => $"\"{face}\":{{\"texture\":\"#all\"}}"));
        return $"{{\"textures\":{{\"all\":\"minecraft:block/weighted_test\"}},\"elements\":[{{\"from\":[0,0,0],\"to\":[16,16,16],\"faces\":{{{faceJson}}}}}]}}";
    }

    private sealed record ExportedSchematic(SchematicExportResult Result, IReadOnlyList<GlbVertex> Vertices);
    private sealed record GlbVertex(Vector3 Position, Vector3 Normal, Vector2 Uv);
}
