namespace MinecraftRenderer;

using System;
using System.Collections.Generic;
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
		return new BiomeTintConfiguration(
			CreateSet(GrassTextureKeys),
			CreateSet(GrassBlockKeys),
			CreateSet(FoliageTextureKeys),
			CreateSet(FoliageBlockKeys),
			CreateSet(DryFoliageTextureKeys),
			CreateSet(DryFoliageBlockKeys),
			CreateSet(ItemTintExclusionKeys),
			CreateColorMap());
	}

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

	private static Dictionary<string, Color> CreateColorMap()
	{
		var result = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
		foreach (var (key, r, g, b) in ConstantColorEntries)
		{
			if (string.IsNullOrWhiteSpace(key))
			{
				continue;
			}

			var normalized = key.Trim().ToLowerInvariant();
			result[normalized] = new Color(new Rgb24(r, g, b));
		}

		return result;
	}

	private static readonly string[] GrassTextureKeys =
	{
		"grass",
		"tall_grass",
		"short_grass",
		"large_fern",
		"fern",
		"grass_block_top",
		"grass_block_side_overlay",
		"grass_block_snow",
		"hanging_roots",
		"pale_hanging_moss",
		"pale_hanging_moss_tip",
		"moss",
		"moss_block",
		"moss_carpet",
		"pale_moss_block",
		"pale_moss_carpet",
		"sugar_cane",
		"cattail",
		"kelp",
		"kelp_top",
		"kelp_plant",
		"seagrass",
		"seagrass_top",
		"tall_seagrass_top",
		"sea_grass"
	};

	private static readonly string[] GrassBlockKeys =
	{
		"grass_block",
		"grass",
		"tall_grass",
		"short_grass",
		"large_fern",
		"fern",
		"hanging_roots",
		"pale_hanging_moss",
		"pale_hanging_moss_tip",
		"moss_block",
		"moss_carpet",
		"pale_moss_block",
		"pale_moss_carpet",
		"seagrass",
		"tall_seagrass",
		"kelp",
		"kelp_plant",
		"sugar_cane",
		"cattail",
		"potted_fern"
	};

	private static readonly string[] FoliageTextureKeys =
	{
		"oak_leaves",
		"spruce_leaves",
		"birch_leaves",
		"jungle_leaves",
		"acacia_leaves",
		"dark_oak_leaves",
		"mangrove_leaves",
		"pale_oak_leaves",
		"azalea_leaves",
		"flowering_azalea_leaves",
		"vine",
		"cave_vines",
		"cave_vines_body",
		"cave_vines_body_lit",
		"cave_vines_head",
		"cave_vines_head_lit",
		"cave_vines_lit",
		"cave_vines_plant",
		"cave_vines_plant_lit",
		"oak_sapling",
		"spruce_sapling",
		"birch_sapling",
		"jungle_sapling",
		"acacia_sapling",
		"dark_oak_sapling",
		"mangrove_propagule",
		"pale_oak_sapling",
		"azalea",
		"flowering_azalea",
		"big_dripleaf_top",
		"big_dripleaf_stem",
		"big_dripleaf_stem_bottom",
		"big_dripleaf_stem_mid",
		"small_dripleaf_top",
		"small_dripleaf_stem",
		"small_dripleaf_stem_top"
	};

	private static readonly string[] FoliageBlockKeys =
	{
		"oak_leaves",
		"spruce_leaves",
		"birch_leaves",
		"jungle_leaves",
		"acacia_leaves",
		"dark_oak_leaves",
		"mangrove_leaves",
		"pale_oak_leaves",
		"azalea_leaves",
		"flowering_azalea_leaves",
		"vine",
		"cave_vines",
		"cave_vines_plant",
		"cave_vines_lit",
		"cave_vines_plant_lit",
		"oak_sapling",
		"spruce_sapling",
		"birch_sapling",
		"jungle_sapling",
		"acacia_sapling",
		"dark_oak_sapling",
		"mangrove_propagule",
		"pale_oak_sapling",
		"azalea",
		"flowering_azalea",
		"big_dripleaf",
		"big_dripleaf_stem",
		"small_dripleaf",
		"small_dripleaf_stem",
		"potted_oak_sapling",
		"potted_spruce_sapling",
		"potted_birch_sapling",
		"potted_jungle_sapling",
		"potted_acacia_sapling",
		"potted_dark_oak_sapling",
		"potted_mangrove_propagule",
		"potted_pale_oak_sapling",
		"potted_azalea_bush",
		"potted_flowering_azalea_bush"
	};

	private static readonly string[] DryFoliageTextureKeys =
	{
		"dead_bush",
		"leaf_litter",
		"leaf_litter_1",
		"leaf_litter_2",
		"leaf_litter_3",
		"leaf_litter_4",
		"short_dry_grass",
		"tall_dry_grass"
	};

	private static readonly string[] DryFoliageBlockKeys =
	{
		"dead_bush",
		"leaf_litter",
		"leaf_litter_1",
		"leaf_litter_2",
		"leaf_litter_3",
		"leaf_litter_4",
		"short_dry_grass",
		"tall_dry_grass",
		"potted_dead_bush"
	};

	private static readonly string[] ItemTintExclusionKeys =
	{
		"oak_sapling",
		"spruce_sapling",
		"birch_sapling",
		"jungle_sapling",
		"acacia_sapling",
		"dark_oak_sapling",
		"mangrove_propagule",
		"pale_oak_sapling",
		"azalea",
		"flowering_azalea",
		"cherry_sapling"
	};

	private static readonly (string Key, byte R, byte G, byte B)[] ConstantColorEntries =
	{
		("birch_leaves", 128, 167, 85),
		("spruce_leaves", 97, 153, 97),
		("lily_pad", 32, 128, 48)
	};
}