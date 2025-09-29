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

		if (model is null)
		{
			return texture.StartsWith('#') ? "minecraft:missingno" : texture;
		}

		static string ExpandTextureReference(string candidate, BlockModelInstance instance)
		{
			if (string.IsNullOrWhiteSpace(candidate))
			{
				return string.Empty;
			}

			var trimmed = candidate.Trim();
			if (trimmed.StartsWith('#'))
			{
				return trimmed;
			}

			if (instance.Textures.TryGetValue(trimmed, out _))
			{
				return "#" + trimmed;
			}

			return trimmed;
		}

		var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var current = ExpandTextureReference(texture, model);

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

			current = ExpandTextureReference(mapped, model);
			if (string.IsNullOrWhiteSpace(current))
			{
				return "minecraft:missingno";
			}
		}

		return current;
	}
}
