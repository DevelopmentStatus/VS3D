using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;
using VS3D.Bootstrap;

namespace VS3D.Rendering.Maps;

// How one VS map is turned into 3D. Keyed by the map prefab name (SuperMap), e.g. "Library".
public sealed class StageKit
{
    public string[] maps { get; set; } = Array.Empty<string>();      // SuperMap names (prefix match, case-insensitive)
    public string[] stages { get; set; } = Array.Empty<string>();    // StageType fallback (substring)
    public string ground { get; set; } = "vs";      // "vs" = capture the map's own ground layers, "crawlers" = kit floor tiles
    public string walls { get; set; } = "extrude";  // "extrude" = stand the map's wall art up, "crawlers" = kit wall modules
    public string crawlersBiome { get; set; }       // biome name in biomes_v6.json for crawlers ground/walls
    public int faceRows { get; set; } = 2;          // wall height in tiles when it can't be read from the art
    public int facadeRows { get; set; } = 0;        // fixed building facade height in tiles (0 = read from the art)
    public string tiles { get; set; }               // obstacle class table file in config/VS3D/tiles (without .json)
    public float[] background { get; set; } = { 0.05f, 0.05f, 0.06f };
}

public static class StageKits
{
    const string FileName = "stagekits_v2.json";

    static StageKit K(string[] maps, string ground, string walls, string biome, string tiles, float r, float g, float b, int faceRows = 2, int facadeRows = 0) => new()
    {
        maps = maps, ground = ground, walls = walls, crawlersBiome = biome, tiles = tiles,
        background = new[] { r, g, b }, faceRows = faceRows, facadeRows = facadeRows,
    };

    // Base game. Maps not listed fall back to the last entry: VS's own art (faithful for any map).
    static List<StageKit> Defaults() => new()
    {
        // Library: the bookshelf walls look the same top to bottom, so their height is fixed.
        K(new[] { "Library" }, "crawlers", "crawlers", "inlaidlibrary", "library", 0.06f, 0.035f, 0.035f, 2, 6),
        K(new[] { "Forest", "02GreenAcres" }, "crawlers", "crawlers", "madforest", "forest", 0.10f, 0.13f, 0.17f),
        K(new[] { "Chapel" }, "crawlers", "crawlers", "capellamagna", "chapel", 0.05f, 0.03f, 0.07f),
        K(new[] { "Tower" }, "crawlers", "crawlers", "gallotower", "tower", 0.04f, 0.04f, 0.07f),
        K(new[] { "Laborratory", "CarloCart" }, "vs", "extrude", null, "laborratory", 0.03f, 0.04f, 0.045f, 3),
        K(new[] { "03RiskyWoods" }, "vs", "extrude", null, "riskywoods", 0.08f, 0.09f, 0.10f),
        K(new[] { "Westwoods" }, "vs", "extrude", null, "westwoods", 0.06f, 0.04f, 0.02f),
        K(new[] { "DevilRoom" }, "vs", "extrude", null, "devilroom", 0.08f, 0.02f, 0.02f, 3),
        K(new[] { "Space" }, "vs", "extrude", null, "space", 0.01f, 0.01f, 0.03f),
        K(new[] { "Plant2", "ADV_SHEMOON_002" }, "vs", "extrude", null, null, 0.03f, 0.045f, 0.045f, 3),
        K(new[] { "Whiteout" }, "vs", "extrude", null, null, 0.55f, 0.58f, 0.62f),
        K(new[] { "AstralStair" }, "vs", "extrude", null, null, 0.02f, 0.02f, 0.06f),
        // Fallback: any other map.
        K(Array.Empty<string>(), "vs", "extrude", null, null, 0.05f, 0.05f, 0.06f),
    };

    public static List<StageKit> Load()
    {
        var dir = Path.Combine(Paths.ConfigPath, "VS3D");
        var path = Path.Combine(dir, FileName);
        var opts = new JsonSerializerOptions { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(Defaults(), opts));
            }
            return JsonSerializer.Deserialize<List<StageKit>>(File.ReadAllText(path), opts);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Stage kits: failed to read {path} ({ex.Message}); using defaults");
            return Defaults();
        }
    }

    // The same map built only from Vampire Survivors' own art (no Crawlers assets).
    public static StageKit VsOnly(StageKit k) => new()
    {
        maps = k.maps, stages = k.stages, ground = "vs", walls = "extrude", crawlersBiome = null,
        faceRows = k.faceRows, facadeRows = k.facadeRows, tiles = k.tiles, background = k.background,
    };

    public static StageKit Pick(List<StageKit> kits, string mapName, string stageType)
    {
        foreach (var k in kits)
            foreach (var m in k.maps)
                if (!string.IsNullOrEmpty(mapName) && mapName.StartsWith(m, StringComparison.OrdinalIgnoreCase))
                    return k;
        foreach (var k in kits)
            foreach (var s in k.stages)
                if (!string.IsNullOrEmpty(s) && stageType.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                    return k;
        return kits[kits.Count - 1];
    }
}
