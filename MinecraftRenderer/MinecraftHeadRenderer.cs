namespace MinecraftRenderer;

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

public class MinecraftHeadRenderer
{
	public record RenderOptions(
		int Size,
		float YawInDegrees,
		float PitchInDegrees,
		float RollInDegrees,
		float PerspectiveAmount = 0f,
		bool ShowOverlay = true);

	public enum IsometricSide
	{
		Left,
		Right
	}

	public record IsometricRenderOptions(int Size, IsometricSide Side = IsometricSide.Right, bool ShowOverlay = true);

	private static readonly Dictionary<Face, Rectangle> BaseMappings = new()
	{
		{ Face.Right, new Rectangle(0, 8, 8, 8) },
		{ Face.Front, new Rectangle(8, 8, 8, 8) },
		{ Face.Left, new Rectangle(16, 8, 8, 8) },
		{ Face.Back, new Rectangle(24, 8, 8, 8) },
		{ Face.Top, new Rectangle(8, 0, 8, 8) },
		{ Face.Bottom, new Rectangle(16, 0, 8, 8) }
	};

	private static readonly Dictionary<Face, Rectangle> OverlayMappings = new()
	{
		{ Face.Right, new Rectangle(32, 8, 8, 8) },
		{ Face.Front, new Rectangle(40, 8, 8, 8) },
		{ Face.Left, new Rectangle(48, 8, 8, 8) },
		{ Face.Back, new Rectangle(56, 8, 8, 8) },
		{ Face.Top, new Rectangle(40, 0, 8, 8) },
		{ Face.Bottom, new Rectangle(48, 0, 8, 8) }
	};

	// Define cube vertices (unit cube centered at origin)
	private static readonly Vector3[] Vertices =
	[
		// Back face vertices (z = -0.5)
		new(-0.5f, -0.5f, -0.5f), // 0: bottom-left-back
		new(0.5f, -0.5f, -0.5f), // 1: bottom-right-back
		new(0.5f, 0.5f, -0.5f), // 2: top-right-back
		new(-0.5f, 0.5f, -0.5f), // 3: top-left-back

		// Front face vertices (z = 0.5)
		new(-0.5f, -0.5f, 0.5f), // 4: bottom-left-front
		new(0.5f, -0.5f, 0.5f), // 5: bottom-right-front
		new(0.5f, 0.5f, 0.5f), // 6: top-right-front
		new(-0.5f, 0.5f, 0.5f) // 7: top-left-front
	];

	private static readonly Vector2[] StandardUvMap =
	[
		new(1, 0), new(0, 0), new(0, 1), new(1, 1)
	];

	private static readonly Vector2[] BackFaceUvMap = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
	private static readonly Vector2[] BottomFaceUvMap = [new(1, 1), new(0, 1), new(0, 0), new(1, 0)];

	// Define faces with correct winding order and UV mappings
	private static readonly FaceData[] FaceDefinitions =
	[
		// Front face (+Z)
		new FaceData(Face.Front, [Vertices[7], Vertices[6], Vertices[5], Vertices[4]], StandardUvMap),
		// Back face (-Z)
		new FaceData(Face.Back, [Vertices[0], Vertices[1], Vertices[2], Vertices[3]], BackFaceUvMap),
		// Right face (+X)
		new FaceData(Face.Right, [Vertices[6], Vertices[2], Vertices[1], Vertices[5]], StandardUvMap),
		// Left face (-X)
		new FaceData(Face.Left, [Vertices[3], Vertices[7], Vertices[4], Vertices[0]], StandardUvMap),
		// Top face (+Y)
		new FaceData(Face.Top, [Vertices[3], Vertices[2], Vertices[6], Vertices[7]], StandardUvMap),
		// Bottom face (-Y)
		new FaceData(Face.Bottom, [Vertices[4], Vertices[5], Vertices[1], Vertices[0]], BottomFaceUvMap)
	];

	public static Image<Rgba32> RenderIsometricHead(IsometricRenderOptions options, Image<Rgba32> skin)
	{
		// Isometric view: showing front, right, and top faces (or left if specified)
		const float isometricRightYaw = -135f;
		const float isometricLeftYaw = 45f;
		const float isometricPitch = 30f;
		const float isometricRoll = 0f;

		var fullOptions = new RenderOptions(
			options.Size,
			options.Side == IsometricSide.Right ? isometricRightYaw : isometricLeftYaw,
			isometricPitch,
			isometricRoll,
			ShowOverlay: options.ShowOverlay
		);

		return RenderHead(fullOptions, skin);
	}

