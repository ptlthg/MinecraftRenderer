namespace MinecraftRenderer;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

public sealed class MinecraftBlockRenderer : IDisposable
{
	public record BlockRenderOptions(
		int Size = 512,
		float YawInDegrees = 0f,
		float PitchInDegrees = 0f,
		float RollInDegrees = 0f,
		float PerspectiveAmount = 0f,
		bool UseGuiTransform = true,
		float Padding = 0.12f,
		float AdditionalScale = 1f,
		Vector3 AdditionalTranslation = default)
	{
		public static BlockRenderOptions Default { get; } = new();
	}

	private readonly BlockModelResolver _modelResolver;
	private readonly TextureRepository _textureRepository;
	private readonly BlockRegistry _blockRegistry;
	private readonly ItemRegistry? _itemRegistry;
	private bool _disposed;

	public TextureRepository TextureRepository => _textureRepository;

	private static readonly (string Suffix, string Replacement)[] InventoryModelSuffixes =
	{
		("_fence", "_fence_inventory"),
		("_wall", "_wall_inventory"),
		("_button", "_button_inventory")
	};

	private static readonly TransformDefinition DefaultGuiTransform = new()
	{
		Rotation = new[] { 30f, 45f, 0f },
		Translation = new[] { 0f, 0f, 0f },
		Scale = new[] { 0.625f, 0.625f, 0.625f }
	};

	private static readonly Dictionary<BlockFaceDirection, int[]> FaceVertexIndices = new()
	{
		{ BlockFaceDirection.South, new[] { 7, 6, 5, 4 } },
		{ BlockFaceDirection.North, new[] { 0, 1, 2, 3 } },
		{ BlockFaceDirection.East, new[] { 6, 2, 1, 5 } },
		{ BlockFaceDirection.West, new[] { 3, 7, 4, 0 } },
		{ BlockFaceDirection.Up, new[] { 3, 2, 6, 7 } },
		{ BlockFaceDirection.Down, new[] { 4, 5, 1, 0 } }
	};

	private MinecraftBlockRenderer(BlockModelResolver modelResolver, TextureRepository textureRepository, BlockRegistry blockRegistry, ItemRegistry? itemRegistry)
	{
		_modelResolver = modelResolver;
		_textureRepository = textureRepository;
		_blockRegistry = blockRegistry;
		_itemRegistry = itemRegistry;
	}

	public static MinecraftBlockRenderer CreateFromDataDirectory(string dataDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

		var modelsPath = Path.Combine(dataDirectory, "blocks_models.json");
		var texturesPath = Path.Combine(dataDirectory, "blocks_textures.json");
		var textureContentPath = Path.Combine(dataDirectory, "texture_content.json");
		var itemsPath = Path.Combine(dataDirectory, "items_textures.json");

		var modelResolver = BlockModelResolver.LoadFromFile(modelsPath);
		var blockRegistry = BlockRegistry.LoadFromFile(texturesPath);
		var textureRepository = new TextureRepository(dataDirectory, File.Exists(textureContentPath) ? textureContentPath : null);
		ItemRegistry? itemRegistry = null;
		if (File.Exists(itemsPath))
		{
			itemRegistry = ItemRegistry.LoadFromFile(itemsPath);
		}

		return new MinecraftBlockRenderer(modelResolver, textureRepository, blockRegistry, itemRegistry);
	}

	public IReadOnlyList<string> GetKnownBlockNames() => _blockRegistry.GetAllBlockNames();
	public IReadOnlyList<string> GetKnownItemNames() => _itemRegistry?.GetAllItemNames() ?? Array.Empty<string>();

	public Image<Rgba32> RenderBlock(string blockName, BlockRenderOptions? options = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(blockName);
		options ??= BlockRenderOptions.Default;

		string modelName = blockName;
		if (_blockRegistry.TryGetModel(blockName, out var mappedModel) && !string.IsNullOrWhiteSpace(mappedModel))
		{
			modelName = mappedModel;
		}

		var model = _modelResolver.Resolve(modelName);
		return RenderModel(model, options);
	}

	public Image<Rgba32> RenderItem(string itemName, BlockRenderOptions? options = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(itemName);
		options ??= BlockRenderOptions.Default;
		EnsureNotDisposed();

		ItemRegistry.ItemInfo? itemInfo = null;
		if (_itemRegistry is not null)
		{
			_itemRegistry.TryGetInfo(itemName, out itemInfo);
		}

		var modelName = itemInfo?.Model;
		if (string.IsNullOrWhiteSpace(modelName))
		{
			if (_blockRegistry.TryGetModel(itemName, out var blockModel) && !string.IsNullOrWhiteSpace(blockModel))
			{
				modelName = blockModel;
			}
			else
			{
				modelName = itemName;
			}
		}

		BlockModelInstance? model = null;
		var hasModel = false;
		foreach (var candidate in BuildModelCandidates(modelName!, itemName))
		{
			try
			{
				model = _modelResolver.Resolve(candidate);
				hasModel = true;
				break;
			}
			catch (KeyNotFoundException)
			{
				continue;
			}
			catch (InvalidOperationException)
			{
				continue;
			}
		}

		var usesBuiltinGenerated = UsesBuiltinGenerated(model);
		var isBuiltinEntityItem = UsesBuiltinEntity(model) || IsBuiltinEntityItemName(itemName);

		if (isBuiltinEntityItem && TryRenderBuiltinEntityItem(itemName, itemInfo, options, out var builtinEntityRender))
		{
			return builtinEntityRender;
		}

		if (model is not null && IsBillboardModel(model))
		{
			var billboardLayers = CollectBillboardTextures(model, itemInfo);
			var resolvedBillboardLayers = ResolveTextureIdentifiers(billboardLayers, model);
			if (resolvedBillboardLayers.Count > 0)
			{
				return RenderFlatItem(resolvedBillboardLayers, options);
			}
		}

		var layerIdentifiers = CollectItemLayerTextures(model, itemInfo);
		if (!hasModel || model is null || model.Elements.Count == 0 || usesBuiltinGenerated)
		{
			if (TryRenderGeneratedGeometry(itemName, model, itemInfo, options, out var generated))
			{
				return generated;
			}

			if (layerIdentifiers.Count > 0)
			{
				var resolvedLayers = ResolveTextureIdentifiers(layerIdentifiers, model);
				if (resolvedLayers.Count > 0)
				{
					return RenderFlatItem(resolvedLayers, options);
				}
			}

			if (TryRenderEmbeddedTexture(itemName, options, out var embeddedFlat))
			{
				return embeddedFlat;
			}
		}

		if (hasModel && model is not null && model.Elements.Count > 0)
		{
			return RenderModel(model, options);
		}

		if (TryRenderEmbeddedTexture(itemName, options, out var fallbackFlat))
		{
			return fallbackFlat;
		}

		throw new InvalidOperationException($"Unable to resolve a model or texture for item '{itemName}'.");
	}

