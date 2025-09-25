using System.Globalization;
using MinecraftRenderer;

var options = ParseArguments(args);

if (options.ShowHelp)
{
	PrintHelp();
	return;
}

var dataDirectory = ResolveDataDirectory(options.DataDirectory);
if (dataDirectory is null)
{
	Console.Error.WriteLine("Unable to locate the 'data' directory. Provide --data <path> explicitly.");
	Environment.ExitCode = 1;
	return;
}

var outputDirectory = options.OutputDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "atlases");
Directory.CreateDirectory(outputDirectory);

var views = BuildViews(options.ViewNames);

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

Console.WriteLine($"Data directory: {dataDirectory}");
Console.WriteLine($"Output directory: {outputDirectory}");
Console.WriteLine($"Views: {string.Join(", ", views.Select(v => v.Name))}");

using var renderer = MinecraftBlockRenderer.CreateFromDataDirectory(dataDirectory);

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

static string? ResolveDataDirectory(string? provided)
{
	if (!string.IsNullOrWhiteSpace(provided))
	{
		var candidate = Path.GetFullPath(provided);
		if (IsValidDataDirectory(candidate))
		{
			return candidate;
		}
		Console.Error.WriteLine($"Provided --data path '{provided}' does not contain the expected JSON files.");
		return null;
	}

	if (TryLocateDataDirectory(Directory.GetCurrentDirectory(), out var fromCwd))
	{
		return fromCwd;
	}

	if (TryLocateDataDirectory(AppContext.BaseDirectory, out var fromBase))
	{
		return fromBase;
	}

	return null;
}

static bool TryLocateDataDirectory(string startDirectory, out string? dataDirectory)
{
	var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
	while (current != null)
	{
		var candidate = Path.Combine(current.FullName, "data");
		if (IsValidDataDirectory(candidate))
		{
			dataDirectory = candidate;
			return true;
		}
		current = current.Parent;
	}

	dataDirectory = null;
	return false;
}

static bool IsValidDataDirectory(string path)
{
	if (string.IsNullOrWhiteSpace(path))
	{
		return false;
	}

	return File.Exists(Path.Combine(path, "blocks_models.json"))
		&& File.Exists(Path.Combine(path, "blocks_textures.json"));
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

static void PrintHelp()
{
	Console.WriteLine("Minecraft Renderer – Atlas Generator");
	Console.WriteLine();
	Console.WriteLine("Usage:");
	Console.WriteLine("  dotnet run --project CreateAtlases.csproj -- [options]");
	Console.WriteLine();
	Console.WriteLine("Options:");
	Console.WriteLine("  --data <path>        Path to the data directory containing blocks_models.json (auto-discovered if omitted)");
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
	Console.WriteLine("  -h | --help          Show this help message");
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
}
