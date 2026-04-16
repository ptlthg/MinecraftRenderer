namespace MinecraftRenderer.Assets;

using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Provides read-only access to a tree of resources such as files and directories.
/// All relative paths use forward-slash (<c>/</c>) separators regardless of platform.
/// Implementations may wrap a filesystem directory, a ZIP archive, or other custom formats.
/// </summary>
public interface IResourceProvider : IDisposable
{
	/// <summary>
	/// A display-friendly identifier for this provider (e.g. filesystem path or zip file path).
	/// </summary>
	string RootPath { get; }

	/// <summary>
	/// Returns <c>true</c> if a file exists at the given relative path.
	/// </summary>
	bool FileExists(string relativePath);

	/// <summary>
	/// Returns <c>true</c> if a directory (or virtual directory prefix) exists at the given relative path.
	/// </summary>
	bool DirectoryExists(string relativePath);

	/// <summary>
	/// Opens a read-only stream for the file at the given relative path.
	/// </summary>
	/// <exception cref="FileNotFoundException">The file does not exist.</exception>
	Stream OpenRead(string relativePath);

	/// <summary>
	/// Enumerates file paths matching a search pattern within a directory.
	/// Returned paths are relative to the provider root and use forward-slash separators.
	/// </summary>
	/// <param name="directory">The directory to search within (relative to provider root). Use <c>""</c> for the root.</param>
	/// <param name="searchPattern">A glob pattern such as <c>*.json</c>.</param>
	/// <param name="recursive">When <c>true</c>, searches all subdirectories.</param>
	IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive);

	/// <summary>
	/// Enumerates immediate subdirectory paths within a directory.
	/// Returned paths are relative to the provider root and use forward-slash separators.
	/// </summary>
	/// <param name="directory">The directory to enumerate (relative to provider root). Use <c>""</c> for the root.</param>
	/// <param name="searchPattern">A glob pattern such as <c>*</c>.</param>
	/// <param name="recursive">When <c>true</c>, searches all subdirectories.</param>
	IEnumerable<string> EnumerateDirectories(string directory, string searchPattern, bool recursive);
}
