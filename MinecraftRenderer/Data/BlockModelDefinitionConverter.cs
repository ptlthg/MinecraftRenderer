namespace MinecraftRenderer;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Accepts legacy texture strings and modern sprite material objects.</summary>
public sealed class BlockModelDefinitionConverter : JsonConverter<BlockModelDefinition>
{
    public override BlockModelDefinition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var fields = JsonSerializer.Deserialize<ModelFields>(ref reader, options)
            ?? throw new JsonException("Block model must be an object");
        Dictionary<string, string>? textures = null;
        var translucent = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (fields.Textures is not null)
        {
            textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in fields.Textures)
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    textures[key] = value.GetString()!;
                }
                else if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("sprite", out var sprite)
                         && sprite.ValueKind == JsonValueKind.String)
                {
                    textures[key] = sprite.GetString()!;
                    if (value.TryGetProperty("force_translucent", out var force))
                    {
                        if (force.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            throw new JsonException($"Texture '{key}' force_translucent must be a boolean");
                        translucent[key] = force.GetBoolean();
                    }
                }
                else throw new JsonException($"Texture '{key}' must be a string or an object with a string sprite");
            }
        }
        return new BlockModelDefinition {
            Parent = fields.Parent, Textures = textures, Display = fields.Display,
            Elements = fields.Elements, GuiLight = fields.GuiLight, AmbientOcclusion = fields.AmbientOcclusion,
            ForceTranslucentTextures = translucent
        };
    }

    public override void Write(Utf8JsonWriter writer, BlockModelDefinition value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteField(writer, "parent", value.Parent, options);
        if (value.Textures is not null)
        {
            writer.WritePropertyName("textures");
            writer.WriteStartObject();
            foreach (var (key, sprite) in value.Textures)
            {
                if (value.ForceTranslucentTextures.GetValueOrDefault(key))
                {
                    writer.WritePropertyName(key);
                    writer.WriteStartObject();
                    writer.WriteString("sprite", sprite);
                    writer.WriteBoolean("force_translucent", true);
                    writer.WriteEndObject();
                }
                else writer.WriteString(key, sprite);
            }
            writer.WriteEndObject();
        }
        WriteField(writer, "display", value.Display, options);
        WriteField(writer, "elements", value.Elements, options);
        WriteField(writer, "gui_light", value.GuiLight, options);
        WriteField(writer, "ambientocclusion", value.AmbientOcclusion, options);
        writer.WriteEndObject();
    }

    private static void WriteField<T>(Utf8JsonWriter writer, string name, T value, JsonSerializerOptions options)
    {
        if (value is null) return;
        writer.WritePropertyName(name);
        JsonSerializer.Serialize(writer, value, options);
    }

    private sealed class ModelFields
    {
        public ModelFields() {}
        [JsonPropertyName("parent")] public string? Parent { get; init; }
        [JsonPropertyName("textures")] public Dictionary<string, JsonElement>? Textures { get; init; }
        [JsonPropertyName("display")] public Dictionary<string, TransformDefinition>? Display { get; init; }
        [JsonPropertyName("elements")] public List<ElementDefinition>? Elements { get; init; }
        [JsonPropertyName("gui_light")] public string? GuiLight { get; init; }
        [JsonPropertyName("ambientocclusion")] public bool? AmbientOcclusion { get; init; }
    }
}
