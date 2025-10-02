using System.Globalization;
using System.Linq;
using MinecraftRenderer;
using MinecraftRenderer.Snbt;
using MinecraftRenderer.TexturePacks;

var options = ParseArguments(args);

if (options.ShowHelp)
{
	PrintHelp();
	return;
}

var assetDirectory = ResolveAssetDirectory(options.DataDirectory);
if (assetDirectory is null)
{
	Console.Error.WriteLine("Unable to locate Minecraft asset data. Provide --data <path> explicitly.");
	Environment.ExitCode = 1;
	return;
}

var resolvedAssetDirectory = assetDirectory!;

var outputDirectory = options.OutputDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "atlases");
Directory.CreateDirectory(outputDirectory);

var views = BuildViews(options.ViewNames);

if (options.TexturePackIds is { Count: > 0 })
{
	var packList = options.TexturePackIds.ToArray();
	views = views.Select(view => view with
	{
		Options = view.Options with { PackIds = packList }
	}).ToList();
}

if (views.Count == 0)
{
	Console.Error.WriteLine("No valid views selected. Use --views to specify a comma-separated list of: " + string.Join(", ", MinecraftAtlasGenerator.DefaultViews.Select(v => v.Name)) + ".");
	Environment.ExitCode = 1;
	return;
}

var blockFilter = options.Blocks?.Count > 0 ? options.Blocks : null;
var itemFilter = options.Items?.Count > 0 ? options.Items : null;
var includeBlocks = options.IncludeBlocks && (blockFilter is null || blockFilter.Count > 0);
var includeItems = options.IncludeItems && (itemFilter is null || itemFilter.Count > 0);

if (!includeBlocks && !includeItems)
{
	Console.Error.WriteLine("Nothing to render. Enable blocks/items or provide non-empty filters.");
	Environment.ExitCode = 1;
	return;
}

Console.WriteLine($"Data directory: {resolvedAssetDirectory.Path}");
Console.WriteLine($"Output directory: {outputDirectory}");
Console.WriteLine($"Views: {string.Join(", ", views.Select(v => v.Name))}");

TexturePackRegistry? texturePackRegistry = null;
if ((options.TexturePackDirectories is { Count: > 0 } || options.TexturePackIds is { Count: > 0 })
	&& resolvedAssetDirectory.IsAggregatedJson)
{
	Console.Error.WriteLine("Texture packs require a Minecraft assets directory. Aggregated JSON rendering does not support packs.");
	Environment.ExitCode = 1;
	return;
}

if (!resolvedAssetDirectory.IsAggregatedJson)
{
	texturePackRegistry = InitializeTexturePackRegistry(options, resolvedAssetDirectory.Path);
}

using var renderer = resolvedAssetDirectory.IsAggregatedJson
	? MinecraftBlockRenderer.CreateFromDataDirectory(resolvedAssetDirectory.Path)
	: MinecraftBlockRenderer.CreateFromMinecraftAssets(resolvedAssetDirectory.Path, texturePackRegistry,
		options.TexturePackIds);

var results = MinecraftAtlasGenerator.GenerateAtlases(
	renderer,
	outputDirectory,
	views,
	options.TileSize,
	options.Columns,
	options.Rows,
	blockFilter,
	itemFilter,
	includeBlocks,
	includeItems).ToList();

if (!string.IsNullOrWhiteSpace(options.SnbtItemDirectory))
{
	var snbtDirectory = Path.GetFullPath(options.SnbtItemDirectory);
	if (!Directory.Exists(snbtDirectory))
	{
		Console.Error.WriteLine($"SNBT directory '{options.SnbtItemDirectory}' was not found.");
		Environment.ExitCode = 1;
		return;
	}

	Console.WriteLine();
	Console.WriteLine($"Loading SNBT items from {snbtDirectory}...");
	var snbtEntries = SnbtItemAtlasGenerator.LoadDirectory(snbtDirectory);
	var snbtCount = snbtEntries.Count;
	var parseErrors = snbtEntries.Count(entry => entry.Error is not null);
	Console.WriteLine(parseErrors > 0
		? $"Found {snbtCount} SNBT {(snbtCount == 1 ? "item" : "items")}, {parseErrors} with parse errors."
		: $"Found {snbtCount} SNBT {(snbtCount == 1 ? "item" : "items")}." );

	if (snbtCount > 0)
	{
		var snbtOutputDirectory = Path.Combine(outputDirectory, "snbt");
		var snbtResults = SnbtItemAtlasGenerator.GenerateAtlases(
			renderer,
			snbtOutputDirectory,
			views,
			options.TileSize,
			options.Columns,
			options.Rows,
			snbtEntries);
		results.AddRange(snbtResults);
	}
}

