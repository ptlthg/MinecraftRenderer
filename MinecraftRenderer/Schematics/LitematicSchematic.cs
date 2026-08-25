namespace MinecraftRenderer.Schematics;

using MinecraftRenderer.Nbt;

public sealed record LitematicReadOptions(
    long MaxCompressedBytes = 10 * 1024 * 1024,
    long MaxDecompressedBytes = 50 * 1024 * 1024,
    int MaxRegions = 64,
    long MaxVolume = 5_000_000);

public sealed record SchematicPosition(int X, int Y, int Z);

public sealed class SchematicBlockState
{
    public SchematicBlockState(string name, IReadOnlyDictionary<string, string>? properties = null)
    {
        Name = NormalizeName(name);
        Properties = properties ?? new Dictionary<string, string>();
        Key = Properties.Count == 0
            ? Name
            : $"{Name}[{string.Join(',', Properties.OrderBy(static entry => entry.Key).Select(static entry => $"{entry.Key}={entry.Value}"))}]";
    }

    public string Name { get; }
    public IReadOnlyDictionary<string, string> Properties { get; }
    public string Key { get; }
    public bool IsAir => Name is "air" or "cave_air" or "void_air";

    private static string NormalizeName(string name)
    {
        var normalized = name.Trim();
        return normalized.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase)
            ? normalized[10..]
            : normalized;
    }
}

public sealed record SchematicBlock(
    SchematicPosition Position,
    SchematicBlockState State,
    NbtCompound? BlockEntity = null,
    string? Region = null);

public sealed record SchematicRegion(
    string Name,
    SchematicPosition Position,
    SchematicPosition Size,
    IReadOnlyList<SchematicBlockState> Palette,
    long Volume);

public sealed class LitematicSchematic
{
    public required string Name { get; init; }
    public string? Author { get; init; }
    public int? MinecraftDataVersion { get; init; }
    public int? FormatVersion { get; init; }
    public required IReadOnlyList<SchematicRegion> Regions { get; init; }
    public required IReadOnlyList<SchematicBlock> Blocks { get; init; }
    public required SchematicPosition Min { get; init; }
    public required SchematicPosition Max { get; init; }

    public int Width => Max.X - Min.X + 1;
    public int Height => Max.Y - Min.Y + 1;
    public int Length => Max.Z - Min.Z + 1;
}

public sealed class LitematicFormatException(string message) : IOException(message);
