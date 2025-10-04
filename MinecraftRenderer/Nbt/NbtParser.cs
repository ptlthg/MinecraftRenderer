namespace MinecraftRenderer.Nbt;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

public static class NbtParser
{
    public static NbtDocument ParseBinary(Stream stream, bool detectCompression = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prepared = PrepareStream(stream, detectCompression);
        var reader = new NbtBinaryReader(prepared);
        return reader.ReadDocument();
    }

    public static NbtDocument ParseBinary(ReadOnlyMemory<byte> data)
    {
        using var memory = new MemoryStream(data.ToArray(), writable: false);
        return ParseBinary(memory, detectCompression: false);
    }

    public static NbtDocument ParseSnbt(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var parser = new SnbtParser(text);
        var tag = parser.Parse();
        return new NbtDocument(tag);
    }

    private static Stream PrepareStream(Stream stream, bool detectCompression)
    {
        Stream working = stream;
        if (!stream.CanSeek)
        {
            var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            working = buffer;
        }
        else
        {
            stream.Position = 0;
        }

        if (!detectCompression)
        {
            return working;
        }

        Span<byte> header = stackalloc byte[2];
        var read = working.Read(header);
        working.Position = 0;
        if (read == 2 && header[0] == 0x1F && header[1] == 0x8B)
        {
            using var gzip = new GZipStream(working, CompressionMode.Decompress, leaveOpen: true);
            var decompressed = new MemoryStream();
            gzip.CopyTo(decompressed);
            decompressed.Position = 0;
            return decompressed;
        }

        return working;
    }

    private sealed class NbtBinaryReader
    {
        private readonly Stream _stream;

