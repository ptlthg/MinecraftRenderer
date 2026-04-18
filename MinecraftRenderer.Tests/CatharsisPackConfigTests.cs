using System;
using System.Collections.Generic;
using System.Linq;
using MinecraftRenderer.TexturePacks;
using Xunit;

namespace MinecraftRenderer.Tests;

public sealed class CatharsisPackConfigTests
{
	private const string PackMcmeta = """
{
  "catharsis:pack/v1": {
    "config": [
      {
        "type": "tab",
        "title": "Legacy",
        "options": [
          { "type": "boolean", "id": "item_melee", "default": true },
          { "type": "boolean", "id": "legacy_voidgloom2" },
          {
            "type": "dropdown",
            "id": "legacy_axerevert",
            "options": [
              { "value": "off", "default": true },
              { "value": "fsr" },
              { "value": "vanilla" }
            ]
          }
        ]
      }
    ]
  },
  "fabric:overlays": {
    "entries": [
      {
        "directory": "fsr_item_melee",
        "condition": { "condition": "catharsis:config", "id": "item_melee" }
      },
      {
        "directory": "fsr_legacy_voidgloom2",
        "condition": { "condition": "catharsis:config", "id": "legacy_voidgloom2" }
      },
      {
        "directory": "fsr_legacy_axerevert",
        "condition": {
          "condition": "fabric:not",
          "value": { "condition": "catharsis:config", "id": "legacy_axerevert", "value": "off" }
        }
      },
      {
        "directory": "fsr_legacy_axerevert",
        "condition": { "condition": "catharsis:config", "id": "legacy_axerevert", "value": "fsr" }
      },
      {
        "directory": "fsr_legacy_axerevert_vanilla",
        "condition": { "condition": "catharsis:config", "id": "legacy_axerevert", "value": "vanilla" }
      }
    ]
  }
}
""";

	private const string ExternalConfig = """
[
  {
    "type": "tab",
    "title": "Items",
    "options": [
      { "type": "boolean", "id": "item_melee", "default": true },
      { "type": "boolean", "id": "item_tool", "default": true },
      {
        "type": "dropdown",
        "id": "legacy_axerevert",
        "options": [
          { "value": "off", "default": true },
          { "value": "fsr" },
          { "value": "vanilla" }
        ]
      }
    ]
  }
]
""";

	[Fact]
	public void ResolveEnabledOverlays_UsesCatharsisDefaults()
	{
		var overlays = CatharsisPackConfig.ResolveEnabledOverlays(PackMcmeta).ToArray();

		Assert.Contains("fsr_item_melee", overlays, StringComparer.OrdinalIgnoreCase);
		Assert.DoesNotContain("fsr_legacy_voidgloom2", overlays, StringComparer.OrdinalIgnoreCase);
		Assert.DoesNotContain("fsr_legacy_axerevert", overlays, StringComparer.OrdinalIgnoreCase);
		Assert.DoesNotContain("fsr_legacy_axerevert_vanilla", overlays, StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public void ResolveEnabledOverlays_AppliesOverridesBeforeEvaluatingConditions()
	{
		var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["legacy_voidgloom2"] = "true",
			["legacy_axerevert"] = "fsr"
		};

		var overlays = CatharsisPackConfig.ResolveEnabledOverlays(PackMcmeta, overrides).ToArray();

		Assert.Contains("fsr_item_melee", overlays, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("fsr_legacy_voidgloom2", overlays, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("fsr_legacy_axerevert", overlays, StringComparer.OrdinalIgnoreCase);
		Assert.DoesNotContain("fsr_legacy_axerevert_vanilla", overlays, StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public void ResolveEnabledOverlays_UsesExternalCatharsisConfigWhenPresent()
	{
		const string packWithoutEmbeddedConfig = """
{
  "catharsis:pack/v1": {
    "id": "furfsky"
  },
  "fabric:overlays": {
    "entries": [
      {
        "directory": "item_melee",
        "condition": { "condition": "catharsis:config", "id": "item_melee" }
      },
      {
        "directory": "item_tool",
        "condition": { "condition": "catharsis:config", "id": "item_tool" }
      },
      {
        "directory": "legacy_axerevert",
        "condition": {
          "condition": "fabric:not",
          "value": { "condition": "catharsis:config", "id": "legacy_axerevert", "value": "off" }
        }
      }
    ]
  }
}
""";

		var overlays = CatharsisPackConfig.ResolveEnabledOverlays(packWithoutEmbeddedConfig, ExternalConfig).ToArray();

		Assert.Contains("item_melee", overlays, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("item_tool", overlays, StringComparer.OrdinalIgnoreCase);
		Assert.DoesNotContain("legacy_axerevert", overlays, StringComparer.OrdinalIgnoreCase);
	}
}