namespace MinecraftRenderer;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using MinecraftRenderer.Assets;

public sealed class ItemRegistry
{
	private readonly Dictionary<string, ItemInfo> _entries;
	private readonly Dictionary<string, ItemInfo> _skyblockItemAliases;

	private static readonly JsonSerializerOptions Options = new JsonSerializerOptions {
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip
	};

	private ItemRegistry(IEnumerable<ItemInfo> entries) {
		var materializedEntries = entries.ToList();
		_entries = materializedEntries.ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
		_skyblockItemAliases = materializedEntries
			.Select(static entry => (Entry: entry, Alias: GetSkyblockItemAlias(entry.Name)))
			.Where(static candidate => candidate.Alias is not null)
			.GroupBy(static candidate => candidate.Alias!, StringComparer.OrdinalIgnoreCase)
			.Where(static group => group.Count() == 1)
			.ToDictionary(static group => group.Key, static group => group.Single().Entry,
				StringComparer.OrdinalIgnoreCase);
	}

	public static ItemRegistry LoadFromFile(string path) {
		if (!File.Exists(path)) {
			throw new FileNotFoundException("Item registry file not found", path);
		}

		var json = File.ReadAllText(path);

		var entries = JsonSerializer.Deserialize<List<ItemInfo>>(json, Options)
		              ?? throw new InvalidOperationException($"Failed to parse item registry data from '{path}'.");

		return new ItemRegistry(entries.Where(static entry => !string.IsNullOrWhiteSpace(entry.Name)));
	}

	public static ItemRegistry LoadFromMinecraftAssets(string assetsRoot,
		IReadOnlyDictionary<string, BlockModelDefinition> modelDefinitions, IEnumerable<string>? overlayRoots = null,
		AssetNamespaceRegistry? assetNamespaces = null) {
		ArgumentException.ThrowIfNullOrWhiteSpace(assetsRoot);
		ArgumentNullException.ThrowIfNull(modelDefinitions);

		var entries = MinecraftAssetLoader.LoadItemInfos(assetsRoot, modelDefinitions, overlayRoots, assetNamespaces);
		return new ItemRegistry(entries.Where(static entry => !string.IsNullOrWhiteSpace(entry.Name)));
	}

	public bool TryGetModel(string itemName, out string modelPath) {
		if (_entries.TryGetValue(itemName, out var info) && !string.IsNullOrWhiteSpace(info.Model)) {
			modelPath = info.Model!;
			return true;
		}

		modelPath = string.Empty;
		return false;
	}

	public bool TryGetInfo(string itemName, out ItemInfo info)
		=> _entries.TryGetValue(itemName, out info!);

	internal bool TryGetSkyblockItemInfo(string skyblockId, out ItemInfo info)
		=> _skyblockItemAliases.TryGetValue(skyblockId, out info!);

	public IReadOnlyList<string> GetAllItemNames() => _entries.Keys.ToList();

	private static string? GetSkyblockItemAlias(string itemName) {
		var separator = itemName.IndexOf(':');
		if (separator <= 0 || separator == itemName.Length - 1) {
			return null;
		}

		var itemNamespace = itemName[..separator];
		var path = itemName[(separator + 1)..].Replace('\\', '/').Trim('/');
		if (itemNamespace.Equals("minecraft", StringComparison.OrdinalIgnoreCase) ||
		    !path.StartsWith("item/", StringComparison.OrdinalIgnoreCase)) {
			return null;
		}

		var lastSeparator = path.LastIndexOf('/');
		return lastSeparator >= 0 && lastSeparator < path.Length - 1
			? path[(lastSeparator + 1)..]
			: null;
	}

	public sealed class ItemInfo
	{
		public string Name { get; set; } = string.Empty;
		public string? Model { get; set; }
		public string? Texture { get; set; }
		internal ItemModelSelector? Selector { get; set; }
		public Dictionary<int, ItemTintInfo> LayerTints { get; set; } = new();
	}

	public sealed class ItemTintInfo
	{
		public ItemTintKind Kind { get; set; } = ItemTintKind.Unspecified;
		public Color? DefaultColor { get; set; }
	}

	public enum ItemTintKind
	{
		Unspecified,
		Dye,
		Constant,
		Unknown
	}
}
