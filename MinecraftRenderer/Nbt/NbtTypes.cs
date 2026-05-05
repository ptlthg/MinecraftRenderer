namespace MinecraftRenderer.Nbt;

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System;

/// <summary>
/// NBT tag types as per the NBT specification.
/// </summary>
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
	private static readonly NbtByte[] Cache = CreateCache();

	private static NbtByte[] CreateCache() {
		var cache = new NbtByte[256];
		for (var i = 0; i < 256; i++) {
			cache[i] = new NbtByte((sbyte)(i - 128));
		}
		return cache;
	}

	public NbtByte(sbyte value) => Value = value;
	public override NbtTagType Type => NbtTagType.Byte;
	public sbyte Value { get; }

	/// <summary>Returns a cached <see cref="NbtByte"/> instance for the given value (avoids allocation).</summary>
	internal static NbtByte GetCached(sbyte value) => Cache[(byte)(value + 128)];
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
	internal static readonly NbtString Empty = new(string.Empty);

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
	internal static readonly NbtList EmptyEnd = new(NbtTagType.End, new List<NbtTag>(0));

	private readonly List<NbtTag> _items;

	public NbtList(NbtTagType elementType, IEnumerable<NbtTag> items) {
		ElementType = elementType;
		_items = items as List<NbtTag> ?? items.ToList();
	}

	/// <summary>
	/// Internal ownership constructor: takes the supplied <paramref name="items"/> list directly without copying.
	/// The caller must not mutate the list afterwards.
	/// </summary>
	internal NbtList(NbtTagType elementType, List<NbtTag> items, bool takeOwnership) {
		_ = takeOwnership;
		ElementType = elementType;
		_items = items;
	}

	public override NbtTagType Type => NbtTagType.List;
	public NbtTagType ElementType { get; }

	public NbtTag this[int index] => _items[index];

	public int Count => _items.Count;

	public List<NbtTag>.Enumerator GetEnumerator() => _items.GetEnumerator();

	IEnumerator<NbtTag> IEnumerable<NbtTag>.GetEnumerator() => _items.GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class NbtCompound : NbtTag, IReadOnlyDictionary<string, NbtTag>
{
	internal static readonly NbtCompound Empty = new(Array.Empty<string>(), Array.Empty<NbtTag>(), 0);

	// Flat parallel arrays. Compounds are typically small (<16 entries); linear scan beats hashing.
	// Capacity may exceed Count; the trailing slots are unused but kept to avoid trim allocations on the hot parser path.
	private readonly string[] _keys;
	private readonly NbtTag[] _values;
	private readonly int _count;

	public NbtCompound(IEnumerable<KeyValuePair<string, NbtTag>> values) {
		ArgumentNullException.ThrowIfNull(values);
		var initialCapacity = values is ICollection<KeyValuePair<string, NbtTag>> c ? c.Count : 4;
		if (initialCapacity == 0) {
			_keys = Array.Empty<string>();
			_values = Array.Empty<NbtTag>();
			_count = 0;
			return;
		}

		var keys = new string[initialCapacity];
		var vals = new NbtTag[initialCapacity];
		var count = 0;
		foreach (var kvp in values) {
			if (kvp.Key is not { Length: > 0 }) {
				continue;
			}

			var existing = IndexOfKey(keys, count, kvp.Key);
			if (existing >= 0) {
				vals[existing] = kvp.Value;
				continue;
			}

			if (count == keys.Length) {
				var newCapacity = keys.Length * 2;
				Array.Resize(ref keys, newCapacity);
				Array.Resize(ref vals, newCapacity);
			}

			keys[count] = kvp.Key;
			vals[count] = kvp.Value;
			count++;
		}

		_keys = keys;
		_values = vals;
		_count = count;
	}

	/// <summary>
	/// Internal ownership constructor: takes the supplied parallel arrays directly without copying.
	/// The caller must guarantee uniqueness of the first <paramref name="count"/> keys and must not mutate the arrays afterwards.
	/// </summary>
	internal NbtCompound(string[] keys, NbtTag[] values, int count) {
		_keys = keys;
		_values = values;
		_count = count;
	}

	public override NbtTagType Type => NbtTagType.Compound;

	public NbtTag this[string key] {
		get {
			var index = IndexOfKey(_keys, _count, key);
			if (index < 0) {
				throw new KeyNotFoundException(key);
			}
			return _values[index];
		}
	}

	public IEnumerable<string> Keys {
		get {
			for (var i = 0; i < _count; i++) {
				yield return _keys[i];
			}
		}
	}

	public IEnumerable<NbtTag> Values {
		get {
			for (var i = 0; i < _count; i++) {
				yield return _values[i];
			}
		}
	}

	public int Count => _count;

	public bool ContainsKey(string key) => IndexOfKey(_keys, _count, key) >= 0;

	public bool TryGetValue(string key, out NbtTag value) {
		var index = IndexOfKey(_keys, _count, key);
		if (index < 0) {
			value = null!;
			return false;
		}
		value = _values[index];
		return true;
	}

	public Enumerator GetEnumerator() => new(this);

	IEnumerator<KeyValuePair<string, NbtTag>> IEnumerable<KeyValuePair<string, NbtTag>>.GetEnumerator() => GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	private static int IndexOfKey(string[] keys, int count, string key) {
		// Reference-equality fast path for interned literal keys (very common in pattern-match consumers).
		for (var i = 0; i < count; i++) {
			if (ReferenceEquals(keys[i], key)) {
				return i;
			}
		}
		for (var i = 0; i < count; i++) {
			if (string.Equals(keys[i], key, StringComparison.Ordinal)) {
				return i;
			}
		}
		return -1;
	}

	public struct Enumerator : IEnumerator<KeyValuePair<string, NbtTag>>
	{
		private readonly NbtCompound _compound;
		private int _index;

		internal Enumerator(NbtCompound compound) {
			_compound = compound;
			_index = -1;
		}

		public KeyValuePair<string, NbtTag> Current => new(_compound._keys[_index], _compound._values[_index]);

		object IEnumerator.Current => Current;

		public bool MoveNext() => ++_index < _compound._count;

		public void Reset() => _index = -1;

		public void Dispose() { }
	}
}

public sealed class NbtDocument
{
	public NbtDocument(NbtTag root) {
		Root = root switch {
			NbtCompound compound => compound,
			_ => root
		};
	}

	public NbtTag Root { get; }

	public NbtCompound? RootCompound => Root as NbtCompound;
}