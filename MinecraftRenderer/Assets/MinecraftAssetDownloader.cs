using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftRenderer;

/// <summary>
/// Downloads and extracts Minecraft client assets from official Mojang sources.
/// Assets are © Mojang Studios and subject to Minecraft's EULA.
/// </summary>
public static class MinecraftAssetDownloader
{
    private const string VersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private static readonly HttpClient HttpClient = new();

    /// <summary>
    /// Downloads and extracts Minecraft client assets for a specific version.
    /// </summary>
    /// <param name="version">Minecraft version (e.g., "1.21.9", "1.21.8")</param>
    /// <param name="outputPath">Directory to extract assets to. Defaults to "./minecraft"</param>
    /// <param name="acceptEula">Must be true to accept Minecraft's EULA (https://www.minecraft.net/en-us/eula)</param>
    /// <param name="forceRedownload">If true, downloads even if assets already exist</param>
    /// <param name="progress">Optional progress callback (receives percentage 0-100 and status message)</param>
    /// <returns>Path to the extracted assets directory</returns>
    /// <exception cref="InvalidOperationException">Thrown if EULA not accepted or version not found</exception>
    /// <exception cref="HttpRequestException">Thrown if download fails</exception>
    public static async Task<string> DownloadAndExtractAssets(
        string version = "1.21.9",
        string? outputPath = null,
        bool acceptEula = false,
        bool forceRedownload = false,
        IProgress<(int Percentage, string Status)>? progress = null)
    {
        if (!acceptEula)
        {
            throw new InvalidOperationException(
                "You must accept Minecraft's EULA to download assets. " +
                "Review the EULA at: https://www.minecraft.net/en-us/eula " +
                "Then set acceptEula=true to proceed.");
        }

        outputPath ??= Path.Combine(Directory.GetCurrentDirectory(), "minecraft");
        var assetsPath = Path.Combine(outputPath, "assets");
        var versionFile = Path.Combine(outputPath, ".version");

        // Check if assets already exist
        if (!forceRedownload && Directory.Exists(assetsPath) && File.Exists(versionFile))
        {
            var existingVersion = await File.ReadAllTextAsync(versionFile);
            if (existingVersion.Trim() == version)
            {
                progress?.Report((100, $"Assets for version {version} already exist at {outputPath}"));
                return outputPath;
            }
        }

        progress?.Report((0, $"Fetching version manifest..."));

        // Step 1: Get version manifest
        var versionManifestJson = await HttpClient.GetStringAsync(VersionManifestUrl);
        var versionManifest = JsonSerializer.Deserialize<VersionManifest>(versionManifestJson)
            ?? throw new InvalidOperationException("Failed to parse version manifest");

        var versionInfo = versionManifest.Versions.FirstOrDefault(v => v.Id == version)
            ?? throw new InvalidOperationException($"Version {version} not found in Mojang's version manifest");

        progress?.Report((10, $"Found version {version}, fetching version metadata..."));

        // Step 2: Get version-specific metadata
        var versionMetadataJson = await HttpClient.GetStringAsync(versionInfo.Url);
        var versionMetadata = JsonSerializer.Deserialize<VersionMetadata>(versionMetadataJson)
            ?? throw new InvalidOperationException("Failed to parse version metadata");

        var clientDownload = versionMetadata.Downloads?.Client
            ?? throw new InvalidOperationException($"No client download found for version {version}");

        progress?.Report((20, $"Downloading client.jar ({clientDownload.Size / 1024 / 1024:F1} MB)..."));

        // Step 3: Download client.jar
        var clientJarPath = Path.Combine(Path.GetTempPath(), $"minecraft-{version}-client.jar");
        using (var clientResponse = await HttpClient.GetAsync(clientDownload.Url, HttpCompletionOption.ResponseHeadersRead))
        {
            clientResponse.EnsureSuccessStatusCode();

            await using var clientStream = await clientResponse.Content.ReadAsStreamAsync();
            await using var fileStream = File.Create(clientJarPath);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await clientStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;

                var percentage = 20 + (int)(totalRead * 40.0 / clientDownload.Size);
                progress?.Report((percentage, $"Downloading client.jar... {totalRead / 1024 / 1024:F1}/{clientDownload.Size / 1024 / 1024:F1} MB"));
            }
        }

        progress?.Report((60, "Verifying download..."));

