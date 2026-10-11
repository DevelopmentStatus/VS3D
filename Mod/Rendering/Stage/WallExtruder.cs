using UnityEngine;
using VS3D.SimulationBridge;

namespace VS3D.Rendering.Maps;

// Builds the 3D walls, pits and props of a window of map cells from CellClassifier's result and the
// stage's own wall art. Every art quad is layered: the wall's own sprite (lowest wall-art layer),
// with decoration from higher layers (pipes, signs) drawn just in front of it. Cut-out sprites on a
// facade (barrels, machines at the foot of a wall) get the nearest solid wall sprite above them as a
// backing, so they read as painted on the wall.
//  - Un-projected walls: roof at the wall height with the roof art; faces show the column's facade.
//  - Legacy walls (no Walls-layer art, e.g. Laborratory): faces at the collision edge, facade from the
//    front art drawn on the open side below, tops from the cell's art.
//  - Pits: art at the bottom of a pit with inner walls; the ground plane gets a hole there.
//  - Low props: not drawn here; PropBillboards turns them into camera-facing billboards.
// Rebuilt as a few meshes (one per atlas) whenever the window moves.
public sealed class WallExtruder
{
    readonly ArtMesh _mesh = new();
    Transform _root;

    // Per-rebuild state.
    CellClassifier _c;
    Sprite[] _low, _top, _front;
    int _w, _h, _x0, _y0;
    float _cw, _ch, _cell, _lift;

    public void Begin(Transform worldRoot)
    {
        End();
        var go = new GameObject("VS3D_ExtrudedWalls");
        go.transform.SetParent(worldRoot, false);
        RenderLayer.Apply(go);
        _root = go.transform;
    }

    public void End()
    {
        if (_root != null && _root) Object.Destroy(_root.gameObject);
        _root = null;
    }

