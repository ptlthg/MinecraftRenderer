using System;
using System.IO;
using System.Linq;
using System.Numerics;
using MinecraftRenderer;
using Xunit;
using Xunit.Abstractions;

namespace MinecraftRenderer.Tests;

public sealed class TextureRepositoryTests : IDisposable
{
	private static readonly string DataDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data"));
	private readonly TextureRepository _repository;

	public TextureRepositoryTests()
	{
		_repository = new TextureRepository(DataDirectory);
	}

	[Fact]
	public void AnimatedTextureUsesFirstFrameDimensions()
	{
		var texture = _repository.GetTexture("minecraft:block/campfire_fire");

		Assert.Equal(16, texture.Width);
		Assert.Equal(16, texture.Height);
	}

	public void Dispose()
	{
		_repository.Dispose();
	}
}

public sealed class BillboardOrientationTests
{
	private readonly ITestOutputHelper _output;

	public BillboardOrientationTests(ITestOutputHelper output)
	{
		_output = output;
	}

	[Fact]
	public void DeadBrainCoralFan_RendersWithoutException()
	{
		var dataDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data"));
		using var renderer = MinecraftBlockRenderer.CreateFromDataDirectory(dataDirectory);
		using var image = renderer.RenderBlock("dead_brain_coral_fan");
		_output.WriteLine($"Rendered image: {image.Width}x{image.Height}");
	}

