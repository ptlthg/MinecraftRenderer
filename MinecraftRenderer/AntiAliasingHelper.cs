namespace MinecraftRenderer;

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

internal static class AntiAliasingHelper
{
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static float Luma(Rgba32 c) => (c.R * 0.299f + c.G * 0.587f + c.B * 0.114f) * (c.A / 255f);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static Rgba32 SampleBilinear(Image<Rgba32> img, float x, float y)
	{
		int ix = (int)MathF.Floor(x);
		int iy = (int)MathF.Floor(y);
		float fx = x - ix;
		float fy = y - iy;
		
		ix = Math.Clamp(ix, 0, img.Width - 2);
		iy = Math.Clamp(iy, 0, img.Height - 2);

		var c00 = img[ix, iy];
		var c10 = img[ix + 1, iy];
		var c01 = img[ix, iy + 1];
		var c11 = img[ix + 1, iy + 1];

		float r = c00.R * (1 - fx) * (1 - fy) + c10.R * fx * (1 - fy) + c01.R * (1 - fx) * fy + c11.R * fx * fy;
		float g = c00.G * (1 - fx) * (1 - fy) + c10.G * fx * (1 - fy) + c01.G * (1 - fx) * fy + c11.G * fx * fy;
		float b = c00.B * (1 - fx) * (1 - fy) + c10.B * fx * (1 - fy) + c01.B * (1 - fx) * fy + c11.B * fx * fy;
		float a = c00.A * (1 - fx) * (1 - fy) + c10.A * fx * (1 - fy) + c01.A * (1 - fx) * fy + c11.A * fx * fy;

		return new Rgba32((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), (byte)Math.Clamp(a, 0, 255));
	}

	public static void ApplyFXAA(Image<Rgba32> image)
	{
		var width = image.Width;
		var height = image.Height;
		using var tempImage = image.Clone();

		const float FXAA_REDUCE_MIN = 1.0f / 128.0f;
		const float FXAA_REDUCE_MUL = 1.0f / 4.0f; // Tuned for higher sensitivity to subtle edges
		const float FXAA_SPAN_MAX = 8.0f;

		Parallel.For(1, height - 1, y =>
		{
			var srcRowU = tempImage.DangerousGetPixelRowMemory(y - 1).Span;
			var srcRowM = tempImage.DangerousGetPixelRowMemory(y).Span;
			var srcRowD = tempImage.DangerousGetPixelRowMemory(y + 1).Span;
			var dstRow = image.DangerousGetPixelRowMemory(y).Span;

			for (var x = 1; x < width - 1; x++)
			{
				var rgbNW = srcRowU[x - 1];
				var rgbNE = srcRowU[x + 1];
				var rgbSW = srcRowD[x - 1];
				var rgbSE = srcRowD[x + 1];
				var rgbM  = srcRowM[x];

				float lumaNW = Luma(rgbNW);
				float lumaNE = Luma(rgbNE);
				float lumaSW = Luma(rgbSW);
				float lumaSE = Luma(rgbSE);
				float lumaM  = Luma(rgbM);

				float lumaMin = Math.Min(lumaM, Math.Min(Math.Min(lumaNW, lumaNE), Math.Min(lumaSW, lumaSE)));
				float lumaMax = Math.Max(lumaM, Math.Max(Math.Max(lumaNW, lumaNE), Math.Max(lumaSW, lumaSE)));

				float contrast = lumaMax - lumaMin;
				// Tuned threshold: highly sensitive but cuts off absolute noise
				if (contrast < Math.Max(0.0156f, lumaMax * 0.0312f))
				{
					continue;
				}

				float dirX = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
				float dirY =  ((lumaNW + lumaSW) - (lumaNE + lumaSE));

				float dirReduce = Math.Max((lumaNW + lumaNE + lumaSW + lumaSE) * (0.25f * FXAA_REDUCE_MUL), FXAA_REDUCE_MIN);
				float rcpDirMin = 1.0f / (Math.Min(Math.Abs(dirX), Math.Abs(dirY)) + dirReduce);

				dirX = Math.Clamp(dirX * rcpDirMin, -FXAA_SPAN_MAX, FXAA_SPAN_MAX);
				dirY = Math.Clamp(dirY * rcpDirMin, -FXAA_SPAN_MAX, FXAA_SPAN_MAX);

				var sample1 = SampleBilinear(tempImage, x + dirX * (1.0f / 3.0f - 0.5f), y + dirY * (1.0f / 3.0f - 0.5f));
				var sample2 = SampleBilinear(tempImage, x + dirX * (2.0f / 3.0f - 0.5f), y + dirY * (2.0f / 3.0f - 0.5f));
				
				float r = (sample1.R * sample1.A + sample2.R * sample2.A) * 0.5f;
				float g = (sample1.G * sample1.A + sample2.G * sample2.A) * 0.5f;
				float b = (sample1.B * sample1.A + sample2.B * sample2.A) * 0.5f;
				float a = (sample1.A + sample2.A) * 0.5f;

				if (a > 0)
				{
					dstRow[x] = new Rgba32(
						(byte)Math.Clamp(r / a, 0, 255),
						(byte)Math.Clamp(g / a, 0, 255),
						(byte)Math.Clamp(b / a, 0, 255),
						(byte)Math.Clamp(a, 0, 255));
				}
				else
				{
					dstRow[x] = new Rgba32(0, 0, 0, 0);
				}
			}
		});
	}
}
