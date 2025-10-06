namespace MinecraftRenderer;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using MinecraftRenderer.Assets;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

public sealed class TextureRepository : IDisposable
{
	private readonly IReadOnlyList<string> _dataRoots;
	private readonly AssetNamespaceRegistry? _assetNamespaces;
	private readonly ConcurrentDictionary<string, Image<Rgba32>> _cache = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> _embedded = new(StringComparer.OrdinalIgnoreCase);
	private readonly Image<Rgba32> _missingTexture;
	private readonly ConcurrentDictionary<string, TextureAnimation> _animationCache =
		new(StringComparer.OrdinalIgnoreCase);
	private volatile AnimationOverride? _activeAnimationOverride;
	private readonly object _animationOverrideLock = new();
	private readonly Dictionary<uint, int> _trimPaletteLookup;
	private readonly int _trimPaletteLength;
	public Image<Rgba32>? GrassColorMap { get; private set; }
	public Image<Rgba32>? FoliageColorMap { get; private set; }
	public Image<Rgba32>? DryFoliageColorMap { get; private set; }
	private bool _disposed;

	public TextureRepository(string dataRoot, string? embeddedTextureFile = null,
		IEnumerable<string>? overlayRoots = null, AssetNamespaceRegistry? assetNamespaces = null)
	{
		_dataRoots = BuildRootList(dataRoot, overlayRoots);
		_assetNamespaces = assetNamespaces;
		_missingTexture = CreateMissingTexture();

		if (TryLoadTrimPaletteColors(out var trimPaletteColors))
		{
			_trimPaletteLength = trimPaletteColors.Length;
			_trimPaletteLookup = new Dictionary<uint, int>(trimPaletteColors.Length);
			for (var i = 0; i < trimPaletteColors.Length; i++)
			{
				_trimPaletteLookup[trimPaletteColors[i].PackedValue] = i;
			}
		}
		else
		{
			_trimPaletteLength = 0;
			_trimPaletteLookup = new Dictionary<uint, int>();
		}

		var colormapRoot = _dataRoots.FirstOrDefault(x => Directory.Exists(Path.Combine(x, "colormap")));
		if (colormapRoot is not null)
		{
			var grassPath = Path.Combine(colormapRoot, "colormap", "grass.png");
			if (File.Exists(grassPath))
			{
				GrassColorMap = Image.Load<Rgba32>(grassPath);
			}

			var foliagePath = Path.Combine(colormapRoot, "colormap", "foliage.png");
			if (File.Exists(foliagePath))
			{
				FoliageColorMap = Image.Load<Rgba32>(foliagePath);
			}

			var dryFoliagePath = Path.Combine(colormapRoot, "colormap", "dryfoliage.png");
			if (File.Exists(dryFoliagePath))
			{
				DryFoliageColorMap = Image.Load<Rgba32>(dryFoliagePath);
			}
		}

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
		var overrideContext = _activeAnimationOverride;
		if (overrideContext is not null && overrideContext.TryGetFrame(normalized, out var overrideFrame))
		{
			return overrideFrame.Image;
		}

		return _cache.GetOrAdd(normalized, LoadTextureInternal);
	}

	public bool TryGetTexture(string textureId, out Image<Rgba32> texture)
	{
		texture = GetTexture(textureId);
		return !ReferenceEquals(texture, _missingTexture);
	}

