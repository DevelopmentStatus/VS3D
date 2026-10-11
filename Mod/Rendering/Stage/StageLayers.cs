using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;
using VampireSurvivors.Objects;

namespace VS3D.Rendering.Maps;

// The stage's own tile layers, per map copy (the game tiles several copies of each map).
// Layer names are SuperTiled2Unity's: Floor, FloorOverlay, Walls, FakeWalls, PlayerWall, Obstacle,
// Decals, Overlay1, Shadows, ShadowDecals, Spawning.
public sealed class StageLayers
{
    public static readonly string[] GroundLayers = { "Floor", "FloorOverlay", "Decals", "Shadows", "ShadowDecals" };
    public static readonly string[] WallArtLayers = { "PlayerWall", "FakeWalls", "Walls" };

    public sealed class MapCopy
    {
        public Transform Root;
        public readonly Dictionary<string, Tilemap> Layers = new();
    }

    public readonly List<MapCopy> Maps = new();
    public string MapName = "";
    public GameObject Background;   // TilingBackground for stages without a tileset

    public void Collect(Stage stage)
    {
        Maps.Clear();
        MapName = "";
        Background = null;
        if (stage == null)
            return;
        var tileset = stage.TilingTileset;
        if (tileset != null && tileset._maps != null)
        {
            var maps = tileset._maps;
            for (int i = 0; i < maps.Count; i++)
            {
                var map = maps[i];
                if (map == null) continue;
                var copy = new MapCopy { Root = map.transform };
                var tms = map.GetComponentsInChildren<Tilemap>(true);
                for (int k = 0; k < tms.Length; k++)
                    copy.Layers[tms[k].gameObject.name] = tms[k];
                Maps.Add(copy);
                if (MapName.Length == 0)
                    MapName = map.gameObject.name.Replace("(Clone)", "").Trim();
            }
        }
        var bg = stage.TilingBackground;
        if (bg != null)
            Background = bg.gameObject;
    }

    // Every GameObject that makes up the ground of this stage.
    public List<GameObject> GroundObjects()
    {
        var list = new List<GameObject>();
        foreach (var m in Maps)
            foreach (var name in GroundLayers)
                if (m.Layers.TryGetValue(name, out var tm) && tm != null)
                    list.Add(tm.gameObject);
        if (Maps.Count == 0 && Background != null)
            list.Add(Background);
        return list;
    }
}
