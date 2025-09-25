using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
using MinecraftRenderer;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace MinecraftRenderer.Tests;

public sealed class BlockRendererTests
{
	private static readonly string DataDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data"));
	private readonly ITestOutputHelper _output;

	public BlockRendererTests(ITestOutputHelper output)
	{
		_output = output;
	}

	[Fact]
	public void RenderStoneProducesOpaquePixels()
	{
		using var renderer = MinecraftBlockRenderer.CreateFromDataDirectory(DataDirectory);
		using var image = renderer.RenderBlock("stone");

		Assert.Equal(512, image.Width);
		Assert.Equal(512, image.Height);

		var hasOpaquePixel = false;
		for (var y = 0; y < image.Height && !hasOpaquePixel; y += 8)
		{
			var row = image.DangerousGetPixelRowMemory(y).Span;
			for (var x = 0; x < image.Width; x += 8)
			{
				if (row[x].A > 10)
				{
					hasOpaquePixel = true;
					break;
				}
			}
		}

		Assert.True(hasOpaquePixel, "Rendered stone block should contain opaque pixels.");
	}

	[Fact]
	public void DefaultInventoryOrientationShowsFrontOnRight()
	{
		using var renderer = MinecraftBlockRenderer.CreateFromDataDirectory(DataDirectory);

		var faceColors = new Dictionary<BlockFaceDirection, Rgba32>
		{
			[BlockFaceDirection.North] = new(0xFF, 0x33, 0x33),
			[BlockFaceDirection.South] = new(0x33, 0x99, 0xFF),
			[BlockFaceDirection.East] = new(0x33, 0xFF, 0x99),
			[BlockFaceDirection.West] = new(0x99, 0x33, 0xFF),
			[BlockFaceDirection.Up] = new(0xFF, 0xFF, 0x66),
			[BlockFaceDirection.Down] = new(0xFF, 0x99, 0x33)
		};

		foreach (var (direction, color) in faceColors)
		{
			var textureId = $"minecraft:block/unit_test_debug_{direction.ToString().ToLowerInvariant()}";
			using var image = new Image<Rgba32>(16, 16, color);
			renderer.TextureRepository.RegisterTexture(textureId, image, overwrite: true);
		}

		var faces = new Dictionary<BlockFaceDirection, ModelFace>
		{
			[BlockFaceDirection.North] = new("#north", new Vector4(0, 0, 16, 16), null, null, null),
			[BlockFaceDirection.South] = new("#south", new Vector4(0, 0, 16, 16), null, null, null),
			[BlockFaceDirection.East] = new("#east", new Vector4(0, 0, 16, 16), null, null, null),
			[BlockFaceDirection.West] = new("#west", new Vector4(0, 0, 16, 16), null, null, null),
			[BlockFaceDirection.Up] = new("#up", new Vector4(0, 0, 16, 16), null, null, null),
			[BlockFaceDirection.Down] = new("#down", new Vector4(0, 0, 16, 16), null, null, null)
		};

		var textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["particle"] = "minecraft:block/unit_test_debug_up",
			["north"] = "minecraft:block/unit_test_debug_north",
			["south"] = "minecraft:block/unit_test_debug_south",
			["east"] = "minecraft:block/unit_test_debug_east",
			["west"] = "minecraft:block/unit_test_debug_west",
			["up"] = "minecraft:block/unit_test_debug_up",
			["down"] = "minecraft:block/unit_test_debug_down"
		};

		var element = new ModelElement(new Vector3(0, 0, 0), new Vector3(16, 16, 16), null, faces, shade: true);
		var model = new BlockModelInstance("unit_test:debug_cube", Array.Empty<string>(), textures, new Dictionary<string, TransformDefinition>(StringComparer.OrdinalIgnoreCase), new[] { element });

		var options = MinecraftBlockRenderer.BlockRenderOptions.Default with { Size = 160 };
		using var rendered = renderer.RenderModel(model, options);

		var rightColor = SampleAverageColor(rendered, (int)(rendered.Width * 0.70f), (int)(rendered.Width * 0.95f), rendered.Height / 2 - 10, rendered.Height / 2 + 10);
		var leftColor = SampleAverageColor(rendered, (int)(rendered.Width * 0.05f), (int)(rendered.Width * 0.30f), rendered.Height / 2 - 10, rendered.Height / 2 + 10);
		var topColor = SampleAverageColor(rendered, rendered.Width / 2 - 10, rendered.Width / 2 + 10, (int)(rendered.Height * 0.05f), (int)(rendered.Height * 0.25f));
		_output.WriteLine($"Top face color {topColor}");

