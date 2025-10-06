using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CreateAtlases;
using MinecraftRenderer;
using MinecraftRenderer.Snbt;
using MinecraftRenderer.TexturePacks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

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

options.AnimatedFormats = NormalizeAnimatedFormats(options.AnimatedFormats);
if (options.AnimatedFormats is { Count: > 0 })
{
	var invalidFormats = options.AnimatedFormats
		.Where(static format => format is not ("gif" or "apng" or "webp"))
		.ToList();
	if (invalidFormats.Count > 0)
	{
		Console.Error.WriteLine($"Unsupported animated format(s): {string.Join(", ", invalidFormats)}. Valid values: gif, apng, webp.");
		Environment.ExitCode = 1;
		return;
	}
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
var results = new List<MinecraftAtlasGenerator.AtlasResult>();
var candidateItemNames = MinecraftAtlasGenerator.GetCandidateItemNames(renderer, itemFilter, includeItems);
AnimatedExportSummary? animatedSummary = null;
var animationRequests = new List<AnimatedItemRequest>();

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

		var snbtAnimationRequests = BuildAnimatedRequestsFromSnbtEntries(snbtEntries);
		if (snbtAnimationRequests.Count > 0)
		{
			animationRequests.AddRange(snbtAnimationRequests);
		}
	}
}
else if (!string.IsNullOrWhiteSpace(options.HypixelInventoryFile))
{
	var inventoryFile = Path.GetFullPath(options.HypixelInventoryFile);
	if (!File.Exists(inventoryFile))
	{
		Console.Error.WriteLine($"Hypixel inventory file '{options.HypixelInventoryFile}' was not found.");
		Environment.ExitCode = 1;
		return;
	}

	Console.WriteLine();
	Console.WriteLine($"Rendering Hypixel inventory from {inventoryFile}...");
	var hypixelOutputDirectory = Path.Combine(outputDirectory, "hypixel_inventory");
	CreateAtlases.HypixelInventoryAtlasGenerator.GenerateInventoryAtlas(
		renderer,
		inventoryFile,
		hypixelOutputDirectory,
		options.TexturePackIds?.LastOrDefault(),
		options.TileSize,
		options.Columns
	);
} 
else 
{
	if (includeItems && candidateItemNames.Count > 0)
	{
		animationRequests.AddRange(candidateItemNames.Select(static name => new AnimatedItemRequest(name, name)));
	}
	results = MinecraftAtlasGenerator.GenerateAtlases(
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
}

var shouldExportAnimations = options.AnimatedFormats is { Count: > 0 } && animationRequests.Count > 0;

if (shouldExportAnimations)
{
	var animatedOutput = options.AnimatedOutputDirectory;
	if (string.IsNullOrWhiteSpace(animatedOutput))
	{
		animatedOutput = Path.Combine(outputDirectory, "animated");
	}
	else if (!Path.IsPathRooted(animatedOutput))
	{
		animatedOutput = Path.GetFullPath(animatedOutput, outputDirectory);
	}

	animatedSummary = ExportAnimatedItems(
		renderer,
		views,
		animationRequests,
		options.AnimatedFormats!,
		animatedOutput!,
		options.TileSize);
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

if (animatedSummary is not null)
{
	Console.WriteLine();
	if (animatedSummary.Animated > 0)
	{
		Console.WriteLine($"Exported {animatedSummary.Animated} animated item {(animatedSummary.Animated == 1 ? "sequence" : "sequences")} across {animatedSummary.Attempted} evaluated combinations.");
		foreach (var (format, count) in animatedSummary.FormatCounts.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
		{
			Console.WriteLine($"   {format.ToUpperInvariant()}: {count} file{(count == 1 ? string.Empty : "s")}");
		}
		if (!string.IsNullOrWhiteSpace(animatedSummary.ManifestPath))
		{
			Console.WriteLine($"Animated manifest: {animatedSummary.ManifestPath}");
		}
		if (animatedSummary.Errors > 0)
		{
			Console.WriteLine($"Animated export failures: {animatedSummary.Errors}");
		}
	}
	else
	{
		Console.WriteLine("No animated texture sequences detected for the selected items/views.");
	}
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
			case "--animated-formats":
				options.AnimatedFormats = ParseList(ReadNext(arguments, ref i, "--animated-formats"));
				break;
			case "--animated-output":
				options.AnimatedOutputDirectory = ReadNext(arguments, ref i, "--animated-output");
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
			case "--hypixel-inventory":
				options.HypixelInventoryFile = ReadNext(arguments, ref i, "--hypixel-inventory");
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

static List<string>? NormalizeAnimatedFormats(List<string>? formats)
{
	if (formats is null || formats.Count == 0)
	{
		return null;
	}

	var normalized = formats
		.Select(static format => format?.Trim().ToLowerInvariant())
		.Where(static format => !string.IsNullOrWhiteSpace(format))
		.Select(static format => format!)
		.Distinct(StringComparer.OrdinalIgnoreCase)
		.ToList();

	return normalized.Count == 0 ? null : normalized;
}
static AnimatedExportSummary ExportAnimatedItems(
	MinecraftBlockRenderer renderer,
	IReadOnlyList<MinecraftAtlasGenerator.AtlasView> views,
	IReadOnlyList<AnimatedItemRequest> items,
	IReadOnlyList<string> formats,
	string outputDirectory,
	int tileSize)
{
	ArgumentNullException.ThrowIfNull(renderer);
	ArgumentNullException.ThrowIfNull(views);
	ArgumentNullException.ThrowIfNull(items);
	ArgumentNullException.ThrowIfNull(formats);
	ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tileSize);

	Directory.CreateDirectory(outputDirectory);
	var formatCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
	foreach (var format in formats)
	{
		if (!formatCounts.ContainsKey(format))
		{
			formatCounts[format] = 0;
		}
	}

	var manifestEntries = new List<AnimatedManifestEntry>();
	var attempted = 0;
	var animated = 0;
	var errors = 0;

	foreach (var view in views)
	{
		var normalizedOptions = MinecraftAtlasGenerator.NormalizeItemRenderOptions(view.Options);
		var viewSegment = MinecraftAtlasGenerator.SanitizeFileName(view.Name);

		foreach (var request in items)
		{
			attempted++;
			var displayLabel = string.IsNullOrWhiteSpace(request.Label) ? request.ItemName : request.Label;

			try
			{
				var requestOptions = normalizedOptions;
				if (request.ItemData is not null)
				{
					requestOptions = requestOptions with { ItemData = request.ItemData };
				}

				using var animation = renderer.RenderAnimatedGuiItemWithResourceId(request.ItemName, requestOptions);
				if (animation.Frames.Count <= 1)
				{
					continue;
				}

				animated++;
				foreach (var frame in animation.Frames)
				{
					if (frame.Image.Width != tileSize || frame.Image.Height != tileSize)
					{
						frame.Image.Mutate(ctx => ctx.Resize(new ResizeOptions
						{
							Size = new Size(tileSize, tileSize),
							Sampler = KnownResamplers.NearestNeighbor,
							Mode = ResizeMode.Stretch
						}));
					}
				}

				var packStackHash = string.IsNullOrWhiteSpace(animation.ResourceId.PackStackHash)
					? "vanilla"
					: animation.ResourceId.PackStackHash;
				var packSegment = MinecraftAtlasGenerator.SanitizeFileName(packStackHash);
				var targetDirectory = Path.Combine(outputDirectory, viewSegment, packSegment);
				Directory.CreateDirectory(targetDirectory);

				var sanitizedLabel = MinecraftAtlasGenerator.SanitizeFileName(displayLabel);
				if (string.IsNullOrWhiteSpace(sanitizedLabel))
				{
					sanitizedLabel = MinecraftAtlasGenerator.SanitizeFileName(request.ItemName);
				}

				var baseFileName = $"{sanitizedLabel}_{animation.ResourceId.ResourceId}";
				var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

				foreach (var format in formats)
				{
					var extension = format switch
					{
						"gif" => ".gif",
						"apng" => ".apng",
						"webp" => ".webp",
						_ => throw new InvalidOperationException($"Unsupported animation format '{format}'.")
					};

					var outputPath = Path.Combine(targetDirectory, baseFileName + extension);
					switch (format)
					{
						case "gif":
							animation.SaveAsGif(outputPath);
							break;
						case "apng":
							animation.SaveAsAnimatedPng(outputPath);
							break;
						case "webp":
							animation.SaveAsWebp(outputPath);
							break;
					}

					formatCounts[format] = formatCounts[format] + 1;
					files[format] = Path.GetRelativePath(outputDirectory, outputPath);
				}

				var frameDurations = animation.Frames.Select(static frame => frame.DurationMs).ToList();
				var manifestEntry = new AnimatedManifestEntry(
					displayLabel,
					view.Name,
					animation.ResourceId.ResourceId,
					animation.ResourceId.SourcePackId,
					packStackHash,
					animation.Frames.Count,
					frameDurations,
					frameDurations.Sum(),
					files,
					animation.ResourceId.Textures,
					request.SourcePath);
				manifestEntries.Add(manifestEntry);
			}
			catch (Exception ex)
			{
				errors++;
				Console.Error.WriteLine($"Failed to export animation for '{displayLabel}' (view: {view.Name}): {ex.Message}");
			}
		}
	}

	string? manifestPath = null;
	if (manifestEntries.Count > 0)
	{
		var manifestOptions = new JsonSerializerOptions
		{
			WriteIndented = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase
		};
		manifestPath = Path.Combine(outputDirectory, "manifest.json");
		File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifestEntries, manifestOptions));
	}

	return new AnimatedExportSummary(attempted, animated, errors, formatCounts, manifestPath);
}

static List<AnimatedItemRequest> BuildAnimatedRequestsFromSnbtEntries(
	IReadOnlyList<SnbtItemAtlasGenerator.SnbtItemEntry> entries)
{
	var requests = new List<AnimatedItemRequest>();
	if (entries is null || entries.Count == 0)
	{
		return requests;
	}

	foreach (var entry in entries)
	{
		var document = entry.Document;
		var compound = document?.RootCompound;
		if (compound is null)
		{
			continue;
		}

		var itemId = SnbtItemUtilities.TryGetItemId(compound);
		if (string.IsNullOrWhiteSpace(itemId))
		{
			continue;
		}

		var itemData = MinecraftBlockRenderer.ExtractItemRenderDataFromNbt(compound);
		var label = string.IsNullOrWhiteSpace(itemId)
			? entry.Name
			: $"{entry.Name} ({itemId})";
		var sourcePath = entry.SourcePath;
		if (!string.IsNullOrWhiteSpace(sourcePath) && Path.IsPathRooted(sourcePath))
		{
			try
			{
				sourcePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), sourcePath);
			}
			catch
			{
				// Ignore path conversion failures and keep the original path.
			}
		}

		requests.Add(new AnimatedItemRequest(itemId.Trim(), label, itemData, sourcePath));
	}

	return requests;
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
	Console.WriteLine("  --animated-formats <list>  Comma-separated animated export formats (gif, apng, webp)");
	Console.WriteLine("  --animated-output <path>   Directory for animated sequences (default: <output>/animated)");
	Console.WriteLine("  --debug-block        Generate a synthetic debug cube with colored faces into its own atlas");
	Console.WriteLine("  --texture-pack-dir <path>  Register an unzipped resource pack directory (can be specified multiple times)");
	Console.WriteLine("  --texture-pack-id <id>     Apply a registered pack id (last flag has highest priority; specify multiple for stacks)");
	Console.WriteLine("  --snbt-dir <path>    Render SNBT item stacks from the specified directory (outputs to an 'snbt' subfolder)");
	Console.WriteLine("  --hypixel-inventory <path> Render items from Hypixel inventory data file (base64 gzipped NBT)");
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
	public string? HypixelInventoryFile { get; set; }
	public List<string>? AnimatedFormats { get; set; }
	public string? AnimatedOutputDirectory { get; set; }
}
