namespace MinecraftRenderer.Tests;

using MinecraftRenderer.Nbt;
using Xunit;

public class NbtExtensionsTests
{
	private static readonly string AssetsDirectory =
		Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "minecraft"));

	[Fact]
	public void WithProfileComponent_CreatesValidProfileStructure()
	{
		// Arrange: Create a basic item compound
		var root = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("minecraft:player_head")),
			new KeyValuePair<string, NbtTag>("count", new NbtInt(1))
		});

		// A sample base64 texture from Minecraft (Steve skin)
		const string testTextureValue = "ewogICJ0aW1lc3RhbXAiIDogMTYzMzQ2NzI4MiwKICAicHJvZmlsZUlkIiA6ICI0MTNkMTdkMzMyODQ0OTYwYTExNWU2ZjYzNmE0ZDcyYyIsCiAgInByb2ZpbGVOYW1lIiA6ICJNaW5lY3JhZnRTa2luIiwKICAidGV4dHVyZXMiIDogewogICAgIlNLSU4iIDogewogICAgICAidXJsIiA6ICJodHRwOi8vdGV4dHVyZXMubWluZWNyYWZ0Lm5ldC90ZXh0dXJlLzE0ZjZhYjdkMWQyOGJkZTY1OTZiZjdkNGU5ZjlmMGI0ZjFlNWY5MTdkNTI1MjQ0ODJlZWM4ODFlYWM4YTZjNTEiCiAgICB9CiAgfQp9";

		// Act: Add profile component
		var withProfile = root.WithProfileComponent(testTextureValue);

		// Assert: Verify structure
		Assert.NotNull(withProfile);
		Assert.True(withProfile.ContainsKey("components"));

		var components = withProfile.GetCompound("components");
		Assert.NotNull(components);
		Assert.True(components.ContainsKey("minecraft:profile"));

		var profile = components.GetCompound("minecraft:profile");
		Assert.NotNull(profile);
		Assert.True(profile.ContainsKey("properties"));

		var properties = profile.GetList("properties");
		Assert.NotNull(properties);
		Assert.Single(properties);

		var property = properties[0] as NbtCompound;
		Assert.NotNull(property);
		Assert.Equal("textures", property.GetString("name"));
		Assert.Equal(testTextureValue, property.GetString("value"));
		Assert.Null(property.GetString("signature")); // No signature provided
	}

	[Fact]
	public void WithProfileComponent_AddsToExistingComponents()
	{
		// Arrange: Create compound with existing components
		var customData = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("CUSTOM_SKULL"))
		});

		var components = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("minecraft:custom_data", customData)
		});

		var root = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("minecraft:player_head")),
			new KeyValuePair<string, NbtTag>("count", new NbtInt(1)),
			new KeyValuePair<string, NbtTag>("components", components)
		});

		const string testTextureValue = "ewogICJ0ZXN0IiA6ICJ2YWx1ZSIgfQ==";

		// Act
		var withProfile = root.WithProfileComponent(testTextureValue);

		// Assert: Both custom_data and profile should exist
		var resultComponents = withProfile.GetCompound("components");
		Assert.NotNull(resultComponents);
		Assert.True(resultComponents.ContainsKey("minecraft:custom_data"));
		Assert.True(resultComponents.ContainsKey("minecraft:profile"));

		// Verify custom_data is preserved
		var resultCustomData = resultComponents.GetCompound("minecraft:custom_data");
		Assert.NotNull(resultCustomData);
		Assert.Equal("CUSTOM_SKULL", resultCustomData.GetString("id"));
	}

	[Fact]
	public void WithProfileComponent_IncludesSignatureWhenProvided()
	{
		// Arrange
		var root = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("minecraft:player_head"))
		});

		const string testTextureValue = "ewogICJ0ZXN0IiA6ICJ2YWx1ZSIgfQ==";
		const string testSignature = "test_signature_value";

		// Act
		var withProfile = root.WithProfileComponent(testTextureValue, testSignature);

		// Assert
		var components = withProfile.GetCompound("components");
		var profile = components?.GetCompound("minecraft:profile");
		var properties = profile?.GetList("properties");
		var property = properties?[0] as NbtCompound;

		Assert.NotNull(property);
		Assert.Equal(testSignature, property.GetString("signature"));
	}

	[Fact]
	public void WithProfileComponent_RendersCorrectly()
	{
		// Arrange: Create a skull with profile
		var renderer = MinecraftBlockRenderer.CreateFromDataDirectory(AssetsDirectory);

		// Use a real base64 texture value (example from Minecraft)
		const string textureValue = "ewogICJ0aW1lc3RhbXAiIDogMTYzMzQ2NzI4MiwKICAicHJvZmlsZUlkIiA6ICI0MTNkMTdkMzMyODQ0OTYwYTExNWU2ZjYzNmE0ZDcyYyIsCiAgInByb2ZpbGVOYW1lIiA6ICJNaW5lY3JhZnRTa2luIiwKICAidGV4dHVyZXMiIDogewogICAgIlNLSU4iIDogewogICAgICAidXJsIiA6ICJodHRwOi8vdGV4dHVyZXMubWluZWNyYWZ0Lm5ldC90ZXh0dXJlLzE0ZjZhYjdkMWQyOGJkZTY1OTZiZjdkNGU5ZjlmMGI0ZjFlNWY5MTdkNTI1MjQ0ODJlZWM4ODFlYWM4YTZjNTEiCiAgICB9CiAgfQp9";

		var root = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("minecraft:player_head")),
			new KeyValuePair<string, NbtTag>("count", new NbtInt(1))
		});

		var withProfile = root.WithProfileComponent(textureValue);

		// Extract ItemRenderData from the compound
		var itemData = MinecraftBlockRenderer.ExtractItemRenderDataFromNbt(withProfile);
		Assert.NotNull(itemData);
		Assert.NotNull(itemData.Profile);

		// Act: Render the item
		var options = MinecraftBlockRenderer.BlockRenderOptions.Default with
		{
			Size = 64,
			ItemData = itemData
		};

		using var image = renderer.RenderGuiItem("minecraft:player_head", options);

		// Assert: Image should be created successfully
		Assert.NotNull(image);
		Assert.Equal(64, image.Width);
		Assert.Equal(64, image.Height);
	}

	[Fact]
	public void AddProfileComponent_ThrowsNotSupportedException()
	{
		// Arrange
		var root = new NbtCompound(new[]
		{
			new KeyValuePair<string, NbtTag>("id", new NbtString("minecraft:player_head"))
		});

		// Act & Assert
		#pragma warning disable CS0618 // Type or member is obsolete
		Assert.Throws<NotSupportedException>(() =>
			root.AddProfileComponent("test_value"));
		#pragma warning restore CS0618
	}
}
