namespace MinecraftRenderer.TexturePacks;

using System;
using System.Collections.Generic;

public sealed record RegisteredResourcePack(
    string Id,
    string DisplayName,
    string RootPath,
    string AssetsPath,
    IReadOnlyDictionary<string, string> NamespaceRoots,
    ResourcePackMeta Meta,
    DateTime LastWriteTimeUtc,
    long SizeBytes,
    bool SupportsCit,
    string Fingerprint)
{
    public bool TryGetNamespacePath(string @namespace, out string path)
    {
        if (NamespaceRoots.TryGetValue(@namespace, out var resolved))
        {
            path = resolved;
            return true;
        }

        path = string.Empty;
        return false;
    }

    public IEnumerable<string> EnumerateOverlayRootPaths()
    {
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var namespaceName in new[] { "minecraft", "firmskyblock", "cittofirmgenerated", "cit" })
        {
            if (NamespaceRoots.TryGetValue(namespaceName, out var namespacePath)
                && emitted.Add(namespacePath))
            {
                yield return namespacePath;
            }
        }

        foreach (var namespacePath in NamespaceRoots.Values)
        {
            if (emitted.Add(namespacePath))
            {
                yield return namespacePath;
            }
        }
    }
}
