namespace MinecraftRenderer;

using System;
using System.Collections.Generic;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using MinecraftRenderer.Geometry;

public sealed partial class MinecraftBlockRenderer
{
	private List<VisibleTriangle> BuildTriangles(BlockModelInstance model, Matrix4x4 transform,
		bool applyInventoryLighting,
		string? blockName = null) {
		var triangles = new List<VisibleTriangle>(model.Elements.Count * 12);

		for (var elementIndex = 0; elementIndex < model.Elements.Count; elementIndex++) {
			var element = model.Elements[elementIndex];
			var elementTriangles = BuildTrianglesForElement(model, element, transform, elementIndex,
				applyInventoryLighting, blockName);
			triangles.AddRange(elementTriangles);
		}

		return triangles;
	}

	private List<VisibleTriangle> BuildTrianglesForElement(BlockModelInstance model, ModelElement element,
		Matrix4x4 transform, int elementIndex, bool applyInventoryLighting, string? blockName) {
		var vertices = BuildElementVertices(element);
		ApplyElementRotation(element, vertices);
		var results = new List<VisibleTriangle>(element.Faces.Count * 2);

		foreach (var (direction, face) in element.Faces) {
			var textureId = ResolveTexture(face.Texture, model);
			Image<Rgba32> texture;

			var renderPriority = face.TintIndex.HasValue ? 1 : 0;

			if (face.TintIndex.HasValue) {
				var constantTint = TryGetConstantTint(textureId, blockName);
				if (constantTint.HasValue) {
					texture = _textureRepository.GetTintedTexture(textureId, constantTint.Value, ConstantTintStrength);
				}
				else if (TryGetBiomeTintKind(textureId, blockName, out var biomeKind)) {
					texture = GetBiomeTintedTexture(textureId, biomeKind);
				}
				else {
					var fallbackTint = GetColorFromBlockName(blockName) ?? GetColorFromBlockName(textureId);
					texture = fallbackTint.HasValue
						? _textureRepository.GetTintedTexture(textureId, fallbackTint.Value, 1f, ColorTintBlend)
						: _textureRepository.GetTexture(textureId);
				}
			}
			else {
				texture = _textureRepository.GetTexture(textureId);
			}

			var faceUv = GetFaceUv(face, direction, element);

			var uvMap = ModelFaceHelper.CreateUvMap(faceUv, face.Rotation ?? 0);
			var textureRect = ComputeTextureRectangle(uvMap, texture);

			// The rasterizer expects UV coordinates in [0,1] relative to textureRect,
			// but uvMap stores absolute [0,1] coordinates across the entire texture.
			// Normalize the UVs so they map into the sub-region defined by textureRect.
			var rectMinU = (float)textureRect.X / texture.Width;
			var rectRangeU = (float)textureRect.Width / texture.Width;
			var rectMinV = (float)textureRect.Y / texture.Height;
			var rectRangeV = (float)textureRect.Height / texture.Height;
			for (var i = 0; i < 4; i++) {
				uvMap[i] = new Vector2(
					rectRangeU > 1e-6f ? (uvMap[i].X - rectMinU) / rectRangeU : 0f,
					rectRangeV > 1e-6f ? (uvMap[i].Y - rectMinV) / rectRangeV : 0f
				);
			}

			var indices = ModelFaceHelper.FaceVertexIndices[direction];
			var localFace = new Vector3[4];
			for (var i = 0; i < 4; i++) {
				localFace[i] = vertices[indices[i]];
			}

			var transformed = new Vector3[4];
			for (var i = 0; i < 4; i++) {
				transformed[i] = Vector3.Transform(localFace[i], transform);
			}


			var depth = (transformed[0].Z + transformed[1].Z + transformed[2].Z + transformed[3].Z) * 0.25f;
			var triangle1Normal = Vector3.Cross(transformed[1] - transformed[0], transformed[2] - transformed[0]);
			var triangle2Normal = Vector3.Cross(transformed[2] - transformed[0], transformed[3] - transformed[0]);
			var triangle1Centroid = (transformed[0] + transformed[1] + transformed[2]) / 3f;
			var triangle2Centroid = (transformed[0] + transformed[2] + transformed[3]) / 3f;
			var shadingEnabled = applyInventoryLighting && element.Shade;
			var triangle1Shading = shadingEnabled ? ComputeInventoryLightingIntensity(triangle1Normal) : 1f;
			var triangle2Shading = shadingEnabled ? ComputeInventoryLightingIntensity(triangle2Normal) : 1f;

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
				renderPriority,
				triangle1Shading));

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
				renderPriority,
				triangle2Shading));
		}

		return results;
	}

	private static Vector3[] BuildElementVertices(ModelElement element) {
		var min = element.From;
		var max = element.To;

		var fx = NormalizeComponent(min.X);
		var fy = NormalizeComponent(min.Y);
		var fz = NormalizeComponent(min.Z);
		var tx = NormalizeComponent(max.X);
		var ty = NormalizeComponent(max.Y);
		var tz = NormalizeComponent(max.Z);

		return [
			new Vector3(fx, fy, fz),
			new Vector3(tx, fy, fz),
			new Vector3(tx, ty, fz),
			new Vector3(fx, ty, fz),
			new Vector3(fx, fy, tz),
			new Vector3(tx, fy, tz),
			new Vector3(tx, ty, tz),
			new Vector3(fx, ty, tz)
		];
	}

	private static float NormalizeComponent(float value) => value / 16f - 0.5f;

	private static void ApplyElementRotation(ModelElement element, Vector3[] vertices) {
		if (element.Rotation is null) {
			return;
		}

		var axis = element.Rotation.Axis switch {
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


		for (var i = 0; i < vertices.Length; i++) {
			var relative = vertices[i] - pivot;
			relative = Vector3.Transform(relative, rotationMatrix);
			vertices[i] = relative + pivot;
		}
	}

	private static Vector4 GetFaceUv(ModelFace face, BlockFaceDirection direction, ModelElement element) {
		if (face.Uv.HasValue) {
			// Pass through raw JSON UV values. Minecraft does NOT sort to min/max —
			// the order matters for UV mirroring (when u1 > u2), same for v.
			return face.Uv.Value;
		}

		return ModelFaceHelper.DefaultFaceUv(element.From, element.To, direction);
	}

	private static Rectangle ComputeTextureRectangle(Vector2[] uvMap, Image<Rgba32> texture) {
		var widthFactor = texture.Width;
		var heightFactor = texture.Height;

		// uvMap coordinates are [0.0, 1.0], meaning we multiply directly with the texture width/height.
		var minU = MathF.Min(MathF.Min(uvMap[0].X, uvMap[1].X), MathF.Min(uvMap[2].X, uvMap[3].X));
		var maxU = MathF.Max(MathF.Max(uvMap[0].X, uvMap[1].X), MathF.Max(uvMap[2].X, uvMap[3].X));
		var minV = MathF.Min(MathF.Min(uvMap[0].Y, uvMap[1].Y), MathF.Min(uvMap[2].Y, uvMap[3].Y));
		var maxV = MathF.Max(MathF.Max(uvMap[0].Y, uvMap[1].Y), MathF.Max(uvMap[2].Y, uvMap[3].Y));

		var minX = (int)MathF.Round(minU * widthFactor);
		var maxX = (int)MathF.Round(maxU * widthFactor);
		var minY = (int)MathF.Round(minV * heightFactor);
		var maxY = (int)MathF.Round(maxV * heightFactor);

		minX = Math.Clamp(minX, 0, texture.Width - 1);
		minY = Math.Clamp(minY, 0, texture.Height - 1);
		maxX = Math.Clamp(Math.Max(maxX, minX + 1), minX + 1, texture.Width);
		maxY = Math.Clamp(Math.Max(maxY, minY + 1), minY + 1, texture.Height);

		return new Rectangle(minX, minY, maxX - minX, maxY - minY);
	}
}