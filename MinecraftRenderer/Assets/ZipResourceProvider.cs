namespace MinecraftRenderer.Assets;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// An <see cref="IResourceProvider"/> that reads assets from a ZIP archive (e.g. a Minecraft JAR or resource pack ZIP).
/// Entry paths within the archive use forward-slash separators.
/// </summary>
public sealed class ZipResourceProvider : IResourceProvider
{
	private readonly string _rootPath;
	private readonly Dictionary<string, byte[]> _entryData;
	private readonly HashSet<string> _directoryIndex;

	/// <summary>
	/// Opens a ZIP archive from a file path, eagerly decompresses all entries into memory,
	/// then closes the archive. The resulting provider is fully thread-safe.
	/// </summary>
	public ZipResourceProvider(string zipFilePath) {
		ArgumentException.ThrowIfNullOrWhiteSpace(zipFilePath);
		_rootPath = Path.GetFullPath(zipFilePath);

		using var stream = File.OpenRead(_rootPath);
		using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
		(_entryData, _directoryIndex) = BuildIndex(archive);
	}

	/// <summary>
	/// Reads all entries from an existing <see cref="ZipArchive"/> into memory.
	/// The archive is NOT disposed by this provider regardless of <paramref name="ownsArchive"/>;
	/// all data is copied eagerly so the archive can be closed immediately after construction.
	/// </summary>
	public ZipResourceProvider(ZipArchive archive, string displayPath, bool ownsArchive = false) {
		ArgumentNullException.ThrowIfNull(archive);
		_rootPath = displayPath;
		(_entryData, _directoryIndex) = BuildIndex(archive);

		if (ownsArchive) {
			archive.Dispose();
		}
	}

	public string RootPath => _rootPath;

	public bool FileExists(string relativePath) {
		var normalized = NormalizePath(relativePath);
		return _entryData.ContainsKey(normalized);
	}

	public bool DirectoryExists(string relativePath) {
		if (string.IsNullOrWhiteSpace(relativePath)) {
			return true; // root always exists
		}

		var normalized = NormalizePath(relativePath).TrimEnd('/');
		return _directoryIndex.Contains(normalized);
	}

	public Stream OpenRead(string relativePath) {
		var normalized = NormalizePath(relativePath);
		if (!_entryData.TryGetValue(normalized, out var data)) {
			throw new FileNotFoundException($"File not found in ZIP archive: '{relativePath}'", relativePath);
		}

		return new MemoryStream(data, writable: false);
	}

	public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive) {
		var prefix = NormalizePath(directory).TrimEnd('/');
		var pattern = GlobToRegex(searchPattern);

		foreach (var (path, _) in _entryData) {
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
		// No-op: all data is held in managed byte arrays, no external resources.
	}

	private static (Dictionary<string, byte[]> entries, HashSet<string> directories) BuildIndex(
		ZipArchive archive) {
		var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
		var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var entry in archive.Entries) {
			var path = entry.FullName.Replace('\\', '/').TrimStart('/');
			if (string.IsNullOrWhiteSpace(path)) {
				continue;
			}

			// Directory entries end with '/' and have zero length
			if (path.EndsWith('/')) {
				var dirPath = path.TrimEnd('/');
				directories.Add(dirPath);

				// Index parent directories of explicit directory entries too
				IndexParentDirectories(dirPath, directories);
				continue;
			}

			entries[path] = ReadEntryBytes(entry);

			// Also index all parent directories (zips don't always have explicit directory entries)
			IndexParentDirectories(path, directories);
		}

		return (entries, directories);
	}

	private static void IndexParentDirectories(string path, HashSet<string> directories) {
		var lastSlash = path.LastIndexOf('/');
		while (lastSlash > 0) {
			var parentDir = path[..lastSlash];
			if (!directories.Add(parentDir)) {
				break; // Already visited this ancestor
			}

			lastSlash = parentDir.LastIndexOf('/');
		}
	}

	private static byte[] ReadEntryBytes(ZipArchiveEntry entry) {
		using var stream = entry.Open();
		using var ms = new MemoryStream();
		stream.CopyTo(ms);
		return ms.ToArray();
	}

	private static string NormalizePath(string path) {
		if (string.IsNullOrWhiteSpace(path)) {
			return string.Empty;
		}

		var normalized = path.Replace('\\', '/').TrimStart('/');

		// Reject path traversal attempts
		if (normalized.Contains("..")) {
			throw new ArgumentException($"Path traversal is not allowed: '{path}'", nameof(path));
		}

		return normalized;
	}

	private static bool IsWithinDirectory(string path, string prefix, bool recursive) {
		if (prefix.Length == 0) {
			if (!recursive) {
				return !path.Contains('/');
			}

			return true;
		}

		if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
		    path.Length <= prefix.Length ||
		    path[prefix.Length] != '/') {
			return false;
		}

		if (!recursive) {
			// Immediate child only: no additional '/' after the prefix separator
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
