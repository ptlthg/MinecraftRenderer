using System;
using System.IO;
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

		var expected = new[]
		{
			new[] { new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(0f, 0f) },
			new[] { new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f) },
			new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) },
			new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) }
		};

		for (var elementIndex = 0; elementIndex < expected.Length && elementIndex < model.Elements.Count; elementIndex++)
		{
			var element = model.Elements[elementIndex];
			Assert.True(element.Faces.TryGetValue(BlockFaceDirection.Up, out var face) && face.Uv is not null, $"Element {elementIndex} missing up face UV data.");

			var parameters = new object[]
			{
				element,
				BlockFaceDirection.Up,
				face!.Uv!.Value,
				face.Rotation ?? 0
			};

			if (createUvMapMethod.Invoke(null, parameters) is not Vector2[] uvMap || uvMap.Length != 4)
			{
				throw new InvalidOperationException($"CreateUvMap returned an unexpected result for element {elementIndex}.");
			}

			for (var i = 0; i < 4; i++)
			{
				Assert.True(AreClose(expected[elementIndex][i], uvMap[i]), $"Element {elementIndex}, corner {i} expected {expected[elementIndex][i]} but got {uvMap[i]}");
			}
		}
	}

	private static bool AreClose(Vector2 expected, Vector2 actual, float tolerance = 1e-4f)
		=> MathF.Abs(expected.X - actual.X) <= tolerance && MathF.Abs(expected.Y - actual.Y) <= tolerance;
}
