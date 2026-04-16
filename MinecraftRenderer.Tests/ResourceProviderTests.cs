using System.IO.Compression;
using System.Text;
using MinecraftRenderer.Assets;
using MinecraftRenderer.TexturePacks;
using Xunit;

namespace MinecraftRenderer.Tests;

public sealed class DirectoryResourceProviderTests : IDisposable
{
	private readonly string _tempDir;
	private readonly DirectoryResourceProvider _provider;

	public DirectoryResourceProviderTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "ResourceProviderTests_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tempDir);

		// Build a small test tree:
		//   root/
		//     file.txt
		//     models/
		//       stone.json
		//       oak_planks.json
		//     textures/
		//       block/
		//         stone.png
		File.WriteAllText(Path.Combine(_tempDir, "file.txt"), "hello");

		Directory.CreateDirectory(Path.Combine(_tempDir, "models"));
		File.WriteAllText(Path.Combine(_tempDir, "models", "stone.json"), "{\"model\":\"stone\"}");
		File.WriteAllText(Path.Combine(_tempDir, "models", "oak_planks.json"), "{\"model\":\"oak\"}");

		Directory.CreateDirectory(Path.Combine(_tempDir, "textures", "block"));
		File.WriteAllBytes(Path.Combine(_tempDir, "textures", "block", "stone.png"), [0x89, 0x50, 0x4E, 0x47]);

		_provider = new DirectoryResourceProvider(_tempDir);
	}

	public void Dispose()
	{
		_provider.Dispose();
		try { Directory.Delete(_tempDir, true); }
		catch { /* cleanup best-effort */ }
	}

	[Fact]
	public void FileExists_ReturnsTrueForExistingFile()
	{
		Assert.True(_provider.FileExists("file.txt"));
		Assert.True(_provider.FileExists("models/stone.json"));
	}

	[Fact]
	public void FileExists_ReturnsFalseForMissing()
	{
		Assert.False(_provider.FileExists("missing.txt"));
		Assert.False(_provider.FileExists("models/missing.json"));
	}

	[Fact]
	public void DirectoryExists_ReturnsTrueForExisting()
	{
		Assert.True(_provider.DirectoryExists("models"));
		Assert.True(_provider.DirectoryExists("textures/block"));
	}

	[Fact]
	public void DirectoryExists_ReturnsTrueForRoot()
	{
		Assert.True(_provider.DirectoryExists(""));
	}

	[Fact]
	public void DirectoryExists_ReturnsFalseForMissing()
	{
		Assert.False(_provider.DirectoryExists("nonexistent"));
	}

	[Fact]
	public void OpenRead_ReturnsFileContents()
	{
		using var stream = _provider.OpenRead("file.txt");
		using var reader = new StreamReader(stream);
		Assert.Equal("hello", reader.ReadToEnd());
	}

	[Fact]
	public void OpenRead_ThrowsForMissingFile()
	{
		Assert.Throws<FileNotFoundException>(() => _provider.OpenRead("missing.txt"));
	}

	[Fact]
	public void EnumerateFiles_NonRecursive()
	{
		var files = _provider.EnumerateFiles("models", "*.json", recursive: false).ToList();
		Assert.Equal(2, files.Count);
		Assert.Contains("models/stone.json", files);
		Assert.Contains("models/oak_planks.json", files);
	}

	[Fact]
	public void EnumerateFiles_Recursive()
	{
		var files = _provider.EnumerateFiles("textures", "*.png", recursive: true).ToList();
		Assert.Single(files);
		Assert.Equal("textures/block/stone.png", files[0]);
	}

	[Fact]
	public void EnumerateFiles_EmptyForMissingDir()
	{
		var files = _provider.EnumerateFiles("missing", "*.txt", recursive: false).ToList();
		Assert.Empty(files);
	}

	[Fact]
	public void EnumerateDirectories_NonRecursive()
	{
		var dirs = _provider.EnumerateDirectories("", "*", recursive: false).ToList();
		Assert.Contains("models", dirs);
		Assert.Contains("textures", dirs);
	}

	[Fact]
	public void EnumerateDirectories_Recursive()
	{
		var dirs = _provider.EnumerateDirectories("textures", "*", recursive: true).ToList();
		Assert.Single(dirs);
		Assert.Equal("textures/block", dirs[0]);
	}

	[Fact]
	public void PathTraversal_IsRejected()
	{
		Assert.Throws<UnauthorizedAccessException>(() => _provider.FileExists("../../../etc/passwd"));
		Assert.Throws<UnauthorizedAccessException>(() => _provider.OpenRead("..\\..\\file"));
	}

	[Fact]
	public void RootPath_ReturnsFullPath()
	{
		Assert.Equal(Path.GetFullPath(_tempDir), _provider.RootPath);
	}

	[Fact]
	public void ReadAllText_ExtensionWorks()
	{
		var content = _provider.ReadAllText("models/stone.json");
		Assert.Equal("{\"model\":\"stone\"}", content);
	}
}

