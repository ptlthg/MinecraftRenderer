using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Linq;
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
	private static readonly string AssetsDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "minecraft"));
	private readonly ITestOutputHelper _output;

	public BlockRendererTests(ITestOutputHelper output)
	{
		_output = output;
	}

	[Fact]
	public void RenderStoneProducesOpaquePixels()
	{
		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
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
	public void ItemRegistryIncludesBlockInventoryItems()
	{
		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
		var knownItems = renderer.GetKnownItemNames();
		Assert.Contains("oak_fence", knownItems);
		Assert.Contains("white_shulker_box", knownItems);

		using var fence = renderer.RenderGuiItem("oak_fence");
		Assert.True(HasOpaquePixels(fence), "Oak fence item render should contain opaque pixels.");

		using var shulker = renderer.RenderGuiItem("white_shulker_box");
		Assert.True(HasOpaquePixels(shulker), "White shulker box item render should contain opaque pixels.");
	}

		[Fact]
		public void RenderBedItemUsesBlockModelFallback()
		{
			using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);

			using var bed = renderer.RenderGuiItem("white_bed");
			Assert.True(HasOpaquePixels(bed), "White bed item render should contain opaque pixels.");

			var (minX, maxX) = GetOpaqueHorizontalBounds(bed);
			Assert.True(minX >= 0 && maxX >= minX, "White bed render should contain opaque horizontal coverage.");
			var horizontalSpan = maxX - minX;
			Assert.True(horizontalSpan > bed.Width / 2, $"White bed render should span more than half the image width, but spanned {horizontalSpan} pixels out of {bed.Width}.");
		}

	[Fact]
	public void DefaultInventoryOrientationShowsFrontOnRight()
	{
		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);

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
	public void RenderChestUsesOverlayModels()
	{
		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
		using var image = renderer.RenderBlock("chest");

		Assert.Equal(512, image.Width);
		Assert.Equal(512, image.Height);

		var hasNonMissingPixel = false;
		for (var y = 0; y < image.Height && !hasNonMissingPixel; y += 16)
		{
			var row = image.DangerousGetPixelRowMemory(y).Span;
			for (var x = 0; x < image.Width; x += 16)
			{
				var pixel = row[x];
				if (pixel.A > 0 && !(pixel.R == 0xFF && pixel.G == 0x00 && pixel.B == 0xFF))
				{
					hasNonMissingPixel = true;
					break;
				}
			}
		}

		Assert.True(hasNonMissingPixel, "Chest rendering should include non-missing texture pixels.");
	}

	// TODO: Fix the issue and re-enable this test
	// [Fact]
	// public void RenderCandleCakeIncludesSideFaces()
	// {
	// 	using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
	// 	using var image = renderer.RenderBlock("candle_cake");

	// 	Assert.Equal(512, image.Width);
	// 	Assert.Equal(512, image.Height);

	// 	static Rgba32 SamplePixel(Image<Rgba32> source, int x, int y)
	// 	{
	// 		var row = source.DangerousGetPixelRowMemory(y).Span;
	// 		return row[x];
	// 	}

	// 	static bool IsTransparent(Rgba32 pixel) => pixel.A <= 5;

	// 	var leftPixel = SamplePixel(image, 240, 125);
	// 	var rightPixel = SamplePixel(image, 380, 125);

	// 	_output.WriteLine($"Left pixel @ (240,125): {leftPixel}");
	// 	_output.WriteLine($"Right pixel @ (380,125): {rightPixel}");

	// 	var bothTransparent = IsTransparent(leftPixel) && IsTransparent(rightPixel);
	// 	Assert.False(bothTransparent, "Rendered candle cake should render side faces; sample pixels were both transparent.");
	// }

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
	public void BillboardTexturesAreNotUpsideDown()
	{
		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);

		using (var customTexture = CreateVerticalSplitTexture(new Rgba32(0xE6, 0x3B, 0x3B, 0xFF), new Rgba32(0x3B, 0x6B, 0xE6, 0xFF)))
		{
			renderer.TextureRepository.RegisterTexture("minecraft:block/birch_sapling", customTexture, overwrite: true);
		}

		var testOptions = new List<MinecraftBlockRenderer.BlockRenderOptions>
		{
			MinecraftBlockRenderer.BlockRenderOptions.Default
		};

		foreach (var yaw in new[] { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f })
		{
			testOptions.Add(MinecraftBlockRenderer.BlockRenderOptions.Default with
			{
				UseGuiTransform = false,
				YawInDegrees = yaw,
				PitchInDegrees = 0f,
				RollInDegrees = 0f,
				Padding = 0.05f,
				Size = 256
			});
		}

		foreach (var options in testOptions)
		{
			using var rendered = renderer.RenderBlock("birch_sapling", options);
			var topColor = FindOpaquePixel(rendered, searchFromTop: true);
			var bottomColor = FindOpaquePixel(rendered, searchFromTop: false);
			_output.WriteLine($"Options (gui={options.UseGuiTransform}, yaw={options.YawInDegrees}) -> top {topColor}, bottom {bottomColor}");
			Assert.True(topColor.R > topColor.B, $"Top of billboarded texture should preserve the top-half color for {options}.");
			Assert.True(bottomColor.B > bottomColor.R, $"Bottom of billboarded texture should preserve the bottom-half color for {options}.");
		}
	}

	[Fact]
	public void BillboardNorthFaceIsUpright()
	{
		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);

		const string textureId = "minecraft:block/unit_test_cross_north";
		using (var customTexture = CreateVerticalSplitTexture(new Rgba32(0xE6, 0x3B, 0x3B, 0xFF), new Rgba32(0x3B, 0x6B, 0xE6, 0xFF)))
		{
			renderer.TextureRepository.RegisterTexture(textureId, customTexture, overwrite: true);
		}

		var element = new ModelElement(
			new Vector3(0.8f, 0f, 8f),
			new Vector3(15.2f, 16f, 8f),
			new ElementRotation(45f, new Vector3(8f, 8f, 8f), "y", rescale: true),
			new Dictionary<BlockFaceDirection, ModelFace>
			{
				[BlockFaceDirection.North] = new("#cross", new Vector4(0f, 0f, 16f, 16f), null, null, null)
			},
			shade: false);

		var model = new BlockModelInstance(
			"unit_test:cross_north",
			Array.Empty<string>(),
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["cross"] = textureId
			},
			new Dictionary<string, TransformDefinition>(StringComparer.OrdinalIgnoreCase),
			new List<ModelElement> { element });

		var options = MinecraftBlockRenderer.BlockRenderOptions.Default with
		{
			UseGuiTransform = false,
			YawInDegrees = 180f,
			PitchInDegrees = 0f,
			RollInDegrees = 0f,
			Padding = 0.05f,
			Size = 256
		};

		using var rendered = renderer.RenderModel(model, options);
		var topColor = FindOpaquePixel(rendered, searchFromTop: true);
		var bottomColor = FindOpaquePixel(rendered, searchFromTop: false);

		Assert.True(topColor.R > topColor.B, "North billboard face should display the top-half color at the top.");
		Assert.True(bottomColor.B > bottomColor.R, "North billboard face should display the bottom-half color at the bottom.");
	}

	[Fact]
	public void BillboardNorthFaceUvTopIsNotFlipped()
	{
		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);

		var element = new ModelElement(
			new Vector3(0.8f, 0f, 8f),
			new Vector3(15.2f, 16f, 8f),
			new ElementRotation(45f, new Vector3(8f, 8f, 8f), "y", rescale: true),
			new Dictionary<BlockFaceDirection, ModelFace>
			{
				[BlockFaceDirection.North] = new("#cross", new Vector4(0f, 0f, 16f, 16f), null, null, null)
			},
			shade: false);

		var model = new BlockModelInstance(
			"unit_test:cross_north",
			Array.Empty<string>(),
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["cross"] = "minecraft:block/birch_sapling"
			},
			new Dictionary<string, TransformDefinition>(StringComparer.OrdinalIgnoreCase),
			new List<ModelElement> { element });

		var buildTriangles = typeof(MinecraftBlockRenderer)
			.GetMethod("BuildTriangles", BindingFlags.NonPublic | BindingFlags.Instance)
			?? throw new InvalidOperationException("BuildTriangles method not found");

		var triangles = (System.Collections.IEnumerable)buildTriangles.Invoke(renderer, new object[] { model, Matrix4x4.Identity })!;
		var v1Prop = triangles.GetType().GetGenericArguments().First().GetProperty("V1")
			?? throw new InvalidOperationException("V1 property not found");
		var v2Prop = triangles.GetType().GetGenericArguments().First().GetProperty("V2")!;
		var v3Prop = triangles.GetType().GetGenericArguments().First().GetProperty("V3")!;
		var t1Prop = triangles.GetType().GetGenericArguments().First().GetProperty("T1")!;
		var t2Prop = triangles.GetType().GetGenericArguments().First().GetProperty("T2")!;
		var t3Prop = triangles.GetType().GetGenericArguments().First().GetProperty("T3")!;

		var topUvValues = new List<float>();
		foreach (var triangle in triangles)
		{
			var v1 = (Vector3)v1Prop.GetValue(triangle)!;
			var v2 = (Vector3)v2Prop.GetValue(triangle)!;
			var v3 = (Vector3)v3Prop.GetValue(triangle)!;
			var t1 = (Vector2)t1Prop.GetValue(triangle)!;
			var t2 = (Vector2)t2Prop.GetValue(triangle)!;
			var t3 = (Vector2)t3Prop.GetValue(triangle)!;

			var topVertexUv = t1;
			var topVertexY = v1.Y;
			if (v2.Y > topVertexY)
			{
				topVertexUv = t2;
				topVertexY = v2.Y;
			}
			if (v3.Y > topVertexY)
			{
				topVertexUv = t3;
			}

			topUvValues.Add(topVertexUv.Y);
		}

		var maxTopUv = topUvValues.Max();
		_output.WriteLine($"Top UV values: {string.Join(", ", topUvValues.Select(v => v.ToString("F3")))}");
		Assert.True(maxTopUv <= 0.05f, $"Expected billboard north face top UV to be near 0 but found max {maxTopUv:F3}.");
	}

	[Fact]
	public void SporeBlossomTopViewHasOpaquePixels()
	{
		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
		var options = MinecraftBlockRenderer.BlockRenderOptions.Default with
		{
			PitchInDegrees = 90f,
			Size = 256
		};

		using var image = renderer.RenderBlock("spore_blossom", options);
		Assert.True(HasOpaquePixels(image), "Spore blossom viewed from above should contain visible pixels.");
	}


	private static Rgba32 FindOpaquePixel(Image<Rgba32> image, bool searchFromTop)
	{
		if (searchFromTop)
		{
			for (var y = 0; y < image.Height; y++)
			{
				var row = image.DangerousGetPixelRowMemory(y).Span;
				for (var x = 0; x < image.Width; x++)
				{
					var pixel = row[x];
					if (pixel.A > 200)
					{
						return pixel;
					}
				}
			}
		}
		else
		{
			for (var y = image.Height - 1; y >= 0; y--)
			{
				var row = image.DangerousGetPixelRowMemory(y).Span;
				for (var x = 0; x < image.Width; x++)
				{
					var pixel = row[x];
					if (pixel.A > 200)
					{
						return pixel;
					}
				}
			}
		}

		throw new InvalidOperationException("No opaque pixels were found in the rendered image.");
	}

	private static Image<Rgba32> CreateVerticalSplitTexture(Rgba32 topColor, Rgba32 bottomColor, int size = 16)
	{
		var image = new Image<Rgba32>(size, size);
		var half = size / 2;
		for (var y = 0; y < size; y++)
		{
			var row = image.DangerousGetPixelRowMemory(y).Span;
			var color = y < half ? topColor : bottomColor;
			for (var x = 0; x < size; x++)
			{
				row[x] = color;
			}
		}

		return image;
	}

	private static Image<Rgba32> CreateSolidTexture(Rgba32 color, int size = 16)
	{
		var image = new Image<Rgba32>(size, size);
		for (var y = 0; y < size; y++)
		{
			var row = image.DangerousGetPixelRowMemory(y).Span;
			for (var x = 0; x < size; x++)
			{
				row[x] = color;
			}
		}

		return image;
	}

	[Fact]
	public void CrafterFrontFaceMatchesNorthTexture()
	{
		using var renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(AssetsDirectory);
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

	private static bool HasOpaquePixels(Image<Rgba32> image)
	{
		for (var y = 0; y < image.Height; y += 4)
		{
			var row = image.DangerousGetPixelRowMemory(y).Span;
			for (var x = 0; x < image.Width; x += 4)
			{
				if (row[x].A > 10)
				{
					return true;
				}
			}
		}

		return false;
	}

	private static (int Min, int Max) GetOpaqueHorizontalBounds(Image<Rgba32> image)
	{
		var min = image.Width;
		var max = -1;

		for (var y = 0; y < image.Height; y++)
		{
			var row = image.DangerousGetPixelRowMemory(y).Span;
			for (var x = 0; x < image.Width; x++)
			{
				if (row[x].A > 10)
				{
					if (x < min)
					{
						min = x;
					}
					if (x > max)
					{
						max = x;
					}
				}
			}
		}

		if (max < min)
		{
			return (-1, -1);
		}

		return (min, max);
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

	private static bool ColorsApproxEqual(Rgba32 left, Rgba32 right, int tolerance = 4)
	{
		return Math.Abs(left.R - right.R) <= tolerance
			&& Math.Abs(left.G - right.G) <= tolerance
			&& Math.Abs(left.B - right.B) <= tolerance;
	}
}