	public Image<Rgba32> GetTintedTexture(string textureId, Color tint, float strengthMultiplier = 1f, float blend = 1f)
	{
		var tintRgba = tint.ToPixel<Rgba32>();
		if (tintRgba.A == 0)
		{
			return GetTexture(textureId);
		}

		var normalized = NormalizeTextureId(textureId);
		var strengthKey = strengthMultiplier.ToString("0.###", CultureInfo.InvariantCulture);
		var blendKey = blend.ToString("0.###", CultureInfo.InvariantCulture);
		var cacheKey = $"{normalized}_{tint.ToHex()}_{strengthKey}_{blendKey}";
		var overrideSuffix = _activeAnimationOverride?.CacheKeySuffix;
		if (!string.IsNullOrEmpty(overrideSuffix))
		{
			cacheKey += $"|anim:{overrideSuffix}";
		}

		return _cache.GetOrAdd(cacheKey, _ =>
		{
			var original = GetTexture(textureId);
			if (ReferenceEquals(original, _missingTexture))
			{
				return _missingTexture;
			}

			var tinted = original.Clone();
			var tintVector = new Vector4(
				MathF.Min(tintRgba.R / 255f * strengthMultiplier, 1f),
				MathF.Min(tintRgba.G / 255f * strengthMultiplier, 1f),
				MathF.Min(tintRgba.B / 255f * strengthMultiplier, 1f),
				tintRgba.A / 255f);

			tinted.ProcessPixelRows(accessor =>
			{
				for (var y = 0; y < accessor.Height; y++)
				{
					var row = accessor.GetRowSpan(y);
					for (var x = 0; x < row.Length; x++)
					{
						var pixelVector = row[x].ToVector4();
						var tintedVector = pixelVector * tintVector;
						tintedVector.W = pixelVector.W * tintVector.W;
						tintedVector = Vector4.Clamp(tintedVector, Vector4.Zero, Vector4.One);

						var clampedBlend = blend;
						if (clampedBlend < 0f)
						{
							clampedBlend = 0f;
						}
						else if (clampedBlend > 1f)
						{
							clampedBlend = 1f;
						}

						var finalVector = clampedBlend >= 0.999f
							? tintedVector
							: Vector4.Lerp(pixelVector, tintedVector, clampedBlend);

						row[x].FromVector4(finalVector);
					}
				}
			});

			return tinted;
		});
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
				return ProcessAnimatedTexture(normalized, candidate, loadedTexture);
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

		if (TryGenerateArmorTrimTexture(normalized, out var generated))
		{
			return generated;
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

		var namespaceName = "minecraft";
		var pathWithinNamespace = sanitized;
		var colonIndex = sanitized.IndexOf(':');
		if (colonIndex >= 0)
		{
			namespaceName = sanitized[..colonIndex];
			pathWithinNamespace = sanitized[(colonIndex + 1)..];
		}

		var candidates = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		IEnumerable<string> EnumerateRoots(string targetNamespace)
		{
			if (_assetNamespaces is null)
			{
				for (var i = _dataRoots.Count - 1; i >= 0; i--)
				{
					yield return _dataRoots[i];
				}

				yield break;
			}

			IReadOnlyList<AssetNamespaceRoot> resolved = _assetNamespaces.ResolveRoots(targetNamespace);
			if (resolved.Count == 0 && !string.Equals(targetNamespace, "minecraft", StringComparison.OrdinalIgnoreCase))
			{
				resolved = _assetNamespaces.ResolveRoots("minecraft");
			}

			for (var i = resolved.Count - 1; i >= 0; i--)
			{
				yield return resolved[i].Path;
			}
		}

		void AddCandidate(string relativePath, string? explicitNamespace = null)
		{
			if (string.IsNullOrWhiteSpace(relativePath))
			{
				return;
			}

			var targetNamespace = explicitNamespace ?? namespaceName;
			var withExtension = relativePath.Replace('/', Path.DirectorySeparatorChar) + ".png";
			foreach (var root in EnumerateRoots(targetNamespace))
			{
				var combined = Path.Combine(root, withExtension);
				if (seen.Add(combined))
				{
					candidates.Add(combined);
				}
			}
		}

		AddCandidate(pathWithinNamespace);

		var segments = pathWithinNamespace.Split('/', StringSplitOptions.RemoveEmptyEntries);
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

	private static IReadOnlyList<string> BuildRootList(string primaryRoot, IEnumerable<string>? overlayRoots)
	{
		var ordered = new List<string>();

		void TryAddDirectory(string? candidate)
		{
			if (string.IsNullOrWhiteSpace(candidate))
			{
				return;
			}

			var fullPath = Path.GetFullPath(candidate);
			if (!Directory.Exists(fullPath))
			{
				return;
			}

			if (!ordered.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
			{
				ordered.Add(fullPath);
			}

			var texturesSubdirectory = Path.Combine(fullPath, "textures");
			if (Directory.Exists(texturesSubdirectory) &&
			    !ordered.Contains(texturesSubdirectory, StringComparer.OrdinalIgnoreCase))
			{
				ordered.Add(texturesSubdirectory);
			}
		}

		TryAddDirectory(primaryRoot);
		if (overlayRoots is not null)
		{
			foreach (var overlay in overlayRoots)
			{
				TryAddDirectory(overlay);
			}
		}

		return ordered;
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

	internal static string NormalizeTextureId(string textureId)
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

		image = null!;
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

	private Image<Rgba32> ProcessAnimatedTexture(string normalizedKey, string texturePath, Image<Rgba32> spriteSheet)
	{
		var animation = TryBuildTextureAnimation(texturePath, spriteSheet);
		if (animation is null || animation.Frames.Count == 0)
		{
			return spriteSheet;
		}

		_animationCache[normalizedKey] = animation;
		var firstFrame = animation.Frames[0].Image.Clone();
		spriteSheet.Dispose();
		return firstFrame;
	}

	private TextureAnimation? TryBuildTextureAnimation(string texturePath, Image<Rgba32> spriteSheet)
	{
		var metadataPath = texturePath + ".mcmeta";
		if (!File.Exists(metadataPath))
		{
			return null;
		}

		try
		{
			using var stream = File.OpenRead(metadataPath);
			using var document = JsonDocument.Parse(stream);

			if (!document.RootElement.TryGetProperty("animation", out var animationElement) ||
			    animationElement.ValueKind != JsonValueKind.Object)
			{
				return null;
			}

			var defaultFrameTime = 1;
			if (animationElement.TryGetProperty("frametime", out var frametimeProperty) &&
			    frametimeProperty.ValueKind == JsonValueKind.Number)
			{
				defaultFrameTime = Math.Max(frametimeProperty.GetInt32(), 1);
			}

			var explicitFrames = ExtractFrameSequence(animationElement, defaultFrameTime);
			var frameWidth = Math.Clamp(GetOptionalPositiveInt(animationElement, "width") ?? spriteSheet.Width,
				1, spriteSheet.Width);
			var frameHeightValue = GetOptionalPositiveInt(animationElement, "height") ?? frameWidth;
			frameHeightValue = Math.Clamp(frameHeightValue, 1, spriteSheet.Height);

			var framesPerRow = Math.Max(1, spriteSheet.Width / frameWidth);
			var framesPerColumn = Math.Max(1, spriteSheet.Height / frameHeightValue);
			var maximumFrameIndex = Math.Max(framesPerRow * framesPerColumn - 1, 0);

			var sequence = explicitFrames.Count > 0
				? explicitFrames
				: BuildSequentialFrames(maximumFrameIndex + 1, defaultFrameTime);

			var frames = new List<TextureAnimationFrame>(sequence.Count);
			foreach (var descriptor in sequence)
			{
				if (descriptor.Index < 0)
				{
					continue;
				}

				var normalizedIndex = maximumFrameIndex > 0
					? descriptor.Index % (maximumFrameIndex + 1)
					: 0;
				var column = normalizedIndex % framesPerRow;
				var row = normalizedIndex / framesPerRow;
				var x = column * frameWidth;
				var y = row * frameHeightValue;

				if (x + frameWidth > spriteSheet.Width || y + frameHeightValue > spriteSheet.Height)
				{
					continue;
				}

				var frameImage = spriteSheet.Clone(ctx => ctx.Crop(new Rectangle(x, y, frameWidth, frameHeightValue)));
				var durationMs = Math.Max(50, descriptor.FrameTime * 50);
				frames.Add(new TextureAnimationFrame(normalizedIndex, frameImage, durationMs));
			}

			if (frames.Count == 0)
			{
				return null;
			}

			var interpolate = animationElement.TryGetProperty("interpolate", out var interpolateElement) &&
			                     interpolateElement.ValueKind == JsonValueKind.True;

			return new TextureAnimation(frames, interpolate, frameWidth, frameHeightValue);
		}
		catch (JsonException)
		{
			return null;
		}
		catch (IOException)
		{
			return null;
		}
	}

	private static IReadOnlyList<AnimationFrameDescriptor> ExtractFrameSequence(JsonElement animationElement,
		int defaultFrameTime)
	{
		if (!animationElement.TryGetProperty("frames", out var framesElement) ||
		    framesElement.ValueKind != JsonValueKind.Array)
		{
			return [];
		}

		var frames = new List<AnimationFrameDescriptor>();
		foreach (var entry in framesElement.EnumerateArray())
		{
			switch (entry.ValueKind)
			{
				case JsonValueKind.Number:
					frames.Add(new AnimationFrameDescriptor(entry.GetInt32(), defaultFrameTime));
					break;
				case JsonValueKind.Object:
					var index = entry.TryGetProperty("index", out var indexElement) &&
					             indexElement.ValueKind == JsonValueKind.Number
						? indexElement.GetInt32()
						: -1;
					if (index < 0)
					{
						continue;
					}

					var frameTime = defaultFrameTime;
					if (entry.TryGetProperty("time", out var timeElement) &&
					    timeElement.ValueKind == JsonValueKind.Number)
					{
						frameTime = Math.Max(timeElement.GetInt32(), 1);
					}

					frames.Add(new AnimationFrameDescriptor(index, frameTime));
					break;
			}
		}

		return frames;
	}

	private static IReadOnlyList<AnimationFrameDescriptor> BuildSequentialFrames(int frameCount, int defaultFrameTime)
	{
		if (frameCount <= 0)
		{
			return [];
		}

		var frames = new List<AnimationFrameDescriptor>(frameCount);
		for (var i = 0; i < frameCount; i++)
		{
			frames.Add(new AnimationFrameDescriptor(i, defaultFrameTime));
		}

		return frames;
	}

	private bool TryLoadTrimPaletteColors(out Rgba32[] colors)
	{
		foreach (var candidate in EnumerateCandidatePaths("trims/color_palettes/trim_palette"))
		{
			if (!File.Exists(candidate)) continue;

			try
			{
				using var image = Image.Load<Rgba32>(candidate);
				if (image.Height <= 0) continue;

				var rowSpan = image.DangerousGetPixelRowMemory(0).Span;
				var copy = new Rgba32[rowSpan.Length];
				rowSpan.CopyTo(copy);
				colors = copy;
				return true;
			}
			catch (IOException)
			{
				continue;
			}
			catch (UnknownImageFormatException)
			{
				continue;
			}
		}

		colors = [];
		return false;
	}

	private bool TryGenerateArmorTrimTexture(string normalized, out Image<Rgba32> generated)
	{
		generated = null!;
		if (!normalized.StartsWith("trims/items/", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		var fileName = Path.GetFileName(normalized);
		if (string.IsNullOrWhiteSpace(fileName))
		{
			return false;
		}

		var trimMarkerIndex = fileName.IndexOf("_trim_", StringComparison.OrdinalIgnoreCase);
		if (trimMarkerIndex < 0)
		{
			return false;
		}

		var baseOverlayName = fileName[..(trimMarkerIndex + "_trim".Length)];
		var materialToken = fileName[(trimMarkerIndex + "_trim_".Length)..];
		if (string.IsNullOrWhiteSpace(baseOverlayName) || string.IsNullOrWhiteSpace(materialToken))
		{
			return false;
		}

		var baseOverlayId = $"trims/items/{baseOverlayName}";
		var overlayBase = GetTexture(baseOverlayId);
		if (ReferenceEquals(overlayBase, _missingTexture))
		{
			return false;
		}

		if (_trimPaletteLength == 0 || _trimPaletteLookup.Count == 0)
		{
			return false;
		}

		var materialPalette = ResolveArmorTrimPalette(materialToken);
		if (materialPalette is null || ReferenceEquals(materialPalette, _missingTexture) || materialPalette.Height == 0)
		{
			return false;
		}

		var materialPaletteRow = materialPalette.DangerousGetPixelRowMemory(0).Span;
		if (materialPaletteRow.Length == 0)
		{
			return false;
		}

		var tinted = overlayBase.Clone();

		for (var y = 0; y < overlayBase.Height; y++)
		{
			var sourceRow = overlayBase.DangerousGetPixelRowMemory(y).Span;
			var targetRow = tinted.DangerousGetPixelRowMemory(y).Span;

			for (var x = 0; x < sourceRow.Length; x++)
			{
				var sourcePixel = sourceRow[x];
				if (sourcePixel.A == 0)
				{
					continue;
				}

				if (!_trimPaletteLookup.TryGetValue(sourcePixel.PackedValue, out var paletteIndex))
				{
					targetRow[x] = sourcePixel;
					continue;
				}

				var clampedIndex = Math.Clamp(paletteIndex, 0, materialPaletteRow.Length - 1);
				var replacement = materialPaletteRow[clampedIndex];
				targetRow[x] = new Rgba32(replacement.R, replacement.G, replacement.B, sourcePixel.A);
			}
		}

		generated = tinted;
		return true;
	}

	private Image<Rgba32>? ResolveArmorTrimPalette(string materialToken)
	{
		foreach (var candidate in EnumerateArmorTrimPaletteCandidates(materialToken))
		{
			var palette = GetTexture($"trims/color_palettes/{candidate}");
			if (!ReferenceEquals(palette, _missingTexture))
			{
				return palette;
			}
		}

		return null;
	}

	private static IEnumerable<string> EnumerateArmorTrimPaletteCandidates(string materialToken)
	{
		if (string.IsNullOrWhiteSpace(materialToken))
		{
			yield break;
		}

		var normalizedMaterial = materialToken.Trim();
		if (normalizedMaterial.Length == 0)
		{
			yield break;
		}

		if (normalizedMaterial.EndsWith("_darker", StringComparison.OrdinalIgnoreCase))
		{
			yield return normalizedMaterial;
			if (normalizedMaterial.Length > 7)
			{
				yield return normalizedMaterial[..^7];
			}

			yield break;
		}

		yield return normalizedMaterial;
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

	internal bool TryGetAnimation(string textureId, out TextureAnimation animation)
	{
		var normalized = NormalizeTextureId(textureId);
		return _animationCache.TryGetValue(normalized, out animation);
	}

	internal IDisposable BeginAnimationOverride(IReadOnlyDictionary<string, TextureAnimationFrame> frames)
	{
		if (frames is null || frames.Count == 0)
		{
			return NoopScope.Instance;
		}

		Monitor.Enter(_animationOverrideLock);
		var previous = _activeAnimationOverride;
		var cacheKey = BuildOverrideCacheKey(frames);
		_activeAnimationOverride = new AnimationOverride(frames, cacheKey);
		return new AnimationOverrideScope(this, previous);
	}

	private static string BuildOverrideCacheKey(IReadOnlyDictionary<string, TextureAnimationFrame> frames)
	{
		return string.Join('|', frames
			.OrderBy(static kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
			.Select(static kvp => $"{kvp.Key}:{kvp.Value.FrameIndex}"));
	}

	private sealed record AnimationFrameDescriptor(int Index, int FrameTime);

	internal sealed class TextureAnimation
	{
		public TextureAnimation(IReadOnlyList<TextureAnimationFrame> frames, bool interpolate, int frameWidth,
			int frameHeight)
		{
			Frames = frames ?? throw new ArgumentNullException(nameof(frames));
			Interpolate = interpolate;
			FrameWidth = frameWidth;
			FrameHeight = frameHeight;
			TotalDurationMs = frames.Sum(static frame => Math.Max(frame.DurationMs, 50));
		}

		public IReadOnlyList<TextureAnimationFrame> Frames { get; }
		public bool Interpolate { get; }
		public int FrameWidth { get; }
		public int FrameHeight { get; }
		public int TotalDurationMs { get; }

		public TextureAnimationFrame GetFrameAtTime(long elapsedMilliseconds, out bool requiresDisposal)
		{
			requiresDisposal = false;
			if (Frames.Count == 0)
			{
				throw new InvalidOperationException("Animation does not contain frames.");
			}

			if (TotalDurationMs <= 0)
			{
				return Frames[0];
			}

			var normalized = (int)(elapsedMilliseconds % TotalDurationMs);
			var accumulated = 0;
			for (var index = 0; index < Frames.Count; index++)
			{
				var frame = Frames[index];
				var duration = Math.Max(frame.DurationMs, 50);
				var nextAccumulated = accumulated + duration;
				if (normalized < nextAccumulated)
				{
					if (!Interpolate || Frames.Count == 1)
					{
						return frame;
					}

					var spanWithinFrame = normalized - accumulated;
					if (spanWithinFrame <= 0)
					{
						return frame;
					}

					var progress = duration <= 0 ? 0d : spanWithinFrame / (double)duration;
					if (progress <= 0d)
					{
						return frame;
					}

					if (progress >= 0.999d)
					{
						var nextFrameNearly = Frames[(index + 1) % Frames.Count];
						return nextFrameNearly;
					}

					requiresDisposal = true;
					var nextIndex = (index + 1) % Frames.Count;
					var blendedFrame = CreateInterpolatedFrame(frame, Frames[nextIndex], progress);
					return blendedFrame;
				}

				accumulated = nextAccumulated;
			}

			return Frames[^1];
		}

		private TextureAnimationFrame CreateInterpolatedFrame(TextureAnimationFrame current,
			TextureAnimationFrame next, double progress)
		{
			var alpha = (float)Math.Clamp(progress, 0d, 1d);
			var blended = current.Image.Clone();
			var width = Math.Min(blended.Width, next.Image.Width);
			var height = Math.Min(blended.Height, next.Image.Height);

			for (var y = 0; y < height; y++)
			{
				var targetRow = blended.DangerousGetPixelRowMemory(y).Span;
				var nextRow = next.Image.DangerousGetPixelRowMemory(y).Span;
				var maxX = Math.Min(targetRow.Length, nextRow.Length);
				for (var x = 0; x < maxX; x++)
				{
					var basePixel = targetRow[x];
					var nextPixel = nextRow[x];
					targetRow[x] = BlendPixel(basePixel, nextPixel, alpha);
				}
			}

			var fractionKey = (int)Math.Clamp(Math.Round(alpha * 1000d), 0d, 1000d);
			var frameKey = HashCode.Combine(current.FrameIndex, next.FrameIndex, fractionKey);
			return new TextureAnimationFrame(frameKey, blended, current.DurationMs);
		}

		private static Rgba32 BlendPixel(Rgba32 source, Rgba32 target, float amount)
		{
			amount = Math.Clamp(amount, 0f, 1f);
			var inverse = 1f - amount;
			var r = (byte)Math.Clamp((source.R * inverse) + (target.R * amount), 0f, 255f);
			var g = (byte)Math.Clamp((source.G * inverse) + (target.G * amount), 0f, 255f);
			var b = (byte)Math.Clamp((source.B * inverse) + (target.B * amount), 0f, 255f);
			var a = (byte)Math.Clamp((source.A * inverse) + (target.A * amount), 0f, 255f);
			return new Rgba32(r, g, b, a);
		}
	}

	internal sealed class TextureAnimationFrame
	{
		public TextureAnimationFrame(int frameIndex, Image<Rgba32> image, int durationMs)
		{
			FrameIndex = frameIndex;
			Image = image ?? throw new ArgumentNullException(nameof(image));
			DurationMs = durationMs;
		}

		public int FrameIndex { get; }
		public Image<Rgba32> Image { get; }
		public int DurationMs { get; }
	}

	private sealed class AnimationOverride
	{
		private readonly IReadOnlyDictionary<string, TextureAnimationFrame> _frames;

		public AnimationOverride(IReadOnlyDictionary<string, TextureAnimationFrame> frames, string cacheKeySuffix)
		{
			_frames = frames;
			CacheKeySuffix = cacheKeySuffix;
		}

		public string CacheKeySuffix { get; }

		public bool TryGetFrame(string normalizedTextureId, out TextureAnimationFrame frame)
			=> _frames.TryGetValue(normalizedTextureId, out frame);
	}

	private sealed class AnimationOverrideScope : IDisposable
	{
		private readonly TextureRepository _owner;
		private readonly AnimationOverride? _previous;
		private bool _disposed;

		public AnimationOverrideScope(TextureRepository owner, AnimationOverride? previous)
		{
			_owner = owner;
			_previous = previous;
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_owner._activeAnimationOverride = _previous;
			Monitor.Exit(_owner._animationOverrideLock);
			_disposed = true;
		}
	}

	private sealed class NoopScope : IDisposable
	{
		public static NoopScope Instance { get; } = new();

		public void Dispose()
		{
			// No-op.
		}
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_activeAnimationOverride = null;

		foreach (var animation in _animationCache.Values)
		{
			foreach (var frame in animation.Frames)
			{
				frame.Image.Dispose();
			}
		}

		_animationCache.Clear();

		foreach (var texture in _cache.Values)
		{
			if (!ReferenceEquals(texture, _missingTexture))
			{
				texture.Dispose();
			}
		}

		_missingTexture.Dispose();
		GrassColorMap?.Dispose();
		FoliageColorMap?.Dispose();
		DryFoliageColorMap?.Dispose();
	}

	private sealed record TextureContentEntry(string Name, string? Texture);
}