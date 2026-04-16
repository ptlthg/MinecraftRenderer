namespace MinecraftRenderer.Assets;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// An <see cref="IResourceProvider"/> that delegates to another provider with a fixed path prefix.
/// Useful for scoping a provider to a subtree, e.g. <c>assets/minecraft</c> within a ZIP.
/// By default this provider does NOT own the inner provider and will not dispose it.
/// Set <see cref="OwnsInner"/> to <c>true</c> to have the inner provider disposed along with this one.
/// </summary>
public sealed class SubPathResourceProvider : IResourceProvider
{
	private readonly IResourceProvider _inner;
	private readonly string _prefix;

	/// <summary>
	/// When <c>true</c>, disposing this provider also disposes the inner provider.
	/// </summary>
	public bool OwnsInner { get; init; }

	/// <param name="inner">The underlying provider to delegate to.</param>
	/// <param name="prefix">A forward-slash-separated prefix to prepend to all relative paths.</param>
	public SubPathResourceProvider(IResourceProvider inner, string prefix) {
		ArgumentNullException.ThrowIfNull(inner);
		_inner = inner;
		_prefix = NormalizePath(prefix);
		if (_prefix.Length > 0 && !_prefix.EndsWith('/')) {
			_prefix += "/";
		}
	}

	public string RootPath => _inner.RootPath + "/" + _prefix.TrimEnd('/');

	public bool FileExists(string relativePath) {
		return _inner.FileExists(_prefix + NormalizePath(relativePath));
	}

	public bool DirectoryExists(string relativePath) {
		if (string.IsNullOrWhiteSpace(relativePath)) {
			return _inner.DirectoryExists(_prefix.TrimEnd('/'));
		}

		return _inner.DirectoryExists(_prefix + NormalizePath(relativePath));
	}

	public Stream OpenRead(string relativePath) {
		return _inner.OpenRead(_prefix + NormalizePath(relativePath));
	}

	public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive) {
		var innerDir = string.IsNullOrWhiteSpace(directory)
			? _prefix.TrimEnd('/')
			: _prefix + NormalizePath(directory);

		return _inner.EnumerateFiles(innerDir, searchPattern, recursive)
			.Select(StripPrefix)
			.Where(static p => p is not null)!;
	}

	public IEnumerable<string> EnumerateDirectories(string directory, string searchPattern, bool recursive) {
		var innerDir = string.IsNullOrWhiteSpace(directory)
			? _prefix.TrimEnd('/')
			: _prefix + NormalizePath(directory);

		return _inner.EnumerateDirectories(innerDir, searchPattern, recursive)
			.Select(StripPrefix)
			.Where(static p => p is not null)!;
	}

	public void Dispose() {
		if (OwnsInner) {
			_inner.Dispose();
		}
	}

	private string? StripPrefix(string path) {
		if (_prefix.Length == 0) {
			return path;
		}

		if (path.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)) {
			return path[_prefix.Length..];
		}

		return null;
	}

	private static string NormalizePath(string path) {
		return path.Replace('\\', '/').TrimStart('/');
	}
}
