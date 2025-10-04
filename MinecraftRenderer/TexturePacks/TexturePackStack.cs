namespace MinecraftRenderer.TexturePacks;

using System.Collections.Generic;
using System.Linq;

public sealed record TexturePackStack(
	IReadOnlyList<RegisteredResourcePack> Packs,
	IReadOnlyList<PackOverlayRoot> OverlayRoots,
	string Fingerprint)
{
	public bool SupportsCit => Packs.Any(static pack => pack.SupportsCit);
}

public sealed record PackOverlayRoot(string Path, string PackId);