using System;
using System.Collections.Generic;
using System.IO;
using BenchmarkDotNet.Attributes;
using MinecraftRenderer;
using MinecraftRenderer.Nbt;
using MinecraftRenderer.TexturePacks;

namespace MinecraftRenderer.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
public class RendererBenchmarks
{
	private MinecraftBlockRenderer? _renderer;
	private MinecraftBlockRenderer.BlockRenderOptions _blockOptions = MinecraftBlockRenderer.BlockRenderOptions.Default;
	private MinecraftBlockRenderer.BlockRenderOptions _itemOptions = MinecraftBlockRenderer.BlockRenderOptions.Default;
	private NbtCompound _diamondSwordNbt = null!;
	private NbtCompound _compassNbt = null!;
	private MinecraftBlockRenderer? _hypixelRenderer;
	private MinecraftBlockRenderer.BlockRenderOptions _hypixelItemOptions = MinecraftBlockRenderer.BlockRenderOptions.Default;
	private (MinecraftBlockRenderer.ItemRenderData ItemData, NbtCompound Nbt)[] _hypixelHeadSamples = Array.Empty<(MinecraftBlockRenderer.ItemRenderData, NbtCompound)>();

	private static readonly string[] HypixelPackIds = ["hypixelplus"];

	private static readonly (string Id, (string Key, string Value)[] Extras)[] HypixelHeadDefinitions =
	[
		("ABICASE", new[] { ("model", "BLUE_AQUA") }),
		("AGARIMOO_ARTIFACT", Array.Empty<(string, string)>()),
		("ANITA_TALISMAN", Array.Empty<(string, string)>()),
		("BAT_PERSON_RING", Array.Empty<(string, string)>()),
		("BINGO_TALISMAN", Array.Empty<(string, string)>()),
		("CANDY_ARTIFACT", Array.Empty<(string, string)>())
	];

