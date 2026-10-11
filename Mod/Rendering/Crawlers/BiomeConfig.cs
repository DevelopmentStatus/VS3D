using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;
using VS3D.Bootstrap;

namespace VS3D.Rendering.Crawlers;

// One Vampire Crawlers biome kit and the VS stages it dresses.
public sealed class BiomeDef
{
    public string bundle { get; set; }                 // "inlaidlibrary" -> biome-inlaidlibrary_assets_all.bundle
    public string[] stages { get; set; } = Array.Empty<string>();   // VS StageType names (substring match)
    public string floor { get; set; }                  // tiled floor piece (Room_Fill, ~12 m)
    public string wallModule { get; set; }             // authored room wall section for thick walls (Room_Edge)
    public string[] rowPieces { get; set; } = Array.Empty<string>(); // freestanding pieces for thin rows, front = +Z
    public float thinRowMax { get; set; } = 4.5f;      // metres: walls this thin or thinner are "rows"
    public string[] islandPieces { get; set; } = Array.Empty<string>(); // furniture sets for small freestanding obstacles
    public float islandMax { get; set; } = 7f;         // metres: obstacles no bigger than this (both ways) are "islands"
    public string cornerPiece { get; set; }            // placed on outside corners of thick walls to hide seams
    public float[] background { get; set; } = { 0.05f, 0.04f, 0.04f };  // camera clear colour (RGB 0..1)
    public string[] decor { get; set; } = Array.Empty<string>();  // low ground cover for stages without collision walls
    public float decorChance { get; set; } = 0.03f;    // per decor cell
    public float decorCell { get; set; } = 4f;         // metres
    public float blockHeight { get; set; } = 4f;       // metres, wall mass height when the kit has no wall module
}

public static class BiomeConfig
{
    const string L = "Assets/_GameAssets/Art/Levels/";
    const string FileName = "biomes_v6.json";

    static BiomeDef Kit(string bundle, string folder, string prefix, string[] stages, string[] rowPieces) => new()
    {
        bundle = bundle,
        stages = stages,
        floor = $"{L}{folder}/Prefabs/Tiles/Rooms/{prefix}_Room_Fill_01_Room.prefab",
        wallModule = $"{L}{folder}/Prefabs/Tiles/Corridors/{prefix}_Room_Edge_01_Corridor.prefab",
        rowPieces = rowPieces,
    };

    static List<BiomeDef> Defaults() => new()
    {
        Library(),
        GalloTower(),
        DairyPlant(),
        CappellaMagna(),
        // Mad Forest last: it is also the fallback for stages without a dedicated kit.
        MadForest(),
    };

    static BiomeDef Library()
    {
        const string P = L + "InlaidLibrary/Prefabs/Props/";
        var k = Kit("inlaidlibrary", "InlaidLibrary", "Library", new[] { "LIBRARY" }, new[]
        {
            P + "Bookshelves_ Set_01.prefab",
            P + "Bookshelves_Set_02.prefab",
        });
        k.islandPieces = new[]
        {
            P + "BlockerBookcase_01.prefab", P + "BlockerBookcase_02.prefab",
            P + "Bookpillar_Set_01.prefab", P + "Bookpillar_Set_02.prefab",
            P + "Table_Set_01.prefab", P + "Table_Set_02.prefab", P + "Table_set_03.prefab", P + "Table_Set_04.prefab",
            P + "Cabinet_Set_01.prefab", P + "Glassshelves_Set_01.prefab", P + "Bottle_Cabinet_Set_01.prefab",
            P + "Jar_Shelves_01.prefab", P + "Object_Shelves_01.prefab",
        };
        k.cornerPiece = P + "Bookpillar_Set_02.prefab";
        k.background = new[] { 0.06f, 0.035f, 0.035f };
        return k;
    }

    static BiomeDef GalloTower()
    {
        const string P = L + "GalloTower/Prefabs/";
        var k = Kit("gallotower", "GalloTower", "GalloTower", new[] { "TOWER", "CASTLE", "ASTRAL", "ENTRANCE", "WHITE" }, Array.Empty<string>());
        k.islandPieces = new[]
        {
            P + "Layouts/GalloTower_Blocker.prefab",
            P + "Props/Fenced_Statue_01.prefab", P + "Props/Fenced_Statue_02.prefab",
            P + "Props/Fireplace_Set_01.prefab", P + "Props/Mirror_Set_01.prefab",
        };
        k.cornerPiece = P + "Props/Fenced_Statue_02.prefab";
        k.background = new[] { 0.04f, 0.04f, 0.07f };
        return k;
    }

