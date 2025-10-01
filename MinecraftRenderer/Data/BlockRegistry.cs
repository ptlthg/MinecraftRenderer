namespace MinecraftRenderer;

using System.Linq;
using System.Text.Json;

public sealed class BlockRegistry
{
	private readonly Dictionary<string, BlockInfo> _entries;

	private BlockRegistry(IEnumerable<BlockInfo> entries)
	{
		_entries = entries.ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
	}

	public static BlockRegistry LoadFromFile(string path)
	{
		if (!File.Exists(path))
		{
			throw new FileNotFoundException("Block registry file not found", path);
		}

		var json = File.ReadAllText(path);
		var options = new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true,
			ReadCommentHandling = JsonCommentHandling.Skip
		};

		var entries = JsonSerializer.Deserialize<List<BlockInfo>>(json, options)
		              ?? throw new InvalidOperationException($"Failed to parse block registry data from '{path}'.");

		return new BlockRegistry(entries.Where(static entry => !string.IsNullOrWhiteSpace(entry.Name)));
	}

	public static BlockRegistry LoadFromMinecraftAssets(string assetsRoot,
		IReadOnlyDictionary<string, BlockModelDefinition> modelDefinitions, IEnumerable<string>? overlayRoots = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(assetsRoot);
		ArgumentNullException.ThrowIfNull(modelDefinitions);

		var entries = MinecraftAssetLoader.LoadBlockInfos(assetsRoot, modelDefinitions, overlayRoots);
		return new BlockRegistry(entries.Where(static entry => !string.IsNullOrWhiteSpace(entry.Name)));
	}

	public bool TryGetModel(string blockName, out string modelPath)
	{
		if (_entries.TryGetValue(blockName, out var info) && !string.IsNullOrWhiteSpace(info.Model))
		{
			modelPath = info.Model!;
			return true;
		}

		modelPath = string.Empty;
		return false;
	}

	public IReadOnlyList<string> GetAllBlockNames() => _entries.Keys.ToList();

	public sealed class BlockInfo
	{
		public string Name { get; set; } = string.Empty;
		public string? BlockState { get; set; }
		public string? Model { get; set; }
		public string? Texture { get; set; }
	}
}