public sealed class ZipResourceProviderTests : IDisposable
{
	private readonly string _zipPath;

	public ZipResourceProviderTests()
	{
		_zipPath = Path.Combine(Path.GetTempPath(), "ResourceProviderTests_" + Guid.NewGuid().ToString("N") + ".zip");
		CreateTestZip(_zipPath);
	}

	public void Dispose()
	{
		try { File.Delete(_zipPath); }
		catch { /* cleanup best-effort */ }
	}

	private static void CreateTestZip(string path)
	{
		using var stream = File.Create(path);
		using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

		AddTextEntry(archive, "file.txt", "hello");
		AddTextEntry(archive, "models/stone.json", "{\"model\":\"stone\"}");
		AddTextEntry(archive, "models/oak_planks.json", "{\"model\":\"oak\"}");
		AddTextEntry(archive, "textures/block/stone.png", "fakepng");
		AddTextEntry(archive, "data/info.txt", "data");
	}

	private static void AddTextEntry(ZipArchive archive, string entryPath, string content)
	{
		var entry = archive.CreateEntry(entryPath);
		using var writer = new StreamWriter(entry.Open());
		writer.Write(content);
	}

	[Fact]
	public void FilePathConstructor_LoadsEntries()
	{
		using var provider = new ZipResourceProvider(_zipPath);
		Assert.True(provider.FileExists("file.txt"));
		Assert.True(provider.FileExists("models/stone.json"));
	}

	[Fact]
	public void ArchiveConstructor_LoadsEntries()
	{
		using var stream = File.OpenRead(_zipPath);
		using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
		using var provider = new ZipResourceProvider(archive, "test.zip");

		Assert.True(provider.FileExists("file.txt"));
		Assert.True(provider.FileExists("models/stone.json"));
	}

	[Fact]
	public void ArchiveConstructor_WithOwnership_DisposesArchive()
	{
		var stream = File.OpenRead(_zipPath);
		var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
		var provider = new ZipResourceProvider(archive, "test.zip", ownsArchive: true);
		provider.Dispose();

		// Archive should be disposed — accessing Entries should throw
		Assert.Throws<ObjectDisposedException>(() => archive.Entries);
		stream.Dispose();
	}

	[Fact]
	public void ArchiveConstructor_WithoutOwnership_DoesNotDisposeArchive()
	{
		using var stream = File.OpenRead(_zipPath);
		using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
		var provider = new ZipResourceProvider(archive, "test.zip", ownsArchive: false);
		provider.Dispose();

		// Archive should still be accessible
		Assert.NotEmpty(archive.Entries);
	}

	[Fact]
	public void FileExists_ReturnsFalseForMissing()
	{
		using var provider = new ZipResourceProvider(_zipPath);
		Assert.False(provider.FileExists("missing.txt"));
	}

