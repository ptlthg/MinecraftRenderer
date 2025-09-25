namespace MinecraftRenderer;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

public static class MinecraftAtlasGenerator
{
	public sealed record AtlasView(string Name, MinecraftBlockRenderer.BlockRenderOptions Options);

	public sealed record AtlasResult(string Category, string ViewName, int PageNumber, string ImagePath, string ManifestPath);

	public sealed record AtlasManifestEntry(int SequentialIndex, string Name, int Column, int Row, string? Error);

	public static readonly IReadOnlyList<AtlasView> DefaultViews =
	[
		new("isometric_right", new MinecraftBlockRenderer.BlockRenderOptions()),
		new("isometric_left", new MinecraftBlockRenderer.BlockRenderOptions(YawInDegrees: 45f, Size: 512)),
		new("front", new MinecraftBlockRenderer.BlockRenderOptions(YawInDegrees: 0f, PitchInDegrees: 0f, Size: 512))
	];

	public static IReadOnlyList<AtlasResult> GenerateAtlases(
		MinecraftBlockRenderer renderer,
		string outputDirectory,
		IReadOnlyList<AtlasView> views,
		int tileSize = 128,
		int columns = 16,
		int rows = 16,
		IEnumerable<string>? blockFilter = null,
		IEnumerable<string>? itemFilter = null,
		bool includeBlocks = true,
		bool includeItems = true)
	{
		ArgumentNullException.ThrowIfNull(renderer);
		ArgumentNullException.ThrowIfNull(outputDirectory);
		ArgumentNullException.ThrowIfNull(views);

		if (tileSize <= 0) throw new ArgumentOutOfRangeException(nameof(tileSize));
		if (columns <= 0) throw new ArgumentOutOfRangeException(nameof(columns));
		if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
		if (views.Count == 0) throw new ArgumentException("At least one view must be provided", nameof(views));

		Directory.CreateDirectory(outputDirectory);

		var blockNames = includeBlocks
			? (blockFilter?.ToList() ?? renderer.GetKnownBlockNames().ToList())
			: new List<string>();
		var itemNames = includeItems
			? (itemFilter?.ToList() ?? renderer.GetKnownItemNames().ToList())
			: new List<string>();

		blockNames.Sort(StringComparer.OrdinalIgnoreCase);
		itemNames.Sort(StringComparer.OrdinalIgnoreCase);

		var results = new List<AtlasResult>();
		var serializerOptions = new JsonSerializerOptions { WriteIndented = true };
		var perPage = columns * rows;

		if (perPage <= 0)
		{
			throw new InvalidOperationException("Columns x Rows must be greater than zero.");
		}

		var categories = new List<(string Category, IReadOnlyList<string> Names, Func<string, MinecraftBlockRenderer.BlockRenderOptions, Image<Rgba32>> RendererFunc)>(2);

		if (blockNames.Count > 0)
		{
			categories.Add(("blocks", blockNames, (name, opts) => renderer.RenderBlock(name, opts)));
		}

		if (itemNames.Count > 0)
		{
			categories.Add(("items", itemNames, (name, opts) => renderer.RenderItem(name, opts)));
		}

		foreach (var (category, names, renderFunc) in categories)
		{
			for (var viewIndex = 0; viewIndex < views.Count; viewIndex++)
			{
				var view = views[viewIndex];
				var totalPages = (int)Math.Ceiling(names.Count / (double)perPage);

				for (var page = 0; page < totalPages; page++)
				{
					var startIndex = page * perPage;
					var count = Math.Min(perPage, names.Count - startIndex);

					if (count <= 0)
					{
						continue;
					}

					using var canvas = new Image<Rgba32>(columns * tileSize, rows * tileSize, Color.Transparent);
					var manifestEntries = new List<AtlasManifestEntry>(count);

					for (var localIndex = 0; localIndex < count; localIndex++)
					{
						var name = names[startIndex + localIndex];
						var globalIndex = startIndex + localIndex;
						var col = localIndex % columns;
						var row = localIndex / columns;
						string? error = null;

						try
						{
							using var tile = renderFunc(name, view.Options);
							tile.Mutate(ctx => ctx.Resize(tileSize, tileSize));
							canvas.Mutate(ctx => ctx.DrawImage(tile, new Point(col * tileSize, row * tileSize), 1f));
						}
						catch (Exception ex)
						{
							error = ex.Message;
						}

						manifestEntries.Add(new AtlasManifestEntry(globalIndex, name, col, row, error));
					}

					var baseFileName = string.Join("_", new[]
					{
						sanitize(category),
						sanitize(view.Name),
						$"page{(page + 1).ToString("D2", CultureInfo.InvariantCulture)}"
					}.Where(static s => !string.IsNullOrWhiteSpace(s)));

					var imagePath = Path.Combine(outputDirectory, baseFileName + ".png");
					var manifestPath = Path.Combine(outputDirectory, baseFileName + ".json");

					canvas.SaveAsPng(imagePath);
					File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifestEntries, serializerOptions));

					results.Add(new AtlasResult(category, view.Name, page + 1, imagePath, manifestPath));
				}
			}
		}

		return results;

		static string sanitize(string input)
		{
			var invalidChars = Path.GetInvalidFileNameChars();
			var sanitized = new string(input
				.Select(ch => invalidChars.Contains(ch) ? '_' : ch)
				.ToArray());
			return sanitized.Replace(' ', '_').ToLowerInvariant();
		}
	}
}
