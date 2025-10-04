using System.Diagnostics.CodeAnalysis;

namespace MinecraftRenderer.Nbt;

/// <summary>
/// Extension methods for convenient NBT data access.
/// </summary>
public static class NbtExtensions
{
    public static string? GetString(this NbtCompound compound, string key)
    {
        if (compound.TryGetValue(key, out var tag) && tag is NbtString str)
        {
            return str.Value;
        }
        return null;
    }

    public static byte? GetByte(this NbtCompound compound, string key)
    {
        if (compound.TryGetValue(key, out var tag) && tag is NbtByte b)
        {
            return (byte)b.Value;
        }
        return null;
    }

    public static short? GetShort(this NbtCompound compound, string key)
    {
        if (compound.TryGetValue(key, out var tag) && tag is NbtShort s)
        {
            return s.Value;
        }
        return null;
    }

    public static int? GetInt(this NbtCompound compound, string key)
    {
        if (compound.TryGetValue(key, out var tag) && tag is NbtInt i)
        {
            return i.Value;
        }
        return null;
    }

    public static long? GetLong(this NbtCompound compound, string key)
    {
        if (compound.TryGetValue(key, out var tag) && tag is NbtLong l)
        {
            return l.Value;
        }
        return null;
    }

    public static float? GetFloat(this NbtCompound compound, string key)
    {
        if (compound.TryGetValue(key, out var tag) && tag is NbtFloat f)
        {
            return f.Value;
        }
        return null;
    }

    public static double? GetDouble(this NbtCompound compound, string key)
    {
        if (compound.TryGetValue(key, out var tag) && tag is NbtDouble d)
        {
            return d.Value;
        }
        return null;
    }

    public static NbtCompound? GetCompound(this NbtCompound compound, string key)
    {
        if (compound.TryGetValue(key, out var tag) && tag is NbtCompound c)
        {
            return c;
        }
        return null;
    }

    public static NbtList? GetList(this NbtCompound compound, string key)
    {
        if (compound.TryGetValue(key, out var tag) && tag is NbtList list)
        {
            return list;
        }
        return null;
    }

    public static byte[]? GetByteArray(this NbtCompound compound, string key)
    {
        if (compound.TryGetValue(key, out var tag) && tag is NbtByteArray arr)
        {
            return arr.Values;
        }
        return null;
    }

    public static int[]? GetIntArray(this NbtCompound compound, string key)
    {
        if (compound.TryGetValue(key, out var tag) && tag is NbtIntArray arr)
        {
            return arr.Values;
        }
        return null;
    }

    public static long[]? GetLongArray(this NbtCompound compound, string key)
    {
        if (compound.TryGetValue(key, out var tag) && tag is NbtLongArray arr)
        {
            return arr.Values;
        }
        return null;
    }
}
