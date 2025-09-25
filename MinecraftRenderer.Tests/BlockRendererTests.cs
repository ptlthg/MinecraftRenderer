using System;
using System.IO;
using MinecraftRenderer;
using SixLabors.ImageSharp.Advanced;
using Xunit;

namespace MinecraftRenderer.Tests;

public sealed class BlockRendererTests
{
	private static readonly string DataDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data"));

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
}
