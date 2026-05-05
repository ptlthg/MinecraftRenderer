namespace MinecraftRenderer.Nbt;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// NBT (Named Binary Tag) parser supporting both binary and SNBT (stringified NBT) formats.
/// Can handle GZip-compressed binary data.
/// </summary>
public static class NbtParser
{
	private const int GzipMagic0 = 0x1F;
	private const int GzipMagic1 = 0x8B;

	/// <summary>
	/// Parse NBT data from a binary stream, optionally detecting and handling GZip compression.
	/// </summary>
	public static NbtDocument ParseBinary(Stream stream, bool detectCompression = true) {
		ArgumentNullException.ThrowIfNull(stream);

		var rented = BufferStream(stream, detectCompression, out var length, out var pool);
		try {
			return ParseBinaryCore(new ReadOnlySpan<byte>(rented, 0, length));
		}
		finally {
			pool?.Return(rented);
		}
	}

	/// <summary>
	/// Parse NBT data from a byte array, automatically detecting and handling GZip compression.
	/// </summary>
	public static NbtDocument ParseBinary(ReadOnlyMemory<byte> data) {
		var span = data.Span;
		if (span.Length >= 2 && span[0] == GzipMagic0 && span[1] == GzipMagic1) {
			var rented = DecompressGzip(span, out var length, out var pool);
			try {
				return ParseBinaryCore(new ReadOnlySpan<byte>(rented, 0, length));
			}
			finally {
				pool.Return(rented);
			}
		}

		return ParseBinaryCore(span);
	}

	/// <summary>
	/// Parse NBT data from a string in SNBT (stringified NBT) format.
	/// </summary>
	public static NbtDocument ParseSnbt(string text) {
		ArgumentException.ThrowIfNullOrWhiteSpace(text);
		var parser = new SnbtParser(text);
		var tag = parser.Parse();
		return new NbtDocument(tag);
	}

	private static NbtDocument ParseBinaryCore(ReadOnlySpan<byte> data) {
		var reader = new SpanNbtReader(data);
		var type = (NbtTagType)reader.ReadByte();
		if (type == NbtTagType.End) {
			return new NbtDocument(NbtCompound.Empty);
		}

		reader.SkipString(); // root name, ignored
		var root = reader.ReadTagPayload(type);
		return new NbtDocument(root);
	}

	private static byte[] BufferStream(Stream stream, bool detectCompression, out int length, out ArrayPool<byte>? pool) {
		pool = ArrayPool<byte>.Shared;

		// Try to size the rental from a known length.
		var initialCapacity = 4096;
		if (stream.CanSeek) {
			var remaining = stream.Length - stream.Position;
			if (remaining > 0 && remaining <= int.MaxValue) {
				initialCapacity = (int)remaining;
			}
		}

		// First peek to detect gzip without losing position.
		if (detectCompression) {
			Span<byte> header = stackalloc byte[2];
			if (stream.CanSeek) {
				var origin = stream.Position;
				var read = stream.Read(header);
				stream.Position = origin;
				if (read == 2 && header[0] == GzipMagic0 && header[1] == GzipMagic1) {
					return DecompressGzipFromStream(stream, out length, out pool);
				}
			}
			else {
				// Non-seekable: buffer everything first, then check.
				var buffered = ReadAllToPooled(stream, initialCapacity, pool, out length);
				if (length >= 2 && buffered[0] == GzipMagic0 && buffered[1] == GzipMagic1) {
					try {
						using var ms = new MemoryStream(buffered, 0, length, writable: false);
						return DecompressGzipFromStream(ms, out length, out pool);
					}
					finally {
						ArrayPool<byte>.Shared.Return(buffered);
					}
				}

				return buffered;
			}
		}

		return ReadAllToPooled(stream, initialCapacity, pool, out length);
	}

