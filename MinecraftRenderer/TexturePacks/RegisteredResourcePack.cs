namespace MinecraftRenderer.TexturePacks;

using System;
using System.Collections.Generic;
using MinecraftRenderer.Assets;

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
	/// <summary>
	/// When set, this pack's assets are served from a provider (e.g. a ZIP archive)
	/// rather than directly from the filesystem paths in <see cref="NamespaceRoots"/>.
	/// </summary>
	public IResourceProvider? Provider { get; init; }

	/// <summary>
	/// Per-namespace <see cref="IResourceProvider"/> instances for zip-backed packs.
	/// Keys are namespace names (e.g. "minecraft"), values are providers scoped to
	/// the corresponding <c>assets/&lt;namespace&gt;</c> subtree within the archive.
	/// </summary>
	public IReadOnlyDictionary<string, IResourceProvider>? NamespaceProviders { get; init; }

	public bool TryGetNamespacePath(string @namespace, out string path) {
		if (NamespaceRoots.TryGetValue(@namespace, out var resolved)) {
			path = resolved;
			return true;
		}

		path = string.Empty;
		return false;
	}

	public IEnumerable<string> EnumerateOverlayRootPaths() {
		var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var namespaceName in new[] { "minecraft", "firmskyblock", "cittofirmgenerated", "cit" }) {
			if (NamespaceRoots.TryGetValue(namespaceName, out var namespacePath)
			    && emitted.Add(namespacePath)) {
				yield return namespacePath;
			}
		}

		foreach (var namespacePath in NamespaceRoots.Values) {
			if (emitted.Add(namespacePath)) {
				yield return namespacePath;
			}
		}
	}
}