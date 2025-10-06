using System.Collections.Generic;
using MinecraftRenderer;

namespace CreateAtlases;

internal sealed record AnimatedExportSummary(int Attempted, int Animated, int Errors,
	Dictionary<string, int> FormatCounts, string? ManifestPath);

internal sealed record AnimatedItemRequest(
	string ItemName,
	string Label,
	MinecraftBlockRenderer.ItemRenderData? ItemData = null,
	string? SourcePath = null);

internal sealed record AnimatedManifestEntry(
	string Item,
	string View,
	string ResourceId,
	string? SourcePackId,
	string PackStackHash,
	int FrameCount,
	IReadOnlyList<int> FrameDurations,
	int LoopDurationMs,
	Dictionary<string, string> Files,
	IReadOnlyList<string> Textures,
	string? SourcePath = null);
