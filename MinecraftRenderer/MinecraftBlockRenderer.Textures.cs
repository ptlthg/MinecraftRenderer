namespace MinecraftRenderer;

using System;
using System.Collections.Generic;

public sealed partial class MinecraftBlockRenderer
{
	private static string ResolveTexture(string texture, BlockModelInstance? model)
	{
		if (string.IsNullOrWhiteSpace(texture))
		{
			return "minecraft:missingno";
		}

		if (!texture.StartsWith('#'))
		{
			return texture;
		}

		if (model is null)
		{
			return "minecraft:missingno";
		}

		var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var current = texture;

		while (current.StartsWith('#'))
		{
			var key = current[1..];
			if (!visited.Add(key))
			{
				return "minecraft:missingno";
			}

			if (!model.Textures.TryGetValue(key, out var mapped) || string.IsNullOrWhiteSpace(mapped))
			{
				return "minecraft:missingno";
			}

			current = mapped;
		}

		return current;
	}
}
