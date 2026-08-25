using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
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
