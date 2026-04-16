namespace MinecraftRenderer.Assets;

using System.IO;

/// <summary>
/// Convenience extensions for <see cref="IResourceProvider"/>.
/// </summary>
public static class ResourceProviderExtensions
{
	/// <summary>
	/// Reads all text from a file within the provider.
	/// </summary>
	public static string ReadAllText(this IResourceProvider provider, string relativePath) {
		using var stream = provider.OpenRead(relativePath);
		using var reader = new StreamReader(stream);
		return reader.ReadToEnd();
	}

	/// <summary>
	/// Returns a relative path within the provider by stripping a directory prefix.
	/// Both <paramref name="fullRelativePath"/> and <paramref name="directoryPrefix"/> are expected
	/// to use forward-slash separators relative to the provider root.
	/// </summary>
	public static string GetRelativePath(string fullRelativePath, string directoryPrefix) {
		var prefix = directoryPrefix.TrimEnd('/');
		if (prefix.Length == 0) {
			return fullRelativePath;
		}

		if (fullRelativePath.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase) &&
		    fullRelativePath.Length > prefix.Length &&
		    fullRelativePath[prefix.Length] == '/') {
			return fullRelativePath[(prefix.Length + 1)..];
		}

		return fullRelativePath;
	}
}