        public NbtBinaryReader(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        public NbtDocument ReadDocument()
        {
            var type = (NbtTagType)ReadByte();
            if (type == NbtTagType.End)
            {
                return new NbtDocument(new NbtCompound(Array.Empty<KeyValuePair<string, NbtTag>>()));
            }

            _ = ReadString(); // Root name, ignored for now.
            var root = ReadTagPayload(type);
            return new NbtDocument(root);
        }

        private NbtTag ReadTagPayload(NbtTagType type)
        {
            switch (type)
            {
                case NbtTagType.Byte:
                    return new NbtByte((sbyte)_stream.ReadByteChecked());
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
                case NbtTagType.String:
                    return new NbtString(ReadString());
                case NbtTagType.List:
                    return ReadList();
                case NbtTagType.Compound:
                    return ReadCompound();
                case NbtTagType.IntArray:
                    return new NbtIntArray(ReadIntArray());
                case NbtTagType.LongArray:
                    return new NbtLongArray(ReadLongArray());
                case NbtTagType.End:
                    return new NbtCompound(Array.Empty<KeyValuePair<string, NbtTag>>());
                default:
                    throw new InvalidDataException($"Unsupported NBT tag type '{type}'.");
            }
        }

        private NbtCompound ReadCompound()
        {
            var items = new List<KeyValuePair<string, NbtTag>>();
            while (true)
            {
                var type = (NbtTagType)ReadByte();
                if (type == NbtTagType.End)
                {
                    break;
                }

                var name = ReadString();
                var value = ReadTagPayload(type);
                items.Add(new KeyValuePair<string, NbtTag>(name, value));
            }

            return new NbtCompound(items);
        }

        private NbtList ReadList()
        {
            var elementType = (NbtTagType)ReadByte();
            var length = ReadInt32();
            if (length < 0)
            {
                throw new InvalidDataException("Encountered negative list length in NBT payload.");
            }

            var items = new List<NbtTag>(length);
            for (var i = 0; i < length; i++)
            {
                items.Add(ReadTagPayload(elementType));
            }

            return new NbtList(elementType, items);
        }

        private byte[] ReadByteArray()
        {
            var length = ReadInt32();
            if (length < 0)
            {
                throw new InvalidDataException("Encountered negative byte array length in NBT payload.");
            }

            var buffer = new byte[length];
            _stream.ReadExactly(buffer);
            return buffer;
        }

        private int[] ReadIntArray()
        {
            var length = ReadInt32();
            if (length < 0)
            {
                throw new InvalidDataException("Encountered negative int array length in NBT payload.");
            }

            var buffer = new int[length];
            for (var i = 0; i < length; i++)
            {
                buffer[i] = ReadInt32();
            }

            return buffer;
        }

        private long[] ReadLongArray()
        {
            var length = ReadInt32();
            if (length < 0)
            {
                throw new InvalidDataException("Encountered negative long array length in NBT payload.");
            }

            var buffer = new long[length];
            for (var i = 0; i < length; i++)
            {
                buffer[i] = ReadInt64();
            }

            return buffer;
        }

        private byte ReadByte() => _stream.ReadByteChecked();

        private short ReadInt16()
        {
            Span<byte> buffer = stackalloc byte[2];
            _stream.ReadExactly(buffer);
            return BinaryPrimitives.ReadInt16BigEndian(buffer);
        }

        private int ReadInt32()
        {
            Span<byte> buffer = stackalloc byte[4];
            _stream.ReadExactly(buffer);
            return BinaryPrimitives.ReadInt32BigEndian(buffer);
        }

        private long ReadInt64()
        {
            Span<byte> buffer = stackalloc byte[8];
            _stream.ReadExactly(buffer);
            return BinaryPrimitives.ReadInt64BigEndian(buffer);
        }

        private float ReadSingle()
        {
            Span<byte> buffer = stackalloc byte[4];
            _stream.ReadExactly(buffer);
            if (BitConverter.IsLittleEndian)
            {
                buffer.Reverse();
            }

            return BitConverter.ToSingle(buffer);
        }

        private double ReadDouble()
        {
            Span<byte> buffer = stackalloc byte[8];
            _stream.ReadExactly(buffer);
            if (BitConverter.IsLittleEndian)
            {
                buffer.Reverse();
            }

            return BitConverter.ToDouble(buffer);
        }

        private string ReadString()
        {
            var length = ReadInt16();
            if (length <= 0)
            {
                return string.Empty;
            }

            var buffer = new byte[length];
            _stream.ReadExactly(buffer);
            return DecodeMutf8(buffer);
        }

        /// <summary>
        /// Decode Modified UTF-8 (MUTF-8) as used by NBT format.
        /// Differences from standard UTF-8:
        /// - Null character (U+0000) encoded as 0xC0 0x80
        /// - Characters above U+FFFF use surrogate pairs
        /// </summary>
        private static string DecodeMutf8(byte[] bytes)
        {
            var chars = new char[bytes.Length]; // Upper bound
            var charIndex = 0;

            for (var i = 0; i < bytes.Length; i++)
            {
                var b1 = bytes[i];

                if ((b1 & 0x80) == 0)
                {
                    // Single-byte character (0xxxxxxx)
                    chars[charIndex++] = (char)b1;
                }
                else if ((b1 & 0xE0) == 0xC0)
                {
                    // Two-byte character (110xxxxx 10xxxxxx)
                    if (i + 1 >= bytes.Length)
                        throw new InvalidDataException("Truncated MUTF-8 sequence");

                    var b2 = bytes[++i];
                    if ((b2 & 0xC0) != 0x80)
                        throw new InvalidDataException("Invalid MUTF-8 continuation byte");

                    var codePoint = ((b1 & 0x1F) << 6) | (b2 & 0x3F);
                    chars[charIndex++] = (char)codePoint;
                }
                else if ((b1 & 0xF0) == 0xE0)
                {
                    // Three-byte character (1110xxxx 10xxxxxx 10xxxxxx)
                    if (i + 2 >= bytes.Length)
                        throw new InvalidDataException("Truncated MUTF-8 sequence");

                    var b2 = bytes[++i];
                    var b3 = bytes[++i];
                    if ((b2 & 0xC0) != 0x80 || (b3 & 0xC0) != 0x80)
                        throw new InvalidDataException("Invalid MUTF-8 continuation byte");

                    var codePoint = ((b1 & 0x0F) << 12) | ((b2 & 0x3F) << 6) | (b3 & 0x3F);
                    chars[charIndex++] = (char)codePoint;
                }
                else
                {
                    // Invalid or unsupported sequence
                    throw new InvalidDataException($"Invalid MUTF-8 byte: 0x{b1:X2}");
                }
            }

            return new string(chars, 0, charIndex);
        }
    }

