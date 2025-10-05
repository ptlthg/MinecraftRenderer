namespace MinecraftRenderer.TexturePacks;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed class TexturePackRegistry
{
	private readonly ConcurrentDictionary<string, RegisteredResourcePack>
		_packs = new(StringComparer.OrdinalIgnoreCase);

	private TexturePackRegistry()
	{
	}

	public static TexturePackRegistry Create() => new();

	public RegisteredResourcePack RegisterPack(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		var fullPath = Path.GetFullPath(directory);
		if (!Directory.Exists(fullPath))
		{
			throw new DirectoryNotFoundException($"Texture pack directory not found: '{directory}'.");
		}

		var metaPath = Path.Combine(fullPath, "meta.json");
		if (!File.Exists(metaPath))
		{
			throw new FileNotFoundException("Texture pack meta.json file not found", metaPath);
		}

		var metaDescriptor = LoadMeta(metaPath);
		if (string.IsNullOrWhiteSpace(metaDescriptor.Id))
		{
			throw new InvalidOperationException(
				$"Texture pack '{directory}' is missing a valid 'id' field in meta.json.");
		}

		var namespaceRoots = ResolveNamespaceRoots(fullPath);
		if (!namespaceRoots.TryGetValue("minecraft", out var assetsPath))
		{
			throw new DirectoryNotFoundException(
				$"Texture pack at '{fullPath}' does not contain an 'assets/minecraft' directory.");
		}

		var packMcMetaPath = Path.Combine(fullPath, "pack.mcmeta");
		int? packFormat = null;
		if (File.Exists(packMcMetaPath))
		{
			packFormat = ParsePackFormat(packMcMetaPath);
		}

		var lastWriteTimeUtc = Directory.GetLastWriteTimeUtc(fullPath);
		var sizeBytes = namespaceRoots.Values.Sum(static path => CalculateDirectorySize(path));
		var supportsCit = metaDescriptor.SupportsCit
		                  || namespaceRoots.ContainsKey("cit")
		                  || Directory.Exists(Path.Combine(assetsPath, "optifine", "cit"));

		var resourceMeta = new ResourcePackMeta(
			metaDescriptor.Id!,
			metaDescriptor.Name ?? metaDescriptor.Id!,
			metaDescriptor.Version ?? "0.0.0",
			metaDescriptor.Description ?? string.Empty,
			metaDescriptor.Authors ?? [],
			metaDescriptor.DownloadUrl)
		{
			SupportsCit = supportsCit,
			PackFormat = packFormat
		};

		var fingerprint = ComputeFingerprint(resourceMeta.Id, resourceMeta.Version, lastWriteTimeUtc, sizeBytes);

		var registered = new RegisteredResourcePack(
			resourceMeta.Id,
			resourceMeta.Name,
			fullPath,
			assetsPath,
			namespaceRoots,
			resourceMeta,
			lastWriteTimeUtc,
			sizeBytes,
			supportsCit,
			fingerprint);

		if (!_packs.TryAdd(resourceMeta.Id, registered))
		{
			throw new InvalidOperationException(
				$"A texture pack with id '{resourceMeta.Id}' has already been registered.");
		}

		return registered;
	}

	/// <summary>
	/// Registers every texture pack located under the specified root directory.
	/// Directories without a <c>meta.json</c> file are ignored.
	/// </summary>
	/// <param name="rootDirectory">Root directory containing one or more texture pack folders.</param>
	/// <param name="searchRecursively">When true, searches all subdirectories; otherwise only immediate children.</param>
	/// <returns>A list of packs that were successfully registered.</returns>
	public IReadOnlyList<RegisteredResourcePack> RegisterAllPacks(string rootDirectory,
		bool searchRecursively = false)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
		var fullRoot = Path.GetFullPath(rootDirectory);
		if (!Directory.Exists(fullRoot))
		{
			throw new DirectoryNotFoundException(
				$"Texture pack root directory not found: '{rootDirectory}'.");
		}

		var results = new List<RegisteredResourcePack>();
		var searchOption = searchRecursively ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

		if (File.Exists(Path.Combine(fullRoot, "meta.json")))
		{
			results.Add(RegisterPack(fullRoot));
		}

		foreach (var candidate in Directory.EnumerateDirectories(fullRoot, "*", searchOption))
		{
			if (!File.Exists(Path.Combine(candidate, "meta.json")))
			{
				continue;
			}

			var registered = RegisterPack(candidate);
			if (!results.Contains(registered))
			{
				results.Add(registered);
			}
		}

		return results;
	}

	/// <summary>
	/// Gets all texture packs that have been registered with this registry.
	/// </summary>
	/// <returns>An immutable snapshot of the registered texture packs.</returns>
	public IReadOnlyCollection<RegisteredResourcePack> GetRegisteredPacks()
		=> _packs.Values.ToArray();

	public bool TryGetPack(string id, out RegisteredResourcePack pack)
		=> _packs.TryGetValue(id, out pack!);

	public TexturePackStack BuildPackStack(IReadOnlyList<string> packIds)
	{
		ArgumentNullException.ThrowIfNull(packIds);
		if (packIds.Count == 0)
		{
			return new TexturePackStack(Array.Empty<RegisteredResourcePack>(), Array.Empty<PackOverlayRoot>(),
				"vanilla");
		}

		var ordered = new List<RegisteredResourcePack>(packIds.Count);
		foreach (var packId in packIds)
		{
			if (!_packs.TryGetValue(packId, out var pack))
			{
				throw new KeyNotFoundException($"Unknown texture pack id '{packId}'.");
			}

			ordered.Add(pack);
		}

		var overlayRoots = new List<PackOverlayRoot>();
		foreach (var pack in ordered)
		{
			foreach (var overlayPath in pack.EnumerateOverlayRootPaths())
			{
				overlayRoots.Add(new PackOverlayRoot(overlayPath, pack.Id));
			}
		}

		var fingerprintInput = string.Join('|', ordered.Select(static pack => $"{pack.Id}:{pack.Fingerprint}"));
		var stackFingerprint = ComputeSha256("packstack:" + fingerprintInput);

		return new TexturePackStack(ordered, overlayRoots, stackFingerprint);
	}

	private static MetaDescriptor LoadMeta(string path)
	{
		using var stream = File.OpenRead(path);
		var descriptor = JsonSerializer.Deserialize<MetaDescriptor>(stream, new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true,
			ReadCommentHandling = JsonCommentHandling.Skip
		});

		if (descriptor is null)
		{
			throw new InvalidOperationException($"Failed to parse texture pack metadata from '{path}'.");
		}

		return descriptor;
	}

	private static IReadOnlyDictionary<string, string> ResolveNamespaceRoots(string root)
	{
		var assetsRoot = Path.Combine(root, "assets");
		if (!Directory.Exists(assetsRoot))
		{
			throw new DirectoryNotFoundException(
				$"Texture pack at '{root}' does not contain an 'assets' directory.");
		}

		var namespaces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var directory in Directory.EnumerateDirectories(assetsRoot, "*", SearchOption.TopDirectoryOnly))
		{
			var name = Path.GetFileName(directory);
			if (string.IsNullOrWhiteSpace(name))
			{
				continue;
			}

			var fullPath = Path.GetFullPath(directory);
			if (!namespaces.ContainsKey(name))
			{
				namespaces.Add(name, fullPath);
			}
		}

		if (!namespaces.TryGetValue("minecraft", out var minecraftPath))
		{
			throw new DirectoryNotFoundException(
				$"Texture pack at '{root}' does not contain an 'assets/minecraft' directory.");
		}

		namespaces["minecraft"] = Path.GetFullPath(minecraftPath);
		return namespaces;
	}

	private static int? ParsePackFormat(string packMcMetaPath)
	{
		try
		{
			using var document = JsonDocument.Parse(File.ReadAllText(packMcMetaPath));
			if (document.RootElement.TryGetProperty("pack", out var packElement) &&
			    packElement.TryGetProperty("pack_format", out var formatElement) &&
			    formatElement.ValueKind == JsonValueKind.Number)
			{
				return formatElement.GetInt32();
			}
		}
		catch (JsonException)
		{
			// Ignore malformed pack.mcmeta.
		}

		return null;
	}

	private static long CalculateDirectorySize(string path)
	{
		if (!Directory.Exists(path))
		{
			return 0;
		}

		long total = 0;
		foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
		{
			try
			{
				var info = new FileInfo(file);
				total += info.Length;
			}
			catch (IOException)
			{
				// Ignore files that disappear mid-enumeration.
			}
		}

		return total;
	}

	private static string ComputeFingerprint(string id, string version, DateTime lastWriteTimeUtc, long sizeBytes)
	{
		var builder = new StringBuilder();
		builder.Append(id);
		builder.Append('|');
		builder.Append(version);
		builder.Append('|');
		builder.Append(lastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture));
		builder.Append('|');
		builder.Append(sizeBytes.ToString(CultureInfo.InvariantCulture));
		return ComputeSha256(builder.ToString());
	}

	private static string ComputeSha256(string input)
	{
		using var sha = SHA256.Create();
		var bytes = Encoding.UTF8.GetBytes(input);
		var hash = sha.ComputeHash(bytes);
		return Convert.ToHexString(hash).ToLowerInvariant();
	}

	private sealed class MetaDescriptor
	{
		public string? Id { get; set; }
		public string? Name { get; set; }
		public string? Version { get; set; }
		public string? Description { get; set; }
		public string[]? Authors { get; set; }
		public string? DownloadUrl { get; set; }
		public bool SupportsCit { get; set; }
	}
}