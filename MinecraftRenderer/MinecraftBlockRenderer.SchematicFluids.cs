namespace MinecraftRenderer;

using System.Numerics;
using MinecraftRenderer.Schematics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

public sealed partial class MinecraftBlockRenderer
{
    private const float MaximumFluidHeight = 8f / 9f;
    private const float MinimumFluidHeight = 0.001f;
    private static readonly Color DefaultWaterTint = Color.FromPixel(new Rgba32(0x3F, 0x76, 0xE4, 0xEB));

    private static IReadOnlyDictionary<SchematicPosition, SchematicFluidCell> BuildSchematicFluidLookup(
        IReadOnlyList<SchematicBlock> blocks)
    {
        var fluids = new Dictionary<SchematicPosition, SchematicFluidCell>();
        foreach (var block in blocks)
        {
            if (TryGetSchematicFluid(block, out var fluid))
            {
                fluids[block.Position] = fluid;
            }
        }

        return fluids;
    }

    private static bool TryGetSchematicFluid(SchematicBlock block, out SchematicFluidCell fluid)
    {
        var state = block.State;
        var name = state.Name.ToLowerInvariant();
        var standalone = true;
        SchematicFluidKind kind;

        if (name is "water" or "flowing_water")
        {
            kind = SchematicFluidKind.Water;
        }
        else if (name is "lava" or "flowing_lava")
        {
            kind = SchematicFluidKind.Lava;
        }
        else if (name == "bubble_column")
        {
            kind = SchematicFluidKind.Water;
        }
        else if (name is "kelp" or "kelp_plant" or "seagrass" or "tall_seagrass")
        {
            kind = SchematicFluidKind.Water;
            standalone = false;
        }
        else if (HasTrueProperty(state, "waterlogged"))
        {
            kind = SchematicFluidKind.Water;
            standalone = false;
        }
        else if (HasTrueProperty(state, "lavalogged") || HasTrueProperty(state, "lava_logged"))
        {
            kind = SchematicFluidKind.Lava;
            standalone = false;
        }
        else if (TryGetContainedFluid(state, out kind))
        {
            standalone = false;
        }
        else
        {
            fluid = default!;
            return false;
        }

        var usesFluidBlockLevel = name is "water" or "flowing_water" or "lava" or "flowing_lava";
        var levelPropertyName = usesFluidBlockLevel ? "level" : "fluid_level";
        var defaultLevel = name.StartsWith("flowing_", StringComparison.Ordinal) ? 1 : 0;
        var rawLevel = state.Properties.TryGetValue(levelPropertyName, out var levelValue)
            && int.TryParse(levelValue, out var parsedLevel)
                ? Math.Clamp(parsedLevel, 0, 15)
                : defaultLevel;
        var falling = rawLevel >= 8
            || (usesFluidBlockLevel && HasTrueProperty(state, "falling"))
            || HasTrueProperty(state, "fluid_falling");
        fluid = new SchematicFluidCell(block.Position, kind, rawLevel, falling, standalone);
        return true;
    }

    private static bool HasTrueProperty(SchematicBlockState state, string name) =>
        state.Properties.TryGetValue(name, out var value)
        && bool.TryParse(value, out var parsed)
        && parsed;

    private static bool TryGetContainedFluid(SchematicBlockState state, out SchematicFluidKind kind)
    {
        foreach (var propertyName in new[] { "fluid", "contained_fluid", "logged_fluid" })
        {
            if (!state.Properties.TryGetValue(propertyName, out var value)) continue;
            var normalized = value.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase) ? value[10..] : value;
            if (normalized.Equals("water", StringComparison.OrdinalIgnoreCase))
            {
                kind = SchematicFluidKind.Water;
                return true;
            }
            if (normalized.Equals("lava", StringComparison.OrdinalIgnoreCase))
            {
                kind = SchematicFluidKind.Lava;
                return true;
            }
        }