    private sealed class SnbtParser
    {
        private readonly string _text;
        private int _index;

        public SnbtParser(string text)
        {
            _text = text;
        }

        public NbtTag Parse()
        {
            SkipWhitespace();
            var value = ParseValue();
            SkipWhitespace();
            if (!IsAtEnd)
            {
                throw new FormatException("Unexpected characters after SNBT payload.");
            }

            return value;
        }

        private NbtTag ParseValue()
        {
            if (Match('{'))
            {
                return ParseCompound();
            }

            if (Match('['))
            {
                return ParseListOrArray();
            }

            if (Peek() == '\"' || Peek() == '\'')
            {
                return new NbtString(ParseQuotedString());
            }

            return ParseScalar();
        }

        private NbtCompound ParseCompound()
        {
            var items = new List<KeyValuePair<string, NbtTag>>();
            SkipWhitespace();
            if (Match('}'))
            {
                return new NbtCompound(items);
            }

            while (true)
            {
                SkipWhitespace();
                var key = ParseKey();
                SkipWhitespace();
                Expect(':');
                SkipWhitespace();
                var value = ParseValue();
                items.Add(new KeyValuePair<string, NbtTag>(key, value));
                SkipWhitespace();
                if (Match('}'))
                {
                    break;
                }

                Expect(',');
            }

            return new NbtCompound(items);
        }

