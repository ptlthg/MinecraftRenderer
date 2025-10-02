namespace MinecraftRenderer.Nbt;

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System;

public enum NbtTagType : byte
{
    End = 0,
    Byte = 1,
    Short = 2,
    Int = 3,
    Long = 4,
    Float = 5,
    Double = 6,
    ByteArray = 7,
    String = 8,
    List = 9,
    Compound = 10,
    IntArray = 11,
    LongArray = 12
}

public abstract class NbtTag
{
    public abstract NbtTagType Type { get; }
}

public sealed class NbtByte : NbtTag
{
    public NbtByte(sbyte value) => Value = value;
    public override NbtTagType Type => NbtTagType.Byte;
    public sbyte Value { get; }
}

public sealed class NbtShort : NbtTag
{
    public NbtShort(short value) => Value = value;
    public override NbtTagType Type => NbtTagType.Short;
    public short Value { get; }
}

public sealed class NbtInt : NbtTag
{
    public NbtInt(int value) => Value = value;
    public override NbtTagType Type => NbtTagType.Int;
    public int Value { get; }
}

public sealed class NbtLong : NbtTag
{
    public NbtLong(long value) => Value = value;
    public override NbtTagType Type => NbtTagType.Long;
    public long Value { get; }
}

public sealed class NbtFloat : NbtTag
{
    public NbtFloat(float value) => Value = value;
    public override NbtTagType Type => NbtTagType.Float;
    public float Value { get; }
}

public sealed class NbtDouble : NbtTag
{
    public NbtDouble(double value) => Value = value;
    public override NbtTagType Type => NbtTagType.Double;
    public double Value { get; }
}

public sealed class NbtString : NbtTag
{
    public NbtString(string value) => Value = value;
    public override NbtTagType Type => NbtTagType.String;
    public string Value { get; }
}

public sealed class NbtByteArray : NbtTag
{
    public NbtByteArray(byte[] values) => Values = values;
    public override NbtTagType Type => NbtTagType.ByteArray;
    public byte[] Values { get; }
}

public sealed class NbtIntArray : NbtTag
{
    public NbtIntArray(int[] values) => Values = values;
    public override NbtTagType Type => NbtTagType.IntArray;
    public int[] Values { get; }
}

public sealed class NbtLongArray : NbtTag
{
    public NbtLongArray(long[] values) => Values = values;
    public override NbtTagType Type => NbtTagType.LongArray;
    public long[] Values { get; }
}

public sealed class NbtList : NbtTag, IReadOnlyList<NbtTag>
{
    private readonly List<NbtTag> _items;

    public NbtList(NbtTagType elementType, IEnumerable<NbtTag> items)
    {
        ElementType = elementType;
        _items = items.ToList();
    }

    public override NbtTagType Type => NbtTagType.List;
    public NbtTagType ElementType { get; }

    public NbtTag this[int index] => _items[index];

    public int Count => _items.Count;

    public IEnumerator<NbtTag> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class NbtCompound : NbtTag, IReadOnlyDictionary<string, NbtTag>
{
    private readonly Dictionary<string, NbtTag> _items;

    public NbtCompound(IEnumerable<KeyValuePair<string, NbtTag>> values)
    {
        _items = new Dictionary<string, NbtTag>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            if (!string.IsNullOrWhiteSpace(key) && value is not null)
            {
                _items[key] = value;
            }
        }
    }

    public override NbtTagType Type => NbtTagType.Compound;

    public NbtTag this[string key] => _items[key];

    public IEnumerable<string> Keys => _items.Keys;

    public IEnumerable<NbtTag> Values => _items.Values;

    public int Count => _items.Count;

    public bool ContainsKey(string key) => _items.ContainsKey(key);

    public bool TryGetValue(string key, out NbtTag value) => _items.TryGetValue(key, out value!);

    public IEnumerator<KeyValuePair<string, NbtTag>> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class NbtDocument
{
    public NbtDocument(NbtTag root)
    {
        Root = root switch
        {
            NbtCompound compound => compound,
            _ => root
        };
    }

    public NbtTag Root { get; }

    public NbtCompound? RootCompound => Root as NbtCompound;
}
