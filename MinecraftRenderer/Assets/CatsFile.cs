namespace MinecraftRenderer.Assets;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

/// <summary>
/// Reads and provides access to a .cats (Catharsis Archive Tool Structure) binary resource pack.
/// The format stores a hierarchical directory/file tree with optional GZIP compression.
/// </summary>
public sealed class CatsFile
{
	private const uint Magic = 0x43415453; // "CATS" in ASCII
	private const byte CurrentVersion = 0x01;
	private const byte CompressionNone = 0xFF;
	private const byte CompressionGzip = 0xFE;

	private readonly byte[] _data;
	private readonly int _dataOffset; // byte offset where the data section begins (after header)
	private readonly CatsDirectoryEntry _root;

	/// <summary>
	/// Opens a .cats file from a file path. The entire file is read into memory.
	/// </summary>
	public CatsFile(string filePath) : this(File.ReadAllBytes(filePath)) { }

	/// <summary>
	/// Parses a .cats archive from raw bytes.
	/// </summary>
	public CatsFile(byte[] data) {
		ArgumentNullException.ThrowIfNull(data);
		if (data.Length < 5) {
			throw new InvalidDataException("Data is too small to be a valid .cats file.");
		}

		_data = data;
		var offset = 0;

		var magic = ReadUInt32BigEndian(ref offset);
		if (magic != Magic) {
			throw new InvalidDataException(
				$"Invalid .cats magic number: 0x{magic:X8} (expected 0x{Magic:X8}).");
		}

		var version = ReadByte(ref offset);
		if (version != CurrentVersion) {
			throw new InvalidDataException(
				$"Unsupported .cats version: {version} (expected {CurrentVersion}).");
		}

		_root = ParseDirectory(ref offset);
		_dataOffset = offset;
	}

	/// <summary>
	/// The root directory of the archive.
	/// </summary>
	public CatsDirectoryEntry Root => _root;

	/// <summary>
	/// Looks up an entry by path. Directories should have a trailing <c>/</c>.
	/// Files should not. Path components are separated by <c>/</c>.
	/// Returns null if the entry does not exist.
	/// </summary>
	public CatsEntry? GetEntry(string path) {
		if (string.IsNullOrEmpty(path) || path == "/") {
			return _root;
		}

		var trimmed = path.TrimStart('/');
		var isDirectory = trimmed.EndsWith('/');
		if (isDirectory) {
			trimmed = trimmed.TrimEnd('/');
		}

		var parts = trimmed.Split('/');
		CatsDirectoryEntry current = _root;

		for (var i = 0; i < parts.Length; i++) {
			var part = parts[i];
			if (!current.Children.TryGetValue(part, out var child)) {
				return null;
			}

			if (i == parts.Length - 1) {
				// Last segment: return it if it matches the expected type
				if (isDirectory && child is not CatsDirectoryEntry) {
					return null;
				}

				return child;
			}

			// Intermediate segment must be a directory
			if (child is CatsDirectoryEntry dir) {
				current = dir;
			}
			else {
				return null;
			}
		}

		return null;
	}

	/// <summary>
	/// Opens a read-only seekable <see cref="MemoryStream"/> for the given file entry.
	/// GZIP-compressed entries are automatically decompressed.
	/// </summary>
	public MemoryStream OpenStream(CatsFileEntry entry) {
		ArgumentNullException.ThrowIfNull(entry);
		var absoluteOffset = _dataOffset + entry.Offset;

		if (absoluteOffset < 0 || absoluteOffset + entry.Size > _data.Length) {
			throw new InvalidDataException(
				$"File entry data range [{absoluteOffset}..{absoluteOffset + entry.Size}] is out of bounds " +
				$"(archive size: {_data.Length}).");
		}

		if (entry.Compression == CompressionGzip) {
			using var compressedStream = new MemoryStream(_data, absoluteOffset, entry.Size, writable: false);
			using var gzip = new GZipStream(compressedStream, CompressionMode.Decompress);
			var decompressed = new MemoryStream();
			gzip.CopyTo(decompressed);
			decompressed.Position = 0;
			return decompressed;
		}

		// Uncompressed — return a non-writable window over the data array
		return new MemoryStream(_data, absoluteOffset, entry.Size, writable: false);
	}

	private CatsDirectoryEntry ParseDirectory(ref int offset) {
		var entryCount = ReadUInt16BigEndian(ref offset);
		var children = new Dictionary<string, CatsEntry>(entryCount, StringComparer.Ordinal);

		for (var i = 0; i < entryCount; i++) {
			var entryType = ReadByte(ref offset);
			var nameLength = ReadByte(ref offset);
			var name = ReadAsciiString(ref offset, nameLength);

			switch (entryType) {
				case 0x00: // File
					var fileOffset = ReadInt32BigEndian(ref offset);
					var fileSize = ReadInt32BigEndian(ref offset);
					var compression = ReadByte(ref offset);
					children[name] = new CatsFileEntry {
						Name = name,
						Offset = fileOffset,
						Size = fileSize,
						Compression = compression
					};
					break;
				case 0x01: // Directory
					var dirEntry = ParseDirectory(ref offset);
					dirEntry.Name = name;
					children[name] = dirEntry;
					break;
				default:
					throw new InvalidDataException($"Unknown entry type: 0x{entryType:X2}.");
			}
		}

		return new CatsDirectoryEntry { Children = children };
	}

	private uint ReadUInt32BigEndian(ref int offset) {
		if (offset + 4 > _data.Length) {
			throw new InvalidDataException("Unexpected end of data while reading UInt32.");
		}

		var value = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset, 4));
		offset += 4;
		return value;
	}

	private int ReadInt32BigEndian(ref int offset) {
		if (offset + 4 > _data.Length) {
			throw new InvalidDataException("Unexpected end of data while reading Int32.");
		}

		var value = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(offset, 4));
		offset += 4;
		return value;
	}

	private ushort ReadUInt16BigEndian(ref int offset) {
		if (offset + 2 > _data.Length) {
			throw new InvalidDataException("Unexpected end of data while reading UInt16.");
		}

		var value = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset, 2));
		offset += 2;
		return value;
	}

	private byte ReadByte(ref int offset) {
		if (offset >= _data.Length) {
			throw new InvalidDataException("Unexpected end of data while reading byte.");
		}

		return _data[offset++];
	}

	private string ReadAsciiString(ref int offset, int length) {
		if (offset + length > _data.Length) {
			throw new InvalidDataException("Unexpected end of data while reading string.");
		}

		var value = System.Text.Encoding.ASCII.GetString(_data, offset, length);
		offset += length;
		return value;
	}
}

/// <summary>
/// Base class for entries in a .cats archive.
/// </summary>
public abstract class CatsEntry
{
	public string Name { get; internal set; } = string.Empty;
}

/// <summary>
/// A file entry in a .cats archive, referencing a data region in the data section.
/// </summary>
public sealed class CatsFileEntry : CatsEntry
{
	/// <summary>Byte offset from the start of the data section.</summary>
	public int Offset { get; internal set; }

	/// <summary>Size of the file data in bytes.</summary>
	public int Size { get; internal set; }

	/// <summary>Compression type: 0xFF = None, 0xFE = GZIP.</summary>
	public byte Compression { get; internal set; }
}

/// <summary>
/// A directory entry in a .cats archive containing child entries.
/// </summary>
public sealed class CatsDirectoryEntry : CatsEntry
{
	public Dictionary<string, CatsEntry> Children { get; internal set; } = new(StringComparer.Ordinal);
}
