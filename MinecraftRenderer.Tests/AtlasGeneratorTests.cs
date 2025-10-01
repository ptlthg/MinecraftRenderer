using System;
using System.IO;
using System.Linq;
using MinecraftRenderer;
using Xunit;

namespace MinecraftRenderer.Tests;

public sealed class AtlasGeneratorTests
{
	private static readonly string AssetsDirectory =
		Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "minecraft"));

	[Fact]
	public void GenerateAtlasesProducesImagesAndManifests()
	{
		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);

		var tempDirectory = Path.Combine(Path.GetTempPath(), "MinecraftRenderer.AtlasTests", Guid.NewGuid().ToString());
		Directory.CreateDirectory(tempDirectory);

		try
		{
			var blockSubset = renderer.GetKnownBlockNames().Take(4).ToList();
			var itemSubset = renderer.GetKnownItemNames().Take(4).ToList();

			var views = new[]
			{
				new MinecraftAtlasGenerator.AtlasView("test", new MinecraftBlockRenderer.BlockRenderOptions(Size: 256))
			};

			var results = MinecraftAtlasGenerator.GenerateAtlases(
				renderer,
				tempDirectory,
				views,
				tileSize: 96,
				columns: 2,
				rows: 2,
				blockFilter: blockSubset,
				itemFilter: itemSubset);

			Assert.NotEmpty(results);
			foreach (var result in results)
			{
				Assert.True(File.Exists(result.ImagePath), $"Expected atlas image '{result.ImagePath}' to exist.");
				Assert.True(File.Exists(result.ManifestPath), $"Expected manifest '{result.ManifestPath}' to exist.");
			}
		}
		finally
		{
			if (Directory.Exists(tempDirectory))
			{
				Directory.Delete(tempDirectory, recursive: true);
			}
		}
	}

	[Fact(Skip = "Manual integration example – use CreateAtlases console tool instead.")]
	public void GenerateAtlases()
	{
		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);

		var outputPath = Path.Combine(Environment.CurrentDirectory, "atlases");
		Directory.CreateDirectory(outputPath);

		var results = MinecraftAtlasGenerator.GenerateAtlases(
			renderer,
			outputPath,
			MinecraftAtlasGenerator.DefaultViews,
			tileSize: 160,
			columns: 12,
			rows: 12);

		foreach (var result in results)
		{
			Console.WriteLine($"Generated {result.ImagePath}");
		}
	}
}