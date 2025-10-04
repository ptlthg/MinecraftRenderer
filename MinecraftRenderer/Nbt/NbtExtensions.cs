using System.Diagnostics.CodeAnalysis;

namespace MinecraftRenderer.Nbt;

/// <summary>
/// Extension methods for convenient NBT data access.
/// </summary>
public static class NbtExtensions
{
	/// <summary>
	/// Get a string value from an NbtCompound by key, or null if not found or wrong type.
	/// </summary>
	/// <param name="compound"></param>
	/// <param name="key"></param>
	/// <returns></returns>
	public static string? GetString(this NbtCompound compound, string key)
	{
		if (compound.TryGetValue(key, out var tag) && tag is NbtString str)
		{
			return str.Value;
		}

		return null;
	}

	/// <summary>
	/// Get a byte value from an NbtCompound by key, or null if not found or wrong type.
	/// </summary>
	/// <param name="compound"></param>
	/// <param name="key"></param>
	/// <returns></returns>
	public static byte? GetByte(this NbtCompound compound, string key)
	{
		if (compound.TryGetValue(key, out var tag) && tag is NbtByte b)
		{
			return (byte)b.Value;
		}

		return null;
	}

	/// <summary>
	/// Get a short value from an NbtCompound by key, or null if not found or wrong type.
	/// </summary>
	/// <param name="compound"></param>
	/// <param name="key"></param>
	/// <returns></returns>
	public static short? GetShort(this NbtCompound compound, string key)
	{
		if (compound.TryGetValue(key, out var tag) && tag is NbtShort s)
		{
			return s.Value;
		}

		return null;
	}

	/// <summary>
	/// Get an int value from an NbtCompound by key, or null if not found or wrong type.
	/// </summary>
	/// <param name="compound"></param>
	/// <param name="key"></param>
	/// <returns></returns>
	public static int? GetInt(this NbtCompound compound, string key)
	{
		if (compound.TryGetValue(key, out var tag) && tag is NbtInt i)
		{
			return i.Value;
		}

		return null;
	}

	/// <summary>
	/// Get a long value from an NbtCompound by key, or null if not found or wrong type.
	/// </summary>
	/// <param name="compound"></param>
	/// <param name="key"></param>
	/// <returns></returns>
	public static long? GetLong(this NbtCompound compound, string key)
	{
		if (compound.TryGetValue(key, out var tag) && tag is NbtLong l)
		{
			return l.Value;
		}

		return null;
	}

	/// <summary>
	/// Get a float value from an NbtCompound by key, or null if not found or wrong type.
	/// </summary>
	/// <param name="compound"></param>
	/// <param name="key"></param>
	/// <returns></returns>
	public static float? GetFloat(this NbtCompound compound, string key)
	{
		if (compound.TryGetValue(key, out var tag) && tag is NbtFloat f)
		{
			return f.Value;
		}

		return null;
	}

	/// <summary>
	/// Get a double value from an NbtCompound by key, or null if not found or wrong type.
	/// </summary>
	/// <param name="compound"></param>
	/// <param name="key"></param>
	/// <returns></returns>
	public static double? GetDouble(this NbtCompound compound, string key)
	{
		if (compound.TryGetValue(key, out var tag) && tag is NbtDouble d)
		{
			return d.Value;
		}

		return null;
	}

	/// <summary>
	/// Get a nested NbtCompound by key, or null if not found or wrong type.
	/// </summary>
	/// <param name="compound"></param>
	/// <param name="key"></param>
	/// <returns></returns>
	public static NbtCompound? GetCompound(this NbtCompound compound, string key)
	{
		if (compound.TryGetValue(key, out var tag) && tag is NbtCompound c)
		{
			return c;
		}

		return null;
	}

	/// <summary>
	/// Get a nested NbtList by key, or null if not found or wrong type.
	/// </summary>
	/// <param name="compound"></param>
	/// <param name="key"></param>
	/// <returns></returns>
	public static NbtList? GetList(this NbtCompound compound, string key)
	{
		if (compound.TryGetValue(key, out var tag) && tag is NbtList list)
		{
			return list;
		}

		return null;
	}

	/// <summary>
	/// Get a byte array from an NbtCompound by key, or null if not found or wrong type.
	/// </summary>
	/// <param name="compound"></param>
	/// <param name="key"></param>
	/// <returns></returns>
	public static byte[]? GetByteArray(this NbtCompound compound, string key)
	{
		if (compound.TryGetValue(key, out var tag) && tag is NbtByteArray arr)
		{
			return arr.Values;
		}

		return null;
	}

	/// <summary>
	/// Get an int array from an NbtCompound by key, or null if not found or wrong type.
	/// </summary>
	/// <param name="compound"></param>
	/// <param name="key"></param>
	/// <returns></returns>
	public static int[]? GetIntArray(this NbtCompound compound, string key)
	{
		if (compound.TryGetValue(key, out var tag) && tag is NbtIntArray arr)
		{
			return arr.Values;
		}

		return null;
	}

	/// <summary>
	/// Get a long array from an NbtCompound by key, or null if not found or wrong type.
	/// </summary>
	/// <param name="compound"></param>
	/// <param name="key"></param>
	/// <returns></returns>
	public static long[]? GetLongArray(this NbtCompound compound, string key)
	{
		if (compound.TryGetValue(key, out var tag) && tag is NbtLongArray arr)
		{
			return arr.Values;
		}

		return null;
	}
}