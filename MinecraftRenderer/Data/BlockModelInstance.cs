namespace MinecraftRenderer;

using System.Numerics;

public enum BlockFaceDirection
{
	North,
	South,
	East,
	West,
	Up,
	Down
}

public sealed class BlockModelInstance
{
	public BlockModelInstance(
		string name,
		IReadOnlyList<string> parentChain,
		IReadOnlyDictionary<string, string> textures,
		IReadOnlyDictionary<string, TransformDefinition> display,
		IReadOnlyList<ModelElement> elements)
	{
		Name = name;
		ParentChain = parentChain;
		Textures = textures;
		Display = display;
		Elements = elements;
	}

	public string Name { get; }

	public IReadOnlyList<string> ParentChain { get; }

	public IReadOnlyDictionary<string, string> Textures { get; }

	public IReadOnlyDictionary<string, TransformDefinition> Display { get; }

	public IReadOnlyList<ModelElement> Elements { get; }

	public TransformDefinition? GetDisplayTransform(string name)
		=> Display.TryGetValue(name, out var transform) ? transform : null;
}

public sealed class ModelElement
{
	public ModelElement(Vector3 from, Vector3 to, ElementRotation? rotation, IReadOnlyDictionary<BlockFaceDirection, ModelFace> faces, bool shade)
	{
		From = from;
		To = to;
		Rotation = rotation;
		Faces = faces;
		Shade = shade;
	}

	public Vector3 From { get; }
	public Vector3 To { get; }
	public ElementRotation? Rotation { get; }
	public IReadOnlyDictionary<BlockFaceDirection, ModelFace> Faces { get; }
	public bool Shade { get; }
}

public sealed class ElementRotation
{
	public ElementRotation(float angleInDegrees, Vector3 origin, string axis, bool rescale)
	{
		AngleInDegrees = angleInDegrees;
		Origin = origin;
		Axis = axis;
		Rescale = rescale;
	}

	public float AngleInDegrees { get; }
	public Vector3 Origin { get; }
	public string Axis { get; }
	public bool Rescale { get; }
}

public sealed class ModelFace
{
	public ModelFace(string texture, Vector4? uv, int? rotation, int? tintIndex, string? cullFace)
	{
		Texture = texture;
		Uv = uv;
		Rotation = rotation;
		TintIndex = tintIndex;
		CullFace = cullFace;
	}

	public string Texture { get; }
	public Vector4? Uv { get; }
	public int? Rotation { get; }
	public int? TintIndex { get; }
	public string? CullFace { get; }
}
