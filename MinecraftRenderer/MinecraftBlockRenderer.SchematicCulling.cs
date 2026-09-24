namespace MinecraftRenderer;

using System.Numerics;
using MinecraftRenderer.Geometry;
using MinecraftRenderer.Schematics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

public sealed partial class MinecraftBlockRenderer
{
    private const float SchematicFaceEpsilon = 0.0001f;

    private SchematicOcclusionProfile BuildSchematicOcclusionProfile(
        ResolvedSchematicState resolved,
        IDictionary<TextureTile, bool> opaqueTextures)
    {
        var faces = Enumerable.Range(0, 6).Select(static _ => new List<SchematicFaceRectangle>()).ToArray();
        foreach (var part in resolved.Parts)
        {
            if (part.XRotation % 90 != 0 || part.YRotation % 90 != 0) continue;
            var rotation = Matrix4x4.CreateRotationX(part.XRotation * DegreesToRadians)
                * Matrix4x4.CreateRotationY(-part.YRotation * DegreesToRadians);
            foreach (var element in part.Model.Elements)
            {
                if (element.Rotation is not null) continue;
                var vertices = BuildElementVertices(element);
                var minimum = new Vector3(float.MaxValue);
                var maximum = new Vector3(float.MinValue);
                foreach (var vertex in vertices)
                {
                    var transformed = Vector3.Transform(vertex, rotation);
                    minimum = Vector3.Min(minimum, transformed);
                    maximum = Vector3.Max(maximum, transformed);
                }

                foreach (var (direction, face) in element.Faces)
                {
                    if (!TryGetSchematicCullDirection(face, out var cullDirection)) continue;
                    var worldDirection = TransformFaceDirection(direction, rotation);
                    if (TransformFaceDirection(cullDirection, rotation) != worldDirection
                        || !TryGetSchematicBoundaryRectangle(minimum, maximum, worldDirection, flat: false, out var rectangle)
                        || !IsSchematicFaceTextureOpaque(part.Model, element, direction, face, opaqueTextures)) continue;
                    faces[(int)worldDirection].Add(rectangle);
                }
            }
        }
        return new SchematicOcclusionProfile(resolved.IsFullCube, faces);
    }

    private bool IsSchematicFaceTextureOpaque(
        BlockModelInstance model,
        ModelElement element,
        BlockFaceDirection direction,
        ModelFace face,
        IDictionary<TextureTile, bool> cache)
    {
        var texture = _textureRepository.GetTexture(ResolveTexture(face.Texture, model));
        var uv = ModelFaceHelper.CreateUvMap(GetFaceUv(face, direction, element), face.Rotation ?? 0);
        var rectangle = ComputeTextureRectangle(uv, texture);
        var tile = new TextureTile(texture, rectangle);
        if (cache.TryGetValue(tile, out var opaque)) return opaque;
        for (var y = rectangle.Top; y < rectangle.Bottom; y++)
        {
            foreach (var pixel in texture.DangerousGetPixelRowMemory(y).Span.Slice(rectangle.Left, rectangle.Width))
            {
                if (pixel.A == byte.MaxValue) continue;
                cache[tile] = false;
                return false;
            }
        }
        cache[tile] = true;
        return true;
    }

    private static bool IsSchematicFaceOccluded(
        SchematicPosition position,
        string stateKey,
        ResolvedModelPart part,
        BlockFaceDirection modelDirection,
        BlockFaceDirection worldDirection,
        VisibleTriangle first,
        VisibleTriangle second,
        Vector3 blockTranslation,
        Matrix4x4 rotation,
        IReadOnlyDictionary<SchematicPosition, SchematicOcclusionBlock> blocks)
    {
        var element = part.Model.Elements[first.ElementIndex];
        if (element.Rotation is not null
            || part.XRotation % 90 != 0 || part.YRotation % 90 != 0
            || !element.Faces.TryGetValue(modelDirection, out var face)
            || !TryGetSchematicCullDirection(face, out var cullDirection)
            || TransformFaceDirection(cullDirection, rotation) != worldDirection)
        {
            return false;
        }

        var minimum = Vector3.Min(Vector3.Min(first.V1, first.V2), Vector3.Min(first.V3, second.V3)) - blockTranslation;
        var maximum = Vector3.Max(Vector3.Max(first.V1, first.V2), Vector3.Max(first.V3, second.V3)) - blockTranslation;
        if (!TryGetSchematicBoundaryRectangle(minimum, maximum, worldDirection, flat: true, out var source)) return false;

        var neighborPosition = worldDirection switch
        {
            BlockFaceDirection.North => position with { Z = position.Z - 1 },
            BlockFaceDirection.South => position with { Z = position.Z + 1 },
            BlockFaceDirection.East => position with { X = position.X + 1 },
            BlockFaceDirection.West => position with { X = position.X - 1 },
            BlockFaceDirection.Up => position with { Y = position.Y + 1 },
            BlockFaceDirection.Down => position with { Y = position.Y - 1 },
            _ => position
        };
        if (!blocks.TryGetValue(neighborPosition, out var neighbor)) return false;

        if (blocks[position].Profile.IsFullCube && neighbor.Profile.IsFullCube
            && string.Equals(stateKey, neighbor.StateKey, StringComparison.Ordinal)) return true;

        var opposite = worldDirection switch
        {
            BlockFaceDirection.North => BlockFaceDirection.South,
            BlockFaceDirection.South => BlockFaceDirection.North,
            BlockFaceDirection.East => BlockFaceDirection.West,
            BlockFaceDirection.West => BlockFaceDirection.East,
            BlockFaceDirection.Up => BlockFaceDirection.Down,
            _ => BlockFaceDirection.Up
        };
        return IsSchematicFaceCovered(source, neighbor.Profile.Faces[(int)opposite]);
    }

