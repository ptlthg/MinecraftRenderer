using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using MinecraftRenderer;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

internal static class DebugBlockGenerator
{
	private static readonly IReadOnlyDictionary<BlockFaceDirection, Rgba32> FaceColors = new Dictionary<BlockFaceDirection, Rgba32>
	{
		[BlockFaceDirection.North] = new Rgba32(0xFF, 0x33, 0x33),
		[BlockFaceDirection.South] = new Rgba32(0x33, 0x99, 0xFF),
		[BlockFaceDirection.East] = new Rgba32(0x33, 0xFF, 0x99),
		[BlockFaceDirection.West] = new Rgba32(0x99, 0x33, 0xFF),
		[BlockFaceDirection.Up] = new Rgba32(0xFF, 0xFF, 0x66),
		[BlockFaceDirection.Down] = new Rgba32(0xFF, 0x99, 0x33)
	};

	public static IReadOnlyList<MinecraftAtlasGenerator.AtlasResult> GenerateDebugBlockAtlases(
		MinecraftBlockRenderer renderer,
		string outputDirectory,
		IReadOnlyList<MinecraftAtlasGenerator.AtlasView> views,
		int tileSize)
	{
		ArgumentNullException.ThrowIfNull(renderer);
		ArgumentNullException.ThrowIfNull(outputDirectory);
		ArgumentNullException.ThrowIfNull(views);

		if (views.Count == 0)
		{
			return Array.Empty<MinecraftAtlasGenerator.AtlasResult>();
		}

		Directory.CreateDirectory(outputDirectory);
		var textureOutputDirectory = Path.Combine(outputDirectory, "textures");
		Directory.CreateDirectory(textureOutputDirectory);

		var faceTextureIds = new Dictionary<BlockFaceDirection, string>();
		foreach (var (direction, color) in FaceColors)
		{
			var textureId = $"minecraft:block/debug_cube_{direction.ToString().ToLowerInvariant()}";
			faceTextureIds[direction] = textureId;

			using var image = new Image<Rgba32>(16, 16);
			for (var y = 0; y < image.Height; y++)
			{
				image.DangerousGetPixelRowMemory(y).Span.Fill(color);
			}
			renderer.TextureRepository.RegisterTexture(textureId, image, overwrite: true);

			var textureFileName = $"debug_cube_{direction.ToString().ToLowerInvariant()}.png";
			var texturePath = Path.Combine(textureOutputDirectory, textureFileName);
			image.SaveAsPng(texturePath);
		}

		var textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["particle"] = faceTextureIds[BlockFaceDirection.Up],
			["north"] = faceTextureIds[BlockFaceDirection.North],
			["south"] = faceTextureIds[BlockFaceDirection.South],
			["east"] = faceTextureIds[BlockFaceDirection.East],
			["west"] = faceTextureIds[BlockFaceDirection.West],
			["up"] = faceTextureIds[BlockFaceDirection.Up],
			["down"] = faceTextureIds[BlockFaceDirection.Down]
		};

		var faces = new Dictionary<BlockFaceDirection, ModelFace>
		{
			[BlockFaceDirection.North] = new ModelFace("#north", new Vector4(0f, 0f, 16f, 16f), 0, null, null),
			[BlockFaceDirection.South] = new ModelFace("#south", new Vector4(0f, 0f, 16f, 16f), 0, null, null),
			[BlockFaceDirection.East] = new ModelFace("#east", new Vector4(0f, 0f, 16f, 16f), 0, null, null),
			[BlockFaceDirection.West] = new ModelFace("#west", new Vector4(0f, 0f, 16f, 16f), 0, null, null),
			[BlockFaceDirection.Up] = new ModelFace("#up", new Vector4(0f, 0f, 16f, 16f), 0, null, null),
			[BlockFaceDirection.Down] = new ModelFace("#down", new Vector4(0f, 0f, 16f, 16f), 0, null, null)
		};

		var element = new ModelElement(
			new Vector3(0f, 0f, 0f),
			new Vector3(16f, 16f, 16f),
			null,
			faces,
			shade: true);

		var debugModel = new BlockModelInstance(
			"debug:cube",
			Array.Empty<string>(),
			textures,
			new Dictionary<string, TransformDefinition>(StringComparer.OrdinalIgnoreCase),
			new List<ModelElement> { element });

		var atlasWidth = Math.Max(1, views.Count) * tileSize;
		var atlasHeight = tileSize;

		using var atlas = new Image<Rgba32>(atlasWidth, atlasHeight);
		for (var y = 0; y < atlas.Height; y++)
		{
			atlas.DangerousGetPixelRowMemory(y).Span.Fill(new Rgba32(0, 0, 0, 0));
		}

		var manifestEntries = new List<MinecraftAtlasGenerator.AtlasManifestEntry>(views.Count);

		for (var index = 0; index < views.Count; index++)
		{
			var view = views[index];
			var renderOptions = view.Options with { Size = tileSize };
			using var tile = renderer.RenderModel(debugModel, renderOptions);
			tile.Mutate(ctx => ctx.Resize(new ResizeOptions
			{
				Size = new Size(tileSize, tileSize),
				Sampler = KnownResamplers.NearestNeighbor,
				Mode = ResizeMode.Stretch
			}));

			var destination = new Point(index * tileSize, 0);
			atlas.Mutate(ctx => ctx.DrawImage(tile, destination, 1f));

			manifestEntries.Add(new MinecraftAtlasGenerator.AtlasManifestEntry(index, view.Name, index, 0, null));
		}

		var imagePath = Path.Combine(outputDirectory, "debug_block_atlas.png");
		atlas.SaveAsPng(imagePath);

		var manifestPath = Path.Combine(outputDirectory, "debug_block_atlas.json");
		File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifestEntries, new JsonSerializerOptions { WriteIndented = true }));

		return new[]
		{
			new MinecraftAtlasGenerator.AtlasResult("debug", "combined_views", 1, imagePath, manifestPath)
		};
	}
}
