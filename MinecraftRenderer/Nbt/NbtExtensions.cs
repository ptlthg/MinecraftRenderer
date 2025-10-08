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
	
	/// <summary>
	/// Create a new NbtCompound with a profile component added.
	/// This creates the minecraft:profile component structure expected by the skull rendering pipeline.
	/// </summary>
	/// <param name="compound">The root NbtCompound (should contain or will contain a "components" compound).</param>
	/// <param name="profileValue">The base64-encoded texture profile value (e.g., from NEU repo or Minecraft API).</param>
	/// <param name="signature">Optional signature for the texture (usually not needed for custom items).</param>
	/// <returns>A new NbtCompound with the profile component added.</returns>
	/// <example>
	/// <code>
	/// var root = new NbtCompound(new[]
	/// {
	///     new KeyValuePair&lt;string, NbtTag&gt;("id", new NbtString("minecraft:player_head")),
	///     new KeyValuePair&lt;string, NbtTag&gt;("count", new NbtInt(1))
	/// });
	/// 
	/// var withProfile = root.WithProfileComponent("ewogICJ0aW1lc3RhbXAiIDogMTYzMzQ2NzI4MiwKICAicHJvZmlsZUlkIiA6ICI0MTNkMTdkMzMyODQ0OTYwYTExNWU2ZjYzNmE0ZDcyYyIsCiAgInByb2ZpbGVOYW1lIiA6ICJNaW5lY3JhZnRTa2luIiwKICAidGV4dHVyZXMiIDogewogICAgIlNLSU4iIDogewogICAgICAidXJsIiA6ICJodHRwOi8vdGV4dHVyZXMubWluZWNyYWZ0Lm5ldC90ZXh0dXJlLzE0ZjZhYjdkMWQyOGJkZTY1OTZiZjdkNGU5ZjlmMGI0ZjFlNWY5MTdkNTI1MjQ0ODJlZWM4ODFlYWM4YTZjNTEiCiAgICB9CiAgfQp9");
	/// </code>
	/// </example>
	public static NbtCompound WithProfileComponent(this NbtCompound compound, string profileValue, string? signature = null)
	{
		ArgumentNullException.ThrowIfNull(compound);
		ArgumentException.ThrowIfNullOrWhiteSpace(profileValue);

		// Build the property compound
		var propertyEntries = new List<KeyValuePair<string, NbtTag>>
		{
			new("name", new NbtString("textures")),
			new("value", new NbtString(profileValue))
		};

		if (!string.IsNullOrWhiteSpace(signature))
		{
			propertyEntries.Add(new KeyValuePair<string, NbtTag>("signature", new NbtString(signature)));
		}

		var propertyCompound = new NbtCompound(propertyEntries);

		// Build the properties list
		var propertiesList = new NbtList(NbtTagType.Compound, [propertyCompound]);

		// Build the profile compound
		var profileCompound = new NbtCompound([
			new KeyValuePair<string, NbtTag>("properties", propertiesList)
		]);

		// Get or create components compound
		NbtCompound components;
		IEnumerable<KeyValuePair<string, NbtTag>> otherRootEntries;

		if (compound.TryGetValue("components", out var componentsTag) && componentsTag is NbtCompound existingComponents)
		{
			// Components exist, add profile to them
			components = new NbtCompound(existingComponents.Concat([
				new KeyValuePair<string, NbtTag>("minecraft:profile", profileCompound)
			]));
			otherRootEntries = compound.Where(kvp => kvp.Key != "components");
		}
		else
		{
			// No components, create new one with just the profile
			components = new NbtCompound([
				new KeyValuePair<string, NbtTag>("minecraft:profile", profileCompound)
			]);
			otherRootEntries = compound;
		}

		// Build the new root compound
		return new NbtCompound(otherRootEntries.Concat([
			new KeyValuePair<string, NbtTag>("components", components)
		]));
	}
}