        private NbtTag ParseListOrArray()
        {
            SkipWhitespace();
            if (!IsAtEnd && (Peek() == 'B' || Peek() == 'b' || Peek() == 'I' || Peek() == 'i' || Peek() == 'L' || Peek() == 'l') && LookAhead(1) == ';')
            {
                var type = char.ToUpperInvariant(Advance());
                Expect(';');
                SkipWhitespace();
                return type switch
                {
                    'B' => ParseNumericArray<byte>(static tag => tag switch
                    {
                        NbtByte nb => unchecked((byte)nb.Value),
                        NbtInt ni => unchecked((byte)ni.Value),
                        NbtLong nl => unchecked((byte)nl.Value),
                        _ => throw new FormatException("Invalid element type for byte array.")
                    }, values => new NbtByteArray(values.ToArray())),
                    'I' => ParseNumericArray<int>(static tag => tag switch
                    {
                        NbtInt ni => ni.Value,
                        NbtByte nb => nb.Value,
                        NbtLong nl => checked((int)nl.Value),
                        _ => throw new FormatException("Invalid element type for int array.")
                    }, values => new NbtIntArray(values.ToArray())),
                    'L' => ParseNumericArray<long>(static tag => tag switch
                    {
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
            if (Match(']'))
            {
                return new NbtList(NbtTagType.End, items);
            }

            while (true)
            {
                SkipWhitespace();
                var item = ParseValue();
                if (items.Count > 0 && item.Type != items[0].Type)
                {
                    throw new FormatException("SNBT lists must contain elements of the same type.");
                }

                items.Add(item);
                SkipWhitespace();
                if (Match(']'))
                {
                    break;
                }

                Expect(',');
            }

            var elementType = items.Count == 0 ? NbtTagType.End : items[0].Type;
            return new NbtList(elementType, items);
        }

        private NbtTag ParseNumericArray<T>(Func<NbtTag, T> converter, Func<List<T>, NbtTag> factory)
        {
            var values = new List<T>();
            SkipWhitespace();
            if (Match(']'))
            {
                return factory(values);
            }

            while (true)
            {
                SkipWhitespace();
                var valueTag = ParseScalar();
                values.Add(converter(valueTag));
                SkipWhitespace();
                if (Match(']'))
                {
                    break;
                }

                Expect(',');
            }

            return factory(values);
        }

        private string ParseQuotedString()
        {
            var quote = Advance();
            var builder = new StringBuilder();
            while (!IsAtEnd)
            {
                var c = Advance();
                if (c == quote)
                {
                    break;
                }

                if (c == '\\' && !IsAtEnd)
                {
                    var escape = Advance();
                    builder.Append(escape switch
                    {
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

        private string ParseKey()
        {
            if (Peek() == '\"' || Peek() == '\'')
            {
                return ParseQuotedString();
            }

            var start = _index;
            while (!IsAtEnd)
            {
                var c = Peek();
                if (char.IsWhiteSpace(c) || c == ':' || c == '}' || c == ',')
                {
                    break;
                }

                Advance();
            }

            return _text.Substring(start, _index - start);
        }

        private NbtTag ParseScalar()
        {
            var token = ReadToken();
            if (token.Length == 0)
            {
                throw new FormatException("Unexpected empty token in SNBT payload.");
            }

            if (string.Equals(token, "true", StringComparison.OrdinalIgnoreCase))
            {
                return new NbtByte(1);
            }

            if (string.Equals(token, "false", StringComparison.OrdinalIgnoreCase))
            {
                return new NbtByte(0);
            }

            char suffix = char.ToLowerInvariant(token[^1]);
            string numberPart = token;

            try
            {
                switch (suffix)
                {
                    case 'b':
                        numberPart = token[..^1];
                        if (sbyte.TryParse(numberPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var byteValue))
                        {
                            return new NbtByte(byteValue);
                        }
                        break;
                    case 's':
                        numberPart = token[..^1];
                        if (short.TryParse(numberPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var shortValue))
                        {
                            return new NbtShort(shortValue);
                        }
                        break;
                    case 'l':
                        numberPart = token[..^1];
                        if (long.TryParse(numberPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                        {
                            return new NbtLong(longValue);
                        }
                        break;
                    case 'f':
                        numberPart = token[..^1];
                        if (float.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var floatValue))
                        {
                            return new NbtFloat(floatValue);
                        }
                        break;
                    case 'd':
                        numberPart = token[..^1];
                        if (double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
                        {
                            return new NbtDouble(doubleValue);
                        }
                        break;
                }

                if (suffix == 'b' || suffix == 's' || suffix == 'l' || suffix == 'f' || suffix == 'd')
                {
                    return new NbtString(token);
                }

                if (token.Contains('.') || token.Contains('e', StringComparison.OrdinalIgnoreCase))
                {
                    if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleDefault))
                    {
                        return new NbtDouble(doubleDefault);
                    }
                }
                else if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                {
                    return new NbtInt(intValue);
                }
                else if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fallbackLong))
                {
                    return new NbtLong(fallbackLong);
                }
            }
            catch (FormatException)
            {
                // Fallback to string literal when parsing fails.
            }

            return new NbtString(token);
        }

        private string ReadToken()
        {
            var start = _index;
            while (!IsAtEnd)
            {
                var c = Peek();
                if (char.IsWhiteSpace(c) || c == ',' || c == ']' || c == '}' || c == ':')
                {
                    break;
                }

                Advance();
            }

            return _text.Substring(start, _index - start);
        }

        private bool Match(char expected)
        {
            if (IsAtEnd || Peek() != expected)
            {
                return false;
            }

            _index++;
            return true;
        }

        private void Expect(char expected)
        {
            if (!Match(expected))
            {
                throw new FormatException($"Expected '{expected}' in SNBT payload.");
            }
        }

        private char Peek()
        {
            if (IsAtEnd)
            {
                return '\0';
            }

            return _text[_index];
        }

        private char LookAhead(int offset)
        {
            var position = _index + offset;
            if (position >= _text.Length)
            {
                return '\0';
            }

            return _text[position];
        }

        private char Advance()
        {
            if (IsAtEnd)
            {
                return '\0';
            }

            return _text[_index++];
        }

        private void SkipWhitespace()
        {
            while (!IsAtEnd && char.IsWhiteSpace(Peek()))
            {
                _index++;
            }
        }

        private bool IsAtEnd => _index >= _text.Length;
    }
}

internal static class StreamExtensions
{
    public static void ReadExactly(this Stream stream, Span<byte> buffer)
    {
        var remaining = buffer.Length;
        while (remaining > 0)
        {
            var slice = buffer.Slice(buffer.Length - remaining);
            var read = stream.Read(slice);
            if (read <= 0)
            {
                throw new EndOfStreamException("Unexpected end of stream while reading NBT payload.");
            }

            remaining -= read;
        }
    }

    public static byte ReadByteChecked(this Stream stream)
    {
        var value = stream.ReadByte();
        if (value < 0)
        {
            throw new EndOfStreamException("Unexpected end of stream while reading NBT payload.");
        }

        return (byte)value;
    }
}
