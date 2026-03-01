namespace MinecraftRenderer.Geometry;

using System;
using System.Collections.Generic;
using System.Numerics;

public static class FaceBakery
{
    // Mappings from Minecraft's FaceBakery.java
    public static readonly Dictionary<BlockFaceDirection, int[]> FaceVertexIndices = new()
    {
        { BlockFaceDirection.South, [7, 6, 5, 4] },
        { BlockFaceDirection.Up,    [3, 2, 6, 7] },
        { BlockFaceDirection.North, [0, 1, 2, 3] },
        { BlockFaceDirection.Down,  [4, 5, 1, 0] },
        { BlockFaceDirection.West,  [3, 7, 4, 0] },
        { BlockFaceDirection.East,  [6, 2, 1, 5] },
    };

    public static Vector4 DefaultFaceUv(Vector3 from, Vector3 to, BlockFaceDirection direction)
    {
        return direction switch
        {
            BlockFaceDirection.Down  => new Vector4(from.X, 16f - to.Z, to.X, 16f - from.Z),
            BlockFaceDirection.Up    => new Vector4(from.X, from.Z, to.X, to.Z),
            BlockFaceDirection.North => new Vector4(16f - to.X, 16f - to.Y, 16f - from.X, 16f - from.Y),
            BlockFaceDirection.South => new Vector4(from.X, 16f - to.Y, to.X, 16f - from.Y),
            BlockFaceDirection.West  => new Vector4(from.Z, 16f - to.Y, to.Z, 16f - from.Y),
            BlockFaceDirection.East  => new Vector4(16f - to.Z, 16f - to.Y, 16f - from.Z, 16f - from.Y),
            _ => new Vector4(0, 0, 16, 16)
        };
    }

    private static float GetU(Vector4 uv, int rotationQuadrant, int vertexIndex)
    {
        var shifted = (vertexIndex + rotationQuadrant) % 4;
        return (shifted != 0 && shifted != 1) ? uv.Z : uv.X;
    }

    private static float GetV(Vector4 uv, int rotationQuadrant, int vertexIndex)
    {
        var shifted = (vertexIndex + rotationQuadrant) % 4;
        return (shifted != 0 && shifted != 3) ? uv.W : uv.Y;
    }

    public static Vector2[] CreateUvMap(Vector4 faceUv, int faceRotationDegrees)
    {
        var normalizedAngle = ((faceRotationDegrees % 360) + 360) % 360;
        var quadrant = normalizedAngle switch
        {
            90 => 1,
            180 => 2,
            270 => 3,
            _ => 0
        };

        var map = new Vector2[4];
        for (var i = 0; i < 4; i++)
        {
            // Note: Minecraft computes min/max so that X is minU, Z is maxU, Y is minV, W is maxV.
            var u = GetU(faceUv, quadrant, i) / 16f;
            var v = GetV(faceUv, quadrant, i) / 16f;

            map[i] = new Vector2(u, v);
        }

        return map;
    }
}