if (options.GenerateDebugBlock)
{
	var debugOutput = Path.Combine(outputDirectory, "debug_block");
	var debugResults = DebugBlockGenerator.GenerateDebugBlockAtlases(renderer, debugOutput, views, options.TileSize);
	results.AddRange(debugResults);
}

Console.WriteLine();
Console.WriteLine($"Generated {results.Count} atlas {(results.Count == 1 ? "file" : "files")}.");
foreach (var result in results)
{
	Console.WriteLine($" - [{result.Category}/{result.ViewName}/page {result.PageNumber}] {result.ImagePath}");
}

return;

static CliOptions ParseArguments(string[] arguments)
{
	var options = new CliOptions();

	for (var i = 0; i < arguments.Length; i++)
	{
		var arg = arguments[i];
		switch (arg)
		{
			case "-h":
			case "--help":
				options.ShowHelp = true;
				break;
			case "--data":
				options.DataDirectory = ReadNext(arguments, ref i, "--data");
				break;
			case "--output":
				options.OutputDirectory = ReadNext(arguments, ref i, "--output");
				break;
			case "--tile-size":
				options.TileSize = ParseInt(ReadNext(arguments, ref i, "--tile-size"), nameof(options.TileSize));
				break;
			case "--columns":
				options.Columns = ParseInt(ReadNext(arguments, ref i, "--columns"), nameof(options.Columns));
				break;
			case "--rows":
				options.Rows = ParseInt(ReadNext(arguments, ref i, "--rows"), nameof(options.Rows));
				break;
			case "--blocks":
				options.Blocks = ParseList(ReadNext(arguments, ref i, "--blocks"));
				break;
			case "--items":
				options.Items = ParseList(ReadNext(arguments, ref i, "--items"));
				break;
			case "--no-blocks":
				options.IncludeBlocks = false;
				break;
			case "--no-items":
				options.IncludeItems = false;
				break;
			case "--views":
				options.ViewNames = ParseList(ReadNext(arguments, ref i, "--views")) ?? new List<string>();
				break;
			case "--debug-block":
				options.GenerateDebugBlock = true;
				break;
			case "--texture-pack-dir":
				options.TexturePackDirectories ??= new List<string>();
				options.TexturePackDirectories.Add(ReadNext(arguments, ref i, "--texture-pack-dir"));
				break;
			case "--texture-pack-id":
				options.TexturePackIds ??= new List<string>();
				options.TexturePackIds.Add(ReadNext(arguments, ref i, "--texture-pack-id"));
				break;
			case "--snbt-dir":
				options.SnbtItemDirectory = ReadNext(arguments, ref i, "--snbt-dir");
				break;
			default:
				Console.Error.WriteLine($"Unknown argument '{arg}'. Use --help for usage information.");
				environmentExit();
				break;
		}
	}

	return options;

	static void environmentExit()
	{
		Environment.ExitCode = 1;
		Environment.Exit(1);
	}
}

static AssetDirectory? ResolveAssetDirectory(string? provided)
{
	if (!string.IsNullOrWhiteSpace(provided))
	{
		var candidate = Path.GetFullPath(provided);
		var kind = GetDirectoryKind(candidate);
		if (kind != DirectoryKind.None)
		{
			return new AssetDirectory(candidate, kind == DirectoryKind.AggregatedJson);
		}

		Console.Error.WriteLine($"Provided --data path '{provided}' does not contain aggregated JSON or Minecraft assets.");
		return null;
	}

	if (TryLocateAssetDirectory(Directory.GetCurrentDirectory(), out var locatedFromCwd))
	{
		return locatedFromCwd;
	}

	if (TryLocateAssetDirectory(AppContext.BaseDirectory, out var locatedFromBase))
	{
		return locatedFromBase;
	}

	return null;
}