	public Image<Rgba32> RenderModel(BlockModelInstance model, BlockRenderOptions options)
	{
		EnsureNotDisposed();

		var displayTransform = BuildDisplayTransform(options.UseGuiTransform ? model.GetDisplayTransform("gui") ?? DefaultGuiTransform : DefaultGuiTransform);
		var additionalRotation = CreateRotationMatrix(options.YawInDegrees * DegreesToRadians, options.PitchInDegrees * DegreesToRadians, options.RollInDegrees * DegreesToRadians);
		var scaleMatrix = Matrix4x4.CreateScale(options.AdditionalScale);
		var translationVector = new Vector3(
			options.AdditionalTranslation.X / 16f,
			options.AdditionalTranslation.Y / 16f,
			options.AdditionalTranslation.Z / 16f);
		var translationMatrix = Matrix4x4.CreateTranslation(translationVector);

		Matrix4x4 totalTransform = Matrix4x4.Identity;
		totalTransform = Matrix4x4.Multiply(totalTransform, displayTransform);
		totalTransform = Matrix4x4.Multiply(totalTransform, additionalRotation);
		totalTransform = Matrix4x4.Multiply(totalTransform, scaleMatrix);
		totalTransform = Matrix4x4.Multiply(totalTransform, translationMatrix);
		var orientationCorrection = Matrix4x4.CreateRotationY(MathF.PI / 2f);
		totalTransform = Matrix4x4.Multiply(orientationCorrection, totalTransform);

		var triangles = BuildTriangles(model, totalTransform);

		if (triangles.Count == 0)
		{
			return new Image<Rgba32>(options.Size, options.Size, Color.Transparent);
		}

		triangles.Sort((a, b) => b.Depth.CompareTo(a.Depth));

		var bounds = ComputeBounds(triangles);
		var referenceBounds = ComputeReferenceBounds(totalTransform);
		var padding = Math.Clamp(options.Padding, 0f, 0.4f);
		var dimensionX = bounds.MaxX - bounds.MinX;
		var dimensionY = bounds.MaxY - bounds.MinY;
		var dimension = MathF.Max(dimensionX, dimensionY);
		if (dimension < 1e-5f)
		{
			dimension = 1f;
		}

		var referenceDimensionX = referenceBounds.MaxX - referenceBounds.MinX;
		var referenceDimensionY = referenceBounds.MaxY - referenceBounds.MinY;
		var referenceDimension = MathF.Max(referenceDimensionX, referenceDimensionY);
		if (referenceDimension < 1e-5f)
		{
			referenceDimension = dimension;
		}

		var availableSize = options.Size * (1f - padding * 2f);
		var scale = availableSize / referenceDimension;
		var center = new Vector2((bounds.MinX + bounds.MaxX) * 0.5f, (bounds.MinY + bounds.MaxY) * 0.5f);
		var offset = new Vector2(options.Size / 2f);

		PerspectiveParams? perspective = options.PerspectiveAmount > 0.01f
			? new PerspectiveParams(options.PerspectiveAmount, 10f, 10f)
			: null;

		var canvas = new Image<Rgba32>(options.Size, options.Size, Color.Transparent);
		var depthBuffer = new float[options.Size * options.Size];
		Array.Fill(depthBuffer, float.PositiveInfinity);
		var triangleOrder = 0;
		const float DepthBiasPerTriangle = 1e-4f;

		foreach (var tri in triangles)
		{
			var centeredV1 = new Vector3(tri.V1.X - center.X, tri.V1.Y - center.Y, tri.V1.Z);
			var centeredV2 = new Vector3(tri.V2.X - center.X, tri.V2.Y - center.Y, tri.V2.Z);
			var centeredV3 = new Vector3(tri.V3.X - center.X, tri.V3.Y - center.Y, tri.V3.Z);

			var p1 = ProjectToScreen(centeredV1, scale, offset, perspective);
			var p2 = ProjectToScreen(centeredV2, scale, offset, perspective);
			var p3 = ProjectToScreen(centeredV3, scale, offset, perspective);

			var depthBias = triangleOrder * DepthBiasPerTriangle;
			triangleOrder++;

			RasterizeTriangle(
				canvas,
				depthBuffer,
				depthBias,
				centeredV1.Z,
				centeredV2.Z,
				centeredV3.Z,
				p1,
				p2,
				p3,
				tri.T1,
				tri.T2,
				tri.T3,
				tri.Texture,
				tri.TextureRect);
		}

		return canvas;
	}

