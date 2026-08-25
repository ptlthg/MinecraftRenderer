namespace MinecraftRenderer;

using System.Buffers.Binary;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MinecraftRenderer.Nbt;
using MinecraftRenderer.Schematics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

public sealed partial class MinecraftBlockRenderer
{
    public SchematicExportResult ExportSchematicGlb(
        LitematicSchematic schematic,
        SchematicExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(schematic);
        options ??= new SchematicExportOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumTextureAtlasWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumBlockCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumTriangleCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumOutputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumTextureAtlasPixels);
        if (schematic.Blocks.Count > options.MaximumBlockCount)
        {
            throw new SchematicExportLimitException("block count", schematic.Blocks.Count, options.MaximumBlockCount);
        }

        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var resolver = new SchematicStateResolver(_assetsDirectory, _modelResolver, _blockRegistry, warnings);
        var opaqueFullCubeModels = new Dictionary<BlockModelInstance, bool>();
        var fullCubeStates = new Dictionary<SchematicPosition, string>();
        var occupiedOpaqueFullCubes = new HashSet<SchematicPosition>();
        foreach (var block in schematic.Blocks)
        {
            var resolved = resolver.Resolve(block.State, block.Position);
            if (!resolved.IsFullCube) continue;
            fullCubeStates[block.Position] = block.State.Key;
            if (IsOpaqueFullCube(resolved, opaqueFullCubeModels))
            {
                occupiedOpaqueFullCubes.Add(block.Position);
            }
        }
        var schematicFluids = BuildSchematicFluidLookup(schematic.Blocks);

        var centerX = (schematic.Min.X + schematic.Max.X) * 0.5f;
        var centerZ = (schematic.Min.Z + schematic.Max.Z) * 0.5f;
        var layerTriangles = new SortedDictionary<int, List<ExportTriangle>>();
        var layerSliceCaps = new SortedDictionary<int, List<ExportTriangle>>();
        var triangleCount = 0L;
        var fluidTextures = new Dictionary<SchematicFluidKind, SchematicFluidTextures>();
        using var signTextRenderer = SignTextRenderer.TryCreate(_assetsDirectory, warnings);
        foreach (var block in schematic.Blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = resolver.Resolve(block.State, block.Position);
            schematicFluids.TryGetValue(block.Position, out var fluid);
            if (resolved.Parts.Count == 0 && fluid is not { Standalone: true })
            {
                resolved = ResolveMissingState(block.State, warnings);
            }

            var target = layerTriangles.GetValueOrDefault(block.Position.Y);
            if (target is null)
            {
                target = [];
                layerTriangles[block.Position.Y] = target;
            }
            var sliceCaps = layerSliceCaps.GetValueOrDefault(block.Position.Y);
            if (sliceCaps is null)
            {
                sliceCaps = [];
                layerSliceCaps[block.Position.Y] = sliceCaps;
            }
            var trianglesBeforeBlock = target.Count;
            var sliceCapsBeforeBlock = sliceCaps.Count;

            var modelParts = fluid is { Standalone: true } ? Array.Empty<ResolvedModelPart>() : resolved.Parts;
            foreach (var part in modelParts)
            {
                var rotation = Matrix4x4.CreateRotationX(part.XRotation * DegreesToRadians)
                    * Matrix4x4.CreateRotationY(-part.YRotation * DegreesToRadians);
                var translation = Matrix4x4.CreateTranslation(
                    block.Position.X - centerX,
                    block.Position.Y - schematic.Min.Y,
                    block.Position.Z - centerZ);
                var triangles = BuildTriangles(part.Model, rotation * translation, applyInventoryLighting: true, block.State.Name);
                var transformedTriangles = TransformSchematicPartTriangles(triangles, rotation, part.UvLock);
                foreach (var triangle in transformedTriangles)
                {
                    var exportTriangle = new ExportTriangle(
                        triangle.V1,
                        triangle.V2,
                        triangle.V3,
                        Vector3.Normalize(triangle.Normal),
                        triangle.T1,
                        triangle.T2,
                        triangle.T3,
                        triangle.Texture,
                        triangle.TextureRect,
                        triangle.Shading);
                    if (options.CullOccludedFullCubeFaces
                        && fullCubeStates.TryGetValue(block.Position, out var blockStateKey)
                        && HasOccludingNeighbor(
                            block.Position,
                            triangle.FaceDirection,
                            blockStateKey,
                            fullCubeStates,
                            occupiedOpaqueFullCubes))
                    {
                        // Keep vertical faces as normally hidden slice caps. The Website
                        // enables them only when this Y layer is viewed by itself.
                        if (triangle.FaceDirection is BlockFaceDirection.Up or BlockFaceDirection.Down)
                        {
                            sliceCaps.Add(exportTriangle);
                        }
                        continue;
                    }

                    target.Add(exportTriangle);
                }
            }

            if (block.BlockEntity is not null && block.State.Name.EndsWith("sign", StringComparison.Ordinal))
            {
                AppendSignText(block, centerX, centerZ, target, signTextRenderer);
            }

            if (fluid is not null)
            {
                AppendSchematicFluid(
                    fluid,
                    schematicFluids,
                    occupiedOpaqueFullCubes,
                    centerX,
                    centerZ,
                    schematic.Min.Y,
                    target,
                    sliceCaps,
                    fluidTextures,
                    warnings);
            }

            triangleCount += target.Count - trianglesBeforeBlock + sliceCaps.Count - sliceCapsBeforeBlock;
            if (triangleCount > options.MaximumTriangleCount)
            {
                throw new SchematicExportLimitException("triangle count", triangleCount, options.MaximumTriangleCount);
            }
        }

