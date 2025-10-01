namespace MinecraftRenderer;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

internal sealed class BiomeTintConfiguration
{
	private BiomeTintConfiguration(
		HashSet<string> grassTextures,
		HashSet<string> grassBlocks,
		HashSet<string> foliageTextures,
		HashSet<string> foliageBlocks,
		HashSet<string> dryFoliageTextures,
		HashSet<string> dryFoliageBlocks,
		HashSet<string> itemTintExclusions,
		Dictionary<string, Color> constantColors)
	{
		GrassTextures = grassTextures;
		GrassBlocks = grassBlocks;
		FoliageTextures = foliageTextures;
		FoliageBlocks = foliageBlocks;
		DryFoliageTextures = dryFoliageTextures;
		DryFoliageBlocks = dryFoliageBlocks;
		ItemTintExclusions = itemTintExclusions;
		ConstantColors = constantColors;
	}

	public HashSet<string> GrassTextures { get; }

	public HashSet<string> GrassBlocks { get; }

	public HashSet<string> FoliageTextures { get; }

	public HashSet<string> FoliageBlocks { get; }

	public HashSet<string> DryFoliageTextures { get; }

	public HashSet<string> DryFoliageBlocks { get; }

	public HashSet<string> ItemTintExclusions { get; }

	public Dictionary<string, Color> ConstantColors { get; }

	public static BiomeTintConfiguration LoadDefault()
	{
		var baseDirectory = AppContext.BaseDirectory;
		var candidates = new[]
		{
			Path.Combine(baseDirectory, "biome_tint_config.json"),
			Path.Combine(baseDirectory, "Data", "biome_tint_config.json")
		};

		foreach (var candidate in candidates)
		{
			if (File.Exists(candidate))
			{
				return LoadFromFile(candidate);
			}
		}

		throw new FileNotFoundException(
			$"Biome tint configuration file not found. Searched paths: {string.Join(", ", candidates)}.");
	}

	public static BiomeTintConfiguration LoadFromFile(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

		var json = File.ReadAllText(path);
		var dto = JsonSerializer.Deserialize<BiomeTintConfigurationDto>(json, SerializerOptions) ??
		          new BiomeTintConfigurationDto();
		return dto.ToConfiguration();
	}

	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};

	private static HashSet<string> CreateSet(IEnumerable<string>? values)
	{
		var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (values is null)
		{
			return set;
		}

		foreach (var value in values)
		{
			var normalized = value?.Trim();
			if (!string.IsNullOrEmpty(normalized))
			{
				set.Add(normalized.ToLowerInvariant());
			}
		}

		return set;
	}

	private static Dictionary<string, Color> CreateColorMap(Dictionary<string, int[]>? values)
	{
		var result = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
		if (values is null)
		{
			return result;
		}

		foreach (var (key, channels) in values)
		{
			if (string.IsNullOrWhiteSpace(key) || channels is null || channels.Length < 3)
			{
				continue;
			}

			var normalized = key.Trim().ToLowerInvariant();
			var r = (byte)Math.Clamp(channels[0], 0, 255);
			var g = (byte)Math.Clamp(channels[1], 0, 255);
			var b = (byte)Math.Clamp(channels[2], 0, 255);
			result[normalized] = new Color(new Rgb24(r, g, b));
		}

		return result;
	}

	private sealed record BiomeTintConfigurationDto
	{
		public BiomeTintCategoryDto? Grass { get; init; }
		public BiomeTintCategoryDto? Foliage { get; init; }
		public BiomeTintCategoryDto? DryFoliage { get; init; }
		public List<string>? ItemTintExclusions { get; init; }
		public Dictionary<string, int[]>? ConstantColors { get; init; }

		public BiomeTintConfiguration ToConfiguration()
		{
			return new BiomeTintConfiguration(
				CreateSet(Grass?.Textures),
				CreateSet(Grass?.Blocks),
				CreateSet(Foliage?.Textures),
				CreateSet(Foliage?.Blocks),
				CreateSet(DryFoliage?.Textures),
				CreateSet(DryFoliage?.Blocks),
				CreateSet(ItemTintExclusions),
				CreateColorMap(ConstantColors));
		}
	}

	private sealed record BiomeTintCategoryDto
	{
		public List<string>? Textures { get; init; }
		public List<string>? Blocks { get; init; }
	}
}