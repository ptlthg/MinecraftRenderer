namespace MinecraftRenderer.Assets;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// An <see cref="IResourceProvider"/> backed by a filesystem directory.
/// </summary>
public sealed class DirectoryResourceProvider : IResourceProvider
{
	private readonly string _rootPath;

	public DirectoryResourceProvider(string rootPath) {
		ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
		_rootPath = Path.GetFullPath(rootPath);
	}

	public string RootPath => _rootPath;

	public bool FileExists(string relativePath) {
		var fullPath = ToFullPath(relativePath);
		return File.Exists(fullPath);
	}

	public bool DirectoryExists(string relativePath) {
		if (string.IsNullOrWhiteSpace(relativePath)) {
			return Directory.Exists(_rootPath);
		}

		var fullPath = ToFullPath(relativePath);
		return Directory.Exists(fullPath);
	}

	public Stream OpenRead(string relativePath) {
		var fullPath = ToFullPath(relativePath);
		if (!File.Exists(fullPath)) {
			throw new FileNotFoundException($"File not found in directory provider: '{relativePath}'", fullPath);
		}

		return File.OpenRead(fullPath);
	}

	public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive) {
		var fullDir = string.IsNullOrWhiteSpace(directory) ? _rootPath : ToFullPath(directory);
		if (!Directory.Exists(fullDir)) {
			return [];
		}

		var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
		return Directory.EnumerateFiles(fullDir, searchPattern, searchOption)
			.Select(ToRelativePath);
	}

	public IEnumerable<string> EnumerateDirectories(string directory, string searchPattern, bool recursive) {
		var fullDir = string.IsNullOrWhiteSpace(directory) ? _rootPath : ToFullPath(directory);
		if (!Directory.Exists(fullDir)) {
			return [];
		}

		var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
		return Directory.EnumerateDirectories(fullDir, searchPattern, searchOption)
			.Select(ToRelativePath);
	}

	public void Dispose() {
		// No resources to release for a directory provider.
	}

	private string ToFullPath(string relativePath) {
		var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
		var combined = Path.Combine(_rootPath, normalized);
		var fullPath = Path.GetFullPath(combined);

		// Ensure the resolved path is within the root to prevent directory traversal.
		// Use GetRelativePath to handle case sensitivity correctly across platforms.
		var relative = Path.GetRelativePath(_rootPath, fullPath);
		if (relative.StartsWith("..", StringComparison.Ordinal)) {
			throw new UnauthorizedAccessException(
				$"Path '{relativePath}' resolves outside the provider root.");
		}

		return fullPath;
	}

	private string ToRelativePath(string absolutePath) {
		return Path.GetRelativePath(_rootPath, absolutePath).Replace('\\', '/');
	}
}
