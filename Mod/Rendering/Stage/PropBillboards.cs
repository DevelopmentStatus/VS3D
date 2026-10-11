using System.Collections.Generic;
using UnityEngine;
using VS3D.SimulationBridge;

namespace VS3D.Rendering.Maps;

// Props drawn as camera-facing billboards made from the map's own tiles: barrels, tanks, crates,
// machines (player-only cut-out cells), and cut-out decoration standing in pits.
// Tiles are joined into one object when their pixels continue across the shared tile edge, so a
// two-tile tank becomes one billboard while a barrel next to a crate stays separate.
// Objects are keyed by their world cells, kept while in the window, and rebuilt only when new.
public sealed class PropBillboards
{
    sealed class Prop
    {
        public GameObject Go;
        public int Seen;
    }

    readonly Dictionary<long, Prop> _props = new();
    readonly List<long> _stale = new();
    Transform _root;
    int _generation;

    int[] _parent = System.Array.Empty<int>();
    byte[] _cat = System.Array.Empty<byte>();
    readonly Dictionary<int, List<int>> _objects = new();

    public int Count => _props.Count;

    public void Begin(Transform parent)
    {
        End();
        var go = new GameObject("VS3D_Props");
        go.transform.SetParent(parent, false);
        RenderLayer.Apply(go);
        _root = go.transform;
    }

    public void End()
    {
        foreach (var p in _props.Values) Destroy(p.Go);
        _props.Clear();
        if (_root != null && _root) Object.Destroy(_root.gameObject);
        _root = null;
    }

    // category per cell: 0 none, 1 player-only cut-out (wall art + decoration), 2 cut-out decoration in a pit.
    public void Rebuild(CellClassifier c, Sprite[] low, Sprite[] top, int x0, int y0, int w, int h, float cw, float ch,
                        float cellMetres, float pitDepth, ArtMaterials mats, int sortingLayer)
    {
        if (_root == null) return;
        _generation++;
        int n = w * h;
        if (_parent.Length < n) { _parent = new int[n]; _cat = new byte[n]; }
        for (int i = 0; i < n; i++)
        {
            _parent[i] = i;
            int x = i % w, y = i / w;
            byte cat = 0;
            if (x > 0 && y > 0 && x < w - 1 && y < h - 1)
            {
                if (c.Kind[i] == CellClassifier.LowProp && (low[i] != null || top[i] != null)) cat = 1;
                else if (c.Kind[i] == CellClassifier.Pit && top[i] != null && top[i] != low[i] && !SpriteOpacity.IsOpaque(top[i])) cat = 2;
            }
            _cat[i] = cat;
        }

        // Join neighbours whose pixels meet across the shared edge.
        for (int i = 0; i < n; i++)
        {
            if (_cat[i] == 0) continue;
            var a = Masks(i, low, top);
            int r = i + 1, u = i + w;
            if ((i % w) + 1 < w && _cat[r] == _cat[i] && (a.Right & Masks(r, low, top).Left) != 0) Union(i, r);
            if (u < n && _cat[u] == _cat[i] && (a.Top & Masks(u, low, top).Bottom) != 0) Union(i, u);
        }

        _objects.Clear();
        for (int i = 0; i < n; i++)
        {
            if (_cat[i] == 0) continue;
            int root = Find(i);
            if (!_objects.TryGetValue(root, out var list)) _objects[root] = list = new List<int>();
            list.Add(i);
        }

        float half = cellMetres * 0.5f;
        foreach (var cells in _objects.Values)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue;
            foreach (int i in cells)
            {
                minX = Mathf.Min(minX, i % w); maxX = Mathf.Max(maxX, i % w); minY = Mathf.Min(minY, i / w);
            }
            byte cat = _cat[cells[0]];
            long key = ((long)((x0 + minX) & 0xFFFFF) << 40) | ((long)((y0 + minY) & 0xFFFFF) << 20) | ((long)(cells.Count & 0x7FFFF) << 1) | (cat == 2 ? 1L : 0L);
            if (_props.TryGetValue(key, out var existing)) { existing.Seen = _generation; continue; }

            // Local layout: x across (centred), y up from the object's bottom row; faces the camera by yaw.
            var mesh = new ArtMesh();
            float cx = (minX + maxX) * 0.5f;
            foreach (int i in cells)
            {
                float lx = (i % w - cx) * cellMetres, ly = (i / w - minY) * cellMetres;
                var bl = new Vector3(lx - half, ly, 0); var br = new Vector3(lx + half, ly, 0);
                var tr = new Vector3(lx + half, ly + cellMetres, 0); var tl = new Vector3(lx - half, ly + cellMetres, 0);
                var o = new Vector3(0, 0, -cellMetres * 0.004f);
                if (cat == 1 && low[i] != null && !BackingTile(low[i], top[i])) mesh.Quad(bl, br, tr, tl, low[i]);
                if (top[i] != null && (cat == 2 || top[i] != low[i])) mesh.Quad(bl + o, br + o, tr + o, tl + o, top[i]);
            }
            var go = new GameObject("prop");
            go.transform.SetParent(_root, false);
            go.layer = RenderLayer.Index;
            var holder = new GameObject("mesh");
            holder.transform.SetParent(go.transform, false);
            holder.layer = RenderLayer.Index;
            mesh.Apply(holder.transform, mats, sortingLayer, cutout: true);
            var anchor = CoordinateMapper.ToWorld3D(new Vector2((x0 + cx + 0.5f) * cw, (y0 + minY) * ch));
            if (cat == 2)
            {
                int c0 = cells[0];
                anchor.y -= c.FaceBase[c0] >= 0 ? c.Height[c0] * cellMetres : pitDepth;
            }
            go.transform.position = anchor;
            _props[key] = new Prop { Go = go, Seen = _generation };
        }