        // Step 4: Verify SHA1 hash
        using (var sha1 = System.Security.Cryptography.SHA1.Create())
        {
            await using var stream = File.OpenRead(clientJarPath);
            var hash = await Task.Run(() => sha1.ComputeHash(stream));
            var hashString = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

            if (hashString != clientDownload.Sha1.ToLowerInvariant())
            {
                File.Delete(clientJarPath);
                throw new InvalidOperationException($"SHA1 hash mismatch! Expected {clientDownload.Sha1}, got {hashString}");
            }
        }

        progress?.Report((70, "Extracting assets from client.jar..."));

        // Step 5: Extract assets from JAR
        Directory.CreateDirectory(outputPath);
        
        using (var archive = ZipFile.OpenRead(clientJarPath))
        {
            var assetEntries = archive.Entries
                .Where(e => e.FullName.StartsWith("assets/minecraft/", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var totalEntries = assetEntries.Count;
            var extractedCount = 0;

            foreach (var entry in assetEntries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue; // Skip directories

                var relativePath = entry.FullName;
                var destinationPath = Path.Combine(outputPath, relativePath);

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                entry.ExtractToFile(destinationPath, overwrite: true);

                extractedCount++;
                if (extractedCount % 100 == 0 || extractedCount == totalEntries)
                {
                    var percentage = 70 + (int)(extractedCount * 25.0 / totalEntries);
                    progress?.Report((percentage, $"Extracting assets... {extractedCount}/{totalEntries}"));
                }
            }
        }

        progress?.Report((95, "Cleaning up..."));

        // Step 6: Write version file
        await File.WriteAllTextAsync(versionFile, version);

        // Step 7: Clean up temp file
        try
        {
            File.Delete(clientJarPath);
        }
        catch
        {
            // Ignore cleanup errors
        }

        // Step 8: Return path to assets/minecraft directory (where blockstates, models, textures are)
        var minecraftAssetsPath = Path.Combine(outputPath, "assets", "minecraft");
        progress?.Report((100, $"Successfully extracted {version} assets to {minecraftAssetsPath}"));

        return minecraftAssetsPath;
    }

    /// <summary>
    /// Gets a list of available Minecraft versions from Mojang's servers.
    /// </summary>
    /// <param name="includeSnapshots">If true, includes snapshot versions</param>
    /// <returns>List of available version IDs</returns>
    public static async Task<IReadOnlyList<string>> GetAvailableVersions(bool includeSnapshots = false)
    {
        var versionManifestJson = await HttpClient.GetStringAsync(VersionManifestUrl);
        var versionManifest = JsonSerializer.Deserialize<VersionManifest>(versionManifestJson)
            ?? throw new InvalidOperationException("Failed to parse version manifest");

        return versionManifest.Versions
            .Where(v => includeSnapshots || v.Type == "release")
            .Select(v => v.Id)
            .ToList();
    }

    /// <summary>
    /// Gets the latest release or snapshot version.
    /// </summary>
    /// <param name="snapshot">If true, gets latest snapshot; otherwise gets latest release</param>
    /// <returns>Version ID</returns>
    public static async Task<string> GetLatestVersion(bool snapshot = false)
    {
        var versionManifestJson = await HttpClient.GetStringAsync(VersionManifestUrl);
        var versionManifest = JsonSerializer.Deserialize<VersionManifest>(versionManifestJson)
            ?? throw new InvalidOperationException("Failed to parse version manifest");

        return snapshot ? versionManifest.Latest.Snapshot : versionManifest.Latest.Release;
    }

    #region JSON Models

    private record VersionManifest(
        [property: JsonPropertyName("latest")] LatestVersions Latest,
        [property: JsonPropertyName("versions")] List<VersionInfo> Versions
    );

    private record LatestVersions(
        [property: JsonPropertyName("release")] string Release,
        [property: JsonPropertyName("snapshot")] string Snapshot
    );

    private record VersionInfo(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("time")] DateTime Time,
        [property: JsonPropertyName("releaseTime")] DateTime ReleaseTime
    );

    private record VersionMetadata(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("downloads")] Downloads? Downloads
    );

    private record Downloads(
        [property: JsonPropertyName("client")] DownloadInfo? Client,
        [property: JsonPropertyName("server")] DownloadInfo? Server
    );

    private record DownloadInfo(
        [property: JsonPropertyName("sha1")] string Sha1,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("url")] string Url
    );

    #endregion
}
