namespace MinecraftRenderer.TexturePacks;

using System.Collections.Generic;

public sealed record ResourcePackMeta(
	string Id,
	string Name,
	string Version,
	string Description,
	IReadOnlyList<string> Authors,
	string? DownloadUrl)
{
	public bool SupportsCit { get; init; }
	public int? PackFormat { get; init; }
}