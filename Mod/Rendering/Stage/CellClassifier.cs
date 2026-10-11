using System;
using System.Collections.Generic;
using System.Text;

namespace VS3D.Rendering.Maps;

// Works out the 3D shape of a window of map cells from the 2D layers. Plain C# (ints and arrays
// only) so the same file runs in the offline Unity probe.
//
// VS draws in a 3/4 view: a building is its roof with its front face stacked below it, and the
// collision covers both. Per cell:
//  - Open: walkable.
//  - Raised: a wall. Regions that carry Walls-layer art are un-projected: the bottom rows of each
//    column (the facade) become the wall height and the roof art is stretched over the whole collision
//    area, so the building is exactly as big as where the player is blocked. The facade height is the
//    deepest split from the south edge at which facade and roof use different sprites.
//    Regions without Walls-layer art (e.g. Laborratory, where Walls is an invisible marker and the
//    face is drawn on the open side below) keep the legacy extrusion.
//  - Behind: (unused: buildings fill their collision.)
//  - Pit: player-only collision (PlayerWall) drawn as solid art, e.g. Dairy Plant's pools. Also
//    un-projected: the band along the pit's north edge is its far inner wall seen from above (its rows
//    become the depth and the wall art), the rows below are the floor, and a distinct south row is
//    the near rim at ground level (PitRim).
//  - LowProp: freestanding cut-out props (barrels, tanks, machines, also small cut-out clusters on the
//    Walls layer): camera-facing billboards.
// Props standing at the foot of a wall (cabinets, lockers, barrels) and cut-outs enclosed by wall
// (pipes down a facade) are part of the wall: their art is painted on its facade.
public sealed class CellClassifier
{
    public const byte Open = 0, Raised = 1, Pit = 2, LowProp = 3, Behind = 4, PitRim = 5;
    const float PitOpacity = 0.6f;    // share of solid tiles that makes a player-only area a pit
    const float SolidCoverage = 0.8f, MarkerCoverage = 0.08f;
    const int MaxPropCells = 24, MaxPropSpan = 7;   // largest cluster of Walls-layer cut-outs treated as props

    public byte[] Kind = Array.Empty<byte>();
    public bool[] Unprojected = Array.Empty<bool>();  // Raised footprint cell of an un-projected region
    // Un-projected walls: height in rows, the column's south edge cell (facade row 0), roof art cell.
    // Pits: depth in rows, the column's north edge cell (top of the inner wall band), floor art cell.
    public int[] Height = Array.Empty<int>();
    public int[] FaceBase = Array.Empty<int>();
    public int[] RoofSrc = Array.Empty<int>();
    public bool[] Fill = Array.Empty<bool>();         // solid without collision (unreachable space)
    public bool[] Foot = Array.Empty<bool>();         // prop at the foot of a wall, merged into it
    public int[] FootBase = Array.Empty<int>();       // un-projected cells: the column's lowest foot prop cell (-1 none)
    public int[] FootRows = Array.Empty<int>();       // foot prop rows painted over the bottom of the face

    public int MergedLow, Pits, LowProps, Regions, MergedBlocks;
    public readonly StringBuilder Heights = new();
    public readonly StringBuilder PitDepths = new();   // band rows per pit (r = has a rim row)

    int[] _comp = Array.Empty<int>(), _queue = Array.Empty<int>(), _mark = Array.Empty<int>(), _list = Array.Empty<int>();
    readonly Dictionary<int, int> _cF = new(), _cR = new();
    readonly List<int> _cells = new();
    readonly List<(int start, int len)> _runs = new();

