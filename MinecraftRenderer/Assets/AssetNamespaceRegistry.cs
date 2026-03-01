namespace MinecraftRenderer.Assets;

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

/// <summary>
/// Maintains an ordered list of namespace roots that should be consulted when resolving Minecraft assets.
/// The order preserves vanilla roots first followed by progressively higher-priority overlays such as custom data
/// and texture packs. Consumers can enumerate roots in either direction depending on whether they need
/// override-first or fallback-first semantics.
/// </summary>
public sealed class AssetNamespaceRegistry
{
	private readonly List<AssetNamespaceRoot> _roots = [];

	private readonly Dictionary<string, List<AssetNamespaceRoot>> _rootsByNamespace =
		new(StringComparer.OrdinalIgnoreCase);

	private readonly HashSet<string> _deduplicationSet = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Adds a namespace root to the registry using the provided insertion order. Duplicate namespace/path pairs
	/// are ignored to keep iteration stable.
	/// </summary>
	public void AddNamespace(string namespaceName, string path, string sourceId, bool isVanilla) {
		if (string.IsNullOrWhiteSpace(namespaceName)) {
			namespaceName = "minecraft";
		}

		if (string.IsNullOrWhiteSpace(path)) {
			return;
		}

		var fullPath = Path.GetFullPath(path);
		if (!Directory.Exists(fullPath)) {
			return;
		}

		var identity = $"{namespaceName.ToLowerInvariant()}|{fullPath.ToLowerInvariant()}";
		if (!_deduplicationSet.Add(identity)) {
			return;
		}

		var root = new AssetNamespaceRoot(namespaceName, fullPath, sourceId, isVanilla);
		_roots.Add(root);

		if (!_rootsByNamespace.TryGetValue(namespaceName, out var bucket)) {
			bucket = new List<AssetNamespaceRoot>();
			_rootsByNamespace[namespaceName] = bucket;
		}

		bucket.Add(root);
	}

	/// <summary>
	/// Returns all roots tracked by the registry in insertion order (vanilla first, overlays last).
	/// </summary>
	public ReadOnlyCollection<AssetNamespaceRoot> Roots => _roots.AsReadOnly();

	/// <summary>
	/// Retrieves the ordered roots for a specific namespace. If no entries are present for the requested namespace,
	/// an empty list is returned.
	/// </summary>
	public IReadOnlyList<AssetNamespaceRoot> GetRoots(string namespaceName) {
		if (string.IsNullOrWhiteSpace(namespaceName)) {
			namespaceName = "minecraft";
		}

		if (_rootsByNamespace.TryGetValue(namespaceName, out var bucket)) {
			return bucket;
		}

		return [];
	}

	/// <summary>
	/// Resolves the ordered set of namespace roots that should be consulted for the supplied namespace. When
	/// <paramref name="fallBackToMinecraft"/> is true and the namespace is unknown, the registry returns the roots
	/// for the vanilla namespace instead.
	/// </summary>
	public IReadOnlyList<AssetNamespaceRoot> ResolveRoots(string namespaceName, bool fallBackToMinecraft = true) {
		var roots = GetRoots(namespaceName);
		if (roots.Count == 0 && fallBackToMinecraft &&
		    !string.Equals(namespaceName, "minecraft", StringComparison.OrdinalIgnoreCase)) {
			return GetRoots("minecraft");
		}

		return roots;
	}

	/// <summary>
	/// Enumerates candidate absolute paths for a relative asset path within the specified namespace.
	/// </summary>
	public IEnumerable<string> EnumerateCandidatePaths(string namespaceName, string relativePath,
		bool preferOverrides = true) {
		var roots = ResolveRoots(namespaceName);
		if (roots.Count == 0) {
			yield break;
		}

		if (preferOverrides) {
			for (var i = roots.Count - 1; i >= 0; i--) {
				yield return Path.Combine(roots[i].Path, relativePath);
			}
		}
		else {
			foreach (var t in roots) {
				yield return Path.Combine(t.Path, relativePath);
			}
		}
	}

	/// <summary>
	/// Returns a list of unique source IDs in the order they were first encountered.
	/// </summary>
	public IReadOnlyList<string> GetSources() {
		var sources = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var root in _roots) {
			if (seen.Add(root.SourceId)) {
				sources.Add(root.SourceId);
			}
		}

		return sources;
	}

	/// <summary>
	/// Retrieves the ordered roots for a specific namespace and source ID.
	/// </summary>
	public IReadOnlyList<AssetNamespaceRoot> GetRoots(string namespaceName, string sourceId) {
		if (string.IsNullOrWhiteSpace(namespaceName)) {
			namespaceName = "minecraft";
		}

		if (!_rootsByNamespace.TryGetValue(namespaceName, out var bucket)) {
			return [];
		}

		var results = new List<AssetNamespaceRoot>();
		foreach (var root in bucket) {
			if (string.Equals(root.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)) {
				results.Add(root);
			}
		}

		return results;
	}
}

/// <summary>
/// A single namespace root entry, representing a base path for a specific namespace.
/// </summary>
/// <param name="Namespace"></param>
/// <param name="Path"></param>
/// <param name="SourceId"></param>
/// <param name="IsVanilla"></param>
public sealed record AssetNamespaceRoot(string Namespace, string Path, string SourceId, bool IsVanilla);