    private static bool TryGetSchematicCullDirection(ModelFace face, out BlockFaceDirection direction) =>
        Enum.TryParse(face.CullFace, ignoreCase: true, out direction)
        && Enum.IsDefined(direction);

    private static bool TryGetSchematicBoundaryRectangle(
        Vector3 minimum,
        Vector3 maximum,
        BlockFaceDirection direction,
        bool flat,
        out SchematicFaceRectangle rectangle)
    {
        float near;
        float far;
        float boundary;
        float minU;
        float maxU;
        float minV;
        float maxV;
        switch (direction)
        {
            case BlockFaceDirection.North:
            case BlockFaceDirection.South:
                near = minimum.Z;
                far = maximum.Z;
                boundary = direction == BlockFaceDirection.North ? -0.5f : 0.5f;
                minU = minimum.X;
                maxU = maximum.X;
                minV = minimum.Y;
                maxV = maximum.Y;
                break;
            case BlockFaceDirection.East:
            case BlockFaceDirection.West:
                near = minimum.X;
                far = maximum.X;
                boundary = direction == BlockFaceDirection.West ? -0.5f : 0.5f;
                minU = minimum.Z;
                maxU = maximum.Z;
                minV = minimum.Y;
                maxV = maximum.Y;
                break;
            default:
                near = minimum.Y;
                far = maximum.Y;
                boundary = direction == BlockFaceDirection.Down ? -0.5f : 0.5f;
                minU = minimum.X;
                maxU = maximum.X;
                minV = minimum.Z;
                maxV = maximum.Z;
                break;
        }

        var facePlane = boundary < 0 ? near : far;
        if (MathF.Abs(facePlane - boundary) > SchematicFaceEpsilon
            || (flat && far - near > SchematicFaceEpsilon)
            || maxU - minU <= SchematicFaceEpsilon
            || maxV - minV <= SchematicFaceEpsilon)
        {
            rectangle = default;
            return false;
        }
        rectangle = new SchematicFaceRectangle(minU, maxU, minV, maxV);
        return true;
    }

    private static bool IsSchematicFaceCovered(
        SchematicFaceRectangle source,
        IReadOnlyList<SchematicFaceRectangle> coverage)
    {
        foreach (var face in coverage)
        {
            if (face.MinU <= source.MinU + SchematicFaceEpsilon
                && face.MaxU >= source.MaxU - SchematicFaceEpsilon
                && face.MinV <= source.MinV + SchematicFaceEpsilon
                && face.MaxV >= source.MaxV - SchematicFaceEpsilon) return true;
        }
        if (coverage.Count is < 2 or > 32) return false;

        var uEdges = new List<float> { source.MinU, source.MaxU };
        var vEdges = new List<float> { source.MinV, source.MaxV };
        foreach (var face in coverage)
        {
            if (face.MaxU <= source.MinU || face.MinU >= source.MaxU
                || face.MaxV <= source.MinV || face.MinV >= source.MaxV) continue;
            uEdges.Add(Math.Clamp(face.MinU, source.MinU, source.MaxU));
            uEdges.Add(Math.Clamp(face.MaxU, source.MinU, source.MaxU));
            vEdges.Add(Math.Clamp(face.MinV, source.MinV, source.MaxV));
            vEdges.Add(Math.Clamp(face.MaxV, source.MinV, source.MaxV));
        }
        uEdges.Sort();
        vEdges.Sort();
        for (var u = 1; u < uEdges.Count; u++)
        {
            if (uEdges[u] - uEdges[u - 1] <= SchematicFaceEpsilon) continue;
            var sampleU = (uEdges[u - 1] + uEdges[u]) * 0.5f;
            for (var v = 1; v < vEdges.Count; v++)
            {
                if (vEdges[v] - vEdges[v - 1] <= SchematicFaceEpsilon) continue;
                var sampleV = (vEdges[v - 1] + vEdges[v]) * 0.5f;
                if (!coverage.Any(face => face.MinU <= sampleU && face.MaxU >= sampleU
                    && face.MinV <= sampleV && face.MaxV >= sampleV)) return false;
            }
        }
        return true;
    }

    private static ExportTriangle ToExportTriangle(VisibleTriangle triangle) => new(
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

    private sealed record SchematicOcclusionBlock(string StateKey, SchematicOcclusionProfile Profile);
    private sealed record SchematicOcclusionProfile(bool IsFullCube, IReadOnlyList<SchematicFaceRectangle>[] Faces);
    private readonly record struct SchematicFaceRectangle(float MinU, float MaxU, float MinV, float MaxV);
}
