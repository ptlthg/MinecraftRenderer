using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using MinecraftRenderer.Nbt;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MinecraftRenderer.Snbt;

public static class SnbtItemUtilities
{
	public static string? TryGetItemId(NbtCompound compound)
	{
		ArgumentNullException.ThrowIfNull(compound);

		if (compound.TryGetValue("id", out var idTag) && idTag is NbtString idString &&
		    !string.IsNullOrWhiteSpace(idString.Value))
		{
			return idString.Value;
		}

		foreach (var key in new[] { "item", "Item", "stack", "Stack" })
		{
			if (compound.TryGetValue(key, out var nested) && nested is NbtCompound nestedCompound)
			{
				var nestedId = TryGetItemId(nestedCompound);
				if (!string.IsNullOrWhiteSpace(nestedId))
				{
					return nestedId;
				}
			}
		}

		return null;
	}
}

public static class SnbtItemAtlasGenerator
{
	public sealed record SnbtItemEntry(string Name, string SourcePath, NbtDocument? Document, string? Error);

	public static IReadOnlyList<SnbtItemEntry> LoadDirectory(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);

		if (!Directory.Exists(directory))
		{
			throw new DirectoryNotFoundException($"SNBT item directory '{directory}' does not exist.");
		}

		var results = new List<SnbtItemEntry>();
		foreach (var path in Directory.EnumerateFiles(directory, "*.snbt", SearchOption.TopDirectoryOnly)
			         .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
		{
			var name = Path.GetFileNameWithoutExtension(path);
			try
			{
				var content = File.ReadAllText(path);
				var document = NbtParser.ParseSnbt(content);
				results.Add(new SnbtItemEntry(name, path, document, null));
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[DEBUG] Error loading SNBT file '{path}': {ex.Message}");
				results.Add(new SnbtItemEntry(name, path, null, ex.Message));
			}
		}

		return results;
	}

	public static IReadOnlyList<MinecraftAtlasGenerator.AtlasResult> GenerateAtlases(
		MinecraftBlockRenderer renderer,
		string outputDirectory,
		IReadOnlyList<MinecraftAtlasGenerator.AtlasView> views,
		int tileSize,
		int columns,
		int rows,
		IReadOnlyList<SnbtItemEntry> items)
	{
		ArgumentNullException.ThrowIfNull(renderer);
		ArgumentNullException.ThrowIfNull(outputDirectory);
		ArgumentNullException.ThrowIfNull(views);
		ArgumentNullException.ThrowIfNull(items);

		if (items.Count == 0)
		{
			return Array.Empty<MinecraftAtlasGenerator.AtlasResult>();
		}

		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tileSize);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
		if (views.Count == 0)
		{
			throw new ArgumentException("At least one view must be provided", nameof(views));
		}

		Directory.CreateDirectory(outputDirectory);

		var results = new List<MinecraftAtlasGenerator.AtlasResult>();
		var serializerOptions = new JsonSerializerOptions { WriteIndented = true };
		var perPage = columns * rows;
		if (perPage <= 0)
		{
			throw new InvalidOperationException("Columns x Rows must be greater than zero.");
		}

		const string category = "snbt-items";

		for (var viewIndex = 0; viewIndex < views.Count; viewIndex++)
		{
			var view = views[viewIndex];
			var totalPages = (int)Math.Ceiling(items.Count / (double)perPage);

			for (var page = 0; page < totalPages; page++)
			{
				var startIndex = page * perPage;
				var count = Math.Min(perPage, items.Count - startIndex);
				if (count <= 0)
				{
					continue;
				}

				using var canvas = new Image<Rgba32>(columns * tileSize, rows * tileSize, Color.Transparent);
				var manifestEntries = new List<MinecraftAtlasGenerator.AtlasManifestEntry>(count);

				for (var localIndex = 0; localIndex < count; localIndex++)
				{
					var entry = items[startIndex + localIndex];
					var globalIndex = startIndex + localIndex;
					var col = localIndex % columns;
					var row = localIndex / columns;
					var label = entry.Name;
					string? error = entry.Error;

					if (entry.Document is not null)
					{
						var compound = entry.Document.RootCompound;
						if (compound is null)
						{
							error = "SNBT root is not a compound.";
						}
						else
						{
							var itemId = SnbtItemUtilities.TryGetItemId(compound);
							if (!string.IsNullOrWhiteSpace(itemId))
							{
								label = $"{label} ({itemId})";
							}

							if (error is null)
							{
								try
								{
									var itemOptions = NormalizeItemRenderOptions(view.Options);
									using var tile = renderer.RenderItemFromNbt(compound, itemOptions);
									tile.Mutate(ctx => ctx.Resize(tileSize, tileSize));
									canvas.Mutate(ctx => ctx.DrawImage(tile, new Point(col * tileSize, row * tileSize), 1f));
								}
								catch (Exception ex)
								{
									error = ex.Message;
								}
							}
						}
					}

					manifestEntries.Add(new MinecraftAtlasGenerator.AtlasManifestEntry(globalIndex, label, col, row, error));
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

				results.Add(new MinecraftAtlasGenerator.AtlasResult(category, view.Name, page + 1, imagePath, manifestPath));
			}
		}

		return results;
	}

	private static MinecraftBlockRenderer.BlockRenderOptions NormalizeItemRenderOptions(
		MinecraftBlockRenderer.BlockRenderOptions options)
	{
		var normalized = options;
		if (MathF.Abs(normalized.YawInDegrees) > 0.01f || MathF.Abs(normalized.PitchInDegrees) > 0.01f ||
		    MathF.Abs(normalized.RollInDegrees) > 0.01f)
		{
			normalized = normalized with { YawInDegrees = 0f, PitchInDegrees = 0f, RollInDegrees = 0f };
		}

		if (!normalized.UseGuiTransform)
		{
			normalized = normalized with { UseGuiTransform = true };
		}

		return normalized;
	}

	private static string Sanitize(string input)
	{
		var invalidChars = Path.GetInvalidFileNameChars();
		var sanitized = new string(input
			.Select(ch => invalidChars.Contains(ch) ? '_' : ch)
			.ToArray());
		return sanitized.Replace(' ', '_').ToLowerInvariant();
	}
}