	public static Image<Rgba32> RenderHead(RenderOptions options, Image<Rgba32> skin)
	{
		// Create rotation matrices
		const float deg2Rad = MathF.PI / 180f;
		var transform = CreateRotationMatrix(
			options.YawInDegrees * deg2Rad,
			options.PitchInDegrees * deg2Rad,
			options.RollInDegrees * deg2Rad
		);

		var initialCapacity = options.ShowOverlay ? FaceDefinitions.Length * 4 : FaceDefinitions.Length * 2;
		var visibleTriangles = new List<VisibleTriangle>(initialCapacity);

		// Process base layer
		ProcessFaces(FaceDefinitions, transform, false, visibleTriangles);

		// Process overlay layer if enabled
		if (options.ShowOverlay)
		{
			var overlayTransform = Matrix4x4.CreateScale(1.125f) * transform;
			ProcessFaces(FaceDefinitions, overlayTransform, true, visibleTriangles);
		}

		// Sort triangles by depth (back to front)
		visibleTriangles.Sort((a, b) => b.Depth.CompareTo(a.Depth));

		// Create output image
		var canvas = new Image<Rgba32>(options.Size, options.Size, Color.Transparent);
		var scale = options.Size / 1.75f;
		var offset = new Vector2(options.Size / 2f);
		var depthBuffer = new float[options.Size * options.Size];
		Array.Fill(depthBuffer, float.PositiveInfinity);
		var triangleOrder = 0;
		const float DepthBiasPerTriangle = 1e-4f;

		// Pre-calculate perspective parameters if needed
		PerspectiveParams? perspectiveParams = options.PerspectiveAmount > 0.01f
			? new PerspectiveParams(options.PerspectiveAmount, 10.0f, 10.0f)
			: null;

		// Render triangles
		foreach (var tri in visibleTriangles)
		{
			var p1 = ProjectToScreen(tri.V1, scale, offset, perspectiveParams);
			var p2 = ProjectToScreen(tri.V2, scale, offset, perspectiveParams);
			var p3 = ProjectToScreen(tri.V3, scale, offset, perspectiveParams);

			var depthBias = triangleOrder * DepthBiasPerTriangle;
			triangleOrder++;

			RasterizeTriangle(
				canvas,
				depthBuffer,
				depthBias,
				tri.V1.Z,
				tri.V2.Z,
				tri.V3.Z,
				p1,
				p2,
				p3,
				tri.T1,
				tri.T2,
				tri.T3,
				skin,
				tri.TextureRect);
		}

		return canvas;
	}

	private static Matrix4x4 CreateRotationMatrix(float yaw, float pitch, float roll)
	{
		// Apply rotations in Y-X-Z order
		var cosY = MathF.Cos(yaw);
		var sinY = MathF.Sin(yaw);
		var cosP = MathF.Cos(pitch);
		var sinP = MathF.Sin(pitch);
		var cosR = MathF.Cos(roll);
		var sinR = MathF.Sin(roll);

		// Combined rotation matrix (Y * X * Z)
		return new Matrix4x4(
			cosY * cosR + sinY * sinP * sinR, -cosY * sinR + sinY * sinP * cosR, sinY * cosP, 0,
			cosP * sinR, cosP * cosR, -sinP, 0,
			-sinY * cosR + cosY * sinP * sinR, sinY * sinR + cosY * sinP * cosR, cosY * cosP, 0,
			0, 0, 0, 1
		);
	}

	private static Vector2 ProjectToScreen(Vector3 point, float scale, Vector2 offset,
		PerspectiveParams? perspectiveParams)
	{
		if (perspectiveParams == null)
		{
			return new Vector2(point.X * scale + offset.X, -point.Y * scale + offset.Y);
		}

		// Calculate the full perspective projection
		var perspectiveFactor =
			perspectiveParams.Value.FocalLength / (perspectiveParams.Value.CameraDistance - point.Z);
		var perspX = point.X * perspectiveFactor;
		var perspY = point.Y * perspectiveFactor;

		// Orthographic projection (no perspective)
		var orthoX = point.X;
		var orthoY = point.Y;

		var finalX = orthoX + (perspX - orthoX) * perspectiveParams.Value.Amount;
		var finalY = orthoY + (perspY - orthoY) * perspectiveParams.Value.Amount;

		return new Vector2(
			finalX * scale + offset.X,
			-finalY * scale + offset.Y
		);
	}