static bool TryLocateAssetDirectory(string startDirectory, out AssetDirectory? assetDirectory)
{
	var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
	while (current is not null)
	{
		foreach (var candidate in EnumerateCandidateDirectories(current))
		{
			var kind = GetDirectoryKind(candidate);
			if (kind == DirectoryKind.None)
			{
				continue;
			}

			assetDirectory = new AssetDirectory(candidate, kind == DirectoryKind.AggregatedJson);
			return true;
		}

		current = current.Parent;
	}

	assetDirectory = null;
	return false;
}

static IEnumerable<string> EnumerateCandidateDirectories(DirectoryInfo current)
{
	yield return current.FullName;
	yield return Path.Combine(current.FullName, "data");
	yield return Path.Combine(current.FullName, "Data");
	yield return Path.Combine(current.FullName, "minecraft");
	yield return Path.Combine(current.FullName, "Minecraft");
}

static DirectoryKind GetDirectoryKind(string path)
{
	if (string.IsNullOrWhiteSpace(path))
	{
		return DirectoryKind.None;
	}

	var fullPath = Path.GetFullPath(path);
	if (!Directory.Exists(fullPath))
	{
		return DirectoryKind.None;
	}

	if (IsAggregatedDataDirectory(fullPath))
	{
		return DirectoryKind.AggregatedJson;
	}

	if (IsAssetsDirectory(fullPath))
	{
		return DirectoryKind.AssetFolder;
	}

	return DirectoryKind.None;
}

static bool IsAggregatedDataDirectory(string path)
{
	return File.Exists(Path.Combine(path, "blocks_models.json"))
		&& File.Exists(Path.Combine(path, "blocks_textures.json"));
}

static bool IsAssetsDirectory(string path)
{
	return Directory.Exists(Path.Combine(path, "models"))
		&& Directory.Exists(Path.Combine(path, "blockstates"))
		&& Directory.Exists(Path.Combine(path, "textures"));
}

static List<MinecraftAtlasGenerator.AtlasView> BuildViews(IReadOnlyList<string>? requested)
{
	var allViews = MinecraftAtlasGenerator.DefaultViews.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);

	if (requested is null || requested.Count == 0)
	{
		return allViews.Values.ToList();
	}

	var selected = new List<MinecraftAtlasGenerator.AtlasView>(requested.Count);
	foreach (var name in requested)
	{
		if (allViews.TryGetValue(name, out var view))
		{
			selected.Add(view);
		}
		else
		{
			Console.Error.WriteLine($"Unknown view '{name}'. Valid options: {string.Join(", ", allViews.Keys)}");
		}
	}

	return selected;
}

static string ReadNext(string[] arguments, ref int index, string name)
{
	if (index + 1 >= arguments.Length)
	{
		Console.Error.WriteLine($"Missing value for {name}.");
		Environment.Exit(1);
	}

	return arguments[++index];
}

static int ParseInt(string value, string propertyName)
{
	if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
	{
		Console.Error.WriteLine($"Unable to parse integer value '{value}' for {propertyName}.");
		Environment.Exit(1);
	}

	return result;
}

static List<string>? ParseList(string? value)
{
	if (string.IsNullOrWhiteSpace(value))
	{
		return null;
	}

	return value
		.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
		.Where(static entry => !string.IsNullOrWhiteSpace(entry))
		.ToList();
}

static TexturePackRegistry? InitializeTexturePackRegistry(CliOptions options, string assetsPath)
{
	var needsRegistry = options.TexturePackDirectories is { Count: > 0 } || options.TexturePackIds is { Count: > 0 };
	var discoveredDirectories = DiscoverDefaultTexturePacks(assetsPath);
	if (!needsRegistry && discoveredDirectories.Count == 0)
	{
		return null;
	}

	var registry = TexturePackRegistry.Create();
	var registeredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	void RegisterDirectory(string directory)
	{
		try
		{
			var registered = registry.RegisterPack(directory);
			if (registeredIds.Add(registered.Id))
			{
				Console.WriteLine($"Registered texture pack '{registered.Id}' from '{directory}'.");
			}
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Failed to register texture pack directory '{directory}': {ex.Message}");
			Environment.Exit(1);
		}
	}

	if (discoveredDirectories.Count > 0)
	{
		foreach (var directory in discoveredDirectories)
		{
			RegisterDirectory(directory);
		}
	}

	if (options.TexturePackDirectories is { Count: > 0 })
	{
		foreach (var directory in options.TexturePackDirectories)
		{
			RegisterDirectory(directory);
		}
	}

	if (options.TexturePackIds is { Count: > 0 })
	{
		foreach (var packId in options.TexturePackIds)
		{
			if (!registry.TryGetPack(packId, out _))
			{
				Console.Error.WriteLine($"Texture pack id '{packId}' was not registered. Use --texture-pack-dir to register it.");
				Environment.Exit(1);
			}
		}
	}

	return registry;
}