	private static IEnumerable<string> BuildModelCandidates(string primaryName, string itemName)
	{
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var candidate in EnumerateCandidateNames(primaryName))
		{
			if (seen.Add(candidate))
			{
				yield return candidate;
			}
		}

		if (!string.Equals(primaryName, itemName, StringComparison.OrdinalIgnoreCase))
		{
			foreach (var candidate in EnumerateCandidateNames(itemName))
			{
				if (seen.Add(candidate))
				{
					yield return candidate;
				}
			}
		}
	}

	private static IEnumerable<string> EnumerateCandidateNames(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			yield break;
		}

		yield return name;

		foreach (var variant in GenerateInventoryVariants(name))
		{
			yield return variant;
		}
	}

	private static IEnumerable<string> GenerateInventoryVariants(string name)
	{
		var (prefix, baseName) = SplitModelName(name);
		if (string.IsNullOrWhiteSpace(baseName))
		{
			yield break;
		}

		if (!baseName.EndsWith("_inventory", StringComparison.OrdinalIgnoreCase))
		{
			yield return prefix + baseName + "_inventory";

			foreach (var (suffix, replacement) in InventoryModelSuffixes)
			{
				if (baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				{
					var replaced = baseName[..^suffix.Length] + replacement;
					yield return prefix + replaced;
				}
			}
		}
	}

	private static (string Prefix, string BaseName) SplitModelName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return (string.Empty, string.Empty);
		}

		var lastSlash = name.LastIndexOf('/');
		if (lastSlash >= 0)
		{
			return (name[..(lastSlash + 1)], name[(lastSlash + 1)..]);
		}

		return (string.Empty, name);
	}

	private static bool IsBillboardModel(BlockModelInstance model)
	{
		if (model.Elements.Count == 0)
		{
			return false;
		}

		if (model.Textures.ContainsKey("cross"))
		{
			return true;
		}

		for (var i = 0; i < model.ParentChain.Count; i++)
		{
			if (ParentIndicatesBillboard(model.ParentChain[i]))
			{
				return true;
			}
		}

		return ParentIndicatesBillboard(model.Name);

		static bool ParentIndicatesBillboard(string value)
			=> value.Contains("cross", StringComparison.OrdinalIgnoreCase)
			|| value.Contains("tinted_cross", StringComparison.OrdinalIgnoreCase)
			|| value.Contains("seagrass", StringComparison.OrdinalIgnoreCase);
	}

	private static List<string> CollectBillboardTextures(BlockModelInstance model, ItemRegistry.ItemInfo? itemInfo)
	{
		var textures = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		void TryAdd(string? candidate)
		{
			if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
			{
				textures.Add(candidate);
			}
		}

		if (model.Textures.TryGetValue("cross", out var crossTexture))
		{
			TryAdd(crossTexture);
		}

		if (model.Textures.TryGetValue("texture", out var genericTexture))
		{
			TryAdd(genericTexture);
		}

		if (textures.Count == 0 && itemInfo is not null)
		{
			TryAdd(itemInfo.Texture);
		}

		return textures;
	}

	private static List<string> CollectItemLayerTextures(BlockModelInstance? model, ItemRegistry.ItemInfo? itemInfo)
	{
		var layers = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		if (model is not null)
		{
			var orderedLayers = model.Textures
				.Where(kvp => kvp.Key.StartsWith("layer", StringComparison.OrdinalIgnoreCase))
				.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);

			foreach (var layer in orderedLayers)
			{
				if (!string.IsNullOrWhiteSpace(layer.Value) && seen.Add(layer.Value))
				{
					layers.Add(layer.Value);
				}
			}
		}

		if (itemInfo is not null && !string.IsNullOrWhiteSpace(itemInfo.Texture) && seen.Add(itemInfo.Texture))
		{
			layers.Add(itemInfo.Texture);
		}

		return layers;
	}

	private static List<string> ResolveTextureIdentifiers(IEnumerable<string> identifiers, BlockModelInstance? model)
	{
		var resolved = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var identifier in identifiers)
		{
			if (string.IsNullOrWhiteSpace(identifier))
			{
				continue;
			}

			var textureId = ResolveTexture(identifier, model);
			if (string.IsNullOrWhiteSpace(textureId) || textureId.Equals("minecraft:missingno", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (seen.Add(textureId))
			{
				resolved.Add(textureId);
			}
		}

		return resolved;
	}

	private bool TryRenderGeneratedGeometry(string itemName, BlockModelInstance? model, ItemRegistry.ItemInfo? itemInfo, BlockRenderOptions options, out Image<Rgba32> generated)
	{
		generated = null!;

		if (IsShulkerBox(itemName))
		{
			var textureReference = itemInfo?.Texture;
			if (model?.Textures.TryGetValue("particle", out var particleTexture) == true)
			{
				textureReference = particleTexture;
			}

			if (!string.IsNullOrWhiteSpace(textureReference))
			{
				var resolvedTexture = ResolveTexture(textureReference!, model);
				if (!string.IsNullOrWhiteSpace(resolvedTexture) && !resolvedTexture.Equals("minecraft:missingno", StringComparison.OrdinalIgnoreCase))
				{
					var generatedModel = CreateGeneratedCubeModel(itemName, resolvedTexture);
					generated = RenderModel(generatedModel, options);
					return true;
				}
			}
		}

		return false;
	}

	private bool TryRenderBuiltinEntityItem(string itemName, ItemRegistry.ItemInfo? itemInfo, BlockRenderOptions options, out Image<Rgba32> rendered)
	{
		if (TryRenderEmbeddedTexture(itemName, options, out rendered))
		{
			return true;
		}

		if (itemInfo is not null && !string.IsNullOrWhiteSpace(itemInfo.Texture))
		{
			var resolved = ResolveTexture(itemInfo.Texture, null);
			if (!string.IsNullOrWhiteSpace(resolved) && _textureRepository.TryGetTexture(resolved, out _))
			{
				rendered = RenderFlatItem(new[] { resolved }, options);
				return true;
			}
		}

		rendered = null!;
		return false;
	}

	private bool TryRenderEmbeddedTexture(string textureId, BlockRenderOptions options, out Image<Rgba32> rendered)
	{
		if (_textureRepository.TryGetTexture(textureId, out _))
		{
			rendered = RenderFlatItem(new[] { textureId }, options);
			return true;
		}

		rendered = null!;
		return false;
	}

	private static bool IsShulkerBox(string itemName)
		=> itemName.EndsWith("_shulker_box", StringComparison.OrdinalIgnoreCase)
		|| string.Equals(itemName, "shulker_box", StringComparison.OrdinalIgnoreCase);

	private static BlockModelInstance CreateGeneratedCubeModel(string name, string textureId)
	{
		var textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["all"] = textureId
		};

		var faces = new Dictionary<BlockFaceDirection, ModelFace>
		{
			[BlockFaceDirection.North] = new ModelFace("#all", new Vector4(0, 0, 16, 16), 0, null, null),
			[BlockFaceDirection.South] = new ModelFace("#all", new Vector4(0, 0, 16, 16), 0, null, null),
			[BlockFaceDirection.East] = new ModelFace("#all", new Vector4(0, 0, 16, 16), 0, null, null),
			[BlockFaceDirection.West] = new ModelFace("#all", new Vector4(0, 0, 16, 16), 0, null, null),
			[BlockFaceDirection.Up] = new ModelFace("#all", new Vector4(0, 0, 16, 16), 0, null, null),
			[BlockFaceDirection.Down] = new ModelFace("#all", new Vector4(0, 0, 16, 16), 0, null, null)
		};

		var element = new ModelElement(
			new Vector3(0, 0, 0),
			new Vector3(16, 16, 16),
			null,
			faces,
			true);

		return new BlockModelInstance(
			$"{name}_generated",
			Array.Empty<string>(),
			textures,
			new Dictionary<string, TransformDefinition>(StringComparer.OrdinalIgnoreCase),
			new List<ModelElement> { element });
	}

	private Image<Rgba32> RenderFlatItem(IReadOnlyList<string> layerTextureIds, BlockRenderOptions options)
	{
		var canvas = new Image<Rgba32>(options.Size, options.Size, Color.Transparent);

		foreach (var textureId in layerTextureIds)
		{
			var texture = _textureRepository.GetTexture(textureId);
			var scale = MathF.Min(options.Size / (float)texture.Width, options.Size / (float)texture.Height);
			var targetWidth = Math.Max(1, (int)MathF.Round(texture.Width * scale));
			var targetHeight = Math.Max(1, (int)MathF.Round(texture.Height * scale));

			using var resized = texture.Clone(ctx => ctx.Resize(new ResizeOptions
			{
				Size = new Size(targetWidth, targetHeight),
				Sampler = KnownResamplers.NearestNeighbor,
				Mode = ResizeMode.Stretch
			}));

			var offset = new Point((canvas.Width - targetWidth) / 2, (canvas.Height - targetHeight) / 2);
			canvas.Mutate(ctx => ctx.DrawImage(resized, offset, 1f));
		}

		return canvas;
	}

	private static bool UsesBuiltinGenerated(BlockModelInstance? model)
		=> UsesModelReference(model, "generated");

	private static bool UsesBuiltinEntity(BlockModelInstance? model)
		=> UsesModelReference(model, "entity");

	private static bool UsesModelReference(BlockModelInstance? model, string reference)
	{
		if (model is null)
		{
			return false;
		}

		if (MatchesModelReference(model.Name, reference))
		{
			return true;
		}

		foreach (var parent in model.ParentChain)
		{
			if (MatchesModelReference(parent, reference))
			{
				return true;
			}
		}

		return false;
	}

	private static bool MatchesModelReference(string candidate, string reference)
	{
		if (string.IsNullOrWhiteSpace(candidate))
		{
			return false;
		}

		var normalized = NormalizeModelReference(candidate);

		if (normalized.Equals(reference, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (normalized.Equals($"item/{reference}", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (normalized.Equals($"builtin/{reference}", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (normalized.StartsWith($"builtin/{reference}/", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		return false;
	}

	private static string NormalizeModelReference(string value)
	{
		var normalized = value.Trim().Replace('\\', '/');

		if (normalized.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[10..];
		}

		return normalized;
	}

	private static bool IsBuiltinEntityItemName(string itemName)
		=> !string.IsNullOrWhiteSpace(itemName)
		&& (itemName.EndsWith("_bed", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(itemName, "bed", StringComparison.OrdinalIgnoreCase));

	private List<VisibleTriangle> BuildTriangles(BlockModelInstance model, Matrix4x4 transform)
	{
		var triangles = new List<VisibleTriangle>(model.Elements.Count * 12);

		foreach (var element in model.Elements)
		{
			var elementTriangles = BuildTrianglesForElement(model, element, transform);
			triangles.AddRange(elementTriangles);
		}

		return triangles;
	}

	private List<VisibleTriangle> BuildTrianglesForElement(BlockModelInstance model, ModelElement element, Matrix4x4 transform)
	{
		var vertices = BuildElementVertices(element);
		ApplyElementRotation(element, vertices);
		var results = new List<VisibleTriangle>(element.Faces.Count * 2);

		foreach (var (direction, face) in element.Faces)
		{
			var textureId = ResolveTexture(face.Texture, model);
			var texture = _textureRepository.GetTexture(textureId);

			var faceUv = GetFaceUv(face, direction, element);
			var textureRect = ComputeTextureRectangle(faceUv, texture);

			var uvMap = CreateUvMap(element, direction, faceUv, face.Rotation ?? 0);

			var indices = FaceVertexIndices[direction];
			var transformed = new Vector3[4];

			for (var i = 0; i < 4; i++)
			{
				transformed[i] = Vector3.Transform(vertices[indices[i]], transform);
			}

			var expectedNormal = direction switch
			{
				BlockFaceDirection.South => Vector3.UnitZ,
				BlockFaceDirection.North => -Vector3.UnitZ,
				BlockFaceDirection.East => Vector3.UnitX,
				BlockFaceDirection.West => -Vector3.UnitX,
				BlockFaceDirection.Up => Vector3.UnitY,
				BlockFaceDirection.Down => -Vector3.UnitY,
				_ => Vector3.UnitZ
			};

			var edge1 = transformed[1] - transformed[0];
			var edge2 = transformed[2] - transformed[0];
			var faceNormal = Vector3.Cross(edge1, edge2);

			if (faceNormal != Vector3.Zero && Vector3.Dot(faceNormal, expectedNormal) < 0)
			{
				(transformed[1], transformed[3]) = (transformed[3], transformed[1]);
				(uvMap[1], uvMap[3]) = (uvMap[3], uvMap[1]);
			}

			var depth = (transformed[0].Z + transformed[1].Z + transformed[2].Z + transformed[3].Z) * 0.25f;

			results.Add(new VisibleTriangle(
				transformed[0], transformed[1], transformed[2],
				uvMap[0], uvMap[1], uvMap[2],
				texture,
				textureRect,
				depth));

			results.Add(new VisibleTriangle(
				transformed[0], transformed[2], transformed[3],
				uvMap[0], uvMap[2], uvMap[3],
				texture,
				textureRect,
				depth));
		}

		return results;
	}

	private static Vector3[] BuildElementVertices(ModelElement element)
	{
		var min = element.From;
		var max = element.To;

		var fx = NormalizeComponent(min.X);
		var fy = NormalizeComponent(min.Y);
		var fz = NormalizeComponent(min.Z);
		var tx = NormalizeComponent(max.X);
		var ty = NormalizeComponent(max.Y);
		var tz = NormalizeComponent(max.Z);

		return new[]
		{
			new Vector3(fx, fy, fz),
			new Vector3(tx, fy, fz),
			new Vector3(tx, ty, fz),
			new Vector3(fx, ty, fz),
			new Vector3(fx, fy, tz),
			new Vector3(tx, fy, tz),
			new Vector3(tx, ty, tz),
			new Vector3(fx, ty, tz)
		};
	}

	private static float NormalizeComponent(float value) => value / 16f - 0.5f;

	private static void ApplyElementRotation(ModelElement element, Vector3[] vertices)
	{
		if (element.Rotation is null)
		{
			return;
		}

		var axis = element.Rotation.Axis switch
		{
			"x" => Vector3.UnitX,
			"z" => Vector3.UnitZ,
			_ => Vector3.UnitY
		};

		var angle = element.Rotation.AngleInDegrees * DegreesToRadians;
		var pivot = new Vector3(
			NormalizeComponent(element.Rotation.Origin.X),
			NormalizeComponent(element.Rotation.Origin.Y),
			NormalizeComponent(element.Rotation.Origin.Z));

		var rotationMatrix = Matrix4x4.CreateFromAxisAngle(axis, angle);

		for (var i = 0; i < vertices.Length; i++)
		{
			var relative = vertices[i] - pivot;
			relative = Vector3.Transform(relative, rotationMatrix);
			vertices[i] = relative + pivot;
		}
	}

	private static Vector4 GetFaceUv(ModelFace face, BlockFaceDirection direction, ModelElement element)
	{
		if (face.Uv.HasValue)
		{
			return face.Uv.Value;
		}

		var from = element.From;
		var to = element.To;

		return direction switch
		{
			BlockFaceDirection.South => new Vector4(from.X, from.Y, to.X, to.Y),
			BlockFaceDirection.North => new Vector4(16f - to.X, from.Y, 16f - from.X, to.Y),
			BlockFaceDirection.East => new Vector4(from.Z, from.Y, to.Z, to.Y),
			BlockFaceDirection.West => new Vector4(16f - to.Z, from.Y, 16f - from.Z, to.Y),
			BlockFaceDirection.Up => new Vector4(from.X, 16f - to.Z, to.X, 16f - from.Z),
			BlockFaceDirection.Down => new Vector4(from.X, from.Z, to.X, to.Z),
			_ => new Vector4(0, 0, 16, 16)
		};
	}

	private static Rectangle ComputeTextureRectangle(Vector4 uv, Image<Rgba32> texture)
	{
		var widthFactor = texture.Width / 16f;
		var heightFactor = texture.Height / 16f;

		var minX = (int)MathF.Round(MathF.Min(uv.X, uv.Z) * widthFactor);
		var maxX = (int)MathF.Round(MathF.Max(uv.X, uv.Z) * widthFactor);
		var minY = (int)MathF.Round(MathF.Min(uv.Y, uv.W) * heightFactor);
		var maxY = (int)MathF.Round(MathF.Max(uv.Y, uv.W) * heightFactor);

		minX = Math.Clamp(minX, 0, texture.Width - 1);
		minY = Math.Clamp(minY, 0, texture.Height - 1);
		maxX = Math.Clamp(Math.Max(maxX, minX + 1), minX + 1, texture.Width);
		maxY = Math.Clamp(Math.Max(maxY, minY + 1), minY + 1, texture.Height);

		return new Rectangle(minX, minY, maxX - minX, maxY - minY);
	}

	private static Vector2[] CreateUvMap(ModelElement element, BlockFaceDirection direction, Vector4 faceUv, int rotationDegrees)
	{
		var corners = GetFaceCornerPositions(element, direction);
		var absolute = new Vector2[corners.Length];

		for (var i = 0; i < corners.Length; i++)
		{
			var corner = corners[i];
			var uv = CalculateFaceCoordinate(element, direction, faceUv, corner);
			absolute[i] = uv;
		}

		ApplyFaceRotationAbsolute(absolute, faceUv, rotationDegrees);
		return NormalizeFaceCoordinates(absolute, faceUv);
	}

	private static Vector2 CalculateFaceCoordinate(ModelElement element, BlockFaceDirection direction, Vector4 faceUv, Vector3 corner)
	{
		static float SafeRatio(float value, float length)
			=> length < 1e-5f ? 0f : Clamp01(value / length);

		var du = faceUv.Z - faceUv.X;
		var dv = faceUv.W - faceUv.Y;

		float uNormalized = direction switch
		{
			BlockFaceDirection.South => SafeRatio(corner.X - element.From.X, element.To.X - element.From.X),
			BlockFaceDirection.North => SafeRatio(corner.X - element.From.X, element.To.X - element.From.X),
			BlockFaceDirection.East => SafeRatio(corner.Z - element.From.Z, element.To.Z - element.From.Z),
			BlockFaceDirection.West => SafeRatio(corner.Z - element.From.Z, element.To.Z - element.From.Z),
			BlockFaceDirection.Up => SafeRatio(corner.X - element.From.X, element.To.X - element.From.X),
			BlockFaceDirection.Down => SafeRatio(corner.X - element.From.X, element.To.X - element.From.X),
			_ => 0f
		};

		float vNormalized = direction switch
		{
			BlockFaceDirection.South => SafeRatio(element.To.Y - corner.Y, element.To.Y - element.From.Y),
			BlockFaceDirection.North => SafeRatio(corner.Y - element.From.Y, element.To.Y - element.From.Y),
			BlockFaceDirection.East => SafeRatio(element.To.Y - corner.Y, element.To.Y - element.From.Y),
			BlockFaceDirection.West => SafeRatio(element.To.Y - corner.Y, element.To.Y - element.From.Y),
			BlockFaceDirection.Up => SafeRatio(corner.Z - element.From.Z, element.To.Z - element.From.Z),
			BlockFaceDirection.Down => SafeRatio(element.To.Z - corner.Z, element.To.Z - element.From.Z),
			_ => 0f
		};

		var u = faceUv.X + du * uNormalized;
		var v = faceUv.Y + dv * vNormalized;
		return new Vector2(u, v);
	}

	private static Vector3[] GetFaceCornerPositions(ModelElement element, BlockFaceDirection direction)
	{
		var from = element.From;
		var to = element.To;

		return direction switch
		{
			BlockFaceDirection.South => new[]
			{
				new Vector3(from.X, to.Y, to.Z),
				new Vector3(to.X, to.Y, to.Z),
				new Vector3(to.X, from.Y, to.Z),
				new Vector3(from.X, from.Y, to.Z)
			},
            BlockFaceDirection.North => new[]
            {
				new Vector3(to.X, to.Y, from.Z),
				new Vector3(from.X, to.Y, from.Z),
				new Vector3(from.X, from.Y, from.Z),
				new Vector3(to.X, from.Y, from.Z)
			},
			BlockFaceDirection.East => new[]
			{
				new Vector3(to.X, to.Y, from.Z),
				new Vector3(to.X, to.Y, to.Z),
				new Vector3(to.X, from.Y, to.Z),
				new Vector3(to.X, from.Y, from.Z)
			},
			BlockFaceDirection.West => new[]
			{
				new Vector3(from.X, to.Y, to.Z),
				new Vector3(from.X, to.Y, from.Z),
				new Vector3(from.X, from.Y, from.Z),
				new Vector3(from.X, from.Y, to.Z)
			},
			BlockFaceDirection.Up => new[]
			{
				new Vector3(from.X, to.Y, from.Z),
				new Vector3(to.X, to.Y, from.Z),
				new Vector3(to.X, to.Y, to.Z),
				new Vector3(from.X, to.Y, to.Z)
			},
			BlockFaceDirection.Down => new[]
			{
				new Vector3(from.X, from.Y, to.Z),
				new Vector3(to.X, from.Y, to.Z),
				new Vector3(to.X, from.Y, from.Z),
				new Vector3(from.X, from.Y, from.Z)
			},
			_ => Array.Empty<Vector3>()
		};
	}

	private static void ApplyFaceRotationAbsolute(Vector2[] uv, Vector4 faceUv, int rotationDegrees)
	{
		var normalized = ((rotationDegrees % 360) + 360) % 360;
		if (normalized == 0)
		{
			return;
		}

		var steps = normalized / 90;
		if (steps == 0)
		{
			return;
		}

		var center = new Vector2((faceUv.X + faceUv.Z) * 0.5f, (faceUv.Y + faceUv.W) * 0.5f);

		for (var s = 0; s < steps; s++)
		{
			for (var i = 0; i < uv.Length; i++)
			{
				var relative = uv[i] - center;
				relative = new Vector2(-relative.Y, relative.X);
				uv[i] = relative + center;
			}
		}
	}

	private static Vector2[] NormalizeFaceCoordinates(Vector2[] absoluteUv, Vector4 faceUv)
	{
		var width = faceUv.Z - faceUv.X;
		var height = faceUv.W - faceUv.Y;
		var invWidth = MathF.Abs(width) < 1e-5f ? 0f : 1f / width;
		var invHeight = MathF.Abs(height) < 1e-5f ? 0f : 1f / height;

		var normalized = new Vector2[absoluteUv.Length];
		for (var i = 0; i < absoluteUv.Length; i++)
		{
			var u = (absoluteUv[i].X - faceUv.X) * invWidth;
			var v = (absoluteUv[i].Y - faceUv.Y) * invHeight;
			normalized[i] = new Vector2(Clamp01(u), Clamp01(v));
		}

		return normalized;
	}

	private static float Clamp01(float value) => value <= 0f ? 0f : value >= 1f ? 1f : value;

	private static Bounds ComputeBounds(IEnumerable<VisibleTriangle> triangles)
	{
		var minX = float.MaxValue;
		var minY = float.MaxValue;
		var maxX = float.MinValue;
		var maxY = float.MinValue;

		void Update(Vector3 v)
		{
			minX = MathF.Min(minX, v.X);
			maxX = MathF.Max(maxX, v.X);
			minY = MathF.Min(minY, v.Y);
			maxY = MathF.Max(maxY, v.Y);
		}

		foreach (var tri in triangles)
		{
			Update(tri.V1);
			Update(tri.V2);
			Update(tri.V3);
		}

		return new Bounds(minX, maxX, minY, maxY);
	}

	private static Bounds ComputeReferenceBounds(Matrix4x4 transform)
	{
		Span<Vector3> corners =
        [
            new(-0.5f, -0.5f, -0.5f),
			new(0.5f, -0.5f, -0.5f),
			new(0.5f, 0.5f, -0.5f),
			new(-0.5f, 0.5f, -0.5f),
			new(-0.5f, -0.5f, 0.5f),
			new(0.5f, -0.5f, 0.5f),
			new(0.5f, 0.5f, 0.5f),
			new(-0.5f, 0.5f, 0.5f)
		];

		var minX = float.MaxValue;
		var minY = float.MaxValue;
		var maxX = float.MinValue;
		var maxY = float.MinValue;

		for (var i = 0; i < corners.Length; i++)
		{
			var transformed = Vector3.Transform(corners[i], transform);
			minX = MathF.Min(minX, transformed.X);
			maxX = MathF.Max(maxX, transformed.X);
			minY = MathF.Min(minY, transformed.Y);
			maxY = MathF.Max(maxY, transformed.Y);
		}

		if (float.IsInfinity(minX) || float.IsInfinity(minY) || float.IsInfinity(maxX) || float.IsInfinity(maxY))
		{
			return new Bounds(-0.5f, 0.5f, -0.5f, 0.5f);
		}

		return new Bounds(minX, maxX, minY, maxY);
	}

	private static Matrix4x4 BuildDisplayTransform(TransformDefinition? transform)
	{
		if (transform is null)
		{
			return Matrix4x4.Identity;
		}

		var rotation = transform.Rotation ?? [0f, 0f, 0f];
		var translation = transform.Translation ?? [0f, 0f, 0f];
		var scale = transform.Scale ?? [1f, 1f, 1f];

		var scaleMatrix = Matrix4x4.CreateScale(scale[0], scale[1], scale[2]);
		var rotationMatrix = CreateRotationMatrix(rotation[1] * DegreesToRadians, rotation[0] * DegreesToRadians, rotation[2] * DegreesToRadians);
		var translationMatrix = Matrix4x4.CreateTranslation(translation[0] / 16f, translation[1] / 16f, translation[2] / 16f);

		return scaleMatrix * rotationMatrix * translationMatrix;
	}

	private static string ResolveTexture(string texture, BlockModelInstance? model)
	{
		if (string.IsNullOrWhiteSpace(texture))
		{
			return "minecraft:missingno";
		}

		if (!texture.StartsWith('#'))
		{
			return texture;
		}

		if (model is null)
		{
			return "minecraft:missingno";
		}

		var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var current = texture;

		while (current.StartsWith('#'))
		{
			var key = current[1..];
			if (!visited.Add(key))
			{
				return "minecraft:missingno";
			}

			if (!model.Textures.TryGetValue(key, out var mapped) || string.IsNullOrWhiteSpace(mapped))
			{
				return "minecraft:missingno";
			}

			current = mapped;
		}

		return current;
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_textureRepository.Dispose();
	}

	private void EnsureNotDisposed()
	{
		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(MinecraftBlockRenderer));
		}
	}

	private static Vector2 ProjectToScreen(Vector3 point, float scale, Vector2 offset, PerspectiveParams? perspectiveParams)
	{
		if (perspectiveParams is null)
		{
			return new Vector2(point.X * scale + offset.X, -point.Y * scale + offset.Y);
		}

		var perspectiveFactor = perspectiveParams.Value.FocalLength / (perspectiveParams.Value.CameraDistance - point.Z);
		var perspX = point.X * perspectiveFactor;
		var perspY = point.Y * perspectiveFactor;

		var orthoX = point.X;
		var orthoY = point.Y;

		var finalX = orthoX + (perspX - orthoX) * perspectiveParams.Value.Amount;
		var finalY = orthoY + (perspY - orthoY) * perspectiveParams.Value.Amount;

		return new Vector2(finalX * scale + offset.X, -finalY * scale + offset.Y);
	}

	private static void RasterizeTriangle(
		Image<Rgba32> canvas,
		float[] depthBuffer,
		float depthBias,
		float z1,
		float z2,
		float z3,
		Vector2 p1, Vector2 p2, Vector2 p3,
		Vector2 t1, Vector2 t2, Vector2 t3,
		Image<Rgba32> texture,
		Rectangle textureRect)
	{
		var area = (p2.X - p1.X) * (p3.Y - p1.Y) - (p3.X - p1.X) * (p2.Y - p1.Y);
		if (MathF.Abs(area) < 0.01f) return;

		var v0 = p2 - p1;
		var v1 = p3 - p1;
		var d00 = Vector2.Dot(v0, v0);
		var d01 = Vector2.Dot(v0, v1);
		var d11 = Vector2.Dot(v1, v1);
		var denom = d00 * d11 - d01 * d01;

		if (MathF.Abs(denom) < 1e-6f) return;

		var baryData = new BarycentricData(v0, v1, d00, d01, d11, denom);

		var minX = (int)MathF.Max(0, MathF.Min(MathF.Min(p1.X, p2.X), p3.X));
		var minY = (int)MathF.Max(0, MathF.Min(MathF.Min(p1.Y, p2.Y), p3.Y));
		var maxX = (int)MathF.Min(canvas.Width - 1, MathF.Ceiling(MathF.Max(MathF.Max(p1.X, p2.X), p3.X)));
		var maxY = (int)MathF.Min(canvas.Height - 1, MathF.Ceiling(MathF.Max(MathF.Max(p1.Y, p2.Y), p3.Y)));

		var texWidth = textureRect.Width - 1;
		var texHeight = textureRect.Height - 1;

		var width = canvas.Width;
		const float depthTestEpsilon = 1e-6f;
		const float alphaThreshold = 10f;

		Parallel.For(minY, maxY + 1, y =>
		{
			var row = canvas.DangerousGetPixelRowMemory(y).Span;
			var rowOffset = y * width;
			for (var x = minX; x <= maxX; x++)
			{
				var point = new Vector2(x + 0.5f, y + 0.5f);
				var bary = GetBarycentric(p1, point, in baryData);

				const float epsilon = 1e-5f;
				if (bary.X < -epsilon || bary.Y < -epsilon || bary.Z < -epsilon)
				{
					continue;
				}

				var depth = z1 * bary.X + z2 * bary.Y + z3 * bary.Z - depthBias;

				var texCoord = t1 * bary.X + t2 * bary.Y + t3 * bary.Z;

				var texX = (int)MathF.Max(0, MathF.Min(texCoord.X * textureRect.Width, texWidth));
				var texY = (int)MathF.Max(0, MathF.Min(texCoord.Y * textureRect.Height, texHeight));

				var color = texture[textureRect.X + texX, textureRect.Y + texY];
				if (color.A <= alphaThreshold)
				{
					continue;
				}

				var bufferIndex = rowOffset + x;
				if (depth >= depthBuffer[bufferIndex] - depthTestEpsilon)
				{
					continue;
				}

				depthBuffer[bufferIndex] = depth;
				row[x] = color;
			}
		});
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static Vector3 GetBarycentric(Vector2 p1, Vector2 p, in BarycentricData data)
	{
		var v2 = p - p1;
		var d20 = Vector2.Dot(v2, data.V0);
		var d21 = Vector2.Dot(v2, data.V1);

		var v = (data.D11 * d20 - data.D01 * d21) / data.Denom;
		var w = (data.D00 * d21 - data.D01 * d20) / data.Denom;
		var u = 1.0f - v - w;

		return new Vector3(u, v, w);
	}

	private const float DegreesToRadians = MathF.PI / 180f;

	private static Matrix4x4 CreateRotationMatrix(float yaw, float pitch, float roll)
	{
		yaw = -yaw;
		var cosY = MathF.Cos(yaw);
		var sinY = MathF.Sin(yaw);
		var cosP = MathF.Cos(pitch);
		var sinP = MathF.Sin(pitch);
		var cosR = MathF.Cos(roll);
		var sinR = MathF.Sin(roll);

		return new Matrix4x4(
			cosY * cosR + sinY * sinP * sinR, -cosY * sinR + sinY * sinP * cosR, sinY * cosP, 0,
			cosP * sinR, cosP * cosR, -sinP, 0,
			-sinY * cosR + cosY * sinP * sinR, sinY * sinR + cosY * sinP * cosR, cosY * cosP, 0,
			0, 0, 0, 1);
	}

	private readonly record struct VisibleTriangle(
		Vector3 V1,
		Vector3 V2,
		Vector3 V3,
		Vector2 T1,
		Vector2 T2,
		Vector2 T3,
		Image<Rgba32> Texture,
		Rectangle TextureRect,
		float Depth);

	private readonly record struct Bounds(float MinX, float MaxX, float MinY, float MaxY);

	private readonly record struct BarycentricData(Vector2 V0, Vector2 V1, float D00, float D01, float D11, float Denom);

	private readonly record struct PerspectiveParams(float Amount, float CameraDistance, float FocalLength);
}
