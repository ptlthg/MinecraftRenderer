namespace MinecraftRenderer.Schematics;

public static class SchematicExportFormat
{
    public const int CurrentVersion = 8;
}

public sealed record SchematicExportOptions(
    int MaximumTextureAtlasWidth = 2048,
    bool CullOccludedFullCubeFaces = true,
    int MaximumBlockCount = 500_000,
    int MaximumTriangleCount = 750_000,
    long MaximumOutputBytes = 128 * 1024 * 1024,
    long MaximumTextureAtlasPixels = 16_777_216,
    bool UseGpuInstancing = false);

public sealed class SchematicExportLimitException(string limitName, long actual, long maximum)
    : InvalidOperationException($"Schematic export exceeded the {limitName} limit ({actual:N0} of {maximum:N0}).")
{
    public string LimitName { get; } = limitName;
    public long Actual { get; } = actual;
    public long Maximum { get; } = maximum;
}

public sealed record SchematicExportResult(
    byte[] Glb,
    int MinY,
    int MaxY,
    int LayerCount,
    int BlockCount,
    int TriangleCount,
    IReadOnlyList<string> Warnings,
    int FormatVersion = SchematicExportFormat.CurrentVersion);