    public void Rebuild(CellClassifier c, Sprite[] low, Sprite[] top, Sprite[] front, int x0, int y0, int w, int h, float cw, float ch,
                        float cellMetres, int faceRows, float pitDepth, ArtMaterials mats, int sortingLayer)
    {
        if (_root == null) return;
        _mesh.Clear();
        _c = c; _low = low; _top = top; _front = front;
        _w = w; _h = h; _x0 = x0; _y0 = y0; _cw = cw; _ch = ch; _cell = cellMetres;
        _lift = cellMetres * 0.004f;
        float half = cellMetres * 0.5f;
        var up = Vector3.up;

        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                byte k = c.Kind[i];
                if (k == CellClassifier.Open || k == CellClassifier.Behind) continue;
                var ctr = C(x, y);
                if (k == CellClassifier.Pit) { BuildPit(x, y, ctr, half, pitDepth); continue; }
                if (k == CellClassifier.PitRim) { BuildRim(i, ctr, half); continue; }
                if (k == CellClassifier.LowProp) continue;   // billboards (PropBillboards)
                if (c.Unprojected[i]) BuildUnprojected(x, y, i, ctr, half);
                else BuildLegacy(x, y, i, ctr, half, Mathf.Max(1, faceRows) * cellMetres, faceRows);
            }
        _mesh.Apply(_root, mats, sortingLayer);
    }

    Vector3 C(int x, int y) => CoordinateMapper.ToWorld3D(new Vector2((_x0 + x + 0.5f) * _cw, (_y0 + y + 0.5f) * _ch));

    // Un-projected wall cell: roof at height H from the roof art; faces from the column's facade rows.
    void BuildUnprojected(int x, int y, int i, Vector3 ctr, float half)
    {
        int H = _c.Height[i];
        float height = H * _cell;
        var up = Vector3.up;
        var t = ctr + up * height;
        Layered(t + new Vector3(-half, 0, -half), t + new Vector3(half, 0, -half),
                t + new Vector3(half, 0, half), t + new Vector3(-half, 0, half), up, _c.RoofSrc[i], -1);

        int faceBase = _c.FaceBase[i];
        for (int f = 0; f < 4; f++)
        {
            int fx = f == 2 ? -1 : f == 3 ? 1 : 0, fy = f == 0 ? -1 : f == 1 ? 1 : 0;
            int nx = x + fx, ny = y + fy;
            if (nx < 0 || ny < 0 || nx >= _w || ny >= _h) continue;
            int j = ny * _w + nx;
            int from = 0;
            if (_c.Kind[j] == CellClassifier.Raised)
            {
                if (!_c.Unprojected[j]) continue;          // legacy wall or fill: solid
                from = _c.Height[j];                        // step between buildings of different height
                if (from >= H) continue;
            }
            var n = new Vector3(fx, 0f, fy);
            var right = Vector3.Cross(n, up);
            var e = ctr + n * half;
            var a = e - right * half; var b = e + right * half;
            int footBase = _c.FootBase[i], footRows = _c.FootRows[i];
            for (int r = from; r < H; r++)
            {
                int src = faceBase + r * _w;
                if (src < 0 || src >= _c.Kind.Length) continue;
                var lo = up * (r * _cell); var hi = up * ((r + 1) * _cell);
                Layered(a + lo, b + lo, b + hi, a + hi, n, src, Backing(src, H - r));
                // Props at the foot of the wall (barrels, crates, lockers): painted over the bottom rows.
                if (footBase >= 0 && r < footRows)
                {
                    int fc = footBase + r * _w;
                    var o = n * (_lift * 3f);
                    var fs = _low[fc]; var ft = _top[fc];
                    if (fs != null) _mesh.Quad(a + lo + o, b + lo + o, b + hi + o, a + hi + o, fs);
                    if (ft != null && ft != fs) _mesh.Quad(a + lo + o * 1.5f, b + lo + o * 1.5f, b + hi + o * 1.5f, a + hi + o * 1.5f, ft);
                }
            }
        }
    }

    // Legacy extrusion (no Walls-layer art): faces at the collision edge.
    void BuildLegacy(int x, int y, int i, Vector3 ctr, float half, float height, int faceRows)
    {
        var up = Vector3.up;
        bool Solid(int xx, int yy) => xx < 0 || yy < 0 || xx >= _w || yy >= _h || _c.Kind[yy * _w + xx] == CellClassifier.Raised;
        Sprite ArtAt(int xx, int yy) => xx < 0 || yy < 0 || xx >= _w || yy >= _h ? null : _top[yy * _w + xx];
        Sprite FrontAt(int xx, int yy) => xx < 0 || yy < 0 || xx >= _w || yy >= _h ? null : _front[yy * _w + xx];
        bool OpenAt(int xx, int yy) => xx >= 0 && yy >= 0 && xx < _w && yy < _h && _c.Kind[yy * _w + xx] == CellClassifier.Open;
        var own = _top[i];
        if (own != null)
        {
            var t = ctr + up * height;
            _mesh.Quad(t + new Vector3(-half, 0, -half), t + new Vector3(half, 0, -half),
                       t + new Vector3(half, 0, half), t + new Vector3(-half, 0, half), own);
        }
        var column = new Sprite[Mathf.Max(1, faceRows)];
        for (int f = 0; f < 4; f++)
        {
            int fx = f == 2 ? -1 : f == 3 ? 1 : 0, fy = f == 0 ? -1 : f == 1 ? 1 : 0;
            if (Solid(x + fx, y + fy)) continue;
            var n = new Vector3(fx, 0f, fy);
            var right = Vector3.Cross(n, up);
            var e = ctr + n * half;
            var a = e - right * half; var b = e + right * half;
            int count = 0;
            if (fy == -1)
            {
                // Facade drawn on the open side below the wall: nearest row is the top of the face.
                int r0 = 0;
                while (r0 < column.Length && FrontAt(x, y - 1 - r0) != null && OpenAt(x, y - 1 - r0)) r0++;
                for (int r = 0; r < r0; r++) column[r] = FrontAt(x, y - r0 + r);
                count = r0;
            }
            if (count == 0)
            {
                for (int r = 0; r < column.Length; r++)
                {
                    var s = ArtAt(x - fx * r, y - fy * r);
                    column[r] = s ?? (r > 0 ? column[r - 1] : own);
                }
                count = column.Length;
            }
            float rowH = height / count;
            for (int r = 0; r < count; r++)
            {
                if (column[r] == null) continue;
                var lo = up * (r * rowH); var hi = up * ((r + 1) * rowH);
                _mesh.Quad(a + lo, b + lo, b + hi, a + hi, column[r]);
            }
        }
    }

    // Pit cell, un-projected: floor at the band depth with the floor art; inner walls where the pit
    // meets anything else, showing the band art (bottom row = band row nearest the floor).
    void BuildPit(int x, int y, Vector3 ctr, float half, float fallbackDepth)
    {
        int i = y * _w + x;
        var up = Vector3.up;
        int rows = _c.Height[i];
        float depth = _c.FaceBase[i] >= 0 ? rows * _cell : fallbackDepth;
        int src = _c.RoofSrc[i] >= 0 ? _c.RoofSrc[i] : i;
        var f = ctr - up * depth;
        // Solid art only: cut-out decoration in the pit stands up as a billboard (PropBillboards).
        var floor = _low[src] ?? _top[src];
        if (floor != null)
            _mesh.Quad(f + new Vector3(-half, 0, -half), f + new Vector3(half, 0, -half),
                       f + new Vector3(half, 0, half), f + new Vector3(-half, 0, half), floor);
        var deco = _top[src];
        if (deco != null && deco != floor && SpriteOpacity.IsOpaque(deco))
        {
            var o = up * _lift;
            _mesh.Quad(f + o + new Vector3(-half, 0, -half), f + o + new Vector3(half, 0, -half),
                       f + o + new Vector3(half, 0, half), f + o + new Vector3(-half, 0, half), deco);
        }
        for (int k = 0; k < 4; k++)
        {
            int fx = k == 2 ? -1 : k == 3 ? 1 : 0, fy = k == 0 ? -1 : k == 1 ? 1 : 0;
            int nx = x + fx, ny = y + fy;
            if (nx >= 0 && ny >= 0 && nx < _w && ny < _h && _c.Kind[ny * _w + nx] == CellClassifier.Pit) continue;
            // The wall stands on the pit's edge and faces back into the pit.
            var n = new Vector3(-fx, 0f, -fy);
            var right = Vector3.Cross(n, up);
            var e = ctr - n * half;
            var a = e - right * half; var b = e + right * half;
            int bandTop = _c.FaceBase[i];
            int count = bandTop >= 0 ? rows : 1;
            float rowH = depth / count;
            for (int r = 0; r < count; r++)
            {
                int cell = bandTop >= 0 ? bandTop - (count - 1 - r) * _w : src;
                var s = cell >= 0 && cell < _low.Length ? _low[cell] ?? _top[cell] : null;
                if (s == null) s = floor;
                if (s == null) continue;
                var lo = up * (-depth + r * rowH); var hi = up * (-depth + (r + 1) * rowH);
                _mesh.Quad(a + lo, b + lo, b + hi, a + hi, s);
            }
        }
    }

    // Near rim of a pit: flat at ground level with its art.
    void BuildRim(int i, Vector3 ctr, float half)
    {
        var up = Vector3.up;
        var t = ctr + up * _lift;
        Layered(t + new Vector3(-half, 0, -half), t + new Vector3(half, 0, -half),
                t + new Vector3(half, 0, half), t + new Vector3(-half, 0, half), up, i, -1);
    }

    // Nearest solid wall sprite above a facade cell (within the rows left in the face), as a backing
    // for cut-out art; -1 when the cell's own sprite is solid.
    int Backing(int cell, int rowsLeft)
    {
        var s = _low[cell];
        if (s == null || SpriteOpacity.IsOpaque(s)) return -1;
        for (int r = 1; r < Mathf.Max(2, rowsLeft + 1); r++)
        {
            int j = cell + r * _w;
            if (j >= _low.Length) break;
            if (_low[j] != null && SpriteOpacity.IsOpaque(_low[j])) return j;
        }
        return -1;
    }

    // Quad with the cell's wall sprite, its decoration just in front, and an optional backing behind.
    void Layered(Vector3 bl, Vector3 br, Vector3 tr, Vector3 tl, Vector3 normal, int cell, int backing)
    {
        if (cell < 0 || cell >= _low.Length) return;
        var lowS = _low[cell]; var topS = _top[cell];
        var o = normal * _lift;
        if (backing >= 0 && _low[backing] != null)
            _mesh.Quad(bl, br, tr, tl, _low[backing]);
        if (lowS != null)
        {
            var oo = backing >= 0 ? o : Vector3.zero;
            _mesh.Quad(bl + oo, br + oo, tr + oo, tl + oo, lowS);
        }
        if (topS != null && topS != lowS)
        {
            var oo = o * (backing >= 0 ? 2f : 1f);
            _mesh.Quad(bl + oo, br + oo, tr + oo, tl + oo, topS);
        }
    }

    public void SetVisible(bool v)
    {
        if (_root != null && _root) _root.gameObject.SetActive(v);
    }
}