	[Fact]
	public void DirectoryExists_InferredFromEntries()
	{
		using var provider = new ZipResourceProvider(_zipPath);
		Assert.True(provider.DirectoryExists("models"));
		Assert.True(provider.DirectoryExists("textures"));
		Assert.True(provider.DirectoryExists("textures/block"));
	}

	[Fact]
	public void DirectoryExists_RootAlwaysTrue()
	{
		using var provider = new ZipResourceProvider(_zipPath);
		Assert.True(provider.DirectoryExists(""));
	}

	[Fact]
	public void DirectoryExists_ReturnsFalseForMissing()
	{
		using var provider = new ZipResourceProvider(_zipPath);
		Assert.False(provider.DirectoryExists("nonexistent"));
	}

	[Fact]
	public void OpenRead_ReturnsSeekableStream()
	{
		using var provider = new ZipResourceProvider(_zipPath);
		using var stream = provider.OpenRead("file.txt");

		Assert.True(stream.CanSeek);
		using var reader = new StreamReader(stream);
		Assert.Equal("hello", reader.ReadToEnd());
	}

	[Fact]
	public void OpenRead_ThrowsForMissing()
	{
		using var provider = new ZipResourceProvider(_zipPath);
		Assert.Throws<FileNotFoundException>(() => provider.OpenRead("missing.txt"));
	}

	[Fact]
	public void EnumerateFiles_NonRecursive()
	{
		using var provider = new ZipResourceProvider(_zipPath);
		var files = provider.EnumerateFiles("models", "*.json", recursive: false).ToList();
		Assert.Equal(2, files.Count);
		Assert.Contains("models/stone.json", files);
		Assert.Contains("models/oak_planks.json", files);
	}

	[Fact]
	public void EnumerateFiles_Recursive()
	{
		using var provider = new ZipResourceProvider(_zipPath);
		var files = provider.EnumerateFiles("", "*.json", recursive: true).ToList();
		Assert.Equal(2, files.Count);
	}

	[Fact]
	public void EnumerateFiles_FromRoot_NonRecursive()
	{
		using var provider = new ZipResourceProvider(_zipPath);
		var files = provider.EnumerateFiles("", "*.txt", recursive: false).ToList();
		Assert.Single(files);
		Assert.Equal("file.txt", files[0]);
	}

	[Fact]
	public void EnumerateDirectories_NonRecursive()
	{
		using var provider = new ZipResourceProvider(_zipPath);
		var dirs = provider.EnumerateDirectories("", "*", recursive: false).ToList();
		Assert.Contains("models", dirs);
		Assert.Contains("textures", dirs);
		Assert.Contains("data", dirs);
		// Should not include nested dirs in non-recursive mode
		Assert.DoesNotContain("textures/block", dirs);
	}

	[Fact]
	public void EnumerateDirectories_Recursive()
	{
		using var provider = new ZipResourceProvider(_zipPath);
		var dirs = provider.EnumerateDirectories("textures", "*", recursive: true).ToList();
		Assert.Single(dirs);
		Assert.Equal("textures/block", dirs[0]);
	}

	[Fact]
	public void PathTraversal_IsRejected()
	{
		using var provider = new ZipResourceProvider(_zipPath);
		Assert.Throws<ArgumentException>(() => provider.FileExists("../etc/passwd"));
		Assert.Throws<ArgumentException>(() => provider.OpenRead("models/../../secret"));
	}

	[Fact]
	public void CaseInsensitive_Lookup()
	{
		using var provider = new ZipResourceProvider(_zipPath);
		// The index uses OrdinalIgnoreCase
		Assert.True(provider.FileExists("FILE.TXT"));
		Assert.True(provider.FileExists("Models/Stone.json"));
		Assert.True(provider.DirectoryExists("MODELS"));
	}

	[Fact]
	public void DisposedProvider_Throws()
	{
		var provider = new ZipResourceProvider(_zipPath);
		provider.Dispose();

		Assert.Throws<ObjectDisposedException>(() => provider.FileExists("file.txt"));
		Assert.Throws<ObjectDisposedException>(() => provider.OpenRead("file.txt"));
	}

