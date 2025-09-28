using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using MinecraftRenderer;
using Xunit;
using Xunit.Abstractions;

namespace MinecraftRenderer.Tests;

public sealed class TextureRepositoryTests : IDisposable
{
	private static readonly string TexturesDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "minecraft", "textures"));
	private readonly TextureRepository _repository;

	public TextureRepositoryTests()
	{
		_repository = new TextureRepository(TexturesDirectory);
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
	private static readonly string AssetsDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "minecraft"));
	private readonly ITestOutputHelper _output;

	public BillboardOrientationTests(ITestOutputHelper output)
	{
		_output = output;
	}

	[Fact]
	public void DeadBrainCoralFan_RendersWithoutException()
	{
		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
		using var image = renderer.RenderBlock("dead_brain_coral_fan");
		_output.WriteLine($"Rendered image: {image.Width}x{image.Height}");
	}

	[Fact]
	public void DeadBrainCoralFan_UpFaceUvsMatchExpectedOrientation()
	{
		var resolver = BlockModelResolver.LoadFromMinecraftAssets(AssetsDirectory);
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
	public void DeadBrainCoralFan_NorthSouthFacesPointOutward()
	{
		var resolver = BlockModelResolver.LoadFromMinecraftAssets(AssetsDirectory);
		var model = resolver.Resolve("dead_brain_coral_fan");

		var buildVertices = typeof(MinecraftBlockRenderer)
			.GetMethod("BuildElementVertices", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
			?? throw new InvalidOperationException("BuildElementVertices not found");
		var applyRotation = typeof(MinecraftBlockRenderer)
			.GetMethod("ApplyElementRotation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
			?? throw new InvalidOperationException("ApplyElementRotation not found");
		var faceVertexIndicesField = typeof(MinecraftBlockRenderer)
			.GetField("FaceVertexIndices", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
			?? throw new InvalidOperationException("FaceVertexIndices not found");
		var faceVertexIndices = (Dictionary<BlockFaceDirection, int[]>)faceVertexIndicesField.GetValue(null)!;
		var upIndices = faceVertexIndices[BlockFaceDirection.Up];

		for (var elementIndex = 0; elementIndex < model.Elements.Count; elementIndex++)
		{
			var element = model.Elements[elementIndex];
			if (element.Rotation is null || !string.Equals(element.Rotation.Axis, "x", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (!element.Faces.TryGetValue(BlockFaceDirection.Up, out _))
			{
				continue;
			}

			var vertices = (Vector3[])(buildVertices.Invoke(null, new object[] { element })
				?? throw new InvalidOperationException("Failed to build vertices"));
			applyRotation.Invoke(null, new object[] { element, vertices });

			var v0 = vertices[upIndices[0]];
			var v1 = vertices[upIndices[1]];
			var v2 = vertices[upIndices[2]];
			var normal = Vector3.Cross(v1 - v0, v2 - v0);
			if (normal == Vector3.Zero)
			{
				continue;
			}

			normal = Vector3.Normalize(normal);
			var outward = element.Rotation.AngleInDegrees < 0 ? Vector3.UnitZ : -Vector3.UnitZ;
			var alignment = Vector3.Dot(normal, outward);
			if (alignment < 0)
			{
				normal = -normal;
				alignment = -alignment;
			}

			Assert.True(alignment > 0.2f,
				$"Element {elementIndex} expected to face {outward} but normal was {normal} (alignment {alignment:F3})");
		}
	}


	[Fact]
	public void TntSideFacesAreNotMirrored()
	{
		var resolver = BlockModelResolver.LoadFromMinecraftAssets(AssetsDirectory);
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
			new Vector2(0f, 0f),
			new Vector2(1f, 0f),
			new Vector2(1f, 1f),
			new Vector2(0f, 1f)
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
				relative = new Vector2(relative.Y, -relative.X);
				uv[i] = relative + center;
			}
		}
	}

	private static Vector2[] NormalizeFaceCoordinates(Vector2[] absoluteUv, Vector4 faceUv)
	{
		var uMin = MathF.Min(faceUv.X, faceUv.Z);
		var uMax = MathF.Max(faceUv.X, faceUv.Z);
		var vMin = MathF.Min(faceUv.Y, faceUv.W);
		var vMax = MathF.Max(faceUv.Y, faceUv.W);

		var width = uMax - uMin;
		var height = vMax - vMin;

		var invWidth = MathF.Abs(width) < 1e-5f ? 0f : 1f / width;
		var invHeight = MathF.Abs(height) < 1e-5f ? 0f : 1f / height;

		var normalized = new Vector2[absoluteUv.Length];
		for (var i = 0; i < absoluteUv.Length; i++)
		{
			var u = (absoluteUv[i].X - uMin) * invWidth;
			var v = (absoluteUv[i].Y - vMin) * invHeight;
			normalized[i] = new Vector2(Clamp01(u), Clamp01(v));
		}

		return normalized;
	}

	private static float Clamp01(float value) => value <= 0f ? 0f : value >= 1f ? 1f : value;

	private static bool AreClose(Vector2 expected, Vector2 actual, float tolerance = 1e-4f)
		=> MathF.Abs(expected.X - actual.X) <= tolerance && MathF.Abs(expected.Y - actual.Y) <= tolerance;
}