		var rightVector = new Vector3(rightColor.R, rightColor.G, rightColor.B);
		var northError = ComputeScaledError(rightVector, ToVector(faceColors[BlockFaceDirection.North]));
		var southError = ComputeScaledError(rightVector, ToVector(faceColors[BlockFaceDirection.South]));
		var eastError = ComputeScaledError(rightVector, ToVector(faceColors[BlockFaceDirection.East]));
		var westError = ComputeScaledError(rightVector, ToVector(faceColors[BlockFaceDirection.West]));
		var leftVector = new Vector3(leftColor.R, leftColor.G, leftColor.B);
		var leftEastError = ComputeScaledError(leftVector, ToVector(faceColors[BlockFaceDirection.East]));
		var leftWestError = ComputeScaledError(leftVector, ToVector(faceColors[BlockFaceDirection.West]));
		var leftNorthError = ComputeScaledError(leftVector, ToVector(faceColors[BlockFaceDirection.North]));
		var leftSouthError = ComputeScaledError(leftVector, ToVector(faceColors[BlockFaceDirection.South]));
		_output.WriteLine($"Right face color {rightColor} -> north {northError:F3}, south {southError:F3}, east {eastError:F3}, west {westError:F3}");
		_output.WriteLine($"Left face color {leftColor} -> east {leftEastError:F3}, west {leftWestError:F3}, north {leftNorthError:F3}, south {leftSouthError:F3}");