    // hitWall/hitLow: Walls / PlayerWall collision. solid: any collision or unreachable.
    // low: lowest wall-art sprite id per cell (-1 none), lowFromWalls: it came from the Walls layer.
    public void Run(int w, int h, bool[] hitWall, bool[] hitLow, bool[] solid, int[] low, bool[] lowFromWalls,
                    Func<int, float> coverage, Func<int, ulong> edges, int defaultFaceRows, int maxFaceRows = 8, int fixedFaceRows = 0)
    {
        // Solid art: at least 80% of the tile visible (thin grout lines still count as solid).
        Func<int, bool> opaque = id => coverage(id) >= SolidCoverage;
        int n = w * h;
        if (Kind.Length < n)
        {
            Kind = new byte[n]; Unprojected = new bool[n]; Height = new int[n]; FaceBase = new int[n]; RoofSrc = new int[n];
            Fill = new bool[n]; _comp = new int[n]; _queue = new int[n];
            Foot = new bool[n]; FootBase = new int[n]; FootRows = new int[n];
        }
        MergedLow = Pits = LowProps = Regions = MergedBlocks = 0;
        Heights.Clear();
        PitDepths.Clear();
        for (int i = 0; i < n; i++)
        {
            Fill[i] = solid[i] && !hitWall[i] && !hitLow[i];
            Kind[i] = !solid[i] ? Open : hitWall[i] || Fill[i] ? Raised : LowProp;
            Unprojected[i] = false; Height[i] = 0; FaceBase[i] = -1; RoofSrc[i] = -1;
            Foot[i] = false; FootBase[i] = -1; FootRows[i] = 0;
            _comp[i] = 0;
        }

        // 0. Props drawn on the Walls layer (tanks, barrels, crates around a building): object-sized
        //    drawn objects (cut-out cells whose pixels continue across the tile edges). They stay wall (painted on it) when they are trim between solid
        //    wall rows, mostly enclosed by wall (pipes running down a facade), or at the foot of a wall.
        int pid = 0;
        for (int start = 0; start < n; start++)
        {
            if (!CutoutWall(start, low, lowFromWalls, coverage) || _comp[start] != 0) continue;
            int comp = --pid;   // negative ids: no clash with the ids below
            int count = FloodJoined(start, w, h, comp, low, edges, c => CutoutWall(c, low, lowFromWalls, coverage));
            int sandwiched = 0, foot = 0, touchWall = 0, boundary = 0, minX = w, maxX = 0, minY = h, maxY = 0;
            for (int k = 0; k < count; k++)
            {
                int i = _queue[k], x = i % w, y = i / w;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                bool south = y > 0 && SolidWall(i - w, low, lowFromWalls, opaque);
                bool north = y + 1 < h && SolidWall(i + w, low, lowFromWalls, opaque);
                if (south && north) sandwiched++;
                if (AtSolidWallFoot(i, w, h, comp, low, lowFromWalls, opaque)) foot++;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int j = ny * w + nx;
                    if (_comp[j] == comp) continue;
                    boundary++;
                    if (SolidWall(j, low, lowFromWalls, opaque)) touchWall++;
                }
            }
            // Object-sized clusters only (a large cut-out area is backdrop, e.g. the void around Gallo Tower).
            bool objectSized = count <= MaxPropCells && maxX - minX < MaxPropSpan && maxY - minY < MaxPropSpan;
            bool partOfWall = sandwiched * 2 >= count || (boundary > 0 && touchWall * 2 >= boundary) || foot * 2 >= count;
            if (objectSized && !partOfWall)
                for (int k = 0; k < count; k++) Kind[_queue[k]] = LowProp;
            else if (objectSized && foot * 2 >= count && sandwiched * 2 < count)
                for (int k = 0; k < count; k++) Foot[_queue[k]] = true;
        }
        for (int i = 0; i < n; i++) _comp[i] = 0;

