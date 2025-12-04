using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace MinecraftRenderer.Tests;

public sealed class ReproductionTests : IDisposable
{
    private readonly string _tempRoot;

    public ReproductionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "MinecraftRenderer_ReproTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void HigherPriorityPackWithDifferentFolderShouldWin()
    {
        var packARoot = CreateTestPack("packA", new Rgba32(255, 0, 0, 255)); // Red
        var packBRoot = CreateTestPack("packB", new Rgba32(0, 0, 255, 255)); // Blue

        // Create specific folder structures
        var packABlockDir = Path.Combine(packARoot, "assets", "minecraft", "textures", "block");
        Directory.CreateDirectory(packABlockDir);
        using (var img = new Image<Rgba32>(16, 16, new Rgba32(255, 0, 0, 255)))
        {
            img.Save(Path.Combine(packABlockDir, "stone.png"));
        }

        var packBBlocksDir = Path.Combine(packBRoot, "assets", "minecraft", "textures", "blocks");
        Directory.CreateDirectory(packBBlocksDir);
        using (var img = new Image<Rgba32>(16, 16, new Rgba32(0, 0, 255, 255)))
        {
            img.Save(Path.Combine(packBBlocksDir, "stone.png"));
        }

        var repository = new TextureRepository(
            Path.Combine(packARoot, "assets", "minecraft", "textures"), 
            overlayRoots: new[] { Path.Combine(packBRoot, "assets", "minecraft", "textures") });
        
        var texture = repository.GetTexture("minecraft:block/stone");
        
        // Check color.
        // If Red -> Pack A (Wrong)
        // If Blue -> Pack B (Correct)
        
        var pixel = texture[0, 0];
        Assert.Equal(new Rgba32(0, 0, 255, 255), pixel); // Expect Blue
    }

    private string CreateTestPack(string id, Rgba32 color)
    {
        var packRoot = Path.Combine(_tempRoot, id);
        Directory.CreateDirectory(packRoot);
        return packRoot;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore
        }
    }
}