		Assert.True(IsCloserTo(rightColor, ToVector(faceColors[BlockFaceDirection.South]), ToVector(faceColors[BlockFaceDirection.North])), "Right face should prefer south color over north.");
		Assert.True(IsCloserTo(leftColor, ToVector(faceColors[BlockFaceDirection.East]), ToVector(faceColors[BlockFaceDirection.West])), "Left face should prefer east color over west.");
		Assert.True(IsCloserTo(topColor, ToVector(faceColors[BlockFaceDirection.Up]), ToVector(faceColors[BlockFaceDirection.Down])), "Top face should prefer up color over down.");
	}

	[Fact]
	public void CubeFaceUvsAreOrientedCorrectly()
	{
		var createUvMap = typeof(MinecraftBlockRenderer)
			.GetMethod("CreateUvMap", BindingFlags.NonPublic | BindingFlags.Static)
			?? throw new InvalidOperationException("CreateUvMap method not found");

		var element = new ModelElement(
			new Vector3(0f, 0f, 0f),
			new Vector3(16f, 16f, 16f),
			null,
			new Dictionary<BlockFaceDirection, ModelFace>(),
			shade: true);

		Vector2[] Map(BlockFaceDirection direction)
		{
			var result = createUvMap.Invoke(null, new object[] { element, direction, new Vector4(0f, 0f, 16f, 16f), 0 })
				as Vector2[] ?? throw new InvalidOperationException("CreateUvMap returned null.");
			return result;
		}

		var north = Map(BlockFaceDirection.North);
		Assert.True(north[0].X > north[1].X, "North face should map east edge to higher U than west edge.");
		Assert.True(north[0].Y > north[2].Y, "North face should map top edge to higher V than bottom edge.");

		var south = Map(BlockFaceDirection.South);
		Assert.True(south[0].X < south[1].X, "South face should map west edge to lower U than east edge.");
		Assert.True(south[0].Y < south[2].Y, "South face should map top edge to lower V than bottom edge.");

		var east = Map(BlockFaceDirection.East);
		Assert.True(east[1].X > east[0].X, "East face should map south edge to higher U than north edge.");
		Assert.True(east[0].Y < east[2].Y, "East face should map top edge to lower V than bottom edge.");

		var west = Map(BlockFaceDirection.West);
		Assert.True(west[1].X > west[0].X, "West face should map north edge to higher U than south edge.");
		Assert.True(west[0].Y < west[2].Y, "West face should map top edge to lower V than bottom edge.");

		var up = Map(BlockFaceDirection.Up);
		Assert.True(up[1].X > up[0].X, "Up face should map east edge to higher U than west edge.");
		Assert.True(up[0].Y < up[2].Y, "Up face should map north edge to lower V than south edge.");

		var down = Map(BlockFaceDirection.Down);
		Assert.True(down[1].X > down[0].X, "Down face should map east edge to higher U than west edge.");
		Assert.True(down[3].Y > down[0].Y, "Down face should map north edge to higher V than south edge.");
	}

	[Fact]
	public void CrafterFrontFaceMatchesNorthTexture()
	{
		using var renderer = MinecraftBlockRenderer.CreateFromDataDirectory(DataDirectory);
		using var rendered = renderer.RenderBlock("crafter", MinecraftBlockRenderer.BlockRenderOptions.Default with { Size = 256 });

		var rightColor = SampleAverageColor(rendered, (int)(rendered.Width * 0.70f), rendered.Width - 1, rendered.Height / 2 - 20, rendered.Height / 2 + 20);
		var leftColor = SampleAverageColor(rendered, 0, (int)(rendered.Width * 0.30f), rendered.Height / 2 - 20, rendered.Height / 2 + 20);

		var northTexture = renderer.TextureRepository.GetTexture("minecraft:block/crafter_north");
		var southTexture = renderer.TextureRepository.GetTexture("minecraft:block/crafter_south");
		var westTexture = renderer.TextureRepository.GetTexture("minecraft:block/crafter_west");
		var eastTexture = renderer.TextureRepository.GetTexture("minecraft:block/crafter_east");

		var northAvg = ComputeAverageColor(northTexture);
		var southAvg = ComputeAverageColor(southTexture);
		var westAvg = ComputeAverageColor(westTexture);
		var eastAvg = ComputeAverageColor(eastTexture);

		var rightVector = new Vector3(rightColor.R, rightColor.G, rightColor.B);
		var northError = ComputeScaledError(rightVector, northAvg);
		var southError = ComputeScaledError(rightVector, southAvg);
		_output.WriteLine($"Right -> north error {northError}, south error {southError}");
		_output.WriteLine($"Right color {rightColor}");
		Assert.True(southError <= northError, "Right face should more closely match crafter south texture than north.");

		Assert.True(IsCloserTo(leftColor, eastAvg, westAvg), "Left face should more closely match crafter east texture than west.");
	}

	private static Rgba32 SampleSolidColor(Image<Rgba32> image, int xStart, int xEnd, int yStart, int yEnd)
	{
		var clampXStart = Math.Clamp(xStart, 0, image.Width - 1);
		var clampXEnd = Math.Clamp(xEnd, 0, image.Width - 1);
		var clampYStart = Math.Clamp(yStart, 0, image.Height - 1);
		var clampYEnd = Math.Clamp(yEnd, 0, image.Height - 1);

		for (var y = clampYStart; y <= clampYEnd; y++)
		{
			var row = image.DangerousGetPixelRowMemory(y).Span;
			for (var x = clampXStart; x <= clampXEnd; x++)
			{
				var pixel = row[x];
				if (pixel.A > 200)
				{
					return pixel;
				}
			}
		}

		throw new InvalidOperationException("Unable to find a solid pixel in the specified region.");
	}

	private static Rgba32 SampleAverageColor(Image<Rgba32> image, int xStart, int xEnd, int yStart, int yEnd)
	{
		var clampXStart = Math.Clamp(xStart, 0, image.Width - 1);
		var clampXEnd = Math.Clamp(xEnd, 0, image.Width - 1);
		var clampYStart = Math.Clamp(yStart, 0, image.Height - 1);
		var clampYEnd = Math.Clamp(yEnd, 0, image.Height - 1);

		long totalR = 0;
		long totalG = 0;
		long totalB = 0;
		long totalA = 0;
		long count = 0;

		for (var y = clampYStart; y <= clampYEnd; y++)
		{
			var row = image.DangerousGetPixelRowMemory(y).Span;
			for (var x = clampXStart; x <= clampXEnd; x++)
			{
				var pixel = row[x];
				totalR += pixel.R;
				totalG += pixel.G;
				totalB += pixel.B;
				totalA += pixel.A;
				count++;
			}
		}

		if (count == 0)
		{
			return default;
		}

		return new Rgba32(
			(byte)(totalR / count),
			(byte)(totalG / count),
			(byte)(totalB / count),
			(byte)(totalA / count));
	}

	private static Vector3 ComputeAverageColor(Image<Rgba32> image)
	{
		long totalR = 0;
		long totalG = 0;
		long totalB = 0;
		long count = 0;

		for (var y = 0; y < image.Height; y++)
		{
			var row = image.DangerousGetPixelRowMemory(y).Span;
			for (var x = 0; x < image.Width; x++)
			{
				var pixel = row[x];
				totalR += pixel.R;
				totalG += pixel.G;
				totalB += pixel.B;
				count++;
			}
		}

		if (count == 0)
		{
			return Vector3.Zero;
		}

		return new Vector3(totalR / (float)count, totalG / (float)count, totalB / (float)count);
	}

	private static bool IsCloserTo(Rgba32 sampleColor, Vector3 expected, Vector3 alternative)
	{
		var sample = new Vector3(sampleColor.R, sampleColor.G, sampleColor.B);
		var expectedError = ComputeScaledError(sample, expected);
		var alternativeError = ComputeScaledError(sample, alternative);
		return expectedError <= alternativeError;
	}

	private static Vector3 ToVector(Rgba32 color) => new(color.R, color.G, color.B);

	private static float ComputeScaledError(Vector3 sample, Vector3 reference)
	{
		var referenceLengthSquared = reference.LengthSquared();
		if (referenceLengthSquared < 1e-3f)
		{
			return sample.LengthSquared();
		}

		var scale = Vector3.Dot(sample, reference) / referenceLengthSquared;
		var adjusted = reference * scale;
		var diff = sample - adjusted;
		return diff.LengthSquared();
	}

	private static void AssertColorApprox(Rgba32 expected, Rgba32 actual, int tolerance = 6)
	{
		Assert.True(Math.Abs(expected.R - actual.R) <= tolerance, $"Expected R≈{expected.R} but got {actual.R}");
		Assert.True(Math.Abs(expected.G - actual.G) <= tolerance, $"Expected G≈{expected.G} but got {actual.G}");
		Assert.True(Math.Abs(expected.B - actual.B) <= tolerance, $"Expected B≈{expected.B} but got {actual.B}");
	}
}