        _stale.Clear();
        foreach (var kv in _props)
            if (kv.Value.Seen != _generation) _stale.Add(kv.Key);
        foreach (var k in _stale)
        {
            Destroy(_props[k].Go);
            _props.Remove(k);
        }
    }

    public void FaceCamera(float cameraYaw)
    {
        var rot = Quaternion.Euler(0f, cameraYaw, 0f);
        foreach (var p in _props.Values)
            if (p.Go != null && p.Go) p.Go.transform.rotation = rot;
    }

    // A solid tile under a cut-out (the floor or wall behind a statue) is background, not part of the
    // prop: standing it up would fill the cut-out's see-through parts with a square of that tile.
    static bool BackingTile(Sprite low, Sprite top) =>
        top != null && top != low && SpriteOpacity.IsOpaque(low) && !SpriteOpacity.IsOpaque(top);

    SpriteOpacity.Info Masks(int i, Sprite[] low, Sprite[] top)
    {
        var info = new SpriteOpacity.Info();
        if (_cat[i] == 1 && low[i] != null && !BackingTile(low[i], top[i])) info = SpriteOpacity.Get(low[i]);
        if (top[i] != null && (_cat[i] == 2 || top[i] != low[i]))
        {
            var t = SpriteOpacity.Get(top[i]);
            info.Top |= t.Top; info.Bottom |= t.Bottom; info.Left |= t.Left; info.Right |= t.Right;
        }
        return info;
    }

    int Find(int i)
    {
        while (_parent[i] != i) { _parent[i] = _parent[_parent[i]]; i = _parent[i]; }
        return i;
    }

    void Union(int a, int b)
    {
        a = Find(a); b = Find(b);
        if (a != b) _parent[b] = a;
    }

    static void Destroy(GameObject go)
    {
        if (go == null || !go) return;
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            if (mf.sharedMesh != null) Object.Destroy(mf.sharedMesh);
        Object.Destroy(go);
    }
}