	[GlobalSetup]
	public void Setup()
	{
		var assetsDirectory = LocateAssetsDirectory();
		_renderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(assetsDirectory);
		_blockOptions = MinecraftBlockRenderer.BlockRenderOptions.Default with { Size = 256, PerspectiveAmount = 0.12f };
		_itemOptions = MinecraftBlockRenderer.BlockRenderOptions.Default with { Size = 128 };

		_diamondSwordNbt = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("minecraft:diamond_sword")),
			new KeyValuePair<string, NbtTag>("Count", new NbtByte((sbyte)1))
		});

		_compassNbt = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("minecraft:compass")),
			new KeyValuePair<string, NbtTag>("Count", new NbtByte((sbyte)1))
		});

		var texturePackDirectory = LocateTexturePackDirectory();
		var hypixelPackPath = Path.Combine(texturePackDirectory, "Hypixel+ 0.23.4 for 1.21.8");
		if (!Directory.Exists(hypixelPackPath))
		{
			throw new DirectoryNotFoundException(
				$"Unable to find the Hypixel+ texture pack at '{hypixelPackPath}'. Ensure the pack is available for texture pack benchmarks.");
		}

		var packRegistry = TexturePackRegistry.Create();
		packRegistry.RegisterPack(hypixelPackPath);
		_hypixelRenderer = MinecraftBlockRenderer.CreateFromMinecraftAssets(assetsDirectory, packRegistry, HypixelPackIds);
		_hypixelItemOptions = _itemOptions with { PackIds = HypixelPackIds };
		_hypixelHeadSamples = CreateHypixelHeadSamples();
	}

	[GlobalCleanup]
	public void Cleanup()
	{
		_renderer?.Dispose();
		_hypixelRenderer?.Dispose();
	}

	[Benchmark]
	public (int Width, int Height) RenderStoneBlock()
	{
		var renderer = _renderer ?? throw new InvalidOperationException("Renderer not initialized.");
		using var image = renderer.RenderBlock("stone", _blockOptions);
		return (image.Width, image.Height);
	}

	[Benchmark]
	public (int Width, int Height) RenderDiamondSwordItem()
	{
		var renderer = _renderer ?? throw new InvalidOperationException("Renderer not initialized.");
		using var image = renderer.RenderItem("diamond_sword", _itemOptions);
		return (image.Width, image.Height);
	}

	[Benchmark]
	public int ComputeDiamondSwordResourceId()
	{
		var renderer = _renderer ?? throw new InvalidOperationException("Renderer not initialized.");
		var result = renderer.ComputeResourceId("diamond_sword", _itemOptions);
		return result.ResourceId.GetHashCode();
	}

	[Benchmark]
	public int RenderDiamondSwordFromNbt()
	{
		var renderer = _renderer ?? throw new InvalidOperationException("Renderer not initialized.");
		using var composite = renderer.RenderItemFromNbtWithResourceId(_diamondSwordNbt, _itemOptions);
		return composite.ResourceId.ResourceId.GetHashCode();
	}

	[Benchmark]
	public int RenderAnimatedCompassFromNbt()
	{
		var renderer = _renderer ?? throw new InvalidOperationException("Renderer not initialized.");
		using var animated = renderer.RenderAnimatedItemFromNbtWithResourceId(_compassNbt, _itemOptions);
		return animated.ResourceId.ResourceId.GetHashCode();
	}

	[Benchmark]
	public int RenderHypixelPlayerHeads()
	{
		var renderer = _hypixelRenderer ?? throw new InvalidOperationException("Hypixel+ texture pack renderer not initialized.");
		var options = _hypixelItemOptions;
		var hash = 17;
		foreach (var sample in _hypixelHeadSamples)
		{
			using var image = renderer.RenderItem("player_head", sample.ItemData, options);
			hash = HashCode.Combine(hash, image.Width, image.Height);
		}
		return hash;
	}

	[Benchmark]
	public int RenderHypixelPlayerHeadsFromNbt()
	{
		var renderer = _hypixelRenderer ?? throw new InvalidOperationException("Hypixel+ texture pack renderer not initialized.");
		var options = _hypixelItemOptions;
		var hash = 23;
		foreach (var sample in _hypixelHeadSamples)
		{
			using var image = renderer.RenderItemFromNbt(sample.Nbt, options);
			hash = HashCode.Combine(hash, image.Width, image.Height);
		}
		return hash;
	}

	[Benchmark]
	public int ComputeHypixelPlayerHeadResourceIds()
	{
		var renderer = _hypixelRenderer ?? throw new InvalidOperationException("Hypixel+ texture pack renderer not initialized.");
		var options = _hypixelItemOptions;
		var hash = 31;
		foreach (var sample in _hypixelHeadSamples)
		{
			var result = renderer.ComputeResourceIdFromNbt(sample.Nbt, options);
			hash = HashCode.Combine(hash, result.ResourceId.GetHashCode());
		}
		return hash;
	}

	private static (MinecraftBlockRenderer.ItemRenderData ItemData, NbtCompound Nbt)[] CreateHypixelHeadSamples()
	{
		var samples = new List<(MinecraftBlockRenderer.ItemRenderData, NbtCompound)>(HypixelHeadDefinitions.Length);
		foreach (var (id, extras) in HypixelHeadDefinitions)
		{
			samples.Add(CreateHypixelHeadSample(id, extras));
		}
		return samples.ToArray();
	}

	private static (MinecraftBlockRenderer.ItemRenderData ItemData, NbtCompound Nbt) CreateHypixelHeadSample(
		string skyblockId, (string Key, string Value)[] extras)
	{
		var customEntries = new List<KeyValuePair<string, NbtTag>> { new("id", new NbtString(skyblockId)) };
		foreach (var (key, value) in extras)
		{
			customEntries.Add(new KeyValuePair<string, NbtTag>(key, new NbtString(value)));
		}

		var customData = new NbtCompound(customEntries);
		var components = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("minecraft:custom_data", customData)
		});

		var root = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("minecraft:player_head")),
			new KeyValuePair<string, NbtTag>("Count", new NbtByte((sbyte)1)),
			new KeyValuePair<string, NbtTag>("components", components)
		});

		var itemData = new MinecraftBlockRenderer.ItemRenderData(CustomData: customData);
		return (itemData, root);
	}

	private static string LocateTexturePackDirectory()
	{
		var current = new DirectoryInfo(AppContext.BaseDirectory);
		while (current is not null)
		{
			var candidate = Path.Combine(current.FullName, "texturepacks");
			if (Directory.Exists(candidate))
			{
				return candidate;
			}

			current = current.Parent;
		}

		throw new DirectoryNotFoundException("Unable to find the texture pack directory for benchmarks.");
	}

	private static string LocateAssetsDirectory()
	{
		var current = new DirectoryInfo(AppContext.BaseDirectory);
		while (current is not null)
		{
			var candidate = Path.Combine(current.FullName, "minecraft");
			if (Directory.Exists(candidate))
			{
				return candidate;
			}

			current = current.Parent;
		}

		throw new DirectoryNotFoundException("Unable to find the minecraft assets directory for benchmarks.");
	}
}
