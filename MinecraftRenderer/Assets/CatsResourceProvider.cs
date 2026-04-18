namespace MinecraftRenderer.Assets;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

/// <summary>
/// An <see cref="IResourceProvider"/> backed by a <see cref="CatsFile"/> (.cats binary archive).
/// Supports an optional path prefix for scoping into overlay subdirectories.
/// </summary>
public sealed class CatsResourceProvider : IResourceProvider
{
	private readonly CatsFile _catsFile;
	private readonly string _prefix; // e.g. "" for root, "fsr_item_melee/" for overlay
	private readonly string _rootPath;
	private readonly Dictionary<string, CatsFileEntry> _fileIndex;
	private readonly HashSet<string> _directoryIndex;

	/// <summary>
	/// Creates a provider backed by a .cats archive.
	/// </summary>
	/// <param name="catsFile">The parsed .cats archive.</param>
	/// <param name="displayPath">A display-friendly path for this provider.</param>
	/// <param name="prefix">
	/// Optional path prefix within the archive. All lookups will be scoped under this prefix.
	/// Use for overlay directories (e.g. <c>"fsr_item_melee"</c>).
	/// </param>
	public CatsResourceProvider(CatsFile catsFile, string displayPath, string? prefix = null) {
		ArgumentNullException.ThrowIfNull(catsFile);
		_catsFile = catsFile;
		_rootPath = displayPath;

		_prefix = string.IsNullOrWhiteSpace(prefix)
			? string.Empty
			: NormalizePath(prefix).TrimEnd('/') + "/";

		(_fileIndex, _directoryIndex) = BuildIndex(catsFile.Root, _prefix);
	}

	public string RootPath => _rootPath;

	public bool FileExists(string relativePath) {
		var normalized = NormalizePath(relativePath);
		return _fileIndex.ContainsKey(normalized);
	}

	public bool DirectoryExists(string relativePath) {
		if (string.IsNullOrWhiteSpace(relativePath)) {
			return true;
		}

		var normalized = NormalizePath(relativePath).TrimEnd('/');
		return _directoryIndex.Contains(normalized);
	}

	public Stream OpenRead(string relativePath) {
		var normalized = NormalizePath(relativePath);
		if (!_fileIndex.TryGetValue(normalized, out var entry)) {
			throw new FileNotFoundException($"File not found in .cats archive: '{relativePath}'", relativePath);
		}

		return _catsFile.OpenStream(entry);
	}

	public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive) {
		var prefix = NormalizePath(directory).TrimEnd('/');
		var pattern = GlobToRegex(searchPattern);

		foreach (var (path, _) in _fileIndex) {
			if (!IsWithinDirectory(path, prefix, recursive)) {
				continue;
			}

			var fileName = path[(path.LastIndexOf('/') + 1)..];
			if (pattern.IsMatch(fileName)) {
				yield return path;
			}
		}
	}

	public IEnumerable<string> EnumerateDirectories(string directory, string searchPattern, bool recursive) {
		var prefix = NormalizePath(directory).TrimEnd('/');
		var pattern = GlobToRegex(searchPattern);

		foreach (var dir in _directoryIndex) {
			if (!IsWithinDirectory(dir, prefix, recursive)) {
				continue;
			}

			var dirName = dir[(dir.LastIndexOf('/') + 1)..];
			if (pattern.IsMatch(dirName)) {
				yield return dir;
			}
		}
	}

	public void Dispose() {
		// No-op: CatsFile holds an in-memory byte array, no external resources to dispose.
	}

	/// <summary>
	/// Builds file and directory indices from the .cats entry tree, scoped by prefix.
	/// Paths in the index are relative to the prefix (i.e. the prefix is stripped).
	/// </summary>
	private static (Dictionary<string, CatsFileEntry> files, HashSet<string> directories) BuildIndex(
		CatsDirectoryEntry root, string prefix) {
		var files = new Dictionary<string, CatsFileEntry>(StringComparer.OrdinalIgnoreCase);
		var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		// Navigate to the prefix directory if one is set
		CatsDirectoryEntry startDir;
		string currentPath;
		if (prefix.Length > 0) {
			var prefixTrimmed = prefix.TrimEnd('/');
			var parts = prefixTrimmed.Split('/');
			var dir = root;
			foreach (var part in parts) {
				if (dir.Children.TryGetValue(part, out var child) && child is CatsDirectoryEntry childDir) {
					dir = childDir;
				}
				else {
					// Prefix doesn't exist in the archive — empty provider
					return (files, directories);
				}
			}

			startDir = dir;
			currentPath = string.Empty;
		}
		else {
			startDir = root;
			currentPath = string.Empty;
		}

		IndexDirectory(startDir, currentPath, files, directories);
		return (files, directories);
	}

	private static void IndexDirectory(CatsDirectoryEntry dir, string parentPath,
		Dictionary<string, CatsFileEntry> files, HashSet<string> directories) {
		foreach (var (name, entry) in dir.Children) {
			var path = parentPath.Length > 0 ? $"{parentPath}/{name}" : name;

			switch (entry) {
				case CatsFileEntry fileEntry:
					files[path] = fileEntry;
					// Index parent directories
					IndexParentDirectories(path, directories);
					break;
				case CatsDirectoryEntry childDir:
					directories.Add(path);
					IndexDirectory(childDir, path, files, directories);
					break;
			}
		}
	}

	private static void IndexParentDirectories(string path, HashSet<string> directories) {
		var lastSlash = path.LastIndexOf('/');
		while (lastSlash > 0) {
			var parentDir = path[..lastSlash];
			if (!directories.Add(parentDir)) {
				break;
			}

			lastSlash = parentDir.LastIndexOf('/');
		}
	}

	private static string NormalizePath(string path) {
		if (string.IsNullOrWhiteSpace(path)) {
			return string.Empty;
		}

		var normalized = path.Replace('\\', '/').TrimStart('/');
		if (normalized.Contains("..")) {
			throw new ArgumentException($"Path traversal is not allowed: '{path}'", nameof(path));
		}

		return normalized;
	}

	private static bool IsWithinDirectory(string path, string prefix, bool recursive) {
		if (prefix.Length == 0) {
			return recursive || !path.Contains('/');
		}

		if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
		    path.Length <= prefix.Length ||
		    path[prefix.Length] != '/') {
			return false;
		}

		if (!recursive) {
			var remainingPath = path[(prefix.Length + 1)..];
			return !remainingPath.Contains('/');
		}

		return true;
	}

	private static Regex GlobToRegex(string pattern) {
		var escaped = Regex.Escape(pattern)
			.Replace(@"\*", ".*")
			.Replace(@"\?", ".");
		return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	}
}
