namespace MinecraftRenderer;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

public sealed class ItemRegistry
{
	private readonly Dictionary<string, ItemInfo> _entries;
	private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip
	};

	private ItemRegistry(IEnumerable<ItemInfo> entries)
	{
		_entries = entries.ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
	}

	public static ItemRegistry LoadFromFile(string path)
	{
		if (!File.Exists(path))
		{
			throw new FileNotFoundException("Item registry file not found", path);
		}

		var json = File.ReadAllText(path);

		var entries = JsonSerializer.Deserialize<List<ItemInfo>>(json, Options)
			?? throw new InvalidOperationException($"Failed to parse item registry data from '{path}'.");

		return new ItemRegistry(entries.Where(static entry => !string.IsNullOrWhiteSpace(entry.Name)));
	}

	public static ItemRegistry LoadFromMinecraftAssets(string assetsRoot, IReadOnlyDictionary<string, BlockModelDefinition> modelDefinitions, IEnumerable<string>? overlayRoots = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(assetsRoot);
		ArgumentNullException.ThrowIfNull(modelDefinitions);

		var entries = MinecraftAssetLoader.LoadItemInfos(assetsRoot, modelDefinitions, overlayRoots);
		return new ItemRegistry(entries.Where(static entry => !string.IsNullOrWhiteSpace(entry.Name)));
	}

	public bool TryGetModel(string itemName, out string modelPath)
	{
		if (_entries.TryGetValue(itemName, out var info) && !string.IsNullOrWhiteSpace(info.Model))
		{
			modelPath = info.Model!;
			return true;
		}

		modelPath = string.Empty;
		return false;
	}

	public bool TryGetInfo(string itemName, out ItemInfo info)
		=> _entries.TryGetValue(itemName, out info!);

	public IReadOnlyList<string> GetAllItemNames() => _entries.Keys.ToList();

	public sealed class ItemInfo
	{
		public string Name { get; set; } = string.Empty;
		public string? Model { get; set; }
		public string? Texture { get; set; }
	}
}
