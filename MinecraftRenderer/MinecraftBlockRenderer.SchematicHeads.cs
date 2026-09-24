namespace MinecraftRenderer;

using System.Numerics;
using MinecraftRenderer.Nbt;
using MinecraftRenderer.Schematics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

public sealed partial class MinecraftBlockRenderer
{
    private static bool IsSchematicHead(string name) =>
        name is "skeleton_skull" or "skeleton_wall_skull" or "player_head" or "player_wall_head";

    private void AppendSchematicHead(
        SchematicBlock block,
        float centerX,
        float centerZ,
        int minimumY,
        List<ExportTriangle> target,
        Dictionary<string, Image<Rgba32>?> headSkins,
        HashSet<string> warnings)
    {
        var player = block.State.Name is "player_head" or "player_wall_head";
        Image<Rgba32>? skin = null;
        if (player)
        {
            var profile = block.BlockEntity?.GetCompound("profile")
                ?? block.BlockEntity?.GetCompound("components")?.GetCompound("minecraft:profile")
                ?? block.BlockEntity?.GetCompound("SkullOwner");
            if (profile is not null)
            {
                if (TryExtractProfileTextureId(profile, out var textureId))
                {
                    if (!headSkins.TryGetValue(textureId, out skin))
                    {
                        skin = TryGetProfileSkin(profile, out var profileSkin) ? profileSkin : null;
                        headSkins[textureId] = skin;
                    }
                }
                if (skin is null)
                {
                    warnings.Add("A custom player head skin could not be loaded. Using the default skin.");
                }
            }
            if (skin is null && TryGetDefaultPlayerSkin(out var defaultSkin)) skin = defaultSkin;
        }
        else if (_textureRepository.TryGetTexture("minecraft:entity/skeleton/skeleton", out var skeletonSkin))
        {
            skin = skeletonSkin;
        }

        if (skin is null || skin.Width < 32 || skin.Height < 16)
        {
            warnings.Add($"{(player ? "Player heads" : "Skeleton skulls")} were omitted because their entity texture was unavailable.");
            return;
        }

        var wall = block.State.Name is "skeleton_wall_skull" or "player_wall_head";
        var yaw = wall
            ? block.State.Properties.GetValueOrDefault("facing") switch
            {
                "north" => MathF.PI,
                "east" => MathF.PI / 2f,
                "west" => -MathF.PI / 2f,
                _ => 0f
            }
            : block.State.Properties.TryGetValue("rotation", out var rotation)
                && int.TryParse(rotation, out var steps)
                    ? -steps * MathF.PI / 8f
                    : 0f;
        var rotationMatrix = Matrix4x4.CreateRotationY(yaw);
        var outward = Vector3.TransformNormal(Vector3.UnitZ, rotationMatrix);
        var center = new Vector3(
            block.Position.X - centerX,
            block.Position.Y - minimumY - (wall ? 0f : 0.25f),
            block.Position.Z - centerZ);
        if (wall) center += outward * 0.25f;

        AppendHeadLayer(0.25f, 0);
        if (player && skin.Height >= 64) AppendHeadLayer(0.28125f, 32);

        void AppendHeadLayer(float half, int textureOffset)
        {
            var faces = new (Vector3 A, Vector3 B, Vector3 C, Vector3 D, Rectangle Tile)[]
            {
                (new(-half, -half, half), new(half, -half, half), new(half, half, half), new(-half, half, half), new Rectangle(textureOffset + 8, 8, 8, 8)),
                (new(half, -half, -half), new(-half, -half, -half), new(-half, half, -half), new(half, half, -half), new Rectangle(textureOffset + 24, 8, 8, 8)),
                (new(half, -half, half), new(half, -half, -half), new(half, half, -half), new(half, half, half), new Rectangle(textureOffset, 8, 8, 8)),
                (new(-half, -half, -half), new(-half, -half, half), new(-half, half, half), new(-half, half, -half), new Rectangle(textureOffset + 16, 8, 8, 8)),
                (new(-half, half, half), new(half, half, half), new(half, half, -half), new(-half, half, -half), new Rectangle(textureOffset + 8, 0, 8, 8)),
                (new(-half, -half, -half), new(half, -half, -half), new(half, -half, half), new(-half, -half, half), new Rectangle(textureOffset + 16, 0, 8, 8))
            };
            foreach (var face in faces)
            {
                var a = Vector3.Transform(face.A, rotationMatrix) + center;
                var b = Vector3.Transform(face.B, rotationMatrix) + center;
                var c = Vector3.Transform(face.C, rotationMatrix) + center;
                var d = Vector3.Transform(face.D, rotationMatrix) + center;
                var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                target.Add(new ExportTriangle(a, b, c, normal, new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), skin, face.Tile, 1));
                target.Add(new ExportTriangle(a, c, d, normal, new Vector2(0, 1), new Vector2(1, 0), new Vector2(0, 0), skin, face.Tile, 1));
            }
        }
    }
}
