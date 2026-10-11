using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace VS3D.Rendering.Maps;

// Cached sprite lookups for the stage's wall-art layers, resolved into a world-aligned window grid.
// Each tilemap is scanned once; per rebuild only the cached entries are mapped into the window.
public sealed class StageArt
{
    sealed class Layer
    {
        public Tilemap Map;
        public bool Front;        // FakeWalls / PlayerWall: drawn wall fronts and facades
        public int Order;
        public readonly List<(int x, int y, Sprite s)> Cells = new();
    }

    readonly List<Layer> _layers = new();

    public Sprite[] Art = System.Array.Empty<Sprite>();     // topmost wall art per window cell (decoration over the wall)
    public Sprite[] Low = System.Array.Empty<Sprite>();     // lowest wall art per window cell (the wall itself)
    public bool[] LowFromWalls = System.Array.Empty<bool>(); // Low came from the Walls layer
    public Sprite[] Front = System.Array.Empty<Sprite>();   // facade art (FakeWalls/PlayerWall) per window cell

    public void Build(StageLayers layers)
    {
        _layers.Clear();
        foreach (var m in layers.Maps)
            foreach (var name in StageLayers.WallArtLayers)
            {
                if (!m.Layers.TryGetValue(name, out var tm) || tm == null) continue;
                var tr = tm.GetComponent<TilemapRenderer>();
                var layer = new Layer { Map = tm, Front = name != "Walls", Order = tr != null ? tr.sortingOrder : 0 };
                var distinct = new HashSet<System.IntPtr>();
                var b = tm.cellBounds;
                for (int y = b.yMin; y < b.yMax; y++)
                    for (int x = b.xMin; x < b.xMax; x++)
                    {
                        var s = tm.GetSprite(new Vector3Int(x, y, 0));
                        if (s == null) continue;
                        layer.Cells.Add((x, y, s));
                        distinct.Add(s.Pointer);
                    }
                // A collision layer drawn with a single sprite is a hidden marker, not art.
                if (!layer.Front && (distinct.Count <= 1 || (tr != null && !tr.enabled)))
                    continue;
                if (layer.Cells.Count > 0)
                    _layers.Add(layer);
            }
        _layers.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    // Fill Art/Front for the window [x0, x0+w) x [y0, y0+h) of world cells (cw, ch = cell size in 2D units).
    public void Resolve(int x0, int y0, int w, int h, float cw, float ch)
    {
        int n = w * h;
        if (Art.Length < n) { Art = new Sprite[n]; Front = new Sprite[n]; Low = new Sprite[n]; LowFromWalls = new bool[n]; }
        System.Array.Clear(Art, 0, n);
        System.Array.Clear(Front, 0, n);
        System.Array.Clear(Low, 0, n);
        System.Array.Clear(LowFromWalls, 0, n);
        foreach (var l in _layers)
        {
            if (l.Map == null || !l.Map) continue;
            // World cell of local cell (0,0).
            var o = l.Map.GetCellCenterWorld(Vector3Int.zero);
            int bx = Mathf.FloorToInt(o.x / cw) - x0, by = Mathf.FloorToInt(o.y / ch) - y0;
            foreach (var (x, y, s) in l.Cells)
            {
                int gx = x + bx, gy = y + by;
                if (gx < 0 || gy < 0 || gx >= w || gy >= h) continue;
                int i = gy * w + gx;
                Art[i] = s;                 // sorted by order: later (higher) layers win
                if (Low[i] == null) { Low[i] = s; LowFromWalls[i] = !l.Front; }
                if (l.Front) Front[i] = s;
            }
        }
    }
}
