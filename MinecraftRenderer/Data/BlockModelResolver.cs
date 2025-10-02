namespace MinecraftRenderer;

using System.Linq;
using System.Numerics;
using System.Text.Json;
using MinecraftRenderer.Assets;

public sealed class BlockModelResolver
{
	private readonly Dictionary<string, BlockModelDefinition> _definitions;
	private readonly Dictionary<string, BlockModelInstance> _cache = new(StringComparer.OrdinalIgnoreCase);

	private BlockModelResolver(Dictionary<string, BlockModelDefinition> definitions)
	{
		_definitions = definitions;
	}

	internal IReadOnlyDictionary<string, BlockModelDefinition> Definitions => _definitions;

	public static BlockModelResolver LoadFromFile(string path)
	{
		if (!File.Exists(path))
		{
			throw new FileNotFoundException("Model definition file not found", path);
		}

		var json = File.ReadAllText(path);
		var options = new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true,
			ReadCommentHandling = JsonCommentHandling.Skip
		};

		var definitions = JsonSerializer.Deserialize<Dictionary<string, BlockModelDefinition>>(json, options)
		                  ?? throw new InvalidOperationException(
			                  $"Failed to parse block model definitions from '{path}'.");

		return new BlockModelResolver(
			new Dictionary<string, BlockModelDefinition>(definitions, StringComparer.OrdinalIgnoreCase));
	}

	public static BlockModelResolver LoadFromMinecraftAssets(string assetsRoot,
		IEnumerable<string>? overlayRoots = null, AssetNamespaceRegistry? assetNamespaces = null)
	{
		var definitions = MinecraftAssetLoader.LoadModelDefinitions(assetsRoot, overlayRoots, assetNamespaces);
		return new BlockModelResolver(
			new Dictionary<string, BlockModelDefinition>(definitions, StringComparer.OrdinalIgnoreCase));
	}

	public BlockModelInstance Resolve(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			throw new ArgumentException("Model name cannot be null or whitespace", nameof(name));
		}

		var normalized = NormalizeName(name);

		if (_cache.TryGetValue(normalized, out var cached))
		{
			return cached;
		}

		var instance = ResolveInternal(normalized, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
		_cache[normalized] = instance;
		return instance;
	}

	private BlockModelInstance ResolveInternal(string name, HashSet<string> stack)
	{
		if (stack.Contains(name))
		{
			throw new InvalidOperationException($"Detected circular model inheritance involving '{name}'.");
		}

		if (!_definitions.TryGetValue(name, out var definition))
		{
			throw new KeyNotFoundException($"Model '{name}' was not found in the loaded definitions.");
		}

		stack.Add(name);

		var parentChain = new List<string>();
		var textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var display = new Dictionary<string, TransformDefinition>(StringComparer.OrdinalIgnoreCase);
		List<ModelElement>? elements = null;

		if (!string.IsNullOrWhiteSpace(definition.Parent))
		{
			var parent = ResolveInternal(NormalizeName(definition.Parent!), stack);
			parentChain.AddRange(parent.ParentChain);
			parentChain.Add(parent.Name);

			foreach (var kvp in parent.Textures)
			{
				textures.TryAdd(kvp.Key, kvp.Value);
			}

			foreach (var kvp in parent.Display)
			{
				display[kvp.Key] = CloneTransform(kvp.Value);
			}

			if (parent.Elements.Count > 0)
			{
				elements = parent.Elements.Select(CloneElement).ToList();
			}
		}

		if (definition.Textures is { Count: > 0 })
		{
			foreach (var kvp in definition.Textures)
			{
				textures[kvp.Key] = kvp.Value;
			}
		}

		if (definition.Display is { Count: > 0 })
		{
			foreach (var kvp in definition.Display)
			{
				display[kvp.Key] = CloneTransform(kvp.Value);
			}
		}

		if (definition.Elements is { Count: > 0 })
		{
			elements = definition.Elements
				.Select(ConvertElement)
				.Where(static element => element is not null)
				.Cast<ModelElement>()
				.ToList();
		}

		stack.Remove(name);

		return new BlockModelInstance(
			name,
			parentChain,
			textures,
			display,
			elements ?? new List<ModelElement>());
	}

	private static TransformDefinition CloneTransform(TransformDefinition source)
	{
		return new TransformDefinition
		{
			Rotation = source.Rotation is null ? null : (float[])source.Rotation.Clone(),
			Translation = source.Translation is null ? null : (float[])source.Translation.Clone(),
			Scale = source.Scale is null ? null : (float[])source.Scale.Clone()
		};
	}

	private static ModelElement CloneElement(ModelElement element)
	{
		var faces = element.Faces.ToDictionary(
			pair => pair.Key,
			pair => new ModelFace(
				pair.Value.Texture,
				pair.Value.Uv,
				pair.Value.Rotation,
				pair.Value.TintIndex,
				pair.Value.CullFace
			),
			EqualityComparer<BlockFaceDirection>.Default);

		return new ModelElement(
			element.From,
			element.To,
			element.Rotation is null
				? null
				: new ElementRotation(
					element.Rotation.AngleInDegrees,
					element.Rotation.Origin,
					element.Rotation.Axis,
					element.Rotation.Rescale
				),
			faces,
			element.Shade
		);
	}

	private static ModelElement? ConvertElement(ElementDefinition definition)
	{
		if (definition.From is not { Length: 3 } from || definition.To is not { Length: 3 } to)
		{
			return null;
		}

		var fromVec = new Vector3(from[0], from[1], from[2]);
		var toVec = new Vector3(to[0], to[1], to[2]);

		ElementRotation? rotation = null;

		if (definition.Rotation is { } rotationDef && rotationDef.Origin is { Length: 3 } origin)
		{
			var originVec = new Vector3(origin[0], origin[1], origin[2]);
			rotation = new ElementRotation(rotationDef.Angle, originVec, rotationDef.Axis.ToLowerInvariant(),
				rotationDef.Rescale ?? false);
		}

		var faces = new Dictionary<BlockFaceDirection, ModelFace>();

		if (definition.Faces is { Count: > 0 })
		{
			foreach (var (key, faceDef) in definition.Faces)
			{
				if (!TryParseFaceDirection(key, out var direction))
				{
					continue;
				}

				Vector4? uvVector = null;
				if (faceDef.Uv is { Length: 4 } uv)
				{
					uvVector = new Vector4(uv[0], uv[1], uv[2], uv[3]);
				}

				faces[direction] = new ModelFace(
					faceDef.Texture,
					uvVector,
					faceDef.Rotation,
					faceDef.TintIndex,
					faceDef.CullFace
				);
			}
		}

		return new ModelElement(fromVec, toVec, rotation, faces, definition.Shade ?? true);
	}

	private static bool TryParseFaceDirection(string key, out BlockFaceDirection direction)
	{
		switch (key.ToLowerInvariant())
		{
			case "north":
				direction = BlockFaceDirection.North;
				return true;
			case "south":
				direction = BlockFaceDirection.South;
				return true;
			case "east":
				direction = BlockFaceDirection.East;
				return true;
			case "west":
				direction = BlockFaceDirection.West;
				return true;
			case "up":
				direction = BlockFaceDirection.Up;
				return true;
			case "down":
				direction = BlockFaceDirection.Down;
				return true;
			default:
				direction = default;
				return false;
		}
	}

	private static string NormalizeName(string name)
	{
		var normalized = name.Trim();

		if (normalized.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[10..];
		}

		if (normalized.StartsWith("block/", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[6..];
		}
		else if (normalized.StartsWith("blocks/", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[7..];
		}

		return normalized;
	}
}