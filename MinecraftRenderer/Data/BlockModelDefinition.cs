namespace MinecraftRenderer;

using System.Text.Json.Serialization;

public sealed class BlockModelDefinition
{
	[JsonPropertyName("parent")] public string? Parent { get; init; }

	[JsonPropertyName("textures")] public Dictionary<string, string>? Textures { get; init; }

	[JsonPropertyName("display")] public Dictionary<string, TransformDefinition>? Display { get; init; }

	[JsonPropertyName("elements")] public List<ElementDefinition>? Elements { get; init; }

	[JsonPropertyName("gui_light")] public string? GuiLight { get; init; }

	[JsonPropertyName("ambientocclusion")] public bool? AmbientOcclusion { get; init; }
}

public sealed class TransformDefinition
{
	[JsonPropertyName("rotation")] public float[]? Rotation { get; init; }

	[JsonPropertyName("translation")] public float[]? Translation { get; init; }

	[JsonPropertyName("scale")] public float[]? Scale { get; init; }
}

public sealed class ElementDefinition
{
	[JsonPropertyName("from")] public float[]? From { get; init; }

	[JsonPropertyName("to")] public float[]? To { get; init; }

	[JsonPropertyName("rotation")] public ElementRotationDefinition? Rotation { get; init; }

	[JsonPropertyName("faces")] public Dictionary<string, FaceDefinition>? Faces { get; init; }

	[JsonPropertyName("shade")] public bool? Shade { get; init; }
}

public sealed class ElementRotationDefinition
{
	[JsonPropertyName("angle")] public float Angle { get; init; }

	[JsonPropertyName("axis")] public string Axis { get; init; } = "y";

	[JsonPropertyName("origin")] public float[]? Origin { get; init; }

	[JsonPropertyName("rescale")] public bool? Rescale { get; init; }
}

public sealed class FaceDefinition
{
	[JsonPropertyName("uv")] public float[]? Uv { get; init; }

	[JsonPropertyName("texture")] public string Texture { get; init; } = string.Empty;

	[JsonPropertyName("rotation")] public int? Rotation { get; init; }

	[JsonPropertyName("tintindex")] public int? TintIndex { get; init; }

	[JsonPropertyName("cullface")] public string? CullFace { get; init; }
}