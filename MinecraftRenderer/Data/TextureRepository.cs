namespace MinecraftRenderer;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

public sealed class TextureRepository : IDisposable
{
	private readonly string _dataRoot;
	private readonly ConcurrentDictionary<string, Image<Rgba32>> _cache = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> _embedded = new(StringComparer.OrdinalIgnoreCase);
	private readonly Image<Rgba32> _missingTexture;
	private bool _disposed;

	public TextureRepository(string dataRoot, string? embeddedTextureFile = null)
	{
		_dataRoot = dataRoot;
		_missingTexture = CreateMissingTexture();

		if (!string.IsNullOrWhiteSpace(embeddedTextureFile) && File.Exists(embeddedTextureFile))
		{
			var json = File.ReadAllText(embeddedTextureFile);
			var options = new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true,
				ReadCommentHandling = JsonCommentHandling.Skip
			};

			var entries = JsonSerializer.Deserialize<List<TextureContentEntry>>(json, options);

			if (entries is not null)
			{
				foreach (var entry in entries)
				{
					if (!string.IsNullOrWhiteSpace(entry.Texture))
					{
						var key = NormalizeTextureId(entry.Name);
						_embedded[key] = entry.Texture!;
					}
				}
			}
		}
	}

	public Image<Rgba32> GetTexture(string textureId)
	{
		if (string.IsNullOrWhiteSpace(textureId))
		{
			return _missingTexture;
		}

		var normalized = NormalizeTextureId(textureId);

		return _cache.GetOrAdd(normalized, LoadTextureInternal);
	}

	public bool TryGetTexture(string textureId, out Image<Rgba32> texture)
	{
		texture = GetTexture(textureId);
		return !ReferenceEquals(texture, _missingTexture);
	}

	public void RegisterTexture(string textureId, Image<Rgba32> image, bool overwrite = true)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(textureId);
		ArgumentNullException.ThrowIfNull(image);

		var normalized = NormalizeTextureId(textureId);
		if (!overwrite && _cache.ContainsKey(normalized))
		{
			return;
		}

		_cache.AddOrUpdate(
			normalized,
			_ => image.Clone(),
			(_, existing) =>
			{
				if (!ReferenceEquals(existing, _missingTexture))
				{
					existing.Dispose();
				}
				return image.Clone();
			});
	}

	private Image<Rgba32> LoadTextureInternal(string normalized)
	{
		foreach (var candidate in EnumerateCandidatePaths(normalized))
		{
			if (File.Exists(candidate))
			{
				var loadedTexture = Image.Load<Rgba32>(candidate);
				return ProcessAnimatedTexture(candidate, loadedTexture);
			}
		}

		if (_embedded.TryGetValue(normalized, out var dataUri) && TryDecodeDataUri(dataUri, out var image))
		{
			return image;
		}

		var shortKeyIndex = normalized.LastIndexOf('/');
		if (shortKeyIndex >= 0)
		{
			var shortKey = normalized[(shortKeyIndex + 1)..];
			if (_embedded.TryGetValue(shortKey, out dataUri) && TryDecodeDataUri(dataUri, out image))
			{
				return image;
			}
		}

		return _missingTexture;
	}

	private IEnumerable<string> EnumerateCandidatePaths(string normalized)
	{
		var sanitized = normalized.TrimStart('/').Replace('\\', '/');
		if (string.IsNullOrWhiteSpace(sanitized))
		{
			yield break;
		}

		var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		void AddCandidate(string relativePath)
		{
			if (string.IsNullOrWhiteSpace(relativePath))
			{
				return;
			}

			var combined = Path.Combine(_dataRoot, relativePath.Replace('/', Path.DirectorySeparatorChar) + ".png");
			candidates.Add(combined);
		}

		AddCandidate(sanitized);

		var segments = sanitized.Split('/', StringSplitOptions.RemoveEmptyEntries);
		var workingSegments = segments;

		if (workingSegments.Length > 1 && workingSegments[0].Equals("textures", StringComparison.OrdinalIgnoreCase))
		{
			workingSegments = workingSegments.Skip(1).ToArray();
			if (workingSegments.Length > 0)
			{
				AddCandidate(string.Join('/', workingSegments));
			}
		}

		if (workingSegments.Length > 1)
		{
			var first = workingSegments[0];
			var remainder = string.Join('/', workingSegments.Skip(1));
			foreach (var variant in EnumerateFolderCandidates(first))
			{
				AddCandidate($"{variant}/{remainder}");
			}
		}

		if (workingSegments.Length > 0)
		{
			AddCandidate(workingSegments[^1]);
		}

		foreach (var candidate in candidates)
		{
			yield return candidate;
		}
	}

	private static IEnumerable<string> EnumerateFolderCandidates(string folder)
	{
		yield return folder;

		if (folder.Equals("block", StringComparison.OrdinalIgnoreCase))
		{
			yield return "blocks";
		}
		else if (folder.Equals("blocks", StringComparison.OrdinalIgnoreCase))
		{
			yield return "block";
		}
		else if (folder.Equals("item", StringComparison.OrdinalIgnoreCase))
		{
			yield return "items";
		}
		else if (folder.Equals("items", StringComparison.OrdinalIgnoreCase))
		{
			yield return "item";
		}
	}

	private static string NormalizeTextureId(string textureId)
	{
		var normalized = textureId.Trim();

		if (normalized.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[10..];
		}

		return normalized.TrimStart('/')
			.Replace('\\', '/')
			.ToLowerInvariant();
	}

	private static bool TryDecodeDataUri(string dataUri, out Image<Rgba32> image)
	{
		const string prefix = "data:image/png;base64,";
		if (dataUri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			var base64 = dataUri[prefix.Length..];
			var bytes = Convert.FromBase64String(base64);
			image = Image.Load<Rgba32>(bytes);
			return true;
		}

		image = default!;
		return false;
	}

	private static Image<Rgba32> CreateMissingTexture()
	{
		var image = new Image<Rgba32>(16, 16);
		var magenta = new Rgba32(0xFF, 0x00, 0xFF, 0xFF);
		var black = new Rgba32(0x00, 0x00, 0x00, 0xFF);

		for (var y = 0; y < 16; y++)
		{
			for (var x = 0; x < 16; x++)
			{
				var isMagenta = (x / 8 + y / 8) % 2 == 0;
				image[x, y] = isMagenta ? magenta : black;
			}
		}

		return image;
	}

	private static Image<Rgba32> ProcessAnimatedTexture(string texturePath, Image<Rgba32> image)
	{
		var metadataPath = texturePath + ".mcmeta";
		if (!File.Exists(metadataPath))
		{
			return image;
		}

		try
		{
			using var stream = File.OpenRead(metadataPath);
			using var document = JsonDocument.Parse(stream);

			if (!document.RootElement.TryGetProperty("animation", out var animation) || animation.ValueKind != JsonValueKind.Object)
			{
				return image;
			}

			var frameWidth = GetOptionalPositiveInt(animation, "width") ?? image.Width;
			var frameHeight = GetOptionalPositiveInt(animation, "height");
			var frameIndices = ExtractFrameIndices(animation);

			if (!frameHeight.HasValue)
			{
				frameHeight = InferFrameHeight(image.Height, frameWidth, frameIndices);
			}

			frameWidth = Math.Clamp(frameWidth, 1, image.Width);
			var frameHeightValue = Math.Clamp(frameHeight ?? image.Height, 1, image.Height);

			if (frameWidth == image.Width && frameHeightValue == image.Height)
			{
				return image;
			}

			var totalFramesByHeight = Math.Max(1, image.Height / frameHeightValue);
			var selectedIndex = SelectRepresentativeFrameIndex(image, frameWidth, frameHeightValue, frameIndices, totalFramesByHeight);

			selectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(totalFramesByHeight - 1, 0));
			var yOffset = selectedIndex * frameHeightValue;
			if (yOffset + frameHeightValue > image.Height)
			{
				yOffset = Math.Max(0, image.Height - frameHeightValue);
			}

			var cropRectangle = new Rectangle(0, yOffset, frameWidth, frameHeightValue);
			var cropped = image.Clone(ctx => ctx.Crop(cropRectangle));
			image.Dispose();
			return cropped;
		}
		catch (JsonException)
		{
			return image;
		}
		catch (IOException)
		{
			return image;
		}
	}

	private static int InferFrameHeight(int imageHeight, int frameWidth, IReadOnlyList<int> explicitFrames)
	{
		if (explicitFrames.Count > 0)
		{
			var maxIndex = explicitFrames.Max();
			if (maxIndex >= 0)
			{
				var divisor = Math.Max(maxIndex + 1, 1);
				var candidate = divisor > 0 ? imageHeight / divisor : imageHeight;
				if (candidate > 0)
				{
					return Math.Max(1, candidate);
				}
			}
		}

		if (frameWidth > 0)
		{
			var frameCountEstimate = Math.Max(1, imageHeight / frameWidth);
			var candidate = frameCountEstimate > 0 ? imageHeight / frameCountEstimate : imageHeight;
			if (candidate > 0)
			{
				return Math.Max(1, candidate);
			}
		}

		return Math.Max(1, imageHeight);
	}

	private static int SelectRepresentativeFrameIndex(Image<Rgba32> spriteSheet, int frameWidth, int frameHeight, IReadOnlyList<int> explicitFrames, int totalFramesByHeight)
	{
		if (explicitFrames.Count > 0)
		{
			var firstFrame = explicitFrames[0];
			return Math.Max(0, firstFrame);
		}

		var clampedFrameWidth = Math.Clamp(frameWidth, 1, spriteSheet.Width);
		var clampedFrameHeight = Math.Clamp(frameHeight, 1, spriteSheet.Height);

		var bestIndex = 0;
		long bestScore = long.MinValue;

		for (var index = 0; index < totalFramesByHeight; index++)
		{
			var yOffset = index * clampedFrameHeight;
			if (yOffset + clampedFrameHeight > spriteSheet.Height)
			{
				break;
			}

			long score = 0;
			for (var y = 0; y < clampedFrameHeight; y++)
			{
				var row = spriteSheet.DangerousGetPixelRowMemory(yOffset + y).Span;
				for (var x = 0; x < clampedFrameWidth; x++)
				{
					score += row[x].A;
				}
			}

			if (score > bestScore)
			{
				bestScore = score;
				bestIndex = index;
			}
		}

		return bestIndex;
	}

	private static IReadOnlyList<int> ExtractFrameIndices(JsonElement animation)
	{
		if (!animation.TryGetProperty("frames", out var framesElement) || framesElement.ValueKind != JsonValueKind.Array)
		{
			return Array.Empty<int>();
		}

		var indices = new List<int>();
		foreach (var frameElement in framesElement.EnumerateArray())
		{
			var index = ParseFrameIndex(frameElement);
			if (index >= 0)
			{
				indices.Add(index);
			}
		}

		return indices;
	}

	private static int ParseFrameIndex(JsonElement element)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Number:
				return element.GetInt32();
			case JsonValueKind.Object when element.TryGetProperty("index", out var indexProperty) && indexProperty.ValueKind == JsonValueKind.Number:
				return indexProperty.GetInt32();
			default:
				return -1;
		}
	}

	private static int? GetOptionalPositiveInt(JsonElement element, string propertyName)
	{
		if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number)
		{
			var value = property.GetInt32();
			return value > 0 ? value : null;
		}

		return null;
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;

		foreach (var texture in _cache.Values)
		{
			if (!ReferenceEquals(texture, _missingTexture))
			{
				texture.Dispose();
			}
		}

		_missingTexture.Dispose();
	}

	private sealed record TextureContentEntry(string Name, string? Texture);
}