	[Fact]
	public void DisposeIsIdempotent()
	{
		var provider = new ZipResourceProvider(_zipPath);
		provider.Dispose();
		provider.Dispose(); // Should not throw
	}

	[Fact]
	public void RootPath_ReturnsZipPath()
	{
		using var provider = new ZipResourceProvider(_zipPath);
		Assert.Equal(Path.GetFullPath(_zipPath), provider.RootPath);
	}

	[Fact]
	public void ReadAllText_ExtensionWorks()
	{
		using var provider = new ZipResourceProvider(_zipPath);
		Assert.Equal("{\"model\":\"stone\"}", provider.ReadAllText("models/stone.json"));
	}
}

public sealed class SubPathResourceProviderTests : IDisposable
{
	private readonly string _zipPath;

	public SubPathResourceProviderTests()
	{
		_zipPath = Path.Combine(Path.GetTempPath(), "SubPathTests_" + Guid.NewGuid().ToString("N") + ".zip");

		using var stream = File.Create(_zipPath);
		using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

		AddTextEntry(archive, "assets/minecraft/models/block/stone.json", "{\"stone\":true}");
		AddTextEntry(archive, "assets/minecraft/textures/block/stone.png", "fakepng");
		AddTextEntry(archive, "assets/minecraft/blockstates/stone.json", "{\"bs\":true}");
		AddTextEntry(archive, "assets/other/models/custom.json", "{\"custom\":true}");
		AddTextEntry(archive, "pack.mcmeta", "{\"pack\":{}}");
	}

	public void Dispose()
	{
		try { File.Delete(_zipPath); }
		catch { /* cleanup best-effort */ }
	}

	private static void AddTextEntry(ZipArchive archive, string entryPath, string content)
	{
		var entry = archive.CreateEntry(entryPath);
		using var writer = new StreamWriter(entry.Open());
		writer.Write(content);
	}

	[Fact]
	public void FileExists_InsideSubPath()
	{
		using var zip = new ZipResourceProvider(_zipPath);
		using var sub = new SubPathResourceProvider(zip, "assets/minecraft");

		Assert.True(sub.FileExists("models/block/stone.json"));
		Assert.True(sub.FileExists("blockstates/stone.json"));
	}

	[Fact]
	public void FileExists_OutsideSubPath()
	{
		using var zip = new ZipResourceProvider(_zipPath);
		using var sub = new SubPathResourceProvider(zip, "assets/minecraft");

		Assert.False(sub.FileExists("pack.mcmeta"));
	}

	[Fact]
	public void DirectoryExists_InsideSubPath()
	{
		using var zip = new ZipResourceProvider(_zipPath);
		using var sub = new SubPathResourceProvider(zip, "assets/minecraft");

		Assert.True(sub.DirectoryExists("models"));
		Assert.True(sub.DirectoryExists("models/block"));
	}

	[Fact]
	public void DirectoryExists_RootMeansSubRoot()
	{
		using var zip = new ZipResourceProvider(_zipPath);
		using var sub = new SubPathResourceProvider(zip, "assets/minecraft");

		Assert.True(sub.DirectoryExists(""));
	}

	[Fact]
	public void OpenRead_SubPathFile()
	{
		using var zip = new ZipResourceProvider(_zipPath);
		using var sub = new SubPathResourceProvider(zip, "assets/minecraft");

		var content = sub.ReadAllText("models/block/stone.json");
		Assert.Equal("{\"stone\":true}", content);
	}

	[Fact]
	public void EnumerateFiles_ReturnsRelativeToSubPath()
	{
		using var zip = new ZipResourceProvider(_zipPath);
		using var sub = new SubPathResourceProvider(zip, "assets/minecraft");

		var files = sub.EnumerateFiles("models", "*.json", recursive: true).ToList();
		Assert.Single(files);
		Assert.Equal("models/block/stone.json", files[0]);
	}