        var allTriangles = layerTriangles.Values
            .SelectMany(static triangles => triangles)
            .Concat(layerSliceCaps.Values.SelectMany(static triangles => triangles))
            .ToArray();
        var atlas = BuildAtlas(allTriangles, options.MaximumTextureAtlasWidth, options.MaximumTextureAtlasPixels);
        var glb = BuildGlb(
            layerTriangles,
            layerSliceCaps,
            atlas,
            schematic,
            warnings,
            options.UseGpuInstancing,
            cancellationToken);
        if (glb.LongLength > options.MaximumOutputBytes)
        {
            throw new SchematicExportLimitException("GLB output size", glb.LongLength, options.MaximumOutputBytes);
        }
        var visibleLayerCount = layerTriangles.Keys
            .Union(layerSliceCaps.Keys)
            .Count(y => layerTriangles.GetValueOrDefault(y)?.Count > 0 || layerSliceCaps.GetValueOrDefault(y)?.Count > 0);
        return new SchematicExportResult(
            glb,
            schematic.Min.Y,
            schematic.Max.Y,
            visibleLayerCount,
            schematic.Blocks.Count,
            allTriangles.Length,
            warnings.Order(StringComparer.Ordinal).ToArray());
    }

    private static void AppendSignText(
        SchematicBlock block,
        float centerX,
        float centerZ,
        List<ExportTriangle> target,
        SignTextRenderer? textRenderer)
    {
        if (textRenderer is null || block.BlockEntity is null) return;
        var front = textRenderer.Render(block.BlockEntity, "front_text", legacy: true);
        var back = textRenderer.Render(block.BlockEntity, "back_text", legacy: false);
        if (front is null && back is null) return;

        var normal = GetSignNormal(block.State);
        var verticalCenter = block.State.Name.Contains("hanging_sign", StringComparison.Ordinal) ? 0.08f
            : block.State.Name.Contains("wall_sign", StringComparison.Ordinal) ? 0f
            : 0.2f;
        var center = new Vector3(
            block.Position.X - centerX,
            block.Position.Y + verticalCenter,
            block.Position.Z - centerZ);
        if (front is not null) AppendTextQuad(target, front, center, normal);
        if (back is not null) AppendTextQuad(target, back, center, -normal);
    }

    private static Vector3 GetSignNormal(SchematicBlockState state)
    {
        if (state.Properties.TryGetValue("facing", out var facing))
        {
            return facing.ToLowerInvariant() switch
            {
                "north" => new Vector3(0, 0, -1),
                "south" => new Vector3(0, 0, 1),
                "east" => new Vector3(1, 0, 0),
                "west" => new Vector3(-1, 0, 0),
                _ => new Vector3(0, 0, 1)
            };
        }

        var rotation = state.Properties.TryGetValue("rotation", out var value) && int.TryParse(value, out var parsed)
            ? parsed & 15
            : 0;
        var radians = rotation * MathF.PI / 8f;
        return Vector3.Normalize(new Vector3(-MathF.Sin(radians), 0, MathF.Cos(radians)));
    }

    private static void AppendTextQuad(List<ExportTriangle> target, Image<Rgba32> texture, Vector3 center, Vector3 normal)
    {
        const float halfWidth = 0.375f;
        const float halfHeight = 0.175f;
        var right = Vector3.Normalize(new Vector3(normal.Z, 0, -normal.X));
        var quadCenter = center + normal * 0.506f;
        var bottomLeft = quadCenter - right * halfWidth - Vector3.UnitY * halfHeight;
        var bottomRight = quadCenter + right * halfWidth - Vector3.UnitY * halfHeight;
        var topRight = quadCenter + right * halfWidth + Vector3.UnitY * halfHeight;
        var topLeft = quadCenter - right * halfWidth + Vector3.UnitY * halfHeight;
        var rect = new Rectangle(0, 0, texture.Width, texture.Height);
        target.Add(new ExportTriangle(bottomLeft, bottomRight, topRight, normal, new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), texture, rect, 1));
        target.Add(new ExportTriangle(bottomLeft, topRight, topLeft, normal, new Vector2(0, 1), new Vector2(1, 0), new Vector2(0, 0), texture, rect, 1));
    }

    private ResolvedSchematicState ResolveMissingState(SchematicBlockState state, HashSet<string> warnings)
    {
        warnings.Add(IsSafeSchematicResourceName(state.Name)
            ? $"Missing block model: minecraft:{state.Name}"
            : "A block with an unsupported resource name was replaced by the missing-model texture.");
        try
        {
            return new ResolvedSchematicState([new ResolvedModelPart(_modelResolver.Resolve("builtin/missing"), 0, 0, false)], true);
        }
        catch
        {
            return new ResolvedSchematicState([], false);
        }
    }

    private static IReadOnlyList<VisibleTriangle> TransformSchematicPartTriangles(
        IReadOnlyList<VisibleTriangle> triangles,
        Matrix4x4 rotation,
        bool uvLock)
    {
        if (triangles.Count == 0) return triangles;
        var transformed = new VisibleTriangle[triangles.Count];
        for (var index = 0; index < triangles.Count; index += 2)
        {
            var first = triangles[index];
            var direction = TransformFaceDirection(first.FaceDirection, rotation);
            if (index + 1 >= triangles.Count
                || triangles[index + 1].ElementIndex != first.ElementIndex
                || triangles[index + 1].FaceDirection != first.FaceDirection)
            {
                transformed[index] = first with { FaceDirection = direction };
                continue;
            }

            var second = triangles[index + 1];
            if (!uvLock)
            {
                transformed[index] = first with { FaceDirection = direction };
                transformed[index + 1] = second with { FaceDirection = direction };
                continue;
            }

            var positions = new[] { first.V1, first.V2, first.V3, second.V3 };
            var uvs = new[] { first.T1, first.T2, first.T3, second.T3 };
            var lockedUvs = LockUvsToWorld(positions, uvs, direction);
            transformed[index] = first with
            {
                T1 = lockedUvs[0],
                T2 = lockedUvs[1],
                T3 = lockedUvs[2],
                FaceDirection = direction
            };
            transformed[index + 1] = second with
            {
                T1 = lockedUvs[0],
                T2 = lockedUvs[2],
                T3 = lockedUvs[3],
                FaceDirection = direction
            };
        }
        return transformed;
    }

    private static Vector2[] LockUvsToWorld(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector2> sourceUvs,
        BlockFaceDirection direction)
    {
        var projected = positions.Select(position => ProjectFace(position, direction)).ToArray();
        var minU = projected.Min(static value => value.X);
        var maxU = projected.Max(static value => value.X);
        var minV = projected.Min(static value => value.Y);
        var maxV = projected.Max(static value => value.Y);
        var result = new Vector2[4];
        for (var index = 0; index < projected.Length; index++)
        {
            var highU = MathF.Abs(projected[index].X - maxU) < MathF.Abs(projected[index].X - minU);
            var highV = MathF.Abs(projected[index].Y - maxV) < MathF.Abs(projected[index].Y - minV);
            var sourceIndex = (highU, highV) switch
            {
                (false, false) => 0,
                (false, true) => 1,
                (true, true) => 2,
                (true, false) => 3
            };
            result[index] = sourceUvs[sourceIndex];
        }
        return result;
    }

    private static Vector2 ProjectFace(Vector3 position, BlockFaceDirection direction) => direction switch
    {
        BlockFaceDirection.Down => new Vector2(position.X, -position.Z),
        BlockFaceDirection.Up => new Vector2(position.X, position.Z),
        BlockFaceDirection.North => new Vector2(-position.X, -position.Y),
        BlockFaceDirection.South => new Vector2(position.X, -position.Y),
        BlockFaceDirection.West => new Vector2(position.Z, -position.Y),
        BlockFaceDirection.East => new Vector2(-position.Z, -position.Y),
        _ => Vector2.Zero
    };

    private static BlockFaceDirection TransformFaceDirection(BlockFaceDirection direction, Matrix4x4 rotation)
    {
        var normal = direction switch
        {
            BlockFaceDirection.North => -Vector3.UnitZ,
            BlockFaceDirection.South => Vector3.UnitZ,
            BlockFaceDirection.East => Vector3.UnitX,
            BlockFaceDirection.West => -Vector3.UnitX,
            BlockFaceDirection.Up => Vector3.UnitY,
            BlockFaceDirection.Down => -Vector3.UnitY,
            _ => Vector3.Zero
        };
        var transformed = Vector3.TransformNormal(normal, rotation);
        var absolute = Vector3.Abs(transformed);
        if (absolute.X >= absolute.Y && absolute.X >= absolute.Z)
        {
            return transformed.X >= 0 ? BlockFaceDirection.East : BlockFaceDirection.West;
        }
        if (absolute.Y >= absolute.Z)
        {
            return transformed.Y >= 0 ? BlockFaceDirection.Up : BlockFaceDirection.Down;
        }
        return transformed.Z >= 0 ? BlockFaceDirection.South : BlockFaceDirection.North;
    }

    private static bool HasOccludingNeighbor(
        SchematicPosition position,
        BlockFaceDirection direction,
        string blockStateKey,
        IReadOnlyDictionary<SchematicPosition, string> fullCubeStates,
        IReadOnlySet<SchematicPosition> opaqueFullCubes)
    {
        var neighbor = direction switch
        {
            BlockFaceDirection.North => position with { Z = position.Z - 1 },
            BlockFaceDirection.South => position with { Z = position.Z + 1 },
            BlockFaceDirection.East => position with { X = position.X + 1 },
            BlockFaceDirection.West => position with { X = position.X - 1 },
            BlockFaceDirection.Up => position with { Y = position.Y + 1 },
            BlockFaceDirection.Down => position with { Y = position.Y - 1 },
            _ => position
        };
        return opaqueFullCubes.Contains(neighbor)
            || (fullCubeStates.TryGetValue(neighbor, out var neighborStateKey)
                && string.Equals(blockStateKey, neighborStateKey, StringComparison.Ordinal));
    }

    private bool IsOpaqueFullCube(
        ResolvedSchematicState resolved,
        IDictionary<BlockModelInstance, bool> modelOpacityCache)
    {
        if (!resolved.IsFullCube || resolved.Parts.Count != 1) return false;
        var model = resolved.Parts[0].Model;
        if (modelOpacityCache.TryGetValue(model, out var cached)) return cached;

        foreach (var face in model.Elements[0].Faces.Values)
        {
            var texture = _textureRepository.GetTexture(ResolveTexture(face.Texture, model));
            for (var y = 0; y < texture.Height; y++)
            {
                foreach (var pixel in texture.DangerousGetPixelRowMemory(y).Span)
                {
                    if (pixel.A != byte.MaxValue)
                    {
                        modelOpacityCache[model] = false;
                        return false;
                    }
                }
            }
        }

        modelOpacityCache[model] = true;
        return true;
    }

    private static TextureAtlas BuildAtlas(
        IReadOnlyList<ExportTriangle> triangles,
        int maximumWidth,
        long maximumPixels)
    {
        var comparer = TextureTileComparer.Instance;
        var tiles = triangles
            .Select(static triangle => new TextureTile(triangle.Texture, triangle.TextureRect))
            .Distinct(comparer)
            .ToArray();
        if (tiles.Length == 0)
        {
            using var empty = new Image<Rgba32>(1, 1);
            empty[0, 0] = new Rgba32(255, 0, 255, 255);
            return new TextureAtlas(ToPng(empty), 1, 1, new Dictionary<TextureTile, AtlasEntry>(comparer));
        }

        maximumWidth = Math.Max(64, maximumWidth);
        var placements = new Dictionary<TextureTile, Rectangle>(comparer);
        var x = 1;
        var y = 1;
        var rowHeight = 0;
        var usedWidth = 1;
        foreach (var tile in tiles)
        {
            var width = Math.Max(1, tile.Rectangle.Width);
            var height = Math.Max(1, tile.Rectangle.Height);
            if (x + width + 1 > maximumWidth && x > 1)
            {
                x = 1;
                y += rowHeight + 2;
                rowHeight = 0;
            }
            placements[tile] = new Rectangle(x, y, width, height);
            x += width + 2;
            rowHeight = Math.Max(rowHeight, height);
            usedWidth = Math.Max(usedWidth, x);
        }

        var atlasHeight = y + rowHeight + 1;
        var atlasWidth = Math.Min(maximumWidth, usedWidth);
        var atlasPixels = checked((long)atlasWidth * atlasHeight);
        if (atlasPixels > maximumPixels)
        {
            throw new SchematicExportLimitException("texture atlas pixels", atlasPixels, maximumPixels);
        }
        using var image = new Image<Rgba32>(atlasWidth, atlasHeight);
        var entries = new Dictionary<TextureTile, AtlasEntry>(comparer);
        foreach (var (tile, placement) in placements)
        {
            var alpha = AlphaKind.Opaque;
            for (var py = 0; py < placement.Height; py++)
            {
                var sourceRow = tile.Image.DangerousGetPixelRowMemory(tile.Rectangle.Y + py).Span;
                var targetRow = image.DangerousGetPixelRowMemory(placement.Y + py).Span;
                for (var px = 0; px < placement.Width; px++)
                {
                    var color = sourceRow[tile.Rectangle.X + px];
                    targetRow[placement.X + px] = color;
                    if (color.A is > 0 and < 250) alpha = AlphaKind.Blend;
                    else if (color.A == 0 && alpha == AlphaKind.Opaque) alpha = AlphaKind.Mask;
                }
            }

            // Repeat the edge texels into the atlas gutter. Even with nearest-neighbor
            // sampling, sampling exactly on a UV boundary can otherwise pick the
            // transparent atlas background and draw a dark seam between block faces.
            for (var px = 0; px < placement.Width; px++)
            {
                image[placement.X + px, placement.Y - 1] = image[placement.X + px, placement.Y];
                image[placement.X + px, placement.Bottom] = image[placement.X + px, placement.Bottom - 1];
            }
            for (var py = -1; py <= placement.Height; py++)
            {
                var sourceY = Math.Clamp(placement.Y + py, placement.Y, placement.Bottom - 1);
                image[placement.X - 1, placement.Y + py] = image[placement.X, sourceY];
                image[placement.Right, placement.Y + py] = image[placement.Right - 1, sourceY];
            }
            entries[tile] = new AtlasEntry(placement, alpha);
        }

        return new TextureAtlas(ToPng(image), image.Width, image.Height, entries);
    }

    private static byte[] ToPng(Image<Rgba32> image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression });
        return stream.ToArray();
    }

    private static byte[] BuildGlb(
        SortedDictionary<int, List<ExportTriangle>> layers,
        SortedDictionary<int, List<ExportTriangle>> sliceCaps,
        TextureAtlas atlas,
        LitematicSchematic schematic,
        HashSet<string> warnings,
        bool useGpuInstancing,
        CancellationToken cancellationToken)
    {
        using var binary = new MemoryStream();
        var bufferViews = new JsonArray();
        var accessors = new JsonArray();
        var meshes = new JsonArray();
        var nodes = new JsonArray();
        var rootChildren = new JsonArray();
        var instancedMeshIndices = new Dictionary<InstancedFaceTemplate, int>(InstancedFaceTemplateComparer.Instance);

        int? AddMesh(string meshName, IReadOnlyList<ExportTriangle> triangles)
        {
            if (triangles.Count == 0) return null;
            var primitives = new JsonArray();
            foreach (var alphaKind in Enum.GetValues<AlphaKind>())
            {
                var selected = triangles.Where(triangle => atlas.Entries[new TextureTile(triangle.Texture, triangle.TextureRect)].Alpha == alphaKind).ToArray();
                if (selected.Length == 0) continue;
                var positions = new List<float>(selected.Length * 9);
                var normals = new List<sbyte>(selected.Length * 9);
                var uvs = new List<float>(selected.Length * 6);
                var colors = new List<byte>(selected.Length * 12);
                for (var triangleIndex = 0; triangleIndex < selected.Length; triangleIndex++)
                {
                    if ((triangleIndex & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var triangle = selected[triangleIndex];
                    var entry = atlas.Entries[new TextureTile(triangle.Texture, triangle.TextureRect)];
                    AppendVertex(triangle.V1, triangle.Normal, triangle.T1, triangle.Shading, entry.Rectangle, atlas, positions, normals, uvs, colors);
                    AppendVertex(triangle.V2, triangle.Normal, triangle.T2, triangle.Shading, entry.Rectangle, atlas, positions, normals, uvs, colors);
                    AppendVertex(triangle.V3, triangle.Normal, triangle.T3, triangle.Shading, entry.Rectangle, atlas, positions, normals, uvs, colors);
                }

                var positionAccessor = AddFloatAccessor(binary, bufferViews, accessors, positions, "VEC3", includeBounds: true);
                var normalAccessor = AddSignedByteAccessor(binary, bufferViews, accessors, normals, "VEC3");
                var uvAccessor = AddFloatAccessor(binary, bufferViews, accessors, uvs, "VEC2");
                var colorAccessor = AddUnsignedByteAccessor(binary, bufferViews, accessors, colors, "VEC4");
                primitives.Add(new JsonObject
                {
                    ["attributes"] = new JsonObject
                    {
                        ["POSITION"] = positionAccessor,
                        ["NORMAL"] = normalAccessor,
                        ["TEXCOORD_0"] = uvAccessor,
                        ["COLOR_0"] = colorAccessor
                    },
                    ["material"] = (int)alphaKind,
                    ["mode"] = 4
                });
            }

            if (primitives.Count == 0) return null;
            var meshIndex = meshes.Count;
            meshes.Add(new JsonObject { ["name"] = meshName, ["primitives"] = primitives });
            return meshIndex;
        }

        int? AddGeometryNode(
            string meshName,
            string nodeName,
            IReadOnlyList<ExportTriangle> triangles,
            JsonObject extras)
        {
            if (AddMesh(meshName, triangles) is not { } meshIndex) return null;
            var nodeIndex = nodes.Count;
            nodes.Add(new JsonObject
            {
                ["name"] = nodeName,
                ["mesh"] = meshIndex,
                ["extras"] = extras
            });
            return nodeIndex;
        }

        int? AddInstancedGeometryNode(
            string meshName,
            string nodeName,
            IReadOnlyList<ExportTriangle> triangles,
            JsonObject extras)
        {
            if (triangles.Count == 0) return null;
            var groups = new Dictionary<InstancedFaceTemplate, List<Vector3>>(InstancedFaceTemplateComparer.Instance);
            for (var triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex += 2)
            {
                if ((triangleIndex & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                var second = triangleIndex + 1 < triangles.Count ? triangles[triangleIndex + 1] : null;
                var template = CreateInstancedFaceTemplate(triangles[triangleIndex], second, out var translation);
                if (!groups.TryGetValue(template, out var translations))
                {
                    translations = [];
                    groups[template] = translations;
                }
                translations.Add(translation);
            }

            var childNodes = new JsonArray();
            var groupIndex = 0;
            foreach (var (template, translations) in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!instancedMeshIndices.TryGetValue(template, out var meshIndex))
                {
                    ExportTriangle[] templateTriangles = template.Second is null
                        ? [template.First]
                        : [template.First, template.Second];
                    if (AddMesh($"{meshName} face {groupIndex}", templateTriangles) is not { } addedMeshIndex)
                    {
                        groupIndex++;
                        continue;
                    }
                    meshIndex = addedMeshIndex;
                    instancedMeshIndices[template] = meshIndex;
                }

                var translationValues = new List<float>(translations.Count * 3);
                foreach (var translation in translations)
                {
                    translationValues.AddRange([translation.X, translation.Y, translation.Z]);
                }
                var translationAccessor = AddFloatAccessor(
                    binary,
                    bufferViews,
                    accessors,
                    translationValues,
                    "VEC3",
                    includeBounds: true);
                var childNodeIndex = nodes.Count;
                nodes.Add(new JsonObject
                {
                    ["name"] = $"{meshName} instances {groupIndex}",
                    ["mesh"] = meshIndex,
                    ["extensions"] = new JsonObject
                    {
                        ["EXT_mesh_gpu_instancing"] = new JsonObject
                        {
                            ["attributes"] = new JsonObject { ["TRANSLATION"] = translationAccessor }
                        }
                    }
                });
                childNodes.Add(childNodeIndex);
                groupIndex++;
            }

            if (childNodes.Count == 0) return null;
            var nodeIndex = nodes.Count;
            nodes.Add(new JsonObject
            {
                ["name"] = nodeName,
                ["children"] = childNodes,
                ["extras"] = extras
            });
            return nodeIndex;
        }

        int? AddLayerNode(
            string meshName,
            string nodeName,
            IReadOnlyList<ExportTriangle> triangles,
            JsonObject extras) => useGpuInstancing
                ? AddInstancedGeometryNode(meshName, nodeName, triangles, extras)
                : AddGeometryNode(meshName, nodeName, triangles, extras);

        foreach (var y in layers.Keys.Union(sliceCaps.Keys).Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (layers.TryGetValue(y, out var layerTriangles)
                && AddLayerNode(
                    $"Layer {y}",
                    $"elite:layer:{y}",
                    layerTriangles,
                    new JsonObject { ["eliteY"] = y }) is { } layerNodeIndex)
            {
                rootChildren.Add(layerNodeIndex);
            }
            if (sliceCaps.TryGetValue(y, out var capTriangles)
                && AddLayerNode(
                    $"Layer {y} slice caps",
                    $"elite:slice-cap:{y}",
                    capTriangles,
                    new JsonObject { ["eliteSliceCapY"] = y }) is { } capNodeIndex)
            {
                rootChildren.Add(capNodeIndex);
            }
        }

        var rootNodeIndex = nodes.Count;
        nodes.Add(new JsonObject
        {
            ["name"] = "Schematic",
            ["children"] = rootChildren,
            ["extras"] = new JsonObject
            {
                ["formatVersion"] = SchematicExportFormat.CurrentVersion,
                ["minY"] = schematic.Min.Y,
                ["maxY"] = schematic.Max.Y,
                ["blockCount"] = schematic.Blocks.Count,
                ["warnings"] = new JsonArray(warnings.Order(StringComparer.Ordinal).Select(static value => JsonValue.Create(value)).ToArray())
            }
        });

        Align4(binary);
        var imageOffset = checked((int)binary.Position);
        binary.Write(atlas.Png);
        var imageLength = atlas.Png.Length;
        bufferViews.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = imageOffset, ["byteLength"] = imageLength });
        Align4(binary);

        var materials = new JsonArray(
            CreateMaterial("Opaque", "OPAQUE", doubleSided: false),
            CreateMaterial("Cutout", "MASK", doubleSided: false),
            CreateMaterial("Translucent", "BLEND", doubleSided: true));
        var extensionsUsed = new JsonArray("KHR_materials_unlit");
        if (useGpuInstancing) extensionsUsed.Add("EXT_mesh_gpu_instancing");
        var json = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "MinecraftRenderer 0.9" },
            ["extensionsUsed"] = extensionsUsed,
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["name"] = schematic.Name, ["nodes"] = new JsonArray(rootNodeIndex) }),
            ["nodes"] = nodes,
            ["meshes"] = meshes,
            ["materials"] = materials,
            ["samplers"] = new JsonArray(new JsonObject { ["magFilter"] = 9728, ["minFilter"] = 9729, ["wrapS"] = 10497, ["wrapT"] = 10497 }),
            ["images"] = new JsonArray(new JsonObject { ["bufferView"] = bufferViews.Count - 1, ["mimeType"] = "image/png" }),
            ["textures"] = new JsonArray(new JsonObject { ["sampler"] = 0, ["source"] = 0 }),
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = binary.Length }),
            ["bufferViews"] = bufferViews,
            ["accessors"] = accessors
        };
        if (useGpuInstancing)
        {
            json["extensionsRequired"] = new JsonArray("EXT_mesh_gpu_instancing");
        }

        var jsonBytes = Encoding.UTF8.GetBytes(json.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        var jsonPadding = (4 - jsonBytes.Length % 4) % 4;
        var binaryBytes = binary.ToArray();
        var totalLength = 12 + 8 + jsonBytes.Length + jsonPadding + 8 + binaryBytes.Length;
        using var glb = new MemoryStream(totalLength);
        WriteUInt32(glb, 0x46546C67);
        WriteUInt32(glb, 2);
        WriteUInt32(glb, checked((uint)totalLength));
        WriteUInt32(glb, checked((uint)(jsonBytes.Length + jsonPadding)));
        WriteUInt32(glb, 0x4E4F534A);
        glb.Write(jsonBytes);
        for (var i = 0; i < jsonPadding; i++) glb.WriteByte(0x20);
        WriteUInt32(glb, checked((uint)binaryBytes.Length));
        WriteUInt32(glb, 0x004E4942);
        glb.Write(binaryBytes);
        return glb.ToArray();
    }

    private static InstancedFaceTemplate CreateInstancedFaceTemplate(
        ExportTriangle first,
        ExportTriangle? second,
        out Vector3 translation)
    {
        translation = first.V1;
        return new InstancedFaceTemplate(
            TranslateTriangle(first, -translation),
            second is null ? null : TranslateTriangle(second, -translation));
    }

    private static ExportTriangle TranslateTriangle(ExportTriangle triangle, Vector3 translation) => triangle with
    {
        V1 = triangle.V1 + translation,
        V2 = triangle.V2 + translation,
        V3 = triangle.V3 + translation
    };

    private static JsonObject CreateMaterial(string name, string alphaMode, bool doubleSided) => new()
    {
        ["name"] = name,
        ["doubleSided"] = doubleSided,
        ["alphaMode"] = alphaMode,
        ["alphaCutoff"] = alphaMode == "MASK" ? 0.1 : null,
        ["pbrMetallicRoughness"] = new JsonObject
        {
            ["baseColorTexture"] = new JsonObject { ["index"] = 0 },
            ["metallicFactor"] = 0,
            ["roughnessFactor"] = 1
        },
        ["extensions"] = new JsonObject { ["KHR_materials_unlit"] = new JsonObject() }
    };

    private static void AppendVertex(
        Vector3 position,
        Vector3 normal,
        Vector2 uv,
        float shading,
        Rectangle tile,
        TextureAtlas atlas,
        List<float> positions,
        List<sbyte> normals,
        List<float> uvs,
        List<byte> colors)
    {
        positions.AddRange([position.X, position.Y, position.Z]);
        normals.AddRange([
            ToNormalizedSignedByte(normal.X),
            ToNormalizedSignedByte(normal.Y),
            ToNormalizedSignedByte(normal.Z)
        ]);
        uvs.Add((tile.X + 0.5f + uv.X * Math.Max(0, tile.Width - 1)) / atlas.Width);
        uvs.Add((tile.Y + 0.5f + uv.Y * Math.Max(0, tile.Height - 1)) / atlas.Height);
        var shade = ToNormalizedUnsignedByte(shading);
        colors.AddRange([shade, shade, shade, byte.MaxValue]);
    }

    private static sbyte ToNormalizedSignedByte(float value) =>
        checked((sbyte)MathF.Round(Math.Clamp(value, -1f, 1f) * sbyte.MaxValue));

    private static byte ToNormalizedUnsignedByte(float value) =>
        checked((byte)MathF.Round(Math.Clamp(value, 0f, 1f) * byte.MaxValue));

    private static int AddFloatAccessor(
        MemoryStream binary,
        JsonArray bufferViews,
        JsonArray accessors,
        IReadOnlyList<float> values,
        string type,
        bool includeBounds = false)
    {
        Align4(binary);
        var offset = checked((int)binary.Position);
        Span<byte> bytes = stackalloc byte[4];
        foreach (var value in values)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
            binary.Write(bytes);
        }
        var viewIndex = bufferViews.Count;
        bufferViews.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = values.Count * 4, ["target"] = 34962 });
        var components = type == "VEC2" ? 2 : type == "VEC4" ? 4 : 3;
        var accessor = new JsonObject { ["bufferView"] = viewIndex, ["componentType"] = 5126, ["count"] = values.Count / components, ["type"] = type };
        if (includeBounds && values.Count >= 3)
        {
            var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var max = new[] { float.MinValue, float.MinValue, float.MinValue };
            for (var i = 0; i < values.Count; i += 3)
            {
                for (var component = 0; component < 3; component++)
                {
                    min[component] = Math.Min(min[component], values[i + component]);
                    max[component] = Math.Max(max[component], values[i + component]);
                }
            }
            accessor["min"] = new JsonArray(min.Select(static value => JsonValue.Create(value)).ToArray());
            accessor["max"] = new JsonArray(max.Select(static value => JsonValue.Create(value)).ToArray());
        }
        var accessorIndex = accessors.Count;
        accessors.Add(accessor);
        return accessorIndex;
    }

    private static int AddSignedByteAccessor(
        MemoryStream binary,
        JsonArray bufferViews,
        JsonArray accessors,
        IReadOnlyList<sbyte> values,
        string type)
    {
        Align4(binary);
        var offset = checked((int)binary.Position);
        foreach (var value in values) binary.WriteByte(unchecked((byte)value));
        var viewIndex = bufferViews.Count;
        bufferViews.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = values.Count, ["target"] = 34962 });
        var accessorIndex = accessors.Count;
        accessors.Add(new JsonObject
        {
            ["bufferView"] = viewIndex,
            ["componentType"] = 5120,
            ["normalized"] = true,
            ["count"] = values.Count / GetComponentCount(type),
            ["type"] = type
        });
        return accessorIndex;
    }

    private static int AddUnsignedByteAccessor(
        MemoryStream binary,
        JsonArray bufferViews,
        JsonArray accessors,
        IReadOnlyList<byte> values,
        string type)
    {
        Align4(binary);
        var offset = checked((int)binary.Position);
        binary.Write(values.ToArray());
        var viewIndex = bufferViews.Count;
        bufferViews.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = values.Count, ["target"] = 34962 });
        var accessorIndex = accessors.Count;
        accessors.Add(new JsonObject
        {
            ["bufferView"] = viewIndex,
            ["componentType"] = 5121,
            ["normalized"] = true,
            ["count"] = values.Count / GetComponentCount(type),
            ["type"] = type
        });
        return accessorIndex;
    }

    private static int GetComponentCount(string type) => type switch
    {
        "VEC2" => 2,
        "VEC3" => 3,
        "VEC4" => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported accessor type.")
    };

    private static void Align4(MemoryStream stream)
    {
        while (stream.Position % 4 != 0) stream.WriteByte(0);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private sealed class SchematicStateResolver(
        string? assetsDirectory,
        BlockModelResolver modelResolver,
        BlockRegistry blockRegistry,
        HashSet<string> warnings)
    {
        private readonly Dictionary<string, JsonDocument?> _blockStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ResolvedSchematicState> _positionIndependentStates = new(StringComparer.Ordinal);

        public ResolvedSchematicState Resolve(SchematicBlockState state, SchematicPosition position)
        {
            if (_positionIndependentStates.TryGetValue(state.Key, out var cached)) return cached;
            if (!IsSafeSchematicResourceName(state.Name))
            {
                warnings.Add("A block with an unsupported resource name was replaced by the missing-model texture.");
                return new ResolvedSchematicState([], false);
            }
            var document = GetBlockState(state.Name);
            if (document is null)
            {
                return _positionIndependentStates[state.Key] = TryDefaultModel(state.Name);
            }

            var parts = new List<ResolvedModelPart>();
            var positionDependent = false;
            var root = document.RootElement;
            if (root.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Object)
            {
                foreach (var variant in variants.EnumerateObject())
                {
                    if (MatchesVariant(variant.Name, state.Properties))
                    {
                        positionDependent |= HasMultipleVariants(variant.Value);
                        AddModel(parts, SelectVariant(variant.Value, state.Key, position, StableStringHash(variant.Name)), state.Name);
                        break;
                    }
                }
            }

            if (root.TryGetProperty("multipart", out var multipart) && multipart.ValueKind == JsonValueKind.Array)
            {
                var partIndex = 0;
                foreach (var part in multipart.EnumerateArray())
                {
                    if ((!part.TryGetProperty("when", out var when) || MatchesWhen(when, state.Properties)) && part.TryGetProperty("apply", out var apply))
                    {
                        positionDependent |= HasMultipleVariants(apply);
                        AddModel(parts, SelectVariant(apply, state.Key, position, partIndex), state.Name);
                    }
                    partIndex++;
                }
            }

            if (parts.Count == 0) return _positionIndependentStates[state.Key] = TryDefaultModel(state.Name);
            var fullCube = parts.Count == 1 && IsFullCube(parts[0].Model);
            var resolved = new ResolvedSchematicState(parts, fullCube);
            if (!positionDependent) _positionIndependentStates[state.Key] = resolved;
            return resolved;
        }

        private static bool HasMultipleVariants(JsonElement value) =>
            value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 1;

        private JsonDocument? GetBlockState(string name)
        {
            if (_blockStates.TryGetValue(name, out var cached)) return cached;
            if (string.IsNullOrWhiteSpace(assetsDirectory)) return _blockStates[name] = null;
            var path = Path.Combine(assetsDirectory, "blockstates", name.Replace('/', Path.DirectorySeparatorChar) + ".json");
            if (!File.Exists(path)) return _blockStates[name] = null;
            try
            {
                return _blockStates[name] = JsonDocument.Parse(File.ReadAllText(path));
            }
            catch (Exception)
            {
                warnings.Add($"Invalid blockstate definition for minecraft:{name}.");
                return _blockStates[name] = null;
            }
        }

        private ResolvedSchematicState TryDefaultModel(string name)
        {
            try
            {
                var modelName = blockRegistry.TryGetModel(name, out var mapped) ? mapped : name;
                var model = modelResolver.Resolve(modelName);
                return new ResolvedSchematicState([new ResolvedModelPart(model, 0, 0, false)], IsFullCube(model));
            }
            catch (Exception)
            {
                warnings.Add($"Missing block model: minecraft:{name}.");
                return new ResolvedSchematicState([], false);
            }
        }

        private void AddModel(List<ResolvedModelPart> parts, JsonElement selected, string blockName)
        {
            if (selected.ValueKind != JsonValueKind.Object || !selected.TryGetProperty("model", out var modelTag)) return;
            var modelName = modelTag.GetString();
            if (string.IsNullOrWhiteSpace(modelName)) return;
            try
            {
                parts.Add(new ResolvedModelPart(
                    modelResolver.Resolve(modelName),
                    selected.TryGetProperty("x", out var x) ? x.GetInt32() : 0,
                    selected.TryGetProperty("y", out var y) ? y.GetInt32() : 0,
                    selected.TryGetProperty("uvlock", out var uvLock) && uvLock.ValueKind == JsonValueKind.True));
            }
            catch (Exception)
            {
                warnings.Add($"Missing model '{modelName}' used by minecraft:{blockName}.");
            }
        }

        private static JsonElement SelectVariant(
            JsonElement value,
            string stateKey,
            SchematicPosition position,
            int salt)
        {
            if (value.ValueKind != JsonValueKind.Array) return value;
            var candidates = value.EnumerateArray().ToArray();
            if (candidates.Length == 0) return value;
            var weights = candidates
                .Select(static candidate => candidate.TryGetProperty("weight", out var weight) ? Math.Max(1, weight.GetInt32()) : 1)
                .ToArray();
            return candidates[SelectWeightedVariantIndex(weights, stateKey, position, salt)];
        }

        private static int SelectWeightedVariantIndex(
            IReadOnlyList<int> weights,
            string stateKey,
            SchematicPosition position,
            int salt)
        {
            var totalWeight = weights.Sum(static weight => Math.Max(1, weight));
            if (totalWeight <= 0) return 0;
            var selectedWeight = (int)(StablePositionHash(stateKey, position, salt) % (uint)totalWeight);
            for (var index = 0; index < weights.Count; index++)
            {
                selectedWeight -= Math.Max(1, weights[index]);
                if (selectedWeight < 0) return index;
            }
            return Math.Max(0, weights.Count - 1);
        }

        private static uint StablePositionHash(string stateKey, SchematicPosition position, int salt)
        {
            var hash = 2166136261u;
            foreach (var character in stateKey)
            {
                hash = (hash ^ (byte)character) * 16777619u;
                hash = (hash ^ (byte)(character >> 8)) * 16777619u;
            }

            Mix(position.X);
            Mix(position.Y);
            Mix(position.Z);
            Mix(salt);
            return hash;

            void Mix(int value)
            {
                var bits = unchecked((uint)value);
                for (var shift = 0; shift < 32; shift += 8)
                {
                    hash = (hash ^ (byte)(bits >> shift)) * 16777619u;
                }
            }
        }

        private static int StableStringHash(string value)
        {
            var hash = 17;
            foreach (var character in value) hash = unchecked(hash * 31 + character);
            return hash;
        }

        private static bool MatchesVariant(string selector, IReadOnlyDictionary<string, string> properties)
        {
            if (string.IsNullOrEmpty(selector)) return true;
            foreach (var clause in selector.Split(','))
            {
                var pair = clause.Split('=', 2);
                if (pair.Length != 2 || !properties.TryGetValue(pair[0], out var actual) || !pair[1].Split('|').Contains(actual, StringComparer.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        private static bool MatchesWhen(JsonElement when, IReadOnlyDictionary<string, string> properties)
        {
            if (when.ValueKind != JsonValueKind.Object) return true;
            if (when.TryGetProperty("OR", out var or) && or.ValueKind == JsonValueKind.Array) return or.EnumerateArray().Any(candidate => MatchesWhen(candidate, properties));
            if (when.TryGetProperty("AND", out var and) && and.ValueKind == JsonValueKind.Array) return and.EnumerateArray().All(candidate => MatchesWhen(candidate, properties));
            foreach (var condition in when.EnumerateObject())
            {
                if (!properties.TryGetValue(condition.Name, out var actual) || condition.Value.GetString()?.Split('|').Contains(actual, StringComparer.OrdinalIgnoreCase) != true) return false;
            }
            return true;
        }

        private static bool IsFullCube(BlockModelInstance model) => model.Elements.Count == 1
            && model.Elements[0].From == Vector3.Zero
            && model.Elements[0].To == new Vector3(16)
            && model.Elements[0].Rotation is null
            && model.Elements[0].Faces.Count == 6;
    }

    private sealed class SignTextRenderer : IDisposable
    {
        private readonly Image<Rgba32> _font;
        private readonly List<Image<Rgba32>> _rendered = [];

        private SignTextRenderer(Image<Rgba32> font) => _font = font;

        public static SignTextRenderer? TryCreate(string? assetsDirectory, HashSet<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(assetsDirectory)) return null;
            var path = Path.Combine(assetsDirectory, "textures", "font", "ascii.png");
            if (!File.Exists(path))
            {
                warnings.Add("Sign text was omitted because minecraft:textures/font/ascii.png was not found.");
                return null;
            }
            try
            {
                return new SignTextRenderer(Image.Load<Rgba32>(path));
            }
            catch (Exception)
            {
                warnings.Add("Sign text was omitted because the Minecraft ASCII font could not be loaded.");
                return null;
            }
        }

        public Image<Rgba32>? Render(NbtCompound entity, string textKey, bool legacy)
        {
            var text = entity.GetCompound(textKey);
            var lines = ReadLines(text, entity, legacy);
            if (lines.All(string.IsNullOrWhiteSpace)) return null;
            var color = ResolveColor(text?.GetString("color"));
            var image = new Image<Rgba32>(128, 48);
            for (var lineIndex = 0; lineIndex < Math.Min(4, lines.Length); lineIndex++)
            {
                DrawCenteredLine(image, lines[lineIndex], lineIndex * 11 + 2, color);
            }
            _rendered.Add(image);
            return image;
        }

        private static string[] ReadLines(NbtCompound? text, NbtCompound entity, bool legacy)
        {
            if (text?.GetList("messages") is { } messages)
            {
                return messages.Take(4)
                    .Select(static tag => tag is NbtString value ? ReadComponentText(value.Value) : string.Empty)
                    .Concat(Enumerable.Repeat(string.Empty, 4))
                    .Take(4)
                    .ToArray();
            }
            if (!legacy) return [string.Empty, string.Empty, string.Empty, string.Empty];
            return Enumerable.Range(1, 4)
                .Select(index => ReadComponentText(entity.GetString($"Text{index}") ?? string.Empty))
                .ToArray();
        }

        private static string ReadComponentText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            try
            {
                using var document = JsonDocument.Parse(value);
                var builder = new StringBuilder();
                AppendComponent(document.RootElement, builder);
                return builder.ToString();
            }
            catch (JsonException)
            {
                return value;
            }
        }

        private static void AppendComponent(JsonElement element, StringBuilder builder)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    builder.Append(element.GetString());
                    break;
                case JsonValueKind.Array:
                    foreach (var child in element.EnumerateArray()) AppendComponent(child, builder);
                    break;
                case JsonValueKind.Object:
                    if (element.TryGetProperty("text", out var text)) AppendComponent(text, builder);
                    if (element.TryGetProperty("extra", out var extra)) AppendComponent(extra, builder);
                    break;
            }
        }

        private void DrawCenteredLine(Image<Rgba32> target, string value, int targetY, Rgba32 color)
        {
            var characters = StripFormatting(value).Where(static character => character <= byte.MaxValue).Take(22).ToArray();
            var widths = characters.Select(GetGlyphWidth).ToArray();
            var totalWidth = widths.Sum() + Math.Max(0, widths.Length - 1);
            var targetX = Math.Max(0, (target.Width - totalWidth) / 2);
            for (var index = 0; index < characters.Length; index++)
            {
                DrawGlyph(target, (byte)characters[index], targetX, targetY, color);
                targetX += widths[index] + 1;
            }
        }

        private int GetGlyphWidth(char character)
        {
            if (character == ' ') return 4;
            var cellWidth = Math.Max(1, _font.Width / 16);
            var cellHeight = Math.Max(1, _font.Height / 16);
            var originX = character % 16 * cellWidth;
            var originY = character / 16 * cellHeight;
            var maxX = -1;
            for (var y = 0; y < Math.Min(8, cellHeight); y++)
                for (var x = 0; x < Math.Min(8, cellWidth); x++)
                    if (_font[originX + x, originY + y].A > 16) maxX = Math.Max(maxX, x);
            return Math.Max(2, maxX + 1);
        }

        private void DrawGlyph(Image<Rgba32> target, byte character, int targetX, int targetY, Rgba32 color)
        {
            if (character == (byte)' ') return;
            var cellWidth = Math.Max(1, _font.Width / 16);
            var cellHeight = Math.Max(1, _font.Height / 16);
            var originX = character % 16 * cellWidth;
            var originY = character / 16 * cellHeight;
            for (var y = 0; y < Math.Min(8, cellHeight) && targetY + y < target.Height; y++)
            {
                for (var x = 0; x < Math.Min(8, cellWidth) && targetX + x < target.Width; x++)
                {
                    var alpha = _font[originX + x, originY + y].A;
                    if (alpha > 16) target[targetX + x, targetY + y] = new Rgba32(color.R, color.G, color.B, alpha);
                }
            }
        }

        private static IEnumerable<char> StripFormatting(string value)
        {
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] == '§' && index + 1 < value.Length)
                {
                    index++;
                    continue;
                }
                yield return value[index];
            }
        }

        private static Rgba32 ResolveColor(string? name) => name?.ToLowerInvariant() switch
        {
            "white" => new Rgba32(255, 255, 255),
            "red" => new Rgba32(180, 35, 35),
            "blue" => new Rgba32(45, 65, 180),
            "green" => new Rgba32(45, 150, 55),
            "yellow" => new Rgba32(220, 200, 45),
            "light_blue" => new Rgba32(80, 170, 220),
            _ => new Rgba32(24, 20, 16)
        };

        public void Dispose()
        {
            foreach (var image in _rendered) image.Dispose();
            _font.Dispose();
        }
    }

    private sealed record ResolvedSchematicState(IReadOnlyList<ResolvedModelPart> Parts, bool IsFullCube);
    private sealed record ResolvedModelPart(BlockModelInstance Model, int XRotation, int YRotation, bool UvLock);
    private sealed record ExportTriangle(Vector3 V1, Vector3 V2, Vector3 V3, Vector3 Normal, Vector2 T1, Vector2 T2, Vector2 T3, Image<Rgba32> Texture, Rectangle TextureRect, float Shading);
    private sealed record InstancedFaceTemplate(ExportTriangle First, ExportTriangle? Second);
    private sealed record TextureTile(Image<Rgba32> Image, Rectangle Rectangle);
    private sealed record AtlasEntry(Rectangle Rectangle, AlphaKind Alpha);
    private sealed record TextureAtlas(byte[] Png, float Width, float Height, IReadOnlyDictionary<TextureTile, AtlasEntry> Entries);
    private enum AlphaKind { Opaque, Mask, Blend }

    private static bool IsSafeSchematicResourceName(string name)
    {
        if (name.Length is 0 or > 256 || name[0] == '/' || name[^1] == '/') return false;
        foreach (var character in name)
        {
            var isAsciiLetter = character is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
            var isDigit = character is >= '0' and <= '9';
            if (!isAsciiLetter && !isDigit && character is not '_' and not '-' and not '/') return false;
        }
        return true;
    }

    private sealed class TextureTileComparer : IEqualityComparer<TextureTile>
    {
        public static TextureTileComparer Instance { get; } = new();
        public bool Equals(TextureTile? x, TextureTile? y) => x is not null && y is not null && ReferenceEquals(x.Image, y.Image) && x.Rectangle == y.Rectangle;
        public int GetHashCode(TextureTile obj) => HashCode.Combine(RuntimeHelpers.GetHashCode(obj.Image), obj.Rectangle);
    }

    private sealed class InstancedFaceTemplateComparer : IEqualityComparer<InstancedFaceTemplate>
    {
        public static InstancedFaceTemplateComparer Instance { get; } = new();

        public bool Equals(InstancedFaceTemplate? x, InstancedFaceTemplate? y) =>
            ReferenceEquals(x, y)
            || (x is not null
                && y is not null
                && TriangleEquals(x.First, y.First)
                && (x.Second is null ? y.Second is null : y.Second is not null && TriangleEquals(x.Second, y.Second)));

        public int GetHashCode(InstancedFaceTemplate obj)
        {
            var hash = new HashCode();
            AddTriangleHash(ref hash, obj.First);
            if (obj.Second is not null) AddTriangleHash(ref hash, obj.Second);
            return hash.ToHashCode();
        }

        private static bool TriangleEquals(ExportTriangle x, ExportTriangle y) =>
            x.V1 == y.V1
            && x.V2 == y.V2
            && x.V3 == y.V3
            && x.Normal == y.Normal
            && x.T1 == y.T1
            && x.T2 == y.T2
            && x.T3 == y.T3
            && ReferenceEquals(x.Texture, y.Texture)
            && x.TextureRect == y.TextureRect
            && x.Shading.Equals(y.Shading);

        private static void AddTriangleHash(ref HashCode hash, ExportTriangle triangle)
        {
            hash.Add(triangle.V1);
            hash.Add(triangle.V2);
            hash.Add(triangle.V3);
            hash.Add(triangle.Normal);
            hash.Add(triangle.T1);
            hash.Add(triangle.T2);
            hash.Add(triangle.T3);
            hash.Add(RuntimeHelpers.GetHashCode(triangle.Texture));
            hash.Add(triangle.TextureRect);
            hash.Add(triangle.Shading);
        }
    }
}
