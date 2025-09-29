namespace MinecraftRenderer;

using System;
using System.Collections.Generic;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

public sealed partial class MinecraftBlockRenderer
{
	private static readonly Dictionary<BlockFaceDirection, int[]> FaceVertexIndices = new()
	{
		{ BlockFaceDirection.South, [7, 6, 5, 4] },
		{ BlockFaceDirection.North, [0, 1, 2, 3] },
		{ BlockFaceDirection.East, [6, 2, 1, 5] },
		{ BlockFaceDirection.West, [3, 7, 4, 0] },
		{ BlockFaceDirection.Up, [3, 2, 6, 7] },
		{ BlockFaceDirection.Down, [4, 5, 1, 0] }
	};

	private List<VisibleTriangle> BuildTriangles(BlockModelInstance model, Matrix4x4 transform, string? blockName = null)
	{
		var triangles = new List<VisibleTriangle>(model.Elements.Count * 12);

		for (var elementIndex = 0; elementIndex < model.Elements.Count; elementIndex++)
		{
			var element = model.Elements[elementIndex];
			var elementTriangles = BuildTrianglesForElement(model, element, transform, elementIndex, blockName);
			triangles.AddRange(elementTriangles);
		}

		return triangles;
	}

	private List<VisibleTriangle> BuildTrianglesForElement(BlockModelInstance model, ModelElement element, Matrix4x4 transform, int elementIndex, string? blockName)
	{
		var vertices = BuildElementVertices(element);
		ApplyElementRotation(element, vertices);
		var results = new List<VisibleTriangle>(element.Faces.Count * 2);

		foreach (var (direction, face) in element.Faces)
		{
			var textureId = ResolveTexture(face.Texture, model);
			Image<Rgba32> texture;

			var renderPriority = face.TintIndex.HasValue ? 1 : 0;

			if (face.TintIndex.HasValue)
			{
				var constantTint = TryGetConstantTint(textureId, blockName);
				if (constantTint.HasValue)
				{
					texture = _textureRepository.GetTintedTexture(textureId, constantTint.Value, ConstantTintStrength);
				}
				else if (TryGetBiomeTintKind(textureId, blockName, out var biomeKind))
				{
					texture = GetBiomeTintedTexture(textureId, biomeKind);
				}
				else
				{
					var fallbackTint = GetColorFromBlockName(blockName) ?? GetColorFromBlockName(textureId);
					texture = fallbackTint.HasValue
						? _textureRepository.GetTintedTexture(textureId, fallbackTint.Value, 1f, ColorTintBlend)
						: _textureRepository.GetTexture(textureId);
				}
			}
			else
			{
				texture = _textureRepository.GetTexture(textureId);
			}

			var faceUv = GetFaceUv(face, direction, element);
			var textureRect = ComputeTextureRectangle(faceUv, texture);

			var uvMap = CreateUvMap(element, direction, faceUv, face.Rotation ?? 0);

			var indices = FaceVertexIndices[direction];
			var localFace = new Vector3[4];
			for (var i = 0; i < 4; i++)
			{
				localFace[i] = vertices[indices[i]];
			}

			var localNormal = Vector3.Cross(localFace[1] - localFace[0], localFace[2] - localFace[0]);
			var shouldFlip = false;

			if (localNormal != Vector3.Zero)
			{
				var centroid = (localFace[0] + localFace[1] + localFace[2] + localFace[3]) * 0.25f;
				var outwardDot = Vector3.Dot(localNormal, centroid);

				if (MathF.Abs(outwardDot) > 1e-6f)
				{
					shouldFlip = outwardDot < 0f;
				}
				else
				{
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

					if (element.Rotation is not null)
					{
						var axisVector = element.Rotation.Axis switch
						{
							"x" or "X" => Vector3.UnitX,
							"z" or "Z" => Vector3.UnitZ,
							_ => Vector3.UnitY
						};
						var angle = element.Rotation.AngleInDegrees * DegreesToRadians;
						var normalRotation = Matrix4x4.CreateFromAxisAngle(axisVector, angle);
						expectedNormal = Vector3.TransformNormal(expectedNormal, normalRotation);
					}

					shouldFlip = Vector3.Dot(localNormal, expectedNormal) < 0f;
				}
			}

			var transformed = new Vector3[4];
			for (var i = 0; i < 4; i++)
			{
				transformed[i] = Vector3.Transform(localFace[i], transform);
			}

			if (shouldFlip)
			{
				(transformed[1], transformed[3]) = (transformed[3], transformed[1]);
				(uvMap[1], uvMap[3]) = (uvMap[3], uvMap[1]);
			}

			if ((direction == BlockFaceDirection.Up || direction == BlockFaceDirection.Down) &&
				element.Rotation is not null &&
				string.Equals(element.Rotation.Axis, "x", StringComparison.OrdinalIgnoreCase))
			{
				for (var i = 0; i < uvMap.Length; i++)
				{
					uvMap[i].Y = 1f - uvMap[i].Y;
				}
			}

			var depth = (transformed[0].Z + transformed[1].Z + transformed[2].Z + transformed[3].Z) * 0.25f;
			var triangle1Normal = Vector3.Cross(transformed[1] - transformed[0], transformed[2] - transformed[0]);
			var triangle2Normal = Vector3.Cross(transformed[2] - transformed[0], transformed[3] - transformed[0]);
			var triangle1Centroid = (transformed[0] + transformed[1] + transformed[2]) / 3f;
			var triangle2Centroid = (transformed[0] + transformed[2] + transformed[3]) / 3f;

			results.Add(new VisibleTriangle(
				transformed[0], transformed[1], transformed[2],
				uvMap[0], uvMap[1], uvMap[2],
				texture,
				textureRect,
				depth,
				triangle1Normal,
				triangle1Centroid,
				direction,
				elementIndex,
				renderPriority));

			results.Add(new VisibleTriangle(
				transformed[0], transformed[2], transformed[3],
				uvMap[0], uvMap[2], uvMap[3],
				texture,
				textureRect,
				depth,
				triangle2Normal,
				triangle2Centroid,
				direction,
				elementIndex,
				renderPriority));
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

		// Currently disabled, as it seems to just squash 2d textures in an undesirable way.
		// TODO: Re-evaluate this in the future.
		
		// if (element.Rotation.Rescale)
		// {
		// 	var scale = 1.0f / MathF.Cos(angle);
		// 	var scaleMatrix = axis switch
		// 	{
		// 		var a when a == Vector3.UnitX => Matrix4x4.CreateScale(1, scale, scale),
		// 		var a when a == Vector3.UnitY => Matrix4x4.CreateScale(scale, 1, scale),
		// 		_ => Matrix4x4.CreateScale(scale, scale, 1)
		// 	};
		// 	rotationMatrix *= scaleMatrix;
		// }


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
			BlockFaceDirection.West => SafeRatio(element.To.Z - corner.Z, element.To.Z - element.From.Z),
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
			BlockFaceDirection.South =>
			[
				new Vector3(from.X, to.Y, to.Z),
				new Vector3(to.X, to.Y, to.Z),
				new Vector3(to.X, from.Y, to.Z),
				new Vector3(from.X, from.Y, to.Z)
			],
			BlockFaceDirection.North =>
			[
				new Vector3(to.X, to.Y, from.Z),
				new Vector3(from.X, to.Y, from.Z),
				new Vector3(from.X, from.Y, from.Z),
				new Vector3(to.X, from.Y, from.Z)
			],
			BlockFaceDirection.East =>
			[
				new Vector3(to.X, to.Y, from.Z),
				new Vector3(to.X, to.Y, to.Z),
				new Vector3(to.X, from.Y, to.Z),
				new Vector3(to.X, from.Y, from.Z)
			],
			BlockFaceDirection.West =>
			[
				new Vector3(from.X, to.Y, to.Z),
				new Vector3(from.X, to.Y, from.Z),
				new Vector3(from.X, from.Y, from.Z),
				new Vector3(from.X, from.Y, to.Z)
			],
			BlockFaceDirection.Up =>
			[
				new Vector3(from.X, to.Y, from.Z),
				new Vector3(to.X, to.Y, from.Z),
				new Vector3(to.X, to.Y, to.Z),
				new Vector3(from.X, to.Y, to.Z)
			],
			BlockFaceDirection.Down =>
			[
				new Vector3(from.X, from.Y, to.Z),
				new Vector3(to.X, from.Y, to.Z),
				new Vector3(to.X, from.Y, from.Z),
				new Vector3(from.X, from.Y, from.Z)
			],
			_ => []
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
				relative = new Vector2(relative.Y, -relative.X);
				uv[i] = relative + center;
			}
		}
	}

	private static Vector2[] NormalizeFaceCoordinates(Vector2[] absoluteUv, Vector4 faceUv)
	{
		var uMin = MathF.Min(faceUv.X, faceUv.Z);
		var uMax = MathF.Max(faceUv.X, faceUv.Z);
		var vMin = MathF.Min(faceUv.Y, faceUv.W);
		var vMax = MathF.Max(faceUv.Y, faceUv.W);

		var width = uMax - uMin;
		var height = vMax - vMin;

		var invWidth = MathF.Abs(width) < 1e-5f ? 0f : 1f / width;
		var invHeight = MathF.Abs(height) < 1e-5f ? 0f : 1f / height;

		var normalized = new Vector2[absoluteUv.Length];
		for (var i = 0; i < absoluteUv.Length; i++)
		{
			var u = (absoluteUv[i].X - uMin) * invWidth;
			var v = (absoluteUv[i].Y - vMin) * invHeight;
			normalized[i] = new Vector2(Clamp01(u), Clamp01(v));
		}

		return normalized;
	}

	private static float Clamp01(float value) => value <= 0f ? 0f : value >= 1f ? 1f : value;
}