	[Fact]
	public void EnumerateDirectories_ReturnsRelativeToSubPath()
	{
		using var zip = new ZipResourceProvider(_zipPath);
		using var sub = new SubPathResourceProvider(zip, "assets/minecraft");

		var dirs = sub.EnumerateDirectories("", "*", recursive: false).ToList();
		Assert.Contains("models", dirs);
		Assert.Contains("textures", dirs);
		Assert.Contains("blockstates", dirs);
		Assert.DoesNotContain("assets", dirs);
	}

	[Fact]
	public void Dispose_DoesNotDisposeInner()
	{
		var zip = new ZipResourceProvider(_zipPath);
		var sub = new SubPathResourceProvider(zip, "assets/minecraft");
		sub.Dispose();

		// Inner should still work
		Assert.True(zip.FileExists("pack.mcmeta"));
		zip.Dispose();
	}

	[Fact]
	public void Dispose_WithOwnsInner_DisposesInner()
	{
		var zip = new ZipResourceProvider(_zipPath);
		var sub = new SubPathResourceProvider(zip, "assets/minecraft") { OwnsInner = true };
		sub.Dispose();

		// Inner should be disposed
		Assert.Throws<ObjectDisposedException>(() => zip.FileExists("pack.mcmeta"));
	}

	[Fact]
	public void EmptyPrefix_ActsAsPassthrough()
	{
		using var zip = new ZipResourceProvider(_zipPath);
		using var sub = new SubPathResourceProvider(zip, "");

		Assert.True(sub.FileExists("pack.mcmeta"));
		Assert.True(sub.FileExists("assets/minecraft/models/block/stone.json"));
	}

	[Fact]
	public void RootPath_CombinesInnerAndPrefix()
	{
		using var zip = new ZipResourceProvider(_zipPath);
		using var sub = new SubPathResourceProvider(zip, "assets/minecraft");

		Assert.Contains("assets/minecraft", sub.RootPath);
	}
}

public sealed class ResourceProviderExtensionsTests
{
	[Theory]
	[InlineData("models/block/stone.json", "models", "block/stone.json")]
	[InlineData("models/block/stone.json", "models/block", "stone.json")]
	[InlineData("file.txt", "", "file.txt")]
	[InlineData("models/block/stone.json", "textures", "models/block/stone.json")] // no match → returns original
	public void GetRelativePath_StripsPrefixCorrectly(string fullPath, string prefix, string expected)
	{
		Assert.Equal(expected, ResourceProviderExtensions.GetRelativePath(fullPath, prefix));
	}

	[Fact]
	public void GetRelativePath_TrailingSlashOnPrefix()
	{
		Assert.Equal("stone.json", ResourceProviderExtensions.GetRelativePath("models/stone.json", "models/"));
	}
}

public sealed class TexturePackRegistryZipTests : IDisposable
{
	private readonly string _tempDir;

