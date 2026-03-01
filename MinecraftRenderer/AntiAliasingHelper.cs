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
	private static Rgba32 SampleBilinear(Image<Rgba32> img, float x, float y) {
		var ix = (int)MathF.Floor(x);
		var iy = (int)MathF.Floor(y);
		var fx = x - ix;
		var fy = y - iy;

		ix = Math.Clamp(ix, 0, img.Width - 2);
		iy = Math.Clamp(iy, 0, img.Height - 2);

		var c00 = img[ix, iy];
		var c10 = img[ix + 1, iy];
		var c01 = img[ix, iy + 1];
		var c11 = img[ix + 1, iy + 1];

		var r = c00.R * (1 - fx) * (1 - fy) + c10.R * fx * (1 - fy) + c01.R * (1 - fx) * fy + c11.R * fx * fy;
		var g = c00.G * (1 - fx) * (1 - fy) + c10.G * fx * (1 - fy) + c01.G * (1 - fx) * fy + c11.G * fx * fy;
		var b = c00.B * (1 - fx) * (1 - fy) + c10.B * fx * (1 - fy) + c01.B * (1 - fx) * fy + c11.B * fx * fy;
		var a = c00.A * (1 - fx) * (1 - fy) + c10.A * fx * (1 - fy) + c01.A * (1 - fx) * fy + c11.A * fx * fy;

		return new Rgba32((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255),
			(byte)Math.Clamp(a, 0, 255));
	}

	public static void ApplyFxaa(Image<Rgba32> image) {
		var width = image.Width;
		var height = image.Height;
		using var tempImage = image.Clone();

		const float fxaaReduceMin = 1.0f / 128.0f;
		const float fxaaReduceMul = 1.0f / 4.0f; // Tuned for higher sensitivity to subtle edges
		const float fxaaSpanMax = 8.0f;

		Parallel.For(1, height - 1, y => {
			// ReSharper disable AccessToDisposedClosure
			var srcRowU = tempImage.DangerousGetPixelRowMemory(y - 1).Span;
			var srcRowM = tempImage.DangerousGetPixelRowMemory(y).Span;
			var srcRowD = tempImage.DangerousGetPixelRowMemory(y + 1).Span;
			var dstRow = image.DangerousGetPixelRowMemory(y).Span;

			for (var x = 1; x < width - 1; x++) {
				var rgbNw = srcRowU[x - 1];
				var rgbNe = srcRowU[x + 1];
				var rgbSw = srcRowD[x - 1];
				var rgbSe = srcRowD[x + 1];
				var rgbM = srcRowM[x];

				var lumaNw = Luma(rgbNw);
				var lumaNe = Luma(rgbNe);
				var lumaSw = Luma(rgbSw);
				var lumaSe = Luma(rgbSe);
				var lumaM = Luma(rgbM);

				var lumaMin = Math.Min(lumaM, Math.Min(Math.Min(lumaNw, lumaNe), Math.Min(lumaSw, lumaSe)));
				var lumaMax = Math.Max(lumaM, Math.Max(Math.Max(lumaNw, lumaNe), Math.Max(lumaSw, lumaSe)));

				var contrast = lumaMax - lumaMin;
				// Tuned threshold: highly sensitive but cuts off absolute noise
				if (contrast < Math.Max(0.0156f, lumaMax * 0.0312f)) {
					continue;
				}

				var dirX = -((lumaNw + lumaNe) - (lumaSw + lumaSe));
				var dirY = ((lumaNw + lumaSw) - (lumaNe + lumaSe));

				var dirReduce = Math.Max((lumaNw + lumaNe + lumaSw + lumaSe) * (0.25f * fxaaReduceMul),
					fxaaReduceMin);
				var rcpDirMin = 1.0f / (Math.Min(Math.Abs(dirX), Math.Abs(dirY)) + dirReduce);

				dirX = Math.Clamp(dirX * rcpDirMin, -fxaaSpanMax, fxaaSpanMax);
				dirY = Math.Clamp(dirY * rcpDirMin, -fxaaSpanMax, fxaaSpanMax);

				var sample1 = SampleBilinear(tempImage, x + dirX * (1.0f / 3.0f - 0.5f),
					y + dirY * (1.0f / 3.0f - 0.5f));
				var sample2 = SampleBilinear(tempImage, x + dirX * (2.0f / 3.0f - 0.5f),
					y + dirY * (2.0f / 3.0f - 0.5f));

				var r = (sample1.R * sample1.A + sample2.R * sample2.A) * 0.5f;
				var g = (sample1.G * sample1.A + sample2.G * sample2.A) * 0.5f;
				var b = (sample1.B * sample1.A + sample2.B * sample2.A) * 0.5f;
				var a = (sample1.A + sample2.A) * 0.5f;

				if (a > 0) {
					dstRow[x] = new Rgba32(
						(byte)Math.Clamp(r / a, 0, 255),
						(byte)Math.Clamp(g / a, 0, 255),
						(byte)Math.Clamp(b / a, 0, 255),
						(byte)Math.Clamp(a, 0, 255));
				}
				else {
					dstRow[x] = new Rgba32(0, 0, 0, 0);
				}
			}
		});
	}
}