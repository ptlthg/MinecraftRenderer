using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MinecraftRenderer;

public static class MinecraftAtlasGenerator
{
	public sealed record AtlasView(string Name, MinecraftBlockRenderer.BlockRenderOptions Options);

	public sealed record AtlasResult(
		string Category,
		string ViewName,
		int PageNumber,
		string ImagePath,
		string ManifestPath);

	public sealed record AtlasManifestEntry(int SequentialIndex, string Name, int Column, int Row, string? Error)
	{
		[JsonPropertyName("model")] public string? Model { get; init; }

		[JsonPropertyName("textures")] public IReadOnlyList<string>? Textures { get; init; }

		[JsonPropertyName("texturePack")] public string? TexturePack { get; init; }
	}

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

		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tileSize);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
		if (views.Count == 0) throw new ArgumentException("At least one view must be provided", nameof(views));

		Directory.CreateDirectory(outputDirectory);

		var blockNames = includeBlocks
			? (blockFilter?.ToList() ?? renderer.GetKnownBlockNames().ToList())
			: [];
		var itemNames = includeItems
			? (itemFilter?.ToList() ?? renderer.GetKnownItemNames().ToList())
			: [];

		blockNames.Sort(StringComparer.OrdinalIgnoreCase);
		itemNames.Sort(StringComparer.OrdinalIgnoreCase);

		var results = new List<AtlasResult>();
		var serializerOptions = new JsonSerializerOptions
		{
			WriteIndented = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
		};
		var perPage = columns * rows;

		if (perPage <= 0)
		{
			throw new InvalidOperationException("Columns x Rows must be greater than zero.");
		}

		var categories =
			new List<(string Category, IReadOnlyList<string> Names,
				Func<string, MinecraftBlockRenderer.BlockRenderOptions, Image<Rgba32>> RendererFunc)>(2);

		if (blockNames.Count > 0)
		{
			categories.Add(("blocks", blockNames, renderer.RenderBlock));
		}

		if (itemNames.Count > 0)
		{
			categories.Add(("items", itemNames, renderer.RenderGuiItem));
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
							var effectiveOptions = category.Equals("items", StringComparison.OrdinalIgnoreCase)
								? NormalizeItemRenderOptions(view.Options)
								: view.Options;

							using var tile = renderFunc(name, effectiveOptions);
							tile.Mutate(ctx => ctx.Resize(tileSize, tileSize));
							// ReSharper disable once AccessToDisposedClosure
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
						Sanitize(category),
						Sanitize(view.Name),
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

		static string Sanitize(string input)
		{
			var invalidChars = Path.GetInvalidFileNameChars();
			var sanitized = new string(input
				.Select(ch => invalidChars.Contains(ch) ? '_' : ch)
				.ToArray());
			return sanitized.Replace(' ', '_').ToLowerInvariant();
		}

		static MinecraftBlockRenderer.BlockRenderOptions NormalizeItemRenderOptions(
			MinecraftBlockRenderer.BlockRenderOptions options)
		{
			if (MathF.Abs(options.YawInDegrees) < 0.01f && MathF.Abs(options.PitchInDegrees) < 0.01f &&
			    MathF.Abs(options.RollInDegrees) < 0.01f)
			{
				return options;
			}

			return options with { YawInDegrees = 0f, PitchInDegrees = 0f, RollInDegrees = 0f };
		}
	}
}