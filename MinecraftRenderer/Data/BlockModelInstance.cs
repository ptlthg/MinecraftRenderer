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

public sealed class BlockModelInstance(
	string name,
	IReadOnlyList<string> parentChain,
	IReadOnlyDictionary<string, string> textures,
	IReadOnlyDictionary<string, TransformDefinition> display,
	IReadOnlyList<ModelElement> elements)
{
	public string Name { get; } = name;

	public IReadOnlyList<string> ParentChain { get; } = parentChain;

	public IReadOnlyDictionary<string, string> Textures { get; } = textures;

	public IReadOnlyDictionary<string, TransformDefinition> Display { get; } = display;

	public IReadOnlyList<ModelElement> Elements { get; } = elements;

	public TransformDefinition? GetDisplayTransform(string name)
		=> Display.GetValueOrDefault(name);
}

public sealed class ModelElement(
	Vector3 from,
	Vector3 to,
	ElementRotation? rotation,
	IReadOnlyDictionary<BlockFaceDirection, ModelFace> faces,
	bool shade)
{
	public Vector3 From { get; } = from;
	public Vector3 To { get; } = to;
	public ElementRotation? Rotation { get; } = rotation;
	public IReadOnlyDictionary<BlockFaceDirection, ModelFace> Faces { get; } = faces;
	public bool Shade { get; } = shade;
}

public sealed class ElementRotation(float angleInDegrees, Vector3 origin, string axis, bool rescale)
{
	public float AngleInDegrees { get; } = angleInDegrees;
	public Vector3 Origin { get; } = origin;
	public string Axis { get; } = axis;
	public bool Rescale { get; } = rescale;
}

public sealed class ModelFace(string texture, Vector4? uv, int? rotation, int? tintIndex, string? cullFace)
{
	public string Texture { get; } = texture;
	public Vector4? Uv { get; } = uv;
	public int? Rotation { get; } = rotation;
	public int? TintIndex { get; } = tintIndex;
	public string? CullFace { get; } = cullFace;
}