static List<string> DiscoverDefaultTexturePacks(string assetsPath)
{
	var results = new List<string>();
	var searchRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		Path.Combine(Directory.GetCurrentDirectory(), "texturepacks"),
		Path.Combine(Path.GetDirectoryName(Path.GetFullPath(assetsPath)) ?? assetsPath, "texturepacks")
	};

	foreach (var root in searchRoots)
	{
		if (!Directory.Exists(root))
		{
			continue;
		}

		foreach (var directory in Directory.EnumerateDirectories(root))
		{
			var metaPath = Path.Combine(directory, "meta.json");
			if (File.Exists(metaPath))
			{
				results.Add(directory);
			}
		}
	}

	return results;
}

static void PrintHelp()
{
	Console.WriteLine("Minecraft Renderer – Atlas Generator");
	Console.WriteLine();
	Console.WriteLine("Usage:");
	Console.WriteLine("  dotnet run --project CreateAtlases.csproj -- [options]");
	Console.WriteLine();
	Console.WriteLine("Options:");
	Console.WriteLine("  --data <path>        Path to aggregated JSON data (blocks_models.json) or a minecraft asset root (auto-discovered if omitted)");
	Console.WriteLine("  --output <path>      Output directory for the generated atlases (default: ./atlases)");
	Console.WriteLine("  --tile-size <int>    Tile size in pixels per rendered asset (default: 160)");
	Console.WriteLine("  --columns <int>      Number of columns per atlas page (default: 12)");
	Console.WriteLine("  --rows <int>         Number of rows per atlas page (default: 12)");
	Console.WriteLine("  --blocks <names>     Comma-separated block names to include (default: all blocks)");
	Console.WriteLine("  --items <names>      Comma-separated item names to include (default: all items)");
	Console.WriteLine("  --no-blocks          Skip block rendering");
	Console.WriteLine("  --no-items           Skip item rendering");
	Console.WriteLine("  --views <names>      Comma-separated view names (default: all). Available: " + string.Join(", ", MinecraftAtlasGenerator.DefaultViews.Select(v => v.Name)));
	Console.WriteLine("  --debug-block        Generate a synthetic debug cube with colored faces into its own atlas");
	Console.WriteLine("  --texture-pack-dir <path>  Register an unzipped resource pack directory (can be specified multiple times)");
	Console.WriteLine("  --texture-pack-id <id>     Apply a registered pack id (last flag has highest priority; specify multiple for stacks)");
	Console.WriteLine("  --snbt-dir <path>    Render SNBT item stacks from the specified directory (outputs to an 'snbt' subfolder)");
	Console.WriteLine("  -h | --help          Show this help message");
}

sealed record AssetDirectory(string Path, bool IsAggregatedJson);

enum DirectoryKind
{
	None,
	AggregatedJson,
	AssetFolder
}

sealed class CliOptions
{
	public bool ShowHelp { get; set; }
	public string? DataDirectory { get; set; }
	public string? OutputDirectory { get; set; }
	public int TileSize { get; set; } = 160;
	public int Columns { get; set; } = 12;
	public int Rows { get; set; } = 12;
	public List<string>? Blocks { get; set; }
	public List<string>? Items { get; set; }
	public bool IncludeBlocks { get; set; } = true;
	public bool IncludeItems { get; set; } = true;
	public List<string>? ViewNames { get; set; }
	public bool GenerateDebugBlock { get; set; }
	public List<string>? TexturePackDirectories { get; set; }
	public List<string>? TexturePackIds { get; set; }
	public string? SnbtItemDirectory { get; set; }
}