	private static byte[] ReadAllToPooled(Stream stream, int initialCapacity, ArrayPool<byte> pool, out int length) {
		var buffer = pool.Rent(initialCapacity);
		var written = 0;
		while (true) {
			if (written == buffer.Length) {
				var bigger = pool.Rent(buffer.Length * 2);
				Buffer.BlockCopy(buffer, 0, bigger, 0, written);
				pool.Return(buffer);
				buffer = bigger;
			}

			var read = stream.Read(buffer, written, buffer.Length - written);
			if (read <= 0) {
				break;
			}

			written += read;
		}

		length = written;
		return buffer;
	}

	private static byte[] DecompressGzipFromStream(Stream stream, out int length, out ArrayPool<byte> pool) {
		pool = ArrayPool<byte>.Shared;
		using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
		return ReadAllToPooled(gzip, 8192, pool, out length);
	}

	private static byte[] DecompressGzip(ReadOnlySpan<byte> compressed, out int length, out ArrayPool<byte> pool) {
		pool = ArrayPool<byte>.Shared;
		// Use a transient MemoryStream over the compressed bytes
		var array = pool.Rent(compressed.Length);
		try {
			compressed.CopyTo(array);
			using var ms = new MemoryStream(array, 0, compressed.Length, writable: false);
			using var gzip = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: false);
			return ReadAllToPooled(gzip, Math.Max(8192, compressed.Length * 2), pool, out length);
		}
		finally {
			pool.Return(array);
		}
	}

	/// <summary>
	/// Allocation-light NBT binary reader that walks a contiguous span without per-read stackallocs or stream calls.
	/// </summary>
	private ref struct SpanNbtReader
	{
		private readonly ReadOnlySpan<byte> _data;
		private int _pos;

		public SpanNbtReader(ReadOnlySpan<byte> data) {
			_data = data;
			_pos = 0;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public byte ReadByte() {
			var data = _data;
			var pos = _pos;
			if ((uint)pos >= (uint)data.Length) {
				ThrowEnd();
			}
			_pos = pos + 1;
			return data[pos];
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public short ReadInt16() {
			var slice = Take(2);
			return BinaryPrimitives.ReadInt16BigEndian(slice);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public ushort ReadUInt16() {
			var slice = Take(2);
			return BinaryPrimitives.ReadUInt16BigEndian(slice);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int ReadInt32() {
			var slice = Take(4);
			return BinaryPrimitives.ReadInt32BigEndian(slice);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public long ReadInt64() {
			var slice = Take(8);
			return BinaryPrimitives.ReadInt64BigEndian(slice);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public float ReadSingle() {
			var slice = Take(4);
			return BinaryPrimitives.ReadSingleBigEndian(slice);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public double ReadDouble() {
			var slice = Take(8);
			return BinaryPrimitives.ReadDoubleBigEndian(slice);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private ReadOnlySpan<byte> Take(int count) {
			var data = _data;
			var pos = _pos;
			if ((uint)(pos + count) > (uint)data.Length) {
				ThrowEnd();
			}
			_pos = pos + count;
			return data.Slice(pos, count);
		}

		public void SkipString() {
			var length = ReadUInt16();
			if (length == 0) {
				return;
			}
			var data = _data;
			var pos = _pos;
			if ((uint)(pos + length) > (uint)data.Length) {
				ThrowEnd();
			}
			_pos = pos + length;
		}

		public string ReadString() {
			var length = ReadUInt16();
			if (length == 0) {
				return string.Empty;
			}
			var slice = Take(length);
			return DecodeMutf8(slice);
		}

		public NbtTag ReadTagPayload(NbtTagType type) {
			switch (type) {
				case NbtTagType.Byte:
					return NbtByte.GetCached((sbyte)ReadByte());
				case NbtTagType.Short:
					return new NbtShort(ReadInt16());
				case NbtTagType.Int:
					return new NbtInt(ReadInt32());
				case NbtTagType.Long:
					return new NbtLong(ReadInt64());
				case NbtTagType.Float:
					return new NbtFloat(ReadSingle());
				case NbtTagType.Double:
					return new NbtDouble(ReadDouble());
				case NbtTagType.ByteArray:
					return new NbtByteArray(ReadByteArray());
				case NbtTagType.String: {
					var s = ReadString();
					return s.Length == 0 ? NbtString.Empty : new NbtString(s);
				}
				case NbtTagType.List:
					return ReadList();
				case NbtTagType.Compound:
					return ReadCompound();
				case NbtTagType.IntArray:
					return new NbtIntArray(ReadIntArray());
				case NbtTagType.LongArray:
					return new NbtLongArray(ReadLongArray());
				case NbtTagType.End:
					return NbtCompound.Empty;
				default:
					throw new InvalidDataException($"Unsupported NBT tag type '{type}'.");
			}
		}

		private NbtCompound ReadCompound() {
			string[]? keys = null;
			NbtTag[]? values = null;
			var count = 0;

			while (true) {
				var type = (NbtTagType)ReadByte();
				if (type == NbtTagType.End) {
					break;
				}

				var name = ReadString();
				var value = ReadTagPayload(type);
				if (name.Length == 0) {
					continue;
				}

				if (keys is null) {
					keys = new string[8];
					values = new NbtTag[8];
				}
				else if (count == keys.Length) {
					var newCapacity = keys.Length * 2;
					Array.Resize(ref keys, newCapacity);
					Array.Resize(ref values, newCapacity);
				}

				keys[count] = name;
				values![count] = value;
				count++;
			}

			if (count == 0) {
				return NbtCompound.Empty;
			}

			return new NbtCompound(keys!, values!, count);
		}

		private NbtList ReadList() {
			var elementType = (NbtTagType)ReadByte();
			var length = ReadInt32();
			if (length < 0) {
				throw new InvalidDataException("Encountered negative list length in NBT payload.");
			}

			if (length == 0) {
				return elementType == NbtTagType.End ? NbtList.EmptyEnd : new NbtList(elementType, new List<NbtTag>(0), takeOwnership: true);
			}

			var items = new List<NbtTag>(length);
			for (var i = 0; i < length; i++) {
				items.Add(ReadTagPayload(elementType));
			}

			return new NbtList(elementType, items, takeOwnership: true);
		}

		private byte[] ReadByteArray() {
			var length = ReadInt32();
			if (length < 0) {
				throw new InvalidDataException("Encountered negative byte array length in NBT payload.");
			}

			if (length == 0) {
				return Array.Empty<byte>();
			}

			var slice = Take(length);
			var buffer = new byte[length];
			slice.CopyTo(buffer);
			return buffer;
		}

		private int[] ReadIntArray() {
			var length = ReadInt32();
			if (length < 0) {
				throw new InvalidDataException("Encountered negative int array length in NBT payload.");
			}

			if (length == 0) {
				return Array.Empty<int>();
			}

			var byteCount = checked(length * sizeof(int));
			var slice = Take(byteCount);
			var buffer = new int[length];
			slice.CopyTo(MemoryMarshal.AsBytes(buffer.AsSpan()));
			if (BitConverter.IsLittleEndian) {
				BinaryPrimitives.ReverseEndianness(buffer.AsSpan(), buffer.AsSpan());
			}
			return buffer;
		}

		private long[] ReadLongArray() {
			var length = ReadInt32();
			if (length < 0) {
				throw new InvalidDataException("Encountered negative long array length in NBT payload.");
			}

			if (length == 0) {
				return Array.Empty<long>();
			}

			var byteCount = checked(length * sizeof(long));
			var slice = Take(byteCount);
			var buffer = new long[length];
			slice.CopyTo(MemoryMarshal.AsBytes(buffer.AsSpan()));
			if (BitConverter.IsLittleEndian) {
				BinaryPrimitives.ReverseEndianness(buffer.AsSpan(), buffer.AsSpan());
			}
			return buffer;
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		private static void ThrowEnd() =>
			throw new EndOfStreamException("Unexpected end of NBT payload.");
	}

	/// <summary>
	/// Decode Modified UTF-8 (MUTF-8) as used by NBT format.
	/// Fast path: most NBT strings contain only ASCII / regular UTF-8 sequences and can be decoded
	/// via the vectorized <see cref="Encoding.UTF8"/> path. We fall back to a manual decoder only
	/// when the input contains the MUTF-8-specific overlong NUL (<c>0xC0 0x80</c>) or 4+ byte
	/// sequences (signalled by a leading byte with the top four bits all set).
	/// </summary>
	private static string DecodeMutf8(ReadOnlySpan<byte> bytes) {
		// Fast path: scan for any byte that requires the slow MUTF-8-specific decoder.
		// 0xC0 followed by 0x80 is the overlong NUL; 0xED is used for surrogate-pair encoding of
		// supplementary characters in MUTF-8. Any byte >= 0xF0 is invalid MUTF-8 (would be a 4-byte
		// UTF-8 sequence) and signals we must fall through to the strict decoder.
		var fastPath = true;
		for (var i = 0; i < bytes.Length; i++) {
			var b = bytes[i];
			if (b < 0x80) {
				continue;
			}
			if (b == 0xC0 || b == 0xED || b >= 0xF0) {
				fastPath = false;
				break;
			}
		}

		if (fastPath) {
			return Encoding.UTF8.GetString(bytes);
		}

		return DecodeMutf8Slow(bytes);
	}

	private static string DecodeMutf8Slow(ReadOnlySpan<byte> bytes) {
		var chars = ArrayPool<char>.Shared.Rent(bytes.Length);
		try {
			var charIndex = 0;
			for (var i = 0; i < bytes.Length; i++) {
				var b1 = bytes[i];
				if ((b1 & 0x80) == 0) {
					chars[charIndex++] = (char)b1;
				}
				else if ((b1 & 0xE0) == 0xC0) {
					if (i + 1 >= bytes.Length) {
						throw new InvalidDataException("Truncated MUTF-8 sequence");
					}
					var b2 = bytes[++i];
					if ((b2 & 0xC0) != 0x80) {
						throw new InvalidDataException("Invalid MUTF-8 continuation byte");
					}
					var codePoint = ((b1 & 0x1F) << 6) | (b2 & 0x3F);
					chars[charIndex++] = (char)codePoint;
				}
				else if ((b1 & 0xF0) == 0xE0) {
					if (i + 2 >= bytes.Length) {
						throw new InvalidDataException("Truncated MUTF-8 sequence");
					}
					var b2 = bytes[++i];
					var b3 = bytes[++i];
					if ((b2 & 0xC0) != 0x80 || (b3 & 0xC0) != 0x80) {
						throw new InvalidDataException("Invalid MUTF-8 continuation byte");
					}
					var codePoint = ((b1 & 0x0F) << 12) | ((b2 & 0x3F) << 6) | (b3 & 0x3F);
					chars[charIndex++] = (char)codePoint;
				}
				else {
					throw new InvalidDataException($"Invalid MUTF-8 byte: 0x{b1:X2}");
				}
			}

			return new string(chars, 0, charIndex);
		}
		finally {
			ArrayPool<char>.Shared.Return(chars);
		}
	}

	private sealed class SnbtParser(string text)
	{
		private int _index;

		public NbtTag Parse() {
			SkipWhitespace();
			var value = ParseValue();
			SkipWhitespace();
			if (!IsAtEnd) {
				throw new FormatException("Unexpected characters after SNBT payload.");
			}

			return value;
		}

		private NbtTag ParseValue() {
			if (Match('{')) {
				return ParseCompound();
			}

			if (Match('[')) {
				return ParseListOrArray();
			}

			if (Peek() == '\"' || Peek() == '\'') {
				return new NbtString(ParseQuotedString());
			}

			return ParseScalar();
		}

		private NbtCompound ParseCompound() {
			var items = new List<KeyValuePair<string, NbtTag>>();
			SkipWhitespace();
			if (Match('}')) {
				return new NbtCompound(items);
			}

			while (true) {
				SkipWhitespace();
				var key = ParseKey();
				SkipWhitespace();
				Expect(':');
				SkipWhitespace();
				var value = ParseValue();
				items.Add(new KeyValuePair<string, NbtTag>(key, value));
				SkipWhitespace();
				if (Match('}')) {
					break;
				}

				Expect(',');
			}

			return new NbtCompound(items);
		}

		private NbtTag ParseListOrArray() {
			SkipWhitespace();
			if (!IsAtEnd &&
			    (Peek() == 'B' || Peek() == 'b' || Peek() == 'I' || Peek() == 'i' || Peek() == 'L' || Peek() == 'l') &&
			    LookAhead(1) == ';') {
				var type = char.ToUpperInvariant(Advance());
				Expect(';');
				SkipWhitespace();
				return type switch {
					'B' => ParseNumericArray(static tag => tag switch {
						NbtByte nb => unchecked((byte)nb.Value),
						NbtInt ni => unchecked((byte)ni.Value),
						NbtLong nl => unchecked((byte)nl.Value),
						_ => throw new FormatException("Invalid element type for byte array.")
					}, values => new NbtByteArray(values.ToArray())),
					'I' => ParseNumericArray(static tag => tag switch {
						NbtInt ni => ni.Value,
						NbtByte nb => nb.Value,
						NbtLong nl => checked((int)nl.Value),
						_ => throw new FormatException("Invalid element type for int array.")
					}, values => new NbtIntArray(values.ToArray())),
					'L' => ParseNumericArray(static tag => tag switch {
						NbtLong nl => nl.Value,
						NbtInt ni => ni.Value,
						NbtByte nb => nb.Value,
						_ => throw new FormatException("Invalid element type for long array.")
					}, values => new NbtLongArray(values.ToArray())),
					_ => throw new FormatException("Unsupported typed array designator in SNBT.")
				};
			}

			var items = new List<NbtTag>();
			SkipWhitespace();
			if (Match(']')) {
				return new NbtList(NbtTagType.End, items);
			}

			while (true) {
				SkipWhitespace();
				var item = ParseValue();
				if (items.Count > 0 && item.Type != items[0].Type) {
					throw new FormatException("SNBT lists must contain elements of the same type.");
				}

				items.Add(item);
				SkipWhitespace();
				if (Match(']')) {
					break;
				}

				Expect(',');
			}

			var elementType = items.Count == 0 ? NbtTagType.End : items[0].Type;
			return new NbtList(elementType, items);
		}

		private NbtTag ParseNumericArray<T>(Func<NbtTag, T> converter, Func<List<T>, NbtTag> factory) {
			var values = new List<T>();
			SkipWhitespace();
			if (Match(']')) {
				return factory(values);
			}

			while (true) {
				SkipWhitespace();
				var valueTag = ParseScalar();
				values.Add(converter(valueTag));
				SkipWhitespace();
				if (Match(']')) {
					break;
				}

				Expect(',');
			}

			return factory(values);
		}

		private string ParseQuotedString() {
			var quote = Advance();
			var builder = new StringBuilder();
			while (!IsAtEnd) {
				var c = Advance();
				if (c == quote) {
					break;
				}

				if (c == '\\' && !IsAtEnd) {
					var escape = Advance();
					builder.Append(escape switch {
						'\"' => '\"',
						'\'' => '\'',
						'\\' => '\\',
						'n' => '\n',
						'r' => '\r',
						't' => '\t',
						'0' => '\0',
						_ => escape
					});
					continue;
				}

				builder.Append(c);
			}

			return builder.ToString();
		}

		private string ParseKey() {
			if (Peek() == '\"' || Peek() == '\'') {
				return ParseQuotedString();
			}

			var start = _index;
			while (!IsAtEnd) {
				var c = Peek();
				if (char.IsWhiteSpace(c) || c == ':' || c == '}' || c == ',') {
					break;
				}

				Advance();
			}

			return text.Substring(start, _index - start);
		}

		private NbtTag ParseScalar() {
			var token = ReadToken();
			if (token.Length == 0) {
				throw new FormatException("Unexpected empty token in SNBT payload.");
			}

			if (string.Equals(token, "true", StringComparison.OrdinalIgnoreCase)) {
				return new NbtByte(1);
			}

			if (string.Equals(token, "false", StringComparison.OrdinalIgnoreCase)) {
				return new NbtByte(0);
			}

			var suffix = char.ToLowerInvariant(token[^1]);

			try {
				string numberPart;
				switch (suffix) {
					case 'b':
						numberPart = token[..^1];
						if (sbyte.TryParse(numberPart, NumberStyles.Integer, CultureInfo.InvariantCulture,
							    out var byteValue)) {
							return new NbtByte(byteValue);
						}

						break;
					case 's':
						numberPart = token[..^1];
						if (short.TryParse(numberPart, NumberStyles.Integer, CultureInfo.InvariantCulture,
							    out var shortValue)) {
							return new NbtShort(shortValue);
						}

						break;
					case 'l':
						numberPart = token[..^1];
						if (long.TryParse(numberPart, NumberStyles.Integer, CultureInfo.InvariantCulture,
							    out var longValue)) {
							return new NbtLong(longValue);
						}

						break;
					case 'f':
						numberPart = token[..^1];
						if (float.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture,
							    out var floatValue)) {
							return new NbtFloat(floatValue);
						}

						break;
					case 'd':
						numberPart = token[..^1];
						if (double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture,
							    out var doubleValue)) {
							return new NbtDouble(doubleValue);
						}

						break;
				}

				if (suffix is 'b' or 's' or 'l' or 'f' or 'd') {
					return new NbtString(token);
				}

				if (token.Contains('.') || token.Contains('e', StringComparison.OrdinalIgnoreCase)) {
					if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture,
						    out var doubleDefault)) {
						return new NbtDouble(doubleDefault);
					}
				}
				else if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue)) {
					return new NbtInt(intValue);
				}
				else if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture,
					         out var fallbackLong)) {
					return new NbtLong(fallbackLong);
				}
			}
			catch (FormatException) {
				// Fallback to string literal when parsing fails.
			}

			return new NbtString(token);
		}

		private string ReadToken() {
			var start = _index;
			while (!IsAtEnd) {
				var c = Peek();
				if (char.IsWhiteSpace(c) || c == ',' || c == ']' || c == '}' || c == ':') {
					break;
				}

				Advance();
			}

			return text.Substring(start, _index - start);
		}

		private bool Match(char expected) {
			if (IsAtEnd || Peek() != expected) {
				return false;
			}

			_index++;
			return true;
		}

		private void Expect(char expected) {
			if (!Match(expected)) {
				throw new FormatException($"Expected '{expected}' in SNBT payload.");
			}
		}

		private char Peek() {
			if (IsAtEnd) {
				return '\0';
			}

			return text[_index];
		}

		private char LookAhead(int offset) {
			var position = _index + offset;
			if (position >= text.Length) {
				return '\0';
			}

			return text[position];
		}

		private char Advance() {
			if (IsAtEnd) {
				return '\0';
			}

			return text[_index++];
		}

		private void SkipWhitespace() {
			while (!IsAtEnd && char.IsWhiteSpace(Peek())) {
				_index++;
			}
		}

		private bool IsAtEnd => _index >= text.Length;
	}
}