        // 1. Player-only components (PlayerWall collision only).
        //    - Cells at the foot of a wall (cabinets, lockers, barrels against it): part of the wall,
        //      their art painted on its facade.
        //    - The rest, piece by piece: solid art enclosed by wall is wall, other solid art a pit,
        //      cut-out art (freestanding barrels, tanks, machines) billboard props.
        if (_mark.Length < n) { _mark = new int[n]; _list = new int[n]; }
        for (int i = 0; i < n; i++) _mark[i] = 0;
        int id = 0;
        for (int start = 0; start < n; start++)
        {
            if (Kind[start] != LowProp || !hitLow[start] || hitWall[start] || _comp[start] != 0) continue;
            int comp = ++id;
            int count = Flood(start, w, h, comp, c => Kind[c] == LowProp && hitLow[c] && !hitWall[c]);
            Array.Copy(_queue, _list, count);
            for (int k = 0; k < count; k++) _mark[_list[k]] = comp;
            int footCells = 0;
            for (int k = 0; k < count; k++)
                if (AtWallFoot(_list[k], w, h, comp, low[_list[k]] < 0 || !opaque(low[_list[k]]))) { _mark[_list[k]] = -comp; footCells++; }
            if (footCells > 0) MergedLow++;
            for (int k = 0; k < count; k++)
            {
                int i = _list[k];
                if (_mark[i] == -comp) { Kind[i] = Raised; Foot[i] = true; }
                else _comp[i] = 0;
            }
            for (int k = 0; k < count; k++)
            {
                int s0 = _list[k];
                if (Kind[s0] != LowProp || _comp[s0] != 0) continue;
                int sub = ++id;
                int c2 = Flood(s0, w, h, sub, c => Kind[c] == LowProp && _mark[c] == comp);
                Measure(c2, w, h, sub, low, opaque, out int touchWall, out int boundary, out int opaqueCells);
                bool solidArt = opaqueCells >= c2 * PitOpacity;
                byte kind;
                if (solidArt && boundary > 0 && touchWall * 2 >= boundary) { kind = Raised; MergedLow++; }
                else if (solidArt && c2 >= 4) { kind = Pit; Pits++; }
                else { kind = LowProp; LowProps++; }
                for (int q = 0; q < c2; q++) Kind[_queue[q]] = kind;
            }
        }

        UnprojectPits(w, h, low);
        MergeDetachedBlocks(w, h, low, lowFromWalls);

        // 2. Raised regions with Walls-layer art: un-project.
        for (int i = 0; i < n; i++) _comp[i] = 0;
        id = 0;
        for (int start = 0; start < n; start++)
        {
            if (Kind[start] != Raised || Fill[start] || _comp[start] != 0) continue;
            id++;
            int rid = id;
            int count = Flood(start, w, h, id, c => Kind[c] == Raised && !Fill[c]);
            bool walls = false;
            _cells.Clear();
            for (int k = 0; k < count; k++)
            {
                _cells.Add(_queue[k]);
                if (lowFromWalls[_queue[k]]) walls = true;
            }
            if (!walls) continue;
            Regions++;

            // Vertical runs (columns) of the region, south to north. Props merged in at the foot of the
            // wall (Foot) are skipped for the facade art: it is measured from the wall line, and the
            // props are painted over the bottom of the face.
            _runs.Clear();
            int maxLen = 0;
            foreach (int c in _cells)
            {
                int y = c / w;
                if (y > 0 && _comp[c - w] == rid) continue;
                int len = 1;
                while (y + len < h && _comp[c + len * w] == rid) len++;
                _runs.Add((c, len));
                int f = FootRowsOf(c, len, w);
                maxLen = Math.Max(maxLen, len - f);
            }

            // Deepest facade/roof split with (almost) no sprite in common.
            int best = 0, kmax = Math.Min(maxFaceRows, maxLen - 1);
            for (int k = 1; k <= kmax; k++)
            {
                if (ArtOverlap(w, low, k, out bool roofEmpty) <= count * 0.05f) best = k;
                if (roofEmpty) break;
            }
            // Distinct rows stacked on a repeating facade body (planks, columns) are the roof's edge
            // (ledges, cornices, pipes along the top): they belong to the roof, not the wall.
            if (best >= 2) best = TrimLedges(w, low, best);
            if (best == 0) best = Math.Max(1, Math.Min(defaultFaceRows, maxLen - 1));
            if (fixedFaceRows > 0) best = Math.Max(1, Math.Min(fixedFaceRows, maxLen - 1));
            if (Heights.Length < 200) Heights.Append(best).Append(' ');

            foreach (var (s0, len) in _runs)
            {
                int f = FootRowsOf(s0, len, w);
                int artBase = s0 + f * w, artLen = len - f;
                int hr = artLen > best ? best : Math.Max(1, artLen - 1);
                // The building fills its whole collision area: the roof art (the rows above the facade)
                // is stretched over the full depth so the 3D shape matches where the player is blocked.
                int roofRows = artLen - hr;
                for (int d = 0; d < len; d++)
                {
                    int c = s0 + d * w;
                    Unprojected[c] = true;
                    Height[c] = hr;
                    FaceBase[c] = artLen > 0 ? artBase : s0;
                    FootBase[c] = f > 0 ? s0 : -1;
                    FootRows[c] = f;
                    RoofSrc[c] = roofRows > 0 ? artBase + (hr + Math.Min(roofRows - 1, d * roofRows / len)) * w : c;
                }
            }
        }