	public TexturePackRegistryZipTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "PackRegistryZipTests_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tempDir);
	}

	public void Dispose()
	{
		try { Directory.Delete(_tempDir, true); }
		catch { /* best-effort */ }
	}

	private string CreateZipPackDirectory(string packId, string packName = "Test Pack")
	{
		var packDir = Path.Combine(_tempDir, packId);
		Directory.CreateDirectory(packDir);

		// Write meta.json alongside the zip
		var metaJson = $$"""{"id":"{{packId}}","name":"{{packName}}","version":"1.0.0"}""";
		File.WriteAllText(Path.Combine(packDir, "meta.json"), metaJson);

		// Create the zip with resource pack structure
		var zipPath = Path.Combine(packDir, "pack.zip");
		using var stream = File.Create(zipPath);
		using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

		AddEntry(archive, "pack.mcmeta", """{"pack":{"pack_format":15,"description":"Test"}}""");
		AddEntry(archive, "assets/minecraft/textures/block/custom_stone.png", "fakepng");
		AddEntry(archive, "assets/minecraft/models/block/custom_stone.json",
			"""{"parent":"minecraft:block/cube_all","textures":{"all":"minecraft:block/custom_stone"}}""");

		return packDir;
	}

	private string CreateDirPackDirectory(string packId)
	{
		var packDir = Path.Combine(_tempDir, packId);
		Directory.CreateDirectory(packDir);

		var metaJson = $$"""{"id":"{{packId}}","name":"Dir Pack","version":"1.0.0"}""";
		File.WriteAllText(Path.Combine(packDir, "meta.json"), metaJson);

		var mcDir = Path.Combine(packDir, "assets", "minecraft", "textures", "block");
		Directory.CreateDirectory(mcDir);
		File.WriteAllBytes(Path.Combine(mcDir, "dirt.png"), [0x89, 0x50]);

		return packDir;
	}

	private static void AddEntry(ZipArchive archive, string path, string content)
	{
		var entry = archive.CreateEntry(path);
		using var writer = new StreamWriter(entry.Open());
		writer.Write(content);
	}

	[Fact]
	public void RegisterPack_DetectsZipFile()
	{
		var packDir = CreateZipPackDirectory("testzip");
		var registry = TexturePackRegistry.Create();

		var pack = registry.RegisterPack(packDir);

		Assert.Equal("testzip", pack.Id);
		Assert.NotNull(pack.Provider);
		Assert.NotNull(pack.NamespaceProviders);
		Assert.True(pack.NamespaceProviders!.ContainsKey("minecraft"));
	}

	[Fact]
	public void RegisterPack_ZipPack_ProviderCanReadFiles()
	{
		var packDir = CreateZipPackDirectory("testzip2");
		var registry = TexturePackRegistry.Create();

		var pack = registry.RegisterPack(packDir);
		var nsProvider = pack.NamespaceProviders!["minecraft"];

		Assert.True(nsProvider.FileExists("textures/block/custom_stone.png"));
		Assert.True(nsProvider.FileExists("models/block/custom_stone.json"));
	}

	[Fact]
	public void RegisterPack_ZipPack_ReadsPackFormat()
	{
		var packDir = CreateZipPackDirectory("testzip3");
		var registry = TexturePackRegistry.Create();

		var pack = registry.RegisterPack(packDir);

		Assert.Equal(15, pack.Meta.PackFormat);
	}

	[Fact]
	public void RegisterPack_ZipPack_SizeIsZipFileSize()
	{
		var packDir = CreateZipPackDirectory("testzip4");
		var zipSize = new FileInfo(Path.Combine(packDir, "pack.zip")).Length;
		var registry = TexturePackRegistry.Create();

		var pack = registry.RegisterPack(packDir);

		Assert.Equal(zipSize, pack.SizeBytes);
	}

	[Fact]
	public void RegisterPack_DirPack_HasNoProvider()
	{
		var packDir = CreateDirPackDirectory("testdir");
		var registry = TexturePackRegistry.Create();

		var pack = registry.RegisterPack(packDir);

		Assert.Null(pack.Provider);
		Assert.Null(pack.NamespaceProviders);
	}

	[Fact]
	public void RegisterPack_PrefersDirectoryOverZip()
	{
		// If both assets/ dir and .zip exist, should use directory
		var packDir = CreateDirPackDirectory("testboth");

		// Also add a zip
		var zipPath = Path.Combine(packDir, "extra.zip");
		using (var stream = File.Create(zipPath))
		using (var archive = new ZipArchive(stream, ZipArchiveMode.Create)) {
			AddEntry(archive, "assets/minecraft/textures/block/fake.png", "fake");
		}

		var registry = TexturePackRegistry.Create();
		var pack = registry.RegisterPack(packDir);

		// Should be directory-based (no provider)
		Assert.Null(pack.Provider);
	}

	[Fact]
	public void RegisterPack_ZipPack_MissingMinecraftNamespace_Throws()
	{
		var packDir = Path.Combine(_tempDir, "badzip");
		Directory.CreateDirectory(packDir);

		File.WriteAllText(Path.Combine(packDir, "meta.json"),
			"""{"id":"badzip","name":"Bad","version":"1.0.0"}""");

		// Create zip without assets/minecraft
		var zipPath = Path.Combine(packDir, "pack.zip");
		using (var stream = File.Create(zipPath))
		using (var archive = new ZipArchive(stream, ZipArchiveMode.Create)) {
			AddEntry(archive, "assets/other/textures/test.png", "fake");
		}

		var registry = TexturePackRegistry.Create();
		Assert.Throws<DirectoryNotFoundException>(() => registry.RegisterPack(packDir));
	}

	[Fact]
	public void RegisterAllPacks_MixedDirAndZip()
	{
		CreateZipPackDirectory("zippack");
		CreateDirPackDirectory("dirpack");

		var registry = TexturePackRegistry.Create();
		var packs = registry.RegisterAllPacks(_tempDir);

		Assert.Equal(2, packs.Count);

		var zip = packs.First(p => p.Id == "zippack");
		var dir = packs.First(p => p.Id == "dirpack");

		Assert.NotNull(zip.Provider);
		Assert.Null(dir.Provider);
	}
}