        kind = default;
        return false;
    }

    private void AppendSchematicFluid(
        SchematicFluidCell fluid,
        IReadOnlyDictionary<SchematicPosition, SchematicFluidCell> fluids,
        IReadOnlySet<SchematicPosition> occupiedOpaqueFullCubes,
        float centerX,
        float centerZ,
        int minimumY,
        List<ExportTriangle> target,
        List<ExportTriangle> sliceCaps,
        Dictionary<SchematicFluidKind, SchematicFluidTextures> textureCache,
        HashSet<string> warnings)
    {
        if (!textureCache.TryGetValue(fluid.Kind, out var textures))
        {
            textures = GetSchematicFluidTextures(fluid.Kind, warnings);
            textureCache[fluid.Kind] = textures;
        }
        var northWestHeight = CalculateFluidCornerHeight(fluid, -1, -1, fluids, occupiedOpaqueFullCubes);
        var northEastHeight = CalculateFluidCornerHeight(fluid, 1, -1, fluids, occupiedOpaqueFullCubes);
        var southEastHeight = CalculateFluidCornerHeight(fluid, 1, 1, fluids, occupiedOpaqueFullCubes);
        var southWestHeight = CalculateFluidCornerHeight(fluid, -1, 1, fluids, occupiedOpaqueFullCubes);

        var center = new Vector3(
            fluid.Position.X - centerX,
            fluid.Position.Y - minimumY,
            fluid.Position.Z - centerZ);
        var bottomY = center.Y - 0.5f;
        var northWest = new Vector3(center.X - 0.5f, bottomY + northWestHeight, center.Z - 0.5f);
        var northEast = new Vector3(center.X + 0.5f, bottomY + northEastHeight, center.Z - 0.5f);
        var southEast = new Vector3(center.X + 0.5f, bottomY + southEastHeight, center.Z + 0.5f);
        var southWest = new Vector3(center.X - 0.5f, bottomY + southWestHeight, center.Z + 0.5f);

        var above = Offset(fluid.Position, 0, 1, 0);
        var topIsOccluded = HasSameFluid(above, fluid.Kind, fluids) || occupiedOpaqueFullCubes.Contains(above);
        var flowingTop = fluid.Falling || !NearlyEqual(northWestHeight, northEastHeight)
            || !NearlyEqual(northWestHeight, southEastHeight) || !NearlyEqual(northWestHeight, southWestHeight);
        var topTexture = flowingTop ? textures.Flow : textures.Still;
        var topUvs = flowingTop
            ? CreateFlowingTopUvs(northWestHeight, northEastHeight, southEastHeight, southWestHeight)
            : UnitQuadUvs;
        AppendFluidQuad(topIsOccluded ? sliceCaps : target, northWest, southWest, southEast, northEast, topUvs, topTexture, 1f);

        var below = Offset(fluid.Position, 0, -1, 0);
        var bottomIsOccluded = HasSameFluid(below, fluid.Kind, fluids) || occupiedOpaqueFullCubes.Contains(below);
        AppendFluidQuad(
            bottomIsOccluded ? sliceCaps : target,
            new Vector3(center.X - 0.5f, bottomY, center.Z - 0.5f),
            new Vector3(center.X + 0.5f, bottomY, center.Z - 0.5f),
            new Vector3(center.X + 0.5f, bottomY, center.Z + 0.5f),
            new Vector3(center.X - 0.5f, bottomY, center.Z + 0.5f),
            UnitQuadUvs,
            textures.Still,
            0.5f);

        AppendFluidSideIfVisible(
            fluid, fluids, occupiedOpaqueFullCubes, Offset(fluid.Position, 0, 0, -1), target,
            new Vector3(center.X - 0.5f, bottomY, center.Z - 0.5f), northWest,
            northEast, new Vector3(center.X + 0.5f, bottomY, center.Z - 0.5f),
            northWestHeight, northEastHeight, textures.Flow, 0.8f);
        AppendFluidSideIfVisible(
            fluid, fluids, occupiedOpaqueFullCubes, Offset(fluid.Position, 0, 0, 1), target,
            new Vector3(center.X + 0.5f, bottomY, center.Z + 0.5f), southEast,
            southWest, new Vector3(center.X - 0.5f, bottomY, center.Z + 0.5f),
            southEastHeight, southWestHeight, textures.Flow, 0.8f);
        AppendFluidSideIfVisible(
            fluid, fluids, occupiedOpaqueFullCubes, Offset(fluid.Position, 1, 0, 0), target,
            new Vector3(center.X + 0.5f, bottomY, center.Z - 0.5f), northEast,
            southEast, new Vector3(center.X + 0.5f, bottomY, center.Z + 0.5f),
            northEastHeight, southEastHeight, textures.Flow, 0.7f);
        AppendFluidSideIfVisible(
            fluid, fluids, occupiedOpaqueFullCubes, Offset(fluid.Position, -1, 0, 0), target,
            new Vector3(center.X - 0.5f, bottomY, center.Z + 0.5f), southWest,
            northWest, new Vector3(center.X - 0.5f, bottomY, center.Z - 0.5f),
            southWestHeight, northWestHeight, textures.Flow, 0.7f);
    }

    private static void AppendFluidSideIfVisible(
        SchematicFluidCell fluid,
        IReadOnlyDictionary<SchematicPosition, SchematicFluidCell> fluids,
        IReadOnlySet<SchematicPosition> occupiedOpaqueFullCubes,
        SchematicPosition neighbor,
        List<ExportTriangle> target,
        Vector3 bottomLeft,
        Vector3 topLeft,
        Vector3 topRight,
        Vector3 bottomRight,
        float leftHeight,
        float rightHeight,
        Image<Rgba32> texture,
        float shading)
    {
        if (HasSameFluid(neighbor, fluid.Kind, fluids) || occupiedOpaqueFullCubes.Contains(neighbor)) return;
        AppendFluidQuad(target, bottomLeft, topLeft, topRight, bottomRight, [
            new Vector2(0, 1),
            new Vector2(0, 1 - leftHeight),
            new Vector2(1, 1 - rightHeight),
            new Vector2(1, 1)
        ], texture, shading);
    }

    private static float CalculateFluidCornerHeight(
        SchematicFluidCell origin,
        int xDirection,
        int zDirection,
        IReadOnlyDictionary<SchematicPosition, SchematicFluidCell> fluids,
        IReadOnlySet<SchematicPosition> occupiedOpaqueFullCubes)
    {
        Span<SchematicPosition> samples = [
            origin.Position,
            Offset(origin.Position, xDirection, 0, 0),
            Offset(origin.Position, 0, 0, zDirection),
            Offset(origin.Position, xDirection, 0, zDirection)
        ];

        var weightedHeight = 0f;
        var totalWeight = 0f;
        foreach (var sample in samples)
        {
            if (HasSameFluid(Offset(sample, 0, 1, 0), origin.Kind, fluids)) return 1f;
            if (fluids.TryGetValue(sample, out var sampledFluid) && sampledFluid.Kind == origin.Kind)
            {
                var height = GetFluidHeight(sampledFluid, fluids);
                // Minecraft biases nearly-full samples so source blocks do not sharply collapse
                // toward a neighboring low level or an exposed corner.
                var weight = height >= 0.8f ? 10f : 1f;
                weightedHeight += height * weight;
                totalWeight += weight;
            }
            else if (!occupiedOpaqueFullCubes.Contains(sample))
            {
                totalWeight += 1f;
            }
        }

        return totalWeight > 0 ? Math.Clamp(weightedHeight / totalWeight, MinimumFluidHeight, 1f) : MaximumFluidHeight;
    }

    private static float GetFluidHeight(
        SchematicFluidCell fluid,
        IReadOnlyDictionary<SchematicPosition, SchematicFluidCell> fluids)
    {
        if (HasSameFluid(Offset(fluid.Position, 0, 1, 0), fluid.Kind, fluids)) return 1f;
        if (fluid.Falling) return MaximumFluidHeight;
        // Fluid block levels 0..7 represent amounts 8..1; 8..15 are falling states.
        return Math.Clamp((8 - Math.Min(fluid.Level, 7)) / 9f, 1f / 9f, MaximumFluidHeight);
    }

    private SchematicFluidTextures GetSchematicFluidTextures(SchematicFluidKind kind, HashSet<string> warnings)
    {
        var prefix = kind == SchematicFluidKind.Water ? "water" : "lava";
        var stillId = $"minecraft:block/{prefix}_still";
        var flowId = $"minecraft:block/{prefix}_flow";
        var hasStill = _textureRepository.TryGetTexture(stillId, out var still);
        var hasFlow = _textureRepository.TryGetTexture(flowId, out var flow);
        if (!hasStill) warnings.Add($"Missing schematic fluid texture: {stillId}");
        if (!hasFlow) warnings.Add($"Missing schematic fluid texture: {flowId}");
        if (kind == SchematicFluidKind.Water)
        {
            still = _textureRepository.GetTintedTexture(stillId, DefaultWaterTint);
            flow = _textureRepository.GetTintedTexture(flowId, DefaultWaterTint);
        }
        warnings.Add("Animated fluid textures use their first frame in schematic GLB exports.");
        return new SchematicFluidTextures(still, flow);
    }

    private static readonly Vector2[] UnitQuadUvs = [
        new(0, 0),
        new(0, 1),
        new(1, 1),
        new(1, 0)
    ];

    private static Vector2[] CreateFlowingTopUvs(float northWest, float northEast, float southEast, float southWest)
    {
        var flowX = (northWest + southWest - northEast - southEast) * 0.5f;
        var flowZ = (northWest + northEast - southWest - southEast) * 0.5f;
        if (MathF.Abs(flowX) < 0.0001f && MathF.Abs(flowZ) < 0.0001f) return UnitQuadUvs;

        var angle = MathF.Atan2(flowZ, flowX);
        var cosine = MathF.Cos(angle);
        var sine = MathF.Sin(angle);
        return UnitQuadUvs.Select(uv =>
        {
            // Fluid flow UVs use the center half of the sprite. That leaves enough room to rotate
            // without crossing into a neighboring tile in the exported texture atlas.
            var centered = (uv - new Vector2(0.5f)) * 0.5f;
            return new Vector2(
                centered.X * cosine - centered.Y * sine + 0.5f,
                centered.X * sine + centered.Y * cosine + 0.5f);
        }).ToArray();
    }

    private static void AppendFluidQuad(
        List<ExportTriangle> target,
        Vector3 first,
        Vector3 second,
        Vector3 third,
        Vector3 fourth,
        IReadOnlyList<Vector2> uvs,
        Image<Rgba32> texture,
        float shading)
    {
        var textureRectangle = new Rectangle(0, 0, texture.Width, texture.Height);
        AppendFluidTriangle(target, first, second, third, uvs[0], uvs[1], uvs[2], texture, textureRectangle, shading);
        AppendFluidTriangle(target, first, third, fourth, uvs[0], uvs[2], uvs[3], texture, textureRectangle, shading);
    }

    private static void AppendFluidTriangle(
        List<ExportTriangle> target,
        Vector3 first,
        Vector3 second,
        Vector3 third,
        Vector2 firstUv,
        Vector2 secondUv,
        Vector2 thirdUv,
        Image<Rgba32> texture,
        Rectangle textureRectangle,
        float shading)
    {
        var cross = Vector3.Cross(second - first, third - first);
        var normal = cross.LengthSquared() > 0.000001f ? Vector3.Normalize(cross) : Vector3.UnitY;
        target.Add(new ExportTriangle(
            first, second, third, normal,
            firstUv, secondUv, thirdUv,
            texture, textureRectangle, shading));
    }

    private static bool HasSameFluid(
        SchematicPosition position,
        SchematicFluidKind kind,
        IReadOnlyDictionary<SchematicPosition, SchematicFluidCell> fluids) =>
        fluids.TryGetValue(position, out var fluid) && fluid.Kind == kind;

    private static SchematicPosition Offset(SchematicPosition position, int x, int y, int z) =>
        new(position.X + x, position.Y + y, position.Z + z);

    private static bool NearlyEqual(float left, float right) => MathF.Abs(left - right) < 0.0001f;

    private enum SchematicFluidKind { Water, Lava }

    private sealed record SchematicFluidCell(
        SchematicPosition Position,
        SchematicFluidKind Kind,
        int Level,
        bool Falling,
        bool Standalone);

    private sealed record SchematicFluidTextures(Image<Rgba32> Still, Image<Rgba32> Flow);
}