        // Closed slots of merged blocks have no facade art of their own: use the neighbouring column's.
        foreach (var (c, dx) in _borrowFace)
            if (Unprojected[c] && Unprojected[c + dx] && FaceBase[c + dx] >= 0)
            {
                FaceBase[c] = FaceBase[c + dx];
                Height[c] = Height[c + dx];
            }
    }

    // Small free-standing wall blocks (altars, daises): the game's collision often leaves 1-tile slots
    // between their columns and none under the top surface drawn behind them, which would stand up
    // separate pillars. Close the slots and extend the block north over wall art without collision,
    // so the block is one solid platform; un-projection then splits it into facade and roof.
    const int MaxBlockW = 10, MaxBlockH = 5, MaxBlockTopRows = 4;
    readonly List<(int cell, int dx)> _borrowFace = new();

    void MergeDetachedBlocks(int w, int h, int[] low, bool[] lowFromWalls)
    {
        _borrowFace.Clear();
        int n = w * h;
        for (int i = 0; i < n; i++) _comp[i] = 0;
        bool Wall(int c) => Kind[c] == Raised && !Fill[c];
        int id = 0;
        for (int start = 0; start < n; start++)
        {
            if (!Wall(start) || _comp[start] != 0) continue;
            int bid = ++id;
            // Wall cells joined 4-way, or across a single open cell in a row.
            int head = 0, tail = 0;
            _comp[start] = bid; _queue[tail++] = start;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            while (head < tail)
            {
                int c = _queue[head++], x = c % w, y = c / w;
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                void Try(int j) { if (_comp[j] == 0 && Wall(j)) { _comp[j] = bid; _queue[tail++] = j; } }
                if (x + 1 < w) Try(c + 1);
                if (x > 0) Try(c - 1);
                if (y + 1 < h) Try(c + w);
                if (y > 0) Try(c - w);
                if (x + 2 < w && Kind[c + 1] == Open) Try(c + 2);
                if (x > 1 && Kind[c - 1] == Open) Try(c - 2);
            }
            int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            if (bw > MaxBlockW || bh > MaxBlockH || bw < 2) continue;
            if (x0 == 0 || y0 == 0 || x1 == w - 1 || y1 == h - 1) continue;   // may continue past the window

            // Free-standing: nothing solid in the ring around the box, and something to close.
            bool alone = true, gaps = false;
            for (int y = y0 - 1; y <= y1 + 1 && alone; y++)
                for (int x = x0 - 1; x <= x1 + 1; x++)
                {
                    int c = y * w + x;
                    bool inBox = x >= x0 && x <= x1 && y >= y0 && y <= y1;
                    if (inBox)
                    {
                        if (Kind[c] == Open)
                        {
                            // Only 1-tile-wide slots: open floor wider than that (a nook the player
                            // walks into, like the library's piano corner) is not part of a block.
                            if ((x > x0 && Kind[c - 1] == Open) || (x < x1 && Kind[c + 1] == Open)) { alone = false; break; }
                            gaps = true;
                        }
                        else if (_comp[c] != bid) { alone = false; break; }
                    }
                    else if (Kind[c] != Open) { alone = false; break; }
                }
            if (!alone || !gaps) continue;

            // Each column with a slot borrows its facade from the nearest complete column.
            int[] src = new int[bw];
            bool ok = true;
            for (int x = x0; x <= x1 && ok; x++)
            {
                src[x - x0] = 0;
                if (Complete(x)) continue;
                int dx = 0;
                for (int d = 1; d < bw && dx == 0; d++)
                {
                    if (x - d >= x0 && Complete(x - d)) dx = -d;
                    else if (x + d <= x1 && Complete(x + d)) dx = d;
                }
                if (dx == 0) ok = false;
                src[x - x0] = dx;
            }
            if (!ok) continue;
            bool Complete(int x)
            {
                for (int y = y0; y <= y1; y++) if (Kind[y * w + x] == Open) return false;
                return true;
            }

            // The top surface drawn north of the block (wall art without collision).
            int top = 0;
            while (top < MaxBlockTopRows && y1 + top + 2 < h)
            {
                int y = y1 + top + 1;
                bool row = true;
                for (int x = x0; x <= x1 && row; x++)
                {
                    int c = y * w + x;
                    row = Kind[c] == Open && low[c] >= 0;
                }
                if (!row) break;
                top++;
            }

            for (int y = y0; y <= y1 + top; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int c = y * w + x;
                    if (Kind[c] == Open)
                    {
                        Kind[c] = Raised;
                        Fill[c] = false;
                        _comp[c] = bid;
                    }
                    if (src[x - x0] != 0) _borrowFace.Add((c, src[x - x0]));
                }
            MergedBlocks++;
        }
        for (int i = 0; i < n; i++) _comp[i] = 0;
    }

    // Leading Foot cells (at most two) of a run, keeping at least one wall row.
    int FootRowsOf(int s0, int len, int w)
    {
        int f = 0;
        while (f < 2 && f < len - 1 && Foot[s0 + f * w]) f++;
        return f;
    }

    // Shared sprites between facade rows (d < k from the wall line) and roof rows of the runs in _runs.
    int ArtOverlap(int w, int[] low, int k, out bool roofEmpty)
    {
        _cF.Clear(); _cR.Clear();
        foreach (var (s0, len) in _runs)
        {
            int f = FootRowsOf(s0, len, w);
            for (int d = 0; d < len - f; d++)
            {
                int s = low[s0 + (f + d) * w];
                if (s < 0) continue;
                var dict = d < k ? _cF : _cR;
                dict[s] = dict.TryGetValue(s, out var v) ? v + 1 : 1;
            }
        }
        roofEmpty = _cR.Count == 0;
        int overlap = 0;
        foreach (var kv in _cF)
            if (_cR.TryGetValue(kv.Key, out var r)) overlap += Math.Min(kv.Value, r);
        return roofEmpty ? int.MaxValue : overlap;
    }

    readonly List<HashSet<int>> _rowSets = new();

    int TrimLedges(int w, int[] low, int rows)
    {
        while (_rowSets.Count < rows) _rowSets.Add(new HashSet<int>());
        for (int d = 0; d < rows; d++) _rowSets[d].Clear();
        foreach (var (s0, len) in _runs)
        {
            int f = FootRowsOf(s0, len, w);
            for (int d = 0; d < rows && d < len - f; d++)
            {
                int sp = low[s0 + (f + d) * w];
                if (sp >= 0) _rowSets[d].Add(sp);
            }
        }
        while (rows >= 2)
        {
            int top = rows - 1;
            bool distinct = true, body = false;
            for (int d = 0; d < top && distinct; d++)
                if (_rowSets[d].Overlaps(_rowSets[top])) distinct = false;
            for (int d = 0; d < top && !body; d++)
                for (int e = d + 1; e < top && !body; e++)
                    if (_rowSets[d].Overlaps(_rowSets[e])) body = true;
            if (!distinct || !body) break;
            rows--;
        }
        return rows;
    }

    // Pits in 3/4 view: north band (inner wall) rows, floor rows, and an optional south rim row.
    void UnprojectPits(int w, int h, int[] low)
    {
        int n = w * h;
        for (int i = 0; i < n; i++) _comp[i] = 0;
        int id = 0;
        for (int start = 0; start < n; start++)
        {
            if (Kind[start] != Pit || _comp[start] != 0) continue;
            int pit = ++id;
            int count = Flood(start, w, h, pit, c => Kind[c] == Pit);
            _runs.Clear();
            int maxLen = 0;
            for (int k = 0; k < count; k++)
            {
                int c = _queue[k], y = c / w;
                if (y > 0 && _comp[c - w] == pit) continue;
                int len = 1;
                while (y + len < h && _comp[c + len * w] == pit) len++;
                _runs.Add((c, len));
                maxLen = Math.Max(maxLen, len);
            }

            // Rim: a south row whose sprites appear nowhere else in the pit (pits of 3+ rows).
            bool rim = maxLen >= 3 &&
                       Overlap(w, low, (d, len) => d == 0, (d, len) => d >= 1) <= count * 0.05f;
            int r0 = rim ? 1 : 0;
            // Band: deepest split from the north edge whose sprites don't appear further south.
            int band = 0;
            for (int k = 1; k <= Math.Min(3, maxLen - 1 - r0); k++)
                if (Overlap(w, low, (d, len) => d >= r0 && len - 1 - d < k, (d, len) => d >= r0 && len - 1 - d >= k) <= count * 0.05f) band = k;

            if (PitDepths.Length < 200) PitDepths.Append(band).Append(rim ? "r " : " ");
            foreach (var (s0, len) in _runs)
            {
                int top = s0 + (len - 1) * w;
                for (int d = 0; d < len; d++)
                {
                    int c = s0 + d * w, fromTop = len - 1 - d;
                    if (rim && d == 0 && len > band + 1) { Kind[c] = PitRim; continue; }
                    Height[c] = Math.Max(1, band);
                    FaceBase[c] = band > 0 ? top : -1;
                    // Under the band the floor continues with the first floor row's art.
                    int floorRow = len - 1 - band;
                    RoofSrc[c] = fromTop < band && floorRow >= (rim ? 1 : 0) ? s0 + floorRow * w : c;
                }
            }
        }
    }

    // Shared sprites (count) between two row sets of the runs in _runs (d = rows from the south edge).
    int Overlap(int w, int[] low, Func<int, int, bool> setA, Func<int, int, bool> setB)
    {
        _cF.Clear(); _cR.Clear();
        foreach (var (s0, len) in _runs)
            for (int d = 0; d < len; d++)
            {
                int s = low[s0 + d * w];
                if (s < 0) continue;
                if (setA(d, len)) _cF[s] = _cF.TryGetValue(s, out var v) ? v + 1 : 1;
                else if (setB(d, len)) _cR[s] = _cR.TryGetValue(s, out var r) ? r + 1 : 1;
            }
        if (_cF.Count == 0) return int.MaxValue;
        int overlap = 0;
        foreach (var kv in _cF)
            if (_cR.TryGetValue(kv.Key, out var r)) overlap += Math.Min(kv.Value, r);
        return overlap;
    }

    // A wall (not fill) directly north of the cell, or two rows up through the same component for a
    // cut-out cell (the lower half of a tall prop). Solid art only counts when right against the wall.
    bool AtWallFoot(int i, int w, int h, int comp, bool cutout)
    {
        int y = i / w;
        for (int up = 1; up <= (cutout ? 2 : 1) && y + up < h; up++)
        {
            int j = i + up * w;
            if (Kind[j] == Raised && !Fill[j] && _comp[j] != comp) return true;
            if (_comp[j] != comp) return false;
        }
        return false;
    }

    // Solid Walls-layer art directly north (two rows up through the component): props stacked on
    // other cut-outs (a tank on a tank) don't count as standing against a wall.
    bool AtSolidWallFoot(int i, int w, int h, int comp, int[] low, bool[] fromWalls, Func<int, bool> opaque)
    {
        int y = i / w;
        for (int up = 1; up <= 2 && y + up < h; up++)
        {
            int j = i + up * w;
            if (_comp[j] != comp) return SolidWall(j, low, fromWalls, opaque);
        }
        return false;
    }

    // A cut-out drawing on the Walls layer: partly visible. Nearly empty tiles are collision markers.
    bool CutoutWall(int i, int[] low, bool[] fromWalls, Func<int, float> coverage)
    {
        if (Kind[i] != Raised || Fill[i] || !fromWalls[i] || low[i] < 0) return false;
        float c = coverage(low[i]);
        return c >= MarkerCoverage && c < SolidCoverage;
    }

    bool SolidWall(int i, int[] low, bool[] fromWalls, Func<int, bool> opaque) =>
        Kind[i] == Raised && !Fill[i] && fromWalls[i] && low[i] >= 0 && opaque(low[i]);

    // Boundary contact and opacity of the component in _queue[0..count) (cells with _comp == id).
    void Measure(int count, int w, int h, int id, int[] low, Func<int, bool> opaque, out int touchWall, out int boundary, out int opaqueCells)
    {
        touchWall = boundary = opaqueCells = 0;
        for (int k = 0; k < count; k++)
        {
            int i = _queue[k], x = i % w, y = i / w;
            if (low[i] >= 0 && opaque(low[i])) opaqueCells++;
            for (int d = 0; d < 4; d++)
            {
                int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int j = ny * w + nx;
                if (_comp[j] == id) continue;
                boundary++;
                if (Kind[j] == Raised && !Fill[j]) touchWall++;
            }
        }
    }

    // Like Flood, but only across tile edges where both sprites have visible pixels at the same spot
    // (the drawing continues): separates objects that merely touch. edges(id) packs the 16-bit masks
    // top | bottom << 16 | left << 32 | right << 48.
    int FloodJoined(int start, int w, int h, int id, int[] low, Func<int, ulong> edges, Func<int, bool> inside)
    {
        int head = 0, tail = 0;
        _comp[start] = id;
        _queue[tail++] = start;
        while (head < tail)
        {
            int i = _queue[head++], x = i % w, y = i / w;
            ulong e = low[i] >= 0 ? edges(low[i]) : 0;
            for (int d = 0; d < 4; d++)
            {
                int j;
                int mine, theirs;   // bit offsets of the touching edges
                if (d == 0) { if (x + 1 >= w) continue; j = i + 1; mine = 48; theirs = 32; }
                else if (d == 1) { if (x == 0) continue; j = i - 1; mine = 32; theirs = 48; }
                else if (d == 2) { if (y + 1 >= h) continue; j = i + w; mine = 0; theirs = 16; }
                else { if (y == 0) continue; j = i - w; mine = 16; theirs = 0; }
                if (_comp[j] != 0 || !inside(j) || low[j] < 0) continue;
                ulong a = (e >> mine) & 0xFFFF, b = (edges(low[j]) >> theirs) & 0xFFFF;
                if ((a & b) == 0) continue;
                _comp[j] = id;
                _queue[tail++] = j;
            }
        }
        return tail;
    }

    // 4-connected flood from start over cells passing `inside`; fills _queue[0..count) and _comp = id.
    int Flood(int start, int w, int h, int id, Func<int, bool> inside)
    {
        int head = 0, tail = 0;
        _comp[start] = id;
        _queue[tail++] = start;
        while (head < tail)
        {
            int i = _queue[head++], x = i % w, y = i / w;
            if (x + 1 < w && _comp[i + 1] == 0 && inside(i + 1)) { _comp[i + 1] = id; _queue[tail++] = i + 1; }
            if (x > 0 && _comp[i - 1] == 0 && inside(i - 1)) { _comp[i - 1] = id; _queue[tail++] = i - 1; }
            if (y + 1 < h && _comp[i + w] == 0 && inside(i + w)) { _comp[i + w] = id; _queue[tail++] = i + w; }
            if (y > 0 && _comp[i - w] == 0 && inside(i - w)) { _comp[i - w] = id; _queue[tail++] = i - w; }
        }
        return tail;
    }

    // ASCII view of the classification ('@' at the player cell), north at the top.
    public string Dump(int w, int h, int px, int py)
    {
        var sb = new StringBuilder();
        sb.AppendLine(". open  # wall (un-projected, height digit below)  L legacy wall  f unreachable fill  b behind  o pit  r pit rim  p low prop  @ player");
        for (int y = h - 1; y >= 0; y--)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                char c = x == px && y == py ? '@' : Kind[i] switch
                {
                    Open => '.',
                    Raised => Fill[i] ? 'f' : Unprojected[i] ? '#' : 'L',
                    Pit => 'o',
                    PitRim => 'r',
                    LowProp => 'p',
                    _ => 'b',
                };
                sb.Append(c);
            }
            sb.AppendLine();
        }
        sb.AppendLine();
        for (int y = h - 1; y >= 0; y--)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                sb.Append(Unprojected[i] ? (char)('0' + Math.Min(9, Height[i])) : Kind[i] == Open ? '.' : ' ');
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