    static BiomeDef DairyPlant()
    {
        const string P = L + "DairyPlant/Prefabs/Props/";
        var k = Kit("dairyplant", "DairyPlant", "DairyPlant", new[] { "WAREHOUSE", "DAIRY", "MACHINE", "LABOR", "SPAZIE", "POLUS" }, Array.Empty<string>());
        k.islandPieces = new[]
        {
            P + "Barrel_Set_01.prefab", P + "Barrel_Set_02.prefab", P + "Barrel_Set_03.prefab",
            P + "Box_Set_01.prefab",
            P + "Locker_Set_01.prefab", P + "Locker_Set_02.prefab", P + "Locker_Set_03.prefab",
            P + "Machinery_Set_03.prefab", P + "DairyPlant/Lab_65.prefab",
        };
        k.cornerPiece = P + "DairyPlant-LightTower.prefab";
        k.background = new[] { 0.035f, 0.045f, 0.045f };
        return k;
    }

    static BiomeDef CappellaMagna()
    {
        const string P = L + "CapellaMagna/Prefabs/Props/";
        var k = Kit("capellamagna", "CapellaMagna", "CappellaMagna", new[] { "CHAPEL", "DEVIL", "MOONSPELL" }, Array.Empty<string>());
        k.islandPieces = new[]
        {
            P + "CappellaMagna_Blocker_01.prefab", P + "CappellaMagna_Blocker_02.prefab", P + "CappellaMagna_Blocker_03.prefab",
            P + "CM_Statue_01.prefab", P + "CM_Statue_02.prefab", P + "CM_Statue_03.prefab",
            P + "CM_Altar_01.prefab", P + "CM_Piano.prefab", P + "CM_Throne.prefab", P + "CM_Fireplace.prefab",
            P + "CM_Bench_01.prefab",
        };
        k.cornerPiece = P + "CM_Pillar_02.prefab";
        k.background = new[] { 0.05f, 0.03f, 0.07f };
        return k;
    }

    static BiomeDef MadForest()
    {
        const string P = L + "MadForest/Prefabs/Props/";
        var k = Kit("madforest", "MadForest", "MadForest", new[] { "FOREST", "GREENACRES", "MOLISE", "BONEZONE", "BATCOUNTRY", "EMERALD", "WESTWOODS" }, Array.Empty<string>());
        k.islandPieces = new[]
        {
            P + "MadForest_Blocker_01.prefab", P + "MadForest_Blocker_02.prefab", P + "MadForest_Blocker_03.prefab",
            P + "MadForest_Blocker_04.prefab", P + "MadForest_Blocker_05.prefab",
            P + "Tree_Dressing_01.prefab", P + "Tree_Dressing_02.prefab", P + "Tree_Dressing_03.prefab",
            P + "Tree_Dressing_04.prefab", P + "Tree_Dressing_05.prefab",
            P + "Log_01.prefab", P + "Log_02.prefab", P + "Tombstones_Set_01.prefab", P + "Tomestone_02.prefab",
        };
        k.cornerPiece = P + "Column_01.prefab";
        // Open fields (no collision walls): sparse low ground cover only, nothing that looks solid.
        k.decor = new[]
        {
            P + "Ground_Foliage_01.prefab", P + "Ground_Foliage_02.prefab", P + "Ground_Foliage_03.prefab",
            P + "Ground_Foliage_04.prefab", P + "FeatherSkull_01.prefab", P + "Tomestone_01.prefab",
        };
        k.decorChance = 0.035f;
        k.background = new[] { 0.10f, 0.13f, 0.17f };
        return k;
    }

    public static List<BiomeDef> Load()
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
            var list = JsonSerializer.Deserialize<List<BiomeDef>>(File.ReadAllText(path), opts);
            Plugin.Log.LogInfo($"Biomes: {list.Count} Crawlers kits from {path}");
            return list;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Biomes: failed to read {path} ({ex.Message}); using defaults");
            return Defaults();
        }
    }

    public static BiomeDef Pick(List<BiomeDef> biomes, string stageType)
    {
        foreach (var b in biomes)
            foreach (var s in b.stages)
                if (!string.IsNullOrEmpty(s) && stageType.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                    return b;
        return biomes[biomes.Count - 1];
    }
}
