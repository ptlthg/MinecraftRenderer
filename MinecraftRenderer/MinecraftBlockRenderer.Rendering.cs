namespace MinecraftRenderer;

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

public sealed partial class MinecraftBlockRenderer
{
	private static readonly TransformDefinition DefaultGuiTransform = new()
	{
		Rotation = new[] { 30f, 45f, 0f },
		Translation = new[] { 0f, 0f, 0f },
		Scale = new[] { 0.625f, 0.625f, 0.625f }
	};

	private const float DegreesToRadians = MathF.PI / 180f;

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
		var cullTargets = DetermineCullTargets(model);
		if (cullTargets.Count > 0)
		{
			CullBackfaces(triangles, cullTargets);
		}

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

	private static void CullBackfaces(List<VisibleTriangle> triangles, HashSet<CullTarget> cullTargets)
	{
		const float NormalLengthThreshold = 1e-6f;
		const float DotCullThreshold = 5e-3f;
		var cameraForward = new Vector3(0f, 0f, 1f);
		for (var i = triangles.Count - 1; i >= 0; i--)
		{
			var triangle = triangles[i];
			if (!cullTargets.Contains(new CullTarget(triangle.ElementIndex, triangle.FaceDirection)))
			{
				continue;
			}

			var normal = triangle.Normal;
			if (normal.LengthSquared() < NormalLengthThreshold)
			{
				continue;
			}

			var dot = Vector3.Dot(normal, cameraForward);
			if (dot > DotCullThreshold)
			{
				triangles.RemoveAt(i);
			}
		}
	}

	private static HashSet<CullTarget> DetermineCullTargets(BlockModelInstance model)
	{
		const float ThicknessThreshold = 1e-3f;
		var targets = new HashSet<CullTarget>();
		for (var elementIndex = 0; elementIndex < model.Elements.Count; elementIndex++)
		{
			var element = model.Elements[elementIndex];
			var thicknessX = MathF.Abs(element.To.X - element.From.X);
			var thicknessY = MathF.Abs(element.To.Y - element.From.Y);
			var thicknessZ = MathF.Abs(element.To.Z - element.From.Z);

			TryAddCullPair(model, targets, elementIndex, element, BlockFaceDirection.North, BlockFaceDirection.South, thicknessZ, ThicknessThreshold);
			TryAddCullPair(model, targets, elementIndex, element, BlockFaceDirection.East, BlockFaceDirection.West, thicknessX, ThicknessThreshold);
			TryAddCullPair(model, targets, elementIndex, element, BlockFaceDirection.Up, BlockFaceDirection.Down, thicknessY, ThicknessThreshold);
		}

		return targets;
	}

	private static void TryAddCullPair(
		BlockModelInstance model,
		HashSet<CullTarget> targets,
		int elementIndex,
		ModelElement element,
		BlockFaceDirection primary,
		BlockFaceDirection opposite,
		float thickness,
		float threshold)
	{
		if (thickness > threshold)
		{
			return;
		}

		if (!element.Faces.TryGetValue(primary, out var primaryFace)
			|| !element.Faces.TryGetValue(opposite, out var oppositeFace))
		{
			return;
		}

		if (!FacesShareDuplicateTexture(model, element, primary, primaryFace, opposite, oppositeFace))
		{
			return;
		}

		targets.Add(new CullTarget(elementIndex, primary));
		targets.Add(new CullTarget(elementIndex, opposite));
	}

	private static bool FacesShareDuplicateTexture(
		BlockModelInstance model,
		ModelElement element,
		BlockFaceDirection primaryDirection,
		ModelFace primaryFace,
		BlockFaceDirection oppositeDirection,
		ModelFace oppositeFace)
	{
		var primaryTexture = ResolveTexture(primaryFace.Texture, model);
		var oppositeTexture = ResolveTexture(oppositeFace.Texture, model);
		if (!primaryTexture.Equals(oppositeTexture, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		var primaryRotation = primaryFace.Rotation ?? 0;
		var oppositeRotation = oppositeFace.Rotation ?? 0;
		if (primaryRotation != oppositeRotation)
		{
			return false;
		}

		var primaryUv = GetFaceUv(primaryFace, primaryDirection, element);
		var oppositeUv = GetFaceUv(oppositeFace, oppositeDirection, element);
		return Vector4ApproximatelyEquals(primaryUv, oppositeUv);
	}

	private static bool Vector4ApproximatelyEquals(Vector4 left, Vector4 right, float epsilon = 1e-4f)
	{
		return MathF.Abs(left.X - right.X) <= epsilon
			&& MathF.Abs(left.Y - right.Y) <= epsilon
			&& MathF.Abs(left.Z - right.Z) <= epsilon
			&& MathF.Abs(left.W - right.W) <= epsilon;
	}

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

	private readonly record struct VisibleTriangle(
		Vector3 V1,
		Vector3 V2,
		Vector3 V3,
		Vector2 T1,
		Vector2 T2,
		Vector2 T3,
		Image<Rgba32> Texture,
		Rectangle TextureRect,
		float Depth,
		Vector3 Normal,
		Vector3 Centroid,
		BlockFaceDirection FaceDirection,
		int ElementIndex);

	private readonly record struct Bounds(float MinX, float MaxX, float MinY, float MaxY);

	private readonly record struct BarycentricData(Vector2 V0, Vector2 V1, float D00, float D01, float D11, float Denom);

	private readonly record struct PerspectiveParams(float Amount, float CameraDistance, float FocalLength);

	private readonly record struct CullTarget(int ElementIndex, BlockFaceDirection FaceDirection);
}