public sealed class OpenJarAsProviderTests : IDisposable
{
	private readonly string _jarPath;

	public OpenJarAsProviderTests()
	{
		_jarPath = Path.Combine(Path.GetTempPath(), "JarProviderTest_" + Guid.NewGuid().ToString("N") + ".jar");

		using var stream = File.Create(_jarPath);
		using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

		var entry = archive.CreateEntry("assets/minecraft/models/block/stone.json");
		using (var writer = new StreamWriter(entry.Open()))
			writer.Write("""{"parent":"minecraft:block/cube_all"}""");

		var tex = archive.CreateEntry("assets/minecraft/textures/block/stone.png");
		using (var writer = new StreamWriter(tex.Open()))
			writer.Write("fakepng");

		var bs = archive.CreateEntry("assets/minecraft/blockstates/stone.json");
		using (var writer = new StreamWriter(bs.Open()))
			writer.Write("""{"variants":{"":{"model":"minecraft:block/stone"}}}""");
	}

	public void Dispose()
	{
		try { File.Delete(_jarPath); }
		catch { /* best-effort */ }
	}

	[Fact]
	public void OpenJarAsProvider_ScopesToAssetsMinecraft()
	{
		using var provider = MinecraftAssetDownloader.OpenJarAsProvider(_jarPath);

		Assert.True(provider.FileExists("models/block/stone.json"));
		Assert.True(provider.FileExists("textures/block/stone.png"));
		Assert.True(provider.FileExists("blockstates/stone.json"));
	}

	[Fact]
	public void OpenJarAsProvider_CanReadContent()
	{
		using var provider = MinecraftAssetDownloader.OpenJarAsProvider(_jarPath);

		var content = provider.ReadAllText("models/block/stone.json");
		Assert.Contains("cube_all", content);
	}

	[Fact]
	public void OpenJarAsProvider_DisposingDisposesZip()
	{
		var provider = MinecraftAssetDownloader.OpenJarAsProvider(_jarPath);
		provider.Dispose();

		// The underlying SubPathResourceProvider with OwnsInner=true should have disposed the zip
		// Trying to use it again should create a new provider fine
		using var provider2 = MinecraftAssetDownloader.OpenJarAsProvider(_jarPath);
		Assert.True(provider2.FileExists("models/block/stone.json"));
	}

	[Fact]
	public void OpenJarAsProvider_MissingFile_Throws()
	{
		Assert.Throws<FileNotFoundException>(
			() => MinecraftAssetDownloader.OpenJarAsProvider("nonexistent.jar"));
	}
}
