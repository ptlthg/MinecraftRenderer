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

	[Fact]
	public void ResolveEnabledOverlays_HandlesModernFabricConditionAliasesAndVersionRanges()
	{
		const string packMcmeta = """
{
  "pack": {
    "min_format": 69,
    "max_format": 84
  },
  "catharsis:pack/v1": {
    "id": "hplus"
  },
  "fabric:overlays": {
    "entries": [
      {
        "directory": "hplus_weapons_swords",
        "condition": {
          "condition": "fabric:and",
          "values": [
            {
              "condition": "fabric:not",
              "value": {
                "condition": "catharsis:config",
                "pack": "hplus",
                "id": "toggle_swords",
                "value": "off"
              }
            },
            {
              "condition": "fabric:not",
              "value": {
                "condition": "catharsis:config",
                "pack": "hplus",
                "id": "toggle_all_weapons",
                "value": "off"
              }
            }
          ]
        }
      },
      {
        "directory": "hplus_anim_shortbow",
        "condition": {
          "condition": "fabric:or",
          "values": [
            {
              "condition": "catharsis:config",
              "pack": "hplus",
              "id": "anim_shortbow",
              "value": "off"
            },
            {
              "condition": "catharsis:config",
              "pack": "hplus",
              "id": "toggle_all_animations",
              "value": "off"
            }
          ]
        }
      },
      {
        "directory": "hplus_1_21_11_ui",
        "condition": {
          "condition": "catharsis:version",
          "type": "pack_format",
          "packFormatRange": {
            "min_inclusive": 69.0,
            "max_inclusive": 75.0
          }
        }
      },
      {
        "directory": "future_ui",
        "condition": {
          "condition": "catharsis:version",
          "type": "pack_format",
          "packFormatRange": {
            "min_inclusive": 85,
            "max_inclusive": 90
          }
        }
      }
    ]
  }
}
""";

		const string config = """
[
  {
    "type": "tab",
    "title": "Items",
    "options": [
      {
        "type": "dropdown",
        "id": "toggle_swords",
        "options": [
          { "value": "on", "default": true },
          { "value": "off" }
        ]
      },
      {
        "type": "dropdown",
        "id": "toggle_all_weapons",
        "options": [
          { "value": "on", "default": true },
          { "value": "off" }
        ]
      },
      {
        "type": "dropdown",
        "id": "anim_shortbow",
        "options": [
          { "value": "on", "default": true },
          { "value": "off" }
        ]
      },
      {
        "type": "dropdown",
        "id": "toggle_all_animations",
        "options": [
          { "value": "on" },
          { "value": "off", "default": true }
        ]
      }
    ]
  }
]
""";

		var overlays = CatharsisPackConfig.ResolveEnabledOverlays(packMcmeta, config).ToArray();

		Assert.Contains("hplus_weapons_swords", overlays, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("hplus_anim_shortbow", overlays, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("hplus_1_21_11_ui", overlays, StringComparer.OrdinalIgnoreCase);
		Assert.DoesNotContain("future_ui", overlays, StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public void ResolveEnabledOverlays_UsesSelectedValuesFromSelectConfigEntries()
	{
		const string packMcmeta = """
{
  "catharsis:pack/v1": {
    "id": "select-test",
    "config": [
      {
        "type": "select",
        "id": "enabled_categories",
        "options": [
          { "value": "farming", "selected": true },
          { "value": "mining" }
        ]
      }
    ]
  },
  "fabric:overlays": {
    "entries": [
      {
        "directory": "farming",
        "condition": {
          "condition": "catharsis:config",
          "id": "enabled_categories",
          "value": "farming"
        }
      },
      {
        "directory": "mining",
        "condition": {
          "condition": "catharsis:config",
          "id": "enabled_categories",
          "value": "mining"
        }
      }
    ]
  }
}
""";

		var overlays = CatharsisPackConfig.ResolveEnabledOverlays(packMcmeta).ToArray();

		Assert.Contains("farming", overlays, StringComparer.OrdinalIgnoreCase);
		Assert.DoesNotContain("mining", overlays, StringComparer.OrdinalIgnoreCase);
	}
}