	[Fact]
	public void DeadBrainCoralFan_UpFaceUvsMatchExpectedOrientation()
	{
		var dataDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data"));
		var modelsPath = Path.Combine(dataDirectory, "blocks_models.json");
		var resolver = BlockModelResolver.LoadFromFile(modelsPath);
		var model = resolver.Resolve("dead_brain_coral_fan");

		var createUvMapMethod = typeof(MinecraftBlockRenderer)
			.GetMethod("CreateUvMap", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
			?? throw new InvalidOperationException("CreateUvMap method not found");

		var expectedBaseUp = new[]
		{
			new Vector2(0f, 0f),
			new Vector2(1f, 0f),
			new Vector2(1f, 1f),
			new Vector2(0f, 1f)
		};

		for (var elementIndex = 0; elementIndex < model.Elements.Count; elementIndex++)
		{
			var element = model.Elements[elementIndex];
			if (!element.Faces.TryGetValue(BlockFaceDirection.Up, out var face) || face.Uv is null)
			{
				continue;
			}

			var parameters = new object[]
			{
				element,
				BlockFaceDirection.Up,
				face.Uv.Value,
				face.Rotation ?? 0
			};

			if (createUvMapMethod.Invoke(null, parameters) is not Vector2[] actualUv || actualUv.Length != 4)
			{
				throw new InvalidOperationException($"CreateUvMap returned an unexpected result for element {elementIndex}.");
			}

			var expected = BuildExpectedUv(face.Uv.Value, face.Rotation ?? 0, expectedBaseUp);
			_output.WriteLine($"Element {elementIndex} actual: {string.Join(", ", actualUv.Select(v => $"({v.X:F3}, {v.Y:F3})"))}");
			_output.WriteLine($"Element {elementIndex} expected: {string.Join(", ", expected.Select(v => $"({v.X:F3}, {v.Y:F3})"))}");

			for (var i = 0; i < actualUv.Length; i++)
			{
				Assert.True(AreClose(expected[i], actualUv[i]),
					$"Element {elementIndex}, corner {i} expected {expected[i]} but got {actualUv[i]}");
			}
		}
	}

	[Fact]
	public void TntSideFacesAreNotMirrored()
	{
		var dataDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data"));
		var modelsPath = Path.Combine(dataDirectory, "blocks_models.json");
		var resolver = BlockModelResolver.LoadFromFile(modelsPath);
		var model = resolver.Resolve("tnt");

		var createUvMapMethod = typeof(MinecraftBlockRenderer)
			.GetMethod("CreateUvMap", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
			?? throw new InvalidOperationException("CreateUvMap method not found");
		var getFaceUvMethod = typeof(MinecraftBlockRenderer)
			.GetMethod("GetFaceUv", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
			?? throw new InvalidOperationException("GetFaceUv method not found");

		var expectedBaseEast = new[]
		{
			new Vector2(0f, 0f),
			new Vector2(1f, 0f),
			new Vector2(1f, 1f),
			new Vector2(0f, 1f)
		};

		var expectedBaseWest = new[]
		{
			new Vector2(1f, 0f),
			new Vector2(0f, 0f),
			new Vector2(0f, 1f),
			new Vector2(1f, 1f)
		};

		var targets = new[]
		{
			(BlockFaceDirection.East, expectedBaseEast),
			(BlockFaceDirection.West, expectedBaseWest)
		};

		for (var elementIndex = 0; elementIndex < model.Elements.Count; elementIndex++)
		{
			var element = model.Elements[elementIndex];
			foreach (var (direction, expectedBase) in targets)
			{
				if (!element.Faces.TryGetValue(direction, out var face))
				{
					continue;
				}

				var faceUv = (Vector4)(getFaceUvMethod.Invoke(null, new object[] { face, direction, element })
					?? throw new InvalidOperationException($"GetFaceUv returned null for element {elementIndex}, direction {direction}."));

				var parameters = new object[]
				{
					element,
					direction,
					faceUv,
					face.Rotation ?? 0
				};

				if (createUvMapMethod.Invoke(null, parameters) is not Vector2[] actualUv || actualUv.Length != 4)
				{
					throw new InvalidOperationException($"CreateUvMap returned an unexpected result for element {elementIndex} ({direction}).");
				}

				var expected = BuildExpectedUv(faceUv, face.Rotation ?? 0, expectedBase);
				_output.WriteLine($"Element {elementIndex} {direction} actual: {string.Join(", ", actualUv.Select(v => $"({v.X:F3}, {v.Y:F3})"))}");
				_output.WriteLine($"Element {elementIndex} {direction} expected: {string.Join(", ", expected.Select(v => $"({v.X:F3}, {v.Y:F3})"))}");

				for (var i = 0; i < actualUv.Length; i++)
				{
					Assert.True(AreClose(expected[i], actualUv[i]),
						$"Element {elementIndex} {direction}, corner {i} expected {expected[i]} but got {actualUv[i]}");
				}
			}
		}
	}

	private static Vector2[] BuildExpectedUv(Vector4 faceUv, int rotationDegrees, Vector2[] baseCoords)
	{
		var width = faceUv.Z - faceUv.X;
		var height = faceUv.W - faceUv.Y;
		var absolute = new Vector2[baseCoords.Length];
		for (var i = 0; i < baseCoords.Length; i++)
		{
			absolute[i] = new Vector2(
				faceUv.X + baseCoords[i].X * width,
				faceUv.Y + baseCoords[i].Y * height);
		}

		ApplyFaceRotation(absolute, faceUv, rotationDegrees);
		return NormalizeFaceCoordinates(absolute, faceUv);
	}

	private static void ApplyFaceRotation(Vector2[] uv, Vector4 faceUv, int rotationDegrees)
	{
		var normalized = ((rotationDegrees % 360) + 360) % 360;
		if (normalized == 0)
		{
			return;
		}

		var steps = normalized / 90;
		var center = new Vector2((faceUv.X + faceUv.Z) * 0.5f, (faceUv.Y + faceUv.W) * 0.5f);

		for (var s = 0; s < steps; s++)
		{
			for (var i = 0; i < uv.Length; i++)
			{
				var relative = uv[i] - center;
				relative = new Vector2(-relative.Y, relative.X);
				uv[i] = relative + center;
			}
		}
	}

	private static Vector2[] NormalizeFaceCoordinates(Vector2[] absoluteUv, Vector4 faceUv)
	{
		var width = faceUv.Z - faceUv.X;
		var height = faceUv.W - faceUv.Y;
		var invWidth = MathF.Abs(width) < 1e-5f ? 0f : 1f / width;
		var invHeight = MathF.Abs(height) < 1e-5f ? 0f : 1f / height;

		var normalized = new Vector2[absoluteUv.Length];
		for (var i = 0; i < absoluteUv.Length; i++)
		{
			var u = (absoluteUv[i].X - faceUv.X) * invWidth;
			var v = (absoluteUv[i].Y - faceUv.Y) * invHeight;
			normalized[i] = new Vector2(Clamp01(u), Clamp01(v));
		}

		return normalized;
	}

	private static float Clamp01(float value) => value <= 0f ? 0f : value >= 1f ? 1f : value;

	private static bool AreClose(Vector2 expected, Vector2 actual, float tolerance = 1e-4f)
		=> MathF.Abs(expected.X - actual.X) <= tolerance && MathF.Abs(expected.Y - actual.Y) <= tolerance;
}
