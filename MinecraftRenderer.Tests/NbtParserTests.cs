using MinecraftRenderer.Nbt;
using Xunit;

namespace MinecraftRenderer.Tests;

public sealed class NbtParserTests
{
	[Fact]
	public void ParseBinaryCompoundWithPrimitiveValues()
	{
		// TAG_Compound("test") { TAG_Int("nbt") = 42 }
		var payload = new byte[]
		{
			0x0A, // compound
			0x00, 0x04, // name length
			(byte)'t', (byte)'e', (byte)'s', (byte)'t',
			0x03, // int
			0x00, 0x03, // name length
			(byte)'n', (byte)'b', (byte)'t',
			0x00, 0x00, 0x00, 0x2A, // value 42
			0x00 // end
		};

		var document = NbtParser.ParseBinary(payload);
		var compound = Assert.IsType<NbtCompound>(document.Root);
		Assert.True(compound.TryGetValue("nbt", out var tag));
		var intTag = Assert.IsType<NbtInt>(tag);
		Assert.Equal(42, intTag.Value);
	}

	[Fact]
	public void ParseSnbtSupportsTypedArraysAndScalars()
	{
		const string snbt = "{flag:true,health:20s,name:\"Player\",speeds:[0.5f,1.25f],items:[B;1b,2b,3b]}";
		var document = NbtParser.ParseSnbt(snbt);
		var compound = Assert.IsType<NbtCompound>(document.Root);

		var flag = Assert.IsType<NbtByte>(compound["flag"]);
		Assert.Equal(1, flag.Value);

		var health = Assert.IsType<NbtShort>(compound["health"]);
		Assert.Equal((short)20, health.Value);

		var name = Assert.IsType<NbtString>(compound["name"]);
		Assert.Equal("Player", name.Value);

		var speeds = Assert.IsType<NbtList>(compound["speeds"]);
		Assert.Equal(NbtTagType.Float, speeds.ElementType);
		Assert.Collection(speeds,
			item => Assert.Equal(0.5f, Assert.IsType<NbtFloat>(item).Value),
			item => Assert.Equal(1.25f, Assert.IsType<NbtFloat>(item).Value));

		var items = Assert.IsType<NbtByteArray>(compound["items"]);
		Assert.Equal(new byte[] { 1, 2, 3 }, items.Values);
	}
}
