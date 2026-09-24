namespace MinecraftRenderer;

using System.Numerics;
using MinecraftRenderer.Schematics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

public sealed partial class MinecraftBlockRenderer
{
    private static bool IsSchematicSign(string name) =>
        name is "wall_sign" or "standing_sign" || name.EndsWith("_sign", StringComparison.Ordinal);

    private static bool IsWallSchematicSign(string name) =>
        name == "wall_sign"
        || name.EndsWith("_wall_sign", StringComparison.Ordinal)
        || name.EndsWith("_wall_hanging_sign", StringComparison.Ordinal);

    private static bool IsHangingSchematicSign(string name) =>
        name.EndsWith("_hanging_sign", StringComparison.Ordinal);

    private static Vector3 GetSchematicSignBoardCenter(SchematicBlockState state, Vector3 origin, Vector3 normal)
    {
        var hanging = IsHangingSchematicSign(state.Name);
        var wall = IsWallSchematicSign(state.Name);
        var center = origin + Vector3.UnitY * (hanging ? -0.075f : wall ? 0f : 0.2f);
        if (wall) center -= normal * (hanging ? 0.25f : 0.4375f);
        return center;
    }

    private void AppendSchematicSign(
        SchematicBlock block,
        float centerX,
        float centerZ,
        int minimumY,
        List<ExportTriangle> target,
        HashSet<string> warnings)
    {
        var name = block.State.Name;
        var wall = IsWallSchematicSign(name);
        var hanging = IsHangingSchematicSign(name);
        var suffix = hanging ? wall ? "_wall_hanging_sign" : "_hanging_sign" : wall ? "_wall_sign" : "_sign";
        var wood = name is "wall_sign" or "standing_sign" ? "oak" : name[..^suffix.Length];
        if (!_textureRepository.TryGetTexture($"minecraft:block/{wood}_planks", out var texture))
        {
            warnings.Add($"{name} was omitted because its wood texture was unavailable.");
            return;
        }

        var normal = GetSignNormal(block.State);
        var right = Vector3.Normalize(new Vector3(normal.Z, 0, -normal.X));
        var origin = new Vector3(
            block.Position.X - centerX,
            block.Position.Y - minimumY,
            block.Position.Z - centerZ);
        var tile = new Rectangle(0, 0, texture.Width, texture.Height);
        var boardCenter = GetSchematicSignBoardCenter(block.State, origin, normal);
        AppendSignBox(boardCenter, new Vector3(0.4375f, 0.25f, 0.0625f));
        if (!wall && !hanging)
        {
            AppendSignBox(origin - Vector3.UnitY * 0.25f, new Vector3(0.0625f, 0.25f, 0.0625f));
        }
        else if (hanging)
        {
            var supportCenter = boardCenter + Vector3.UnitY * 0.4125f;
            var supportHalf = new Vector3(0.03125f, 0.1625f, 0.03125f);
            if (block.State.Properties.GetValueOrDefault("attached") == "true")
            {
                AppendSignBox(supportCenter, supportHalf);
            }
            else
            {
                AppendSignBox(supportCenter - right * 0.3125f, supportHalf);
                AppendSignBox(supportCenter + right * 0.3125f, supportHalf);
            }
        }

        void AppendSignBox(Vector3 center, Vector3 half)
        {
            var faces = new (Vector3 A, Vector3 B, Vector3 C, Vector3 D)[]
            {
                (new(-half.X, -half.Y, half.Z), new(half.X, -half.Y, half.Z), new(half.X, half.Y, half.Z), new(-half.X, half.Y, half.Z)),
                (new(half.X, -half.Y, -half.Z), new(-half.X, -half.Y, -half.Z), new(-half.X, half.Y, -half.Z), new(half.X, half.Y, -half.Z)),
                (new(half.X, -half.Y, half.Z), new(half.X, -half.Y, -half.Z), new(half.X, half.Y, -half.Z), new(half.X, half.Y, half.Z)),
                (new(-half.X, -half.Y, -half.Z), new(-half.X, -half.Y, half.Z), new(-half.X, half.Y, half.Z), new(-half.X, half.Y, -half.Z)),
                (new(-half.X, half.Y, half.Z), new(half.X, half.Y, half.Z), new(half.X, half.Y, -half.Z), new(-half.X, half.Y, -half.Z)),
                (new(-half.X, -half.Y, -half.Z), new(half.X, -half.Y, -half.Z), new(half.X, -half.Y, half.Z), new(-half.X, -half.Y, half.Z))
            };
            foreach (var face in faces)
            {
                var a = Transform(face.A);
                var b = Transform(face.B);
                var c = Transform(face.C);
                var d = Transform(face.D);
                var faceNormal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                target.Add(new ExportTriangle(a, b, c, faceNormal, new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), texture, tile, 1));
                target.Add(new ExportTriangle(a, c, d, faceNormal, new Vector2(0, 1), new Vector2(1, 0), new Vector2(0, 0), texture, tile, 1));
            }

            Vector3 Transform(Vector3 vertex) => center + right * vertex.X + Vector3.UnitY * vertex.Y + normal * vertex.Z;
        }
    }
}