	private static void ProcessFaces(FaceData[] faces, Matrix4x4 transform,
		bool isOverlay, List<VisibleTriangle> triangles)
	{
		var mappings = isOverlay ? OverlayMappings : BaseMappings;
		Span<Vector3> transformed = stackalloc Vector3[4];

		foreach (var face in faces)
		{
			// Extract texture for this face
			var texRect = mappings[face.FaceType];

			for (var i = 0; i < 4; i++)
			{
				transformed[i] = Vector3.Transform(face.Vertices[i], transform);
			}

			// Backface culling for non-overlay faces
			if (!isOverlay)
			{
				// Calculate face normal for backface culling
				var v1 = transformed[1] - transformed[0];
				var v2 = transformed[2] - transformed[0];
				var normal = Vector3.Cross(v1, v2);

				// Skip back-facing triangles
				if (normal.Z < 0) continue;
			}

			// Calculate average depth for sorting
			var depth = (transformed[0].Z + transformed[1].Z + transformed[2].Z + transformed[3].Z) * 0.25f;

			// Create two triangles for the quad
			triangles.Add(new VisibleTriangle(
				transformed[0], transformed[1], transformed[2],
				face.UvMap[0], face.UvMap[1], face.UvMap[2],
				texRect, depth
			));

			triangles.Add(new VisibleTriangle(
				transformed[0], transformed[2], transformed[3],
				face.UvMap[0], face.UvMap[2], face.UvMap[3],
				texRect, depth
			));
		}
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
		Image<Rgba32> skin, Rectangle textureRect)
	{
		var area = (p2.X - p1.X) * (p3.Y - p1.Y) - (p3.X - p1.X) * (p2.Y - p1.Y);
		if (MathF.Abs(area) < 0.01f) return; // Degenerate triangle

		// Pre-calculate values that are constant for every pixel in the triangle.
		var v0 = p2 - p1;
		var v1 = p3 - p1;
		var d00 = Vector2.Dot(v0, v0);
		var d01 = Vector2.Dot(v0, v1);
		var d11 = Vector2.Dot(v1, v1);
		var denom = d00 * d11 - d01 * d01;

		// If the denominator is zero, the triangle is degenerate (a line or point).
		if (MathF.Abs(denom) < 1e-6f) return;

		var baryData = new BarycentricData(v0, v1, d00, d01, d11, denom);

		// Calculate bounding box
		var minX = (int)MathF.Max(0, MathF.Min(MathF.Min(p1.X, p2.X), p3.X));
		var minY = (int)MathF.Max(0, MathF.Min(MathF.Min(p1.Y, p2.Y), p3.Y));
		var maxX = (int)MathF.Min(canvas.Width - 1, MathF.Ceiling(MathF.Max(MathF.Max(p1.X, p2.X), p3.X)));
		var maxY = (int)MathF.Min(canvas.Height - 1, MathF.Ceiling(MathF.Max(MathF.Max(p1.Y, p2.Y), p3.Y)));

		// Pre-calculate texture dimensions for clamping
		var texWidth = textureRect.Width - 1;
		var texHeight = textureRect.Height - 1;

		// Rasterize triangle
		var width = canvas.Width;
		const float depthTestEpsilon = 1e-6f;
		const float alphaThreshold = 10f;

		Parallel.For((long)minY, maxY + 1, y =>
		{
			// Get a span for the current row for direct memory access.
			// Dangerous, but should be fine as canvas's lifetime is not at risk here.
			var canvasRow = canvas.DangerousGetPixelRowMemory((int)y).Span;
			var rowOffset = (int)y * width;

			for (var x = minX; x <= maxX; x++)
			{
				var point = new Vector2(x + 0.5f, y + 0.5f);
				var bary = GetBarycentric(p1, point, in baryData);

				const float epsilon = 1e-5f;
				if (bary.X < -epsilon || bary.Y < -epsilon || bary.Z < -epsilon) continue;

				var depth = z1 * bary.X + z2 * bary.Y + z3 * bary.Z - depthBias;

				var texCoord = t1 * bary.X + t2 * bary.Y + t3 * bary.Z;

				var texX = (int)MathF.Max(0, MathF.Min(texCoord.X * textureRect.Width, texWidth));
				var texY = (int)MathF.Max(0, MathF.Min(texCoord.Y * textureRect.Height, texHeight));

				var color = skin[textureRect.X + texX, textureRect.Y + texY];

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
				canvasRow[x] = color;
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

	private enum Face
	{
		Top,
		Bottom,
		Left,
		Right,
		Front,
		Back
	}

	private record FaceData(Face FaceType, Vector3[] Vertices, Vector2[] UvMap);

	private record VisibleTriangle(
		Vector3 V1,
		Vector3 V2,
		Vector3 V3,
		Vector2 T1,
		Vector2 T2,
		Vector2 T3,
		Rectangle TextureRect,
		float Depth);

	private readonly record struct BarycentricData(
		Vector2 V0,
		Vector2 V1,
		float D00,
		float D01,
		float D11,
		float Denom);

	private readonly record struct PerspectiveParams(float Amount, float CameraDistance, float FocalLength);
}