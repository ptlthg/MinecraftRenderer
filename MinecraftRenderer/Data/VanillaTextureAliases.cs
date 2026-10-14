namespace MinecraftRenderer;

/// <summary>Vanilla texture moves used by the bundled block-entity models.</summary>
internal static class VanillaTextureAliases
{
    private static readonly Dictionary<string, string> Paths = BuildPaths();

    private static Dictionary<string, string> BuildPaths()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (oldPath, newPath) in new[] {
            ("entity/shield_base_nopattern", "entity/shield/shield_base_nopattern"),
            ("entity/banner_base", "entity/banner/banner_base"),
            ("entity/copper_golem/exposed_copper_golem", "entity/copper_golem/copper_golem_exposed"),
            ("entity/copper_golem/weathered_copper_golem", "entity/copper_golem/copper_golem_weathered"),
            ("entity/copper_golem/oxidized_copper_golem", "entity/copper_golem/copper_golem_oxidized")
        }) {
            result[oldPath] = newPath;
            result[newPath] = oldPath;
        }
        return result;
    }

    public static IEnumerable<string> Alternatives(string namespaceName, string path)
    {
        if (namespaceName.Equals("minecraft", StringComparison.OrdinalIgnoreCase)
            && Paths.TryGetValue(path, out var alternate)) yield return alternate;
    }
}
