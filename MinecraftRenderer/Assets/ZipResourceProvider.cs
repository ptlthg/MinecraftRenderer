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
	private readonly ZipArchive _archive;
	private readonly Stream? _ownedStream;
	private readonly string _rootPath;
	private readonly bool _ownsArchive;
	private readonly Dictionary<string, ZipArchiveEntry> _entryIndex;
	private readonly HashSet<string> _directoryIndex;
	private bool _disposed;

	/// <summary>
	/// Opens a ZIP archive from a file path.
	/// </summary>
	public ZipResourceProvider(string zipFilePath) {
		ArgumentException.ThrowIfNullOrWhiteSpace(zipFilePath);
		_rootPath = Path.GetFullPath(zipFilePath);
		_ownedStream = File.OpenRead(_rootPath);

		try {
			_archive = new ZipArchive(_ownedStream, ZipArchiveMode.Read, leaveOpen: false);
		}
		catch {
			_ownedStream.Dispose();
			throw;
		}

		(_entryIndex, _directoryIndex) = BuildIndex(_archive);
	}

	/// <summary>
	/// Wraps an existing <see cref="ZipArchive"/>. The caller retains ownership of the archive
	/// unless <paramref name="ownsArchive"/> is <c>true</c>, in which case this provider will
	/// dispose the archive when it is itself disposed.
	/// </summary>
	public ZipResourceProvider(ZipArchive archive, string displayPath, bool ownsArchive = false) {
		ArgumentNullException.ThrowIfNull(archive);
		_archive = archive;
		_rootPath = displayPath;
		_ownsArchive = ownsArchive;
		(_entryIndex, _directoryIndex) = BuildIndex(_archive);
	}

	public string RootPath => _rootPath;

	public bool FileExists(string relativePath) {
		ObjectDisposedException.ThrowIf(_disposed, this);
		var normalized = NormalizePath(relativePath);
		return _entryIndex.ContainsKey(normalized);
	}

	public bool DirectoryExists(string relativePath) {
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (string.IsNullOrWhiteSpace(relativePath)) {
			return true; // root always exists
		}

		var normalized = NormalizePath(relativePath).TrimEnd('/');
		return _directoryIndex.Contains(normalized);
	}

	public Stream OpenRead(string relativePath) {
		ObjectDisposedException.ThrowIf(_disposed, this);
		var normalized = NormalizePath(relativePath);
		if (!_entryIndex.TryGetValue(normalized, out var entry)) {
			throw new FileNotFoundException($"File not found in ZIP archive: '{relativePath}'", relativePath);
		}

		// ZipArchiveEntry.Open() returns a non-seekable stream.
		// Many consumers (e.g. ImageSharp) need a seekable stream, so copy into a MemoryStream.
		var zipStream = entry.Open();
		var memoryStream = new MemoryStream();
		try {
			zipStream.CopyTo(memoryStream);
			memoryStream.Position = 0;
			zipStream.Dispose();
			return memoryStream;
		}
		catch {
			memoryStream.Dispose();
			zipStream.Dispose();
			throw;
		}
	}

	public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive) {
		ObjectDisposedException.ThrowIf(_disposed, this);
		var prefix = NormalizePath(directory).TrimEnd('/');
		var pattern = GlobToRegex(searchPattern);

		foreach (var (path, _) in _entryIndex) {
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
		ObjectDisposedException.ThrowIf(_disposed, this);
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
		if (_disposed) {
			return;
		}

		_disposed = true;

		// File-path constructor: _ownedStream is disposed by ZipArchive (leaveOpen: false).
		// Archive constructor: only dispose if we own it.
		if (_ownedStream is not null || _ownsArchive) {
			_archive.Dispose();
		}
	}

	private static (Dictionary<string, ZipArchiveEntry> entries, HashSet<string> directories) BuildIndex(
		ZipArchive archive) {
		var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
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

			entries[path] = entry;

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
