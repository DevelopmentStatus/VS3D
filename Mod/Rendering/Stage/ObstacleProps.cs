using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using BepInEx;
using UnityEngine;
using UnityEngine.Tilemaps;
using VS3D.Bootstrap;
using VS3D.SimulationBridge;

namespace VS3D.Rendering.Maps;

// The map's Obstacle layer as location indicators: every connected group of obstacle tiles is one
// object. Its class comes from config/VS3D/tiles/<table>.json (exact tile-signature match, generated
// offline from the map data); clear matches become Crawlers props, everything else keeps its own art:
//  - "standee": the group's tiles stood upright, turning to face the camera;
//  - "flat": the group's tiles lying on the ground (rugs, dust, blood, grates).
public sealed class ObstacleProps
{
    const string L = "Assets/_GameAssets/Art/Levels/";

    // Class -> Crawlers prefabs (any kit). Front of these pieces = local +Z.
    static readonly Dictionary<string, string[]> Classes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bookshelf"] = new[] { L + "InlaidLibrary/Prefabs/Props/Bookshelves_ Set_01.prefab", L + "InlaidLibrary/Prefabs/Props/Bookshelves_Set_02.prefab" },
        ["table"] = new[] { L + "InlaidLibrary/Prefabs/Props/Table_Set_01.prefab", L + "InlaidLibrary/Prefabs/Props/Table_Set_02.prefab", L + "InlaidLibrary/Prefabs/Props/Table_set_03.prefab" },
        ["piano"] = new[] { L + "InlaidLibrary/Prefabs/Props/Piano_Set.prefab" },
        ["seat"] = new[] { L + "InlaidLibrary/Prefabs/Props/Chair_Set_01.prefab" },
        ["cabinet"] = new[] { L + "InlaidLibrary/Prefabs/Props/Cabinet_Set_01.prefab", L + "InlaidLibrary/Prefabs/Props/Drawers_Set_01.prefab" },
        ["lab"] = new[] { L + "DairyPlant/Prefabs/Props/DairyPlant/Lab_65.prefab", L + "DairyPlant/Prefabs/Props/Machinery_Set_03.prefab" },
        ["statue"] = new[] { L + "CapellaMagna/Prefabs/Props/CM_Statue_01.prefab", L + "CapellaMagna/Prefabs/Props/CM_Statue_02.prefab", L + "CapellaMagna/Prefabs/Props/CM_Statue_03.prefab" },
        ["fireplace"] = new[] { L + "GalloTower/Prefabs/Props/Fireplace_Set_01.prefab" },
        ["tree"] = new[] { L + "MadForest/Prefabs/Props/Tree_Dressing_01.prefab", L + "MadForest/Prefabs/Props/Tree_Dressing_02.prefab", L + "MadForest/Prefabs/Props/Tree_Dressing_03.prefab", L + "MadForest/Prefabs/Props/Tree_Dressing_04.prefab" },
        ["bush"] = new[] { L + "MadForest/Prefabs/Props/Ground_Foliage_03.prefab", L + "MadForest/Prefabs/Props/Ground_Foliage_05.prefab" },
        ["grass"] = new[] { L + "MadForest/Prefabs/Props/Ground_Foliage_01.prefab", L + "MadForest/Prefabs/Props/Ground_Foliage_02.prefab" },
        ["log"] = new[] { L + "MadForest/Prefabs/Props/Log_01.prefab", L + "MadForest/Prefabs/Props/Log_02.prefab" },
        ["grave"] = new[] { L + "MadForest/Prefabs/Props/Tomestone_01.prefab", L + "MadForest/Prefabs/Props/Tomestone_02.prefab" },
    };
    // Vegetation keeps its natural size; furniture is fitted to the obstacle footprint.
    static readonly HashSet<string> NaturalSize = new(StringComparer.OrdinalIgnoreCase) { "tree", "bush", "grass", "log", "grave" };

    public sealed class Group
    {
        public int Map;
        public Tilemap Tilemap;
        public readonly List<Vector3Int> Cells = new();
        public readonly List<Sprite> Sprites = new();
        public int MinX, MinY, MaxX, MaxY;
        public string Class;
        public GameObject Art;          // standee / flat mesh
        public bool Standee;
    }

    public readonly List<Group> Groups = new();
    readonly Dictionary<string, string> _table = new();
    Transform _root;
    ArtMaterials _mats;
    int _sortingLayer;

    public static IEnumerable<string> AllPrefabs()
    {
        foreach (var list in Classes.Values) foreach (var p in list) yield return p;
    }

    public static string[] PrefabsFor(string cls) => cls != null && Classes.TryGetValue(cls, out var l) ? l : null;
    public static bool KeepsNaturalSize(string cls) => cls != null && NaturalSize.Contains(cls);

    public void Begin(Transform worldRoot, StageLayers layers, string tableName, ArtMaterials mats, int sortingLayer)
    {
        End();
        _mats = mats;
        _sortingLayer = sortingLayer;
        var go = new GameObject("VS3D_Obstacles");
        go.transform.SetParent(worldRoot, false);
        RenderLayer.Apply(go);
        _root = go.transform;
        LoadTable(tableName);

        for (int m = 0; m < layers.Maps.Count; m++)
        {
            if (!layers.Maps[m].Layers.TryGetValue("Obstacle", out var tm) || tm == null) continue;
            var cells = new Dictionary<Vector3Int, Sprite>();
            var b = tm.cellBounds;
            for (int y = b.yMin; y < b.yMax; y++)
                for (int x = b.xMin; x < b.xMax; x++)
                {
                    var p = new Vector3Int(x, y, 0);
                    var s = tm.GetSprite(p);
                    if (s != null) cells[p] = s;
                }
            var seen = new HashSet<Vector3Int>();
            foreach (var start in cells.Keys)
            {
                if (seen.Contains(start)) continue;
                var g = new Group { Map = m, Tilemap = tm, MinX = int.MaxValue, MinY = int.MaxValue, MaxX = int.MinValue, MaxY = int.MinValue };
                var q = new Queue<Vector3Int>(); q.Enqueue(start); seen.Add(start);
                while (q.Count > 0)
                {
                    var c = q.Dequeue();
                    g.Cells.Add(c); g.Sprites.Add(cells[c]);
                    g.MinX = Math.Min(g.MinX, c.x); g.MinY = Math.Min(g.MinY, c.y); g.MaxX = Math.Max(g.MaxX, c.x); g.MaxY = Math.Max(g.MaxY, c.y);
                    foreach (var d in new[] { new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0), new Vector3Int(0, 1, 0), new Vector3Int(0, -1, 0) })
                        if (cells.ContainsKey(c + d) && seen.Add(c + d)) q.Enqueue(c + d);
                }
                g.Class = _table.TryGetValue(Signature(g), out var cls) ? cls : "standee";
                Groups.Add(g);
            }
        }
        var counts = new Dictionary<string, int>();
        foreach (var g in Groups) counts[g.Class] = counts.TryGetValue(g.Class, out var n) ? n + 1 : 1;
        var sb = new StringBuilder();
        foreach (var kv in counts) sb.Append($"{kv.Key}={kv.Value} ");
        Plugin.Log.LogInfo($"Obstacles: {Groups.Count} object(s) from table '{tableName ?? "-"}' ({_table.Count} known): {sb}");
    }

    public void End()
    {
        if (_root != null && _root) UnityEngine.Object.Destroy(_root.gameObject);
        _root = null;
        Groups.Clear();
        _table.Clear();
    }

    void LoadTable(string name)
    {
        _table.Clear();
        if (string.IsNullOrEmpty(name)) return;
        var path = Path.Combine(Paths.ConfigPath, "VS3D", "tiles", name + ".json");
        try
        {
            if (!File.Exists(path)) { Plugin.Log.LogWarning($"Obstacles: no class table {path}"); return; }
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            foreach (var kv in map) _table[kv.Key] = kv.Value;
        }
        catch (Exception ex) { Plugin.Log.LogError($"Obstacles: failed to read {path}: {ex.Message}"); }
    }

    // Same format as the offline probe: "dx,dy:sprite" for each cell, sorted by y then x.
    static string Signature(Group g)
    {
        var idx = new List<int>(g.Cells.Count);
        for (int i = 0; i < g.Cells.Count; i++) idx.Add(i);
        idx.Sort((a, b) => g.Cells[a].y != g.Cells[b].y ? g.Cells[a].y.CompareTo(g.Cells[b].y) : g.Cells[a].x.CompareTo(g.Cells[b].x));
        var sb = new StringBuilder();
        for (int k = 0; k < idx.Count; k++)
        {
            var c = g.Cells[idx[k]];
            if (k > 0) sb.Append(';');
            sb.Append(c.x - g.MinX).Append(',').Append(c.y - g.MinY).Append(':').Append(g.Sprites[idx[k]].name);
        }
        return sb.ToString();
    }

    // World-space footprint of a group: centre (3D, on the ground) and size in metres.
    public static (Vector3 center, float sx, float sz, Vector2 center2D) Footprint(Group g)
    {
        var a = g.Tilemap.GetCellCenterWorld(new Vector3Int(g.MinX, g.MinY, 0));
        var b = g.Tilemap.GetCellCenterWorld(new Vector3Int(g.MaxX, g.MaxY, 0));
        var c2 = new Vector2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f);
        var cs = g.Tilemap.cellSize; var ls = g.Tilemap.transform.lossyScale;
        float sx = (g.MaxX - g.MinX + 1) * Mathf.Abs(cs.x * ls.x) * CoordinateMapper.Scale;
        float sz = (g.MaxY - g.MinY + 1) * Mathf.Abs(cs.y * ls.y) * CoordinateMapper.Scale;
        return (CoordinateMapper.ToWorld3D(c2), sx, sz, c2);
    }

    // Standees and flat decals: built lazily from the group's own art, shown within range.
    // Any class without a 3D prop (or with Crawlers assets off) is a standee unless it is "flat".
    public void UpdateArt(Group g, bool inRange, float cellMetres)
    {
        if (!inRange)
        {
            if (g.Art != null && g.Art) g.Art.SetActive(false);
            return;
        }
        if (g.Art == null || !g.Art)
        {
            g.Standee = g.Class != "flat";
            g.Art = new GameObject("obstacle");
            g.Art.transform.SetParent(_root, false);
            g.Art.layer = RenderLayer.Index;
            var mesh = new ArtMesh();
            int w = g.MaxX - g.MinX + 1;
            float half = cellMetres * 0.5f;
            for (int i = 0; i < g.Cells.Count; i++)
            {
                var c = g.Cells[i];
                // Local layout: x across, y up (standee) or z forward (flat); centred on the group.
                float lx = (c.x - g.MinX - (w - 1) * 0.5f) * cellMetres;
                float ly = (c.y - g.MinY) * cellMetres;
                if (g.Standee)
                    mesh.Quad(new Vector3(lx - half, ly, 0), new Vector3(lx + half, ly, 0), new Vector3(lx + half, ly + cellMetres, 0), new Vector3(lx - half, ly + cellMetres, 0), g.Sprites[i]);
                else
                {
                    float lz = (c.y - (g.MinY + g.MaxY) * 0.5f) * cellMetres;
                    mesh.Quad(new Vector3(lx - half, 0.03f, lz - half), new Vector3(lx + half, 0.03f, lz - half), new Vector3(lx + half, 0.03f, lz + half), new Vector3(lx - half, 0.03f, lz + half), g.Sprites[i]);
                }
            }
            var holder = new GameObject("mesh");
            holder.transform.SetParent(g.Art.transform, false);
            holder.layer = RenderLayer.Index;
            mesh.Apply(holder.transform, _mats, _sortingLayer, g.Standee ? 0 : -100, cutout: true);
        }
        g.Art.SetActive(true);
        var fp = Footprint(g);
        // Standees stand on the group's south edge (where the 2D art touches the ground).
        var pos = g.Standee ? fp.center + new Vector3(0, 0, -fp.sz * 0.5f + cellMetres * 0.5f) : fp.center;
        g.Art.transform.position = pos;
    }

    // Standees turn to face the camera (yaw only), like the sprite billboards.
    public void FaceCamera(float cameraYaw)
    {
        var rot = Quaternion.Euler(0f, cameraYaw, 0f);
        foreach (var g in Groups)
            if (g.Standee && g.Art != null && g.Art && g.Art.activeSelf)
                g.Art.transform.rotation = rot;
    }
}
