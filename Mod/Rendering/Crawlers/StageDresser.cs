using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Tilemaps;
using VampireSurvivors.Framework;
using VampireSurvivors.Objects;
using VS3D.Bootstrap;
using VS3D.Config;
using VS3D.Rendering.Maps;
using VS3D.SimulationBridge;

namespace VS3D.Rendering.Crawlers;

// Rebuilds the stage around the player from Vampire Crawlers' 3D kit pieces.
//
// Layout comes from the game's own collision tilemaps (authoritative): all tiling copies are merged
// into one world-aligned grid, open space the player can't reach counts as solid, and then:
//  - thick walls: their faces get Crawlers' authored room-wall sections (furniture in front of the wall
//    face is removed so nothing spills into walkable space);
//  - thin rows (obstacles you walk around): back-to-back freestanding bookcase runs fitted to the row;
//  - the inside of wall areas: solid boxes at the height of the pieces around them;
//  - floor: the kit's floor tile on a grid under the player.
//
// Streaming: everything is limited to the render distance around the player. New pieces are queued
// nearest-first and created within a small per-frame time budget; pieces that leave the radius are
// returned to a pool (excess pooled objects are destroyed gradually).
public sealed class StageDresser
{
    const int RebuildEvery = 15;           // frames
    const int WindowSnap = 8;              // cells; keeps run keys stable while walking
    const double SpawnBudgetMs = 2.0;      // per frame
    const int PoolKeep = 48;               // pooled spares kept per piece

    readonly CrawlersAssets _assets;
    readonly List<BiomeDef> _biomes;
    Transform _root;
    BiomeDef _biome;
    GameManager _game;
    int _sortingLayer;
    int _frame;
    float RenderDistance => Mathf.Max(20f, ModConfig.RenderDistance.Value);

    // ------------------------------------------------------------------ measurements

    sealed class Info
    {
        public Bounds All;          // enabled renderers, local, identity transform
        // wall modules
        public Vector3 Back, Along;
        public float Length, FrontOffset, AlongCenter, FloorY, Depth, Height;
        public Bounds Wall;
    }
    readonly Dictionary<string, Info> _info = new();

    // ------------------------------------------------------------------ placement state

    struct Want
    {
        public Vector3 Pos; public Quaternion Rot; public Vector3 Scale;
        public bool Occluder; public Bounds World; public float Dist2;
    }

    sealed class Placed
    {
        public string Path; public GameObject Go; public Want Want;
        public int Seen; public bool Hidden;
    }

    readonly Dictionary<long, Placed> _placed = new();
    readonly Dictionary<string, Stack<GameObject>> _pool = new();
    readonly List<long> _pending = new();
    readonly HashSet<long> _pendingSet = new();
    readonly Stopwatch _budget = new();
    int _generation;

    const string MassBox = "<mass-box>";
    Material _massMaterial;

    readonly List<Tilemap> _collision = new();
    readonly List<bool> _collisionLow = new();   // PlayerWall: blocks the player only (pits, machines)
    float _floorSize;
    readonly Dictionary<IntPtr, Material> _doubleSided = new();

    float _rowHeight = 3f, _rowDepth = 0.75f;
    readonly List<string> _rows = new();
    readonly List<string> _islandPieces = new();
    readonly List<string> _decor = new();

    public Color Background => _kit?.background is { Length: >= 3 } c ? new Color(c[0], c[1], c[2], 1f) : new Color(0.05f, 0.04f, 0.04f, 1f);

    // Per-map kit (stagekits_v1.json) and the stage's own layers.
    readonly List<StageKit> _kits = StageKits.Load();
    StageKit _kit;
    readonly StageLayers _layers = new();
    readonly GroundCapture _groundCap = new();
    readonly WallExtruder _extruder = new();
    readonly PropBillboards _props = new();
    readonly StageArt _art = new();
    readonly ObstacleProps _obstacles = new();
    ArtMaterials _artMats;
    bool _crawlersWalls, _extrude;
    int _extrudeKey = int.MinValue, _extrudeAge;
    bool[] _obsMask = Array.Empty<bool>();
    bool[] _hitWall = Array.Empty<bool>(), _hitLow = Array.Empty<bool>(), _holes = Array.Empty<bool>();
    readonly CellClassifier _classifier = new();
    int[] _lowIds = Array.Empty<int>();
    readonly Dictionary<IntPtr, int> _spriteIds = new();
    readonly List<Sprite> _spriteList = new();
    string _lastSummary = "";
    Vector2 _lastPlayer2D;
    int[] _lowComp = Array.Empty<int>();
    // Last merged-grid window (for obstacle facing).
    int _gx0, _gy0, _gw, _gh; float _gcw, _gch; bool _gridValid;

    public bool Active { get; private set; }
    public bool HasFloor => _floorSize > 0f || _groundCap.Active;
    public string BiomeName => _biome?.bundle ?? "-";
    public int PlacedCount => _placed.Count;
    public int PendingCount => _pending.Count;

    public StageDresser(CrawlersAssets assets)
    {
        _assets = assets;
        _biomes = BiomeConfig.Load();
    }

    // ------------------------------------------------------------------ lifecycle

    public bool Begin(Transform worldRoot, GameManager game, Stage stage, int sortingLayer)
    {
        End();
        _game = game;
        _sortingLayer = sortingLayer;
        string stageType = stage != null ? stage.StageType.ToString() : "FOREST";
        _layers.Collect(stage);
        _kit = StageKits.Pick(_kits, _layers.MapName, stageType);
        if (_assets == null)
            _kit = StageKits.VsOnly(_kit);   // no Vampire Crawlers assets: the map's own art only
        bool crawlersGround = _kit.ground == "crawlers", crawlersWalls = _kit.walls == "crawlers";
        _biome = (crawlersGround || crawlersWalls) && !string.IsNullOrEmpty(_kit.crawlersBiome)
            ? _biomes.Find(b => string.Equals(b.bundle, _kit.crawlersBiome, StringComparison.OrdinalIgnoreCase)) : null;
        if (_biome != null && !_assets.LoadBiome(_biome.bundle))
            _biome = null;
        _crawlersWalls = crawlersWalls && _biome != null;

        var go = new GameObject("VS3D_CrawlersStage");
        go.transform.SetParent(worldRoot, false);
        RenderLayer.Apply(go);
        _root = go.transform;

        CollectCollisionTilemaps(stage);

        var unlit = Shader.Find("Universal Render Pipeline/Unlit");
        var template = unlit != null ? new Material(unlit) : null;
        // Cut-outs use the game's own sprite material (what the player is drawn with): the build
        // strips URP Unlit's alpha-clip variant, so clipped Unlit draws the see-through texels.
        var playerRend = game != null && game.PlayerOne != null ? game.PlayerOne.Rend : null;
        var spriteMat = playerRend != null ? playerRend.sharedMaterial : null;
        _artMats = template != null ? new ArtMaterials(template, spriteMat) : null;
        if (template == null)
            Plugin.Log.LogWarning("Stage: URP Unlit shader not found; VS ground/wall art disabled");

        if (_biome != null)
        {
            // Measure the Crawlers kit up front (a handful of instances).
            if (crawlersGround && !string.IsNullOrEmpty(_biome.floor) && _assets.GetPrefab(_biome.floor) != null)
            {
                var fb = Measure(_biome.floor).All;
                _floorSize = Mathf.Max(1f, Mathf.Max(fb.size.x, fb.size.z));
            }
            if (_crawlersWalls)
            {
                if (!string.IsNullOrEmpty(_biome.wallModule) && _assets.GetPrefab(_biome.wallModule) != null)
                {
                    var m = Measure(_biome.wallModule, wallModule: true);
                    Plugin.Log.LogInfo($"Crawlers wall module: back {m.Back}, length {m.Length:0.##}, depth {m.Depth:0.##}, height {m.Height:0.##}, floorY {m.FloorY:0.##}, mass material '{_massMaterial?.name}'");
                }
                _rows.Clear();
                float rh = 0f, rd = 0f;
                foreach (var path in _biome.rowPieces ?? Array.Empty<string>())
                {
                    if (_assets.GetPrefab(path) == null) continue;
                    var b = Measure(path).All;
                    _rows.Add(path);
                    rh = Mathf.Max(rh, b.max.y);
                    rd = Mathf.Max(rd, b.size.z);
                }
                if (_rows.Count > 0) { _rowHeight = rh; _rowDepth = rd; }
                _islandPieces.Clear();
                foreach (var path in _biome.islandPieces ?? Array.Empty<string>())
                    if (_assets.GetPrefab(path) != null) { Measure(path); _islandPieces.Add(path); }
                if (!string.IsNullOrEmpty(_biome.cornerPiece) && _assets.GetPrefab(_biome.cornerPiece) != null)
                    Measure(_biome.cornerPiece);
            }
            _decor.Clear();
            foreach (var path in _biome.decor ?? Array.Empty<string>())
                if (_assets.GetPrefab(path) != null) { Measure(path); _decor.Add(path); }
        }

        // The map's own art: ground capture, extruded walls, obstacle props.
        if (!crawlersGround && template != null)
            _groundCap.Begin(_root, _layers, RenderDistance + 10f, template, sortingLayer);
        _extrude = _kit.walls == "extrude" && _artMats != null;
        if (_extrude)
        {
            _art.Build(_layers);
            _extruder.Begin(_root);
            _props.Begin(_root);
        }
        if (_artMats != null)
        {
            _obstacles.Begin(_root, _layers, _kit.tiles, _artMats, sortingLayer);
            var needed = new HashSet<string>();
            if (_assets != null)
                foreach (var g in _obstacles.Groups)
                foreach (var path in ObstacleProps.PrefabsFor(g.Class) ?? Array.Empty<string>())
                    needed.Add(path);
            foreach (var path in needed)
                if (EnsureBundleFor(path) && _assets.GetPrefab(path) != null)
                    Measure(path);
        }

        Active = true;
        Plugin.Log.LogInfo($"Stage: map '{_layers.MapName}' ({stageType}) -> ground {_kit.ground}, walls {_kit.walls}, biome '{_biome?.bundle ?? "-"}', tiles '{_kit.tiles ?? "-"}', {_layers.Maps.Count} map copies, {_collision.Count} collision tilemap(s) [{CollisionNames()}], assets {(_assets != null ? "VS+Crawlers" : "VS only")}");
        return true;
    }

    // Crawlers prefab paths are "Assets/_GameAssets/Art/Levels/<Folder>/..."; load that biome's bundle.
    bool EnsureBundleFor(string path)
    {
        if (_assets == null) return false;
        string[] folders = { "InlaidLibrary", "MadForest", "GalloTower", "DairyPlant", "CapellaMagna" };
        foreach (var f in folders)
            if (path.Contains("/Levels/" + f + "/"))
                return _assets.LoadBiome(f.ToLowerInvariant());
        return true;   // shared assets are in the core bundles
    }

    // 3D view toggled: the ground capture borrows the game's ground layers only while 3D is on.
    public void SetViewActive(bool on) => _groundCap.SetEnabled(on);

    public void End()
    {
        _groundCap.End();
        _extruder.End();
        _props.End();
        _obstacles.End();
        _extrudeKey = int.MinValue;
        _gridValid = false;
        if (_root != null && _root)
            UnityEngine.Object.Destroy(_root.gameObject);
        _root = null;
        _placed.Clear();
        _pool.Clear();
        _pending.Clear();
        _pendingSet.Clear();
        _info.Clear();
        _doubleSided.Clear();
        _collision.Clear();
        _collisionLow.Clear();
        _rows.Clear();
        _islandPieces.Clear();
        _decor.Clear();
        _floorSize = 0f;
        _massMaterial = null;
        Active = false;
    }

    public void SetVisible(bool visible)
    {
        if (_root != null && _root)
            _root.gameObject.SetActive(visible);
        _groundCap.SetEnabled(visible);
    }

    public void Tick(Vector2 player2D, Vector3 player3D, Vector3 cameraPos, float cameraYaw)
    {
        if (!Active)
            return;
        _groundCap.Tick(player2D);
        _obstacles.FaceCamera(cameraYaw);
        _props.FaceCamera(cameraYaw);
        if (_frame++ % RebuildEvery == 0)
            Rebuild(player2D, player3D);
        ProcessQueue();
        TrimPools();
        UpdateOcclusion(player3D, cameraPos);
    }

    string CollisionNames()
    {
        var names = new HashSet<string>();
        foreach (var t in _collision) if (t != null && t) names.Add(t.gameObject.name);
        return string.Join(", ", names);
    }

    void CollectCollisionTilemaps(Stage stage)
    {
        _collision.Clear();
        _collisionLow.Clear();
        var tileset = stage != null ? stage.TilingTileset : null;
        if (tileset == null)
            return;
        var maps = tileset.GetPhaserTilemaps();
        if (maps == null)
            return;
        for (int i = 0; i < maps.Count; i++)
        {
            var tm = maps[i]?._layer;
            if (tm != null)
            {
                _collision.Add(tm);
                _collisionLow.Add(tm.gameObject.name == "PlayerWall");
            }
        }
    }

    // ------------------------------------------------------------------ rebuild (desired set)

    void Rebuild(Vector2 player2D, Vector3 player3D)
    {
        _generation++;
        WantFloor(player3D);
        _gridValid = false;
        if (_collision.Count > 0)
            WantWalls(player2D);
        else if (_biome != null && _decor.Count > 0)
            WantOpenDecor(player3D);
        WantObstacles(player3D);

        // Out of range or no longer wanted: back to the pool.
        List<long> stale = null;
        foreach (var kv in _placed)
            if (kv.Value.Seen != _generation)
                (stale ??= new List<long>()).Add(kv.Key);
        if (stale != null)
            foreach (var key in stale)
            {
                Release(_placed[key]);
                _placed.Remove(key);
            }

        // Nearest first.
        _pending.RemoveAll(k => !_placed.TryGetValue(k, out var p) || p.Go != null);
        _pendingSet.Clear();
        foreach (var k in _pending) _pendingSet.Add(k);
        _pending.Sort((a, b) => _placed[a].Want.Dist2.CompareTo(_placed[b].Want.Dist2));
    }

    void WantFloor(Vector3 player)
    {
        if (_floorSize <= 0f)
            return;
        var b = _info[_biome.floor].All;
        float y = b.size.y < 1f ? -b.max.y : 0f;
        int ring = Mathf.CeilToInt(RenderDistance / _floorSize);
        int cx = Mathf.RoundToInt((player.x - CoordinateMapper.WorldOffset.x) / _floorSize);
        int cz = Mathf.RoundToInt((player.z - CoordinateMapper.WorldOffset.z) / _floorSize);
        float r = RenderDistance + _floorSize;
        for (int dz = -ring; dz <= ring; dz++)
            for (int dx = -ring; dx <= ring; dx++)
            {
                int gx = cx + dx, gz = cz + dz;
                var center = CoordinateMapper.WorldOffset + new Vector3(gx * _floorSize, 0f, gz * _floorSize);
                float d2 = (center - player).sqrMagnitude;
                if (d2 > r * r) continue;
                // Random quarter-turn per tile breaks up visible repetition.
                var rot = Quaternion.Euler(0f, (Hash(gx, gz, 5) % 4) * 90f, 0f);
                var off = rot * new Vector3(b.center.x, 0f, b.center.z);
                Upsert(Key(1, gx, gz, 0), _biome.floor, new Want
                {
                    Pos = center + new Vector3(-off.x, y, -off.z), Rot = rot, Scale = Vector3.one, Dist2 = d2,
                });
            }
    }

    // Open stages (no collision walls): sparse low ground cover on a world grid, deterministic per cell.
    void WantOpenDecor(Vector3 player)
    {
        float cell = Mathf.Max(1f, _biome.decorCell), rd = RenderDistance;
        var p = player - CoordinateMapper.WorldOffset;
        int cx = Mathf.RoundToInt(p.x / cell), cz = Mathf.RoundToInt(p.z / cell);
        int r = Mathf.CeilToInt(rd / cell);
        for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                int gx = cx + dx, gz = cz + dz;
                uint hash = Hash(gx, gz, 77);
                if ((hash % 10000) >= _biome.decorChance * 10000f) continue;
                // Jitter inside the cell so it doesn't read as a grid.
                var pos = CoordinateMapper.WorldOffset + new Vector3((gx + ((hash >> 4) % 100) / 100f - 0.5f) * cell, 0f, (gz + ((hash >> 11) % 100) / 100f - 0.5f) * cell);
                float d2 = (pos - player).sqrMagnitude;
                if (d2 > rd * rd) continue;
                string path = _decor[(int)((hash >> 18) % (uint)_decor.Count)];
                var b = _info[path].All;
                var rot = Quaternion.Euler(0f, (hash >> 24) % 360, 0f);
                Upsert(Key(6, gx, gz, 0), path, new Want
                {
                    Pos = pos - rot * new Vector3(b.center.x, b.min.y, b.center.z), Rot = rot, Scale = Vector3.one, Dist2 = d2,
                });
            }
    }

    bool[] _grid = Array.Empty<bool>();
    bool[] _used = Array.Empty<bool>();

    void WantWalls(Vector2 player2D)
    {
        _lastPlayer2D = player2D;
        Tilemap first = null;
        foreach (var t in _collision) if (t != null && t) { first = t; break; }
        if (first == null) return;
        var cs = first.cellSize;
        var ls = first.transform.lossyScale;
        float cw = Mathf.Abs(cs.x * ls.x), ch = Mathf.Abs(cs.y * ls.y);                 // 2D units
        float cellX = cw * CoordinateMapper.Scale, cellY = ch * CoordinateMapper.Scale;  // metres
        if (cellX < 0.01f || cellY < 0.01f) return;

        int px = Mathf.FloorToInt(player2D.x / cw), py = Mathf.FloorToInt(player2D.y / ch);
        int cx = Mathf.FloorToInt(px / (float)WindowSnap) * WindowSnap;
        int cy = Mathf.FloorToInt(py / (float)WindowSnap) * WindowSnap;
        int r = Mathf.CeilToInt(RenderDistance / Mathf.Min(cellX, cellY)) + WindowSnap;
        int x0 = cx - r, y0 = cy - r, w = 2 * r + 1, h = 2 * r + 1;
        if (_grid.Length < w * h)
        {
            _grid = new bool[w * h]; _used = new bool[w * h];
            _hitWall = new bool[w * h]; _hitLow = new bool[w * h];
        }
        Array.Clear(_grid, 0, w * h);
        Array.Clear(_hitWall, 0, w * h);
        Array.Clear(_hitLow, 0, w * h);

        // Merge every collision tilemap copy into one world-aligned grid.
        var wmin = new Vector3(x0 * cw, y0 * ch, 0f);
        var wmax = new Vector3((x0 + w) * cw, (y0 + h) * ch, 0f);
        for (int ti = 0; ti < _collision.Count; ti++)
        {
            var tm = _collision[ti];
            if (tm == null || !tm) continue;
            var hit = _collisionLow[ti] ? _hitLow : _hitWall;
            var a = tm.WorldToCell(wmin); var b = tm.WorldToCell(wmax);
            var cb = tm.cellBounds;
            int ax = Math.Max(Math.Min(a.x, b.x) - 1, cb.xMin), bx = Math.Min(Math.Max(a.x, b.x) + 1, cb.xMax - 1);
            int ay = Math.Max(Math.Min(a.y, b.y) - 1, cb.yMin), by = Math.Min(Math.Max(a.y, b.y) + 1, cb.yMax - 1);
            for (int ty = ay; ty <= by; ty++)
                for (int tx = ax; tx <= bx; tx++)
                {
                    var cell = new Vector3Int(tx, ty, 0);
                    if (!tm.HasTile(cell)) continue;
                    var c = tm.GetCellCenterWorld(cell);
                    int gx = Mathf.FloorToInt(c.x / cw) - x0, gy = Mathf.FloorToInt(c.y / ch) - y0;
                    if (gx >= 0 && gy >= 0 && gx < w && gy < h)
                    {
                        _grid[gy * w + gx] = true;
                        hit[gy * w + gx] = true;
                    }
                }
        }
        MarkUnreachable(w, h, px - x0, py - y0);
        _gx0 = x0; _gy0 = y0; _gw = w; _gh = h; _gcw = cw; _gch = ch; _gridValid = true;
        MarkObstacles(x0, y0, w, h, cw, ch);

        if (!_crawlersWalls)
        {
            if (_extrude)
            {
                int key = x0 * 73856093 ^ y0 * 19349663;
                if (key != _extrudeKey || ++_extrudeAge > 8)
                {
                    _extrudeKey = key; _extrudeAge = 0;
                    _art.Resolve(x0, y0, w, h, cw, ch);
                    bool pits = ClassifyCells(w, h);
                    _extruder.Rebuild(_classifier, _art.Low, _art.Art, _art.Front, x0, y0, w, h, cw, ch, cellX, _kit.faceRows, cellX * 1.5f, _artMats, _sortingLayer);
                    _props.Rebuild(_classifier, _art.Low, _art.Art, x0, y0, w, h, cw, ch, cellX, cellX * 1.5f, _artMats, _sortingLayer);
                    if (pits)
                    {
                        if (_holes.Length < w * h) _holes = new bool[w * h];
                        for (int i = 0; i < w * h; i++) _holes[i] = _classifier.Kind[i] == CellClassifier.Pit;
                        _groundCap.SetHoles(_holes, x0, y0, w, h, cw, ch);
                    }
                    else
                        _groundCap.SetHoles(null, 0, 0, 0, 0, cw, ch);
                }
            }
            return;
        }

        bool Wall(int x, int y) => x < 0 || y < 0 || x >= w || y >= h || _grid[y * w + x];
        Vector2 Center(int x, int y) => new Vector2((x0 + x + 0.5f) * cw, (y0 + y + 0.5f) * ch);
        float rd = RenderDistance;
        var player3D = CoordinateMapper.ToWorld3D(player2D);

        // Small freestanding obstacles become one furniture set each; their cells are then left out of
        // the wall/row/mass passes below.
        if (_islandPieces.Count > 0)
            WantIslands(w, h, x0, y0, cw, ch, cellX, cellY, player3D);
        bool Island(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && (_island[y * w + x] || _obsMask[y * w + x]);

        _info.TryGetValue(_biome.wallModule ?? "", out var mod);
        bool haveRows = _rows.Count > 0;
        float thin = _biome.thinRowMax;

        // ---- wall faces: one run per straight stretch of wall bordering walkable floor
        for (int face = 0; face < 4; face++)
        {
            int fx = face == 2 ? -1 : face == 3 ? 1 : 0;
            int fy = face == 0 ? -1 : face == 1 ? 1 : 0;
            bool horizontal = fy != 0;
            int outer = horizontal ? h : w, inner = horizontal ? w : h;
            float cellAcross = horizontal ? cellY : cellX;
            int maxSteps = Mathf.CeilToInt(thin / cellAcross) + 1;
            for (int o = 0; o < outer; o++)
            {
                int i = 0;
                while (i < inner)
                {
                    int x = horizontal ? i : o, y = horizontal ? o : i;
                    if (!(_grid[y * w + x] && !Island(x, y) && !Wall(x + fx, y + fy))) { i++; continue; }
                    int start = i, minThick = int.MaxValue;
                    while (i < inner)
                    {
                        int xx = horizontal ? i : o, yy = horizontal ? o : i;
                        if (!(_grid[yy * w + xx] && !Island(xx, yy) && !Wall(xx + fx, yy + fy))) break;
                        // Thickness behind this face cell (walking into the wall, staying inside the window).
                        int steps = 0;
                        while (steps < maxSteps)
                        {
                            int sx = xx - fx * steps, sy = yy - fy * steps;
                            if (sx < 0 || sy < 0 || sx >= w || sy >= h) { steps = maxSteps; break; }
                            if (!_grid[sy * w + sx]) break;
                            steps++;
                        }
                        minThick = Math.Min(minThick, steps);
                        i++;
                    }
                    int end = i - 1;
                    int ax = horizontal ? start : o, ay = horizontal ? o : start;
                    int bx = horizontal ? end : o, by = horizontal ? o : end;
                    var a2 = Center(ax, ay); var b2 = Center(bx, by);
                    var n2 = new Vector2(fx, fy);
                    var along2 = horizontal ? new Vector2(1, 0) : new Vector2(0, 1);
                    float hn = (horizontal ? ch : cw) * 0.5f, ha = (horizontal ? cw : ch) * 0.5f;
                    a2 += n2 * hn - along2 * ha;
                    b2 += n2 * hn + along2 * ha;
                    var a = CoordinateMapper.ToWorld3D(a2); var b = CoordinateMapper.ToWorld3D(b2);
                    // Skip runs entirely outside the render distance.
                    float dRun = DistanceToSegment(player3D, a, b);
                    if (dRun > rd) continue;

                    long runKey = Key(2, horizontal ? y0 + o : x0 + o, horizontal ? x0 + start : y0 + start, (face << 16) | ((end - start) & 0xFFFF));
                    var normal = new Vector3(fx, 0f, fy);
                    bool isThin = minThick * cellAcross <= thin;
                    if (isThin && haveRows)
                        WantRowRun(runKey, a, b, normal, player3D);
                    else if (mod != null)
                        WantWallRun(runKey, a, b, normal, mod, player3D);
                }
            }
        }

        // ---- outside corners of thick walls: a pillar hides where two wall faces meet
        if (!string.IsNullOrEmpty(_biome.cornerPiece) && _info.TryGetValue(_biome.cornerPiece, out var corner))
        {
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                {
                    if (!_grid[y * w + x] || Island(x, y)) continue;
                    int ox = !Wall(x - 1, y) ? -1 : !Wall(x + 1, y) ? 1 : 0;
                    int oy = !Wall(x, y - 1) ? -1 : !Wall(x, y + 1) ? 1 : 0;
                    if (ox == 0 || oy == 0) continue;
                    // Only real outside corners of thick walls (the diagonal neighbour is open too).
                    if (Wall(x + ox, y + oy)) continue;
                    var cornerPt = CoordinateMapper.ToWorld3D(Center(x, y) + new Vector2(ox * cw * 0.5f, oy * ch * 0.5f));
                    float d = Vector3.Distance(cornerPt, player3D);
                    if (d > rd) continue;
                    var cb = corner.All;
                    // Centre the pillar's footprint just inside the corner.
                    var inward = new Vector3(-ox * cb.extents.x, 0f, -oy * cb.extents.z) * 0.6f;
                    var pos = cornerPt + inward - new Vector3(cb.center.x, cb.min.y, cb.center.z);
                    Upsert(Key(4, x0 + x, y0 + y, (ox + 1) * 3 + (oy + 1)), _biome.cornerPiece, new Want
                    {
                        Pos = pos, Rot = Quaternion.identity, Scale = Vector3.one, Occluder = true,
                        World = new Bounds(pos + cb.center, cb.size * 0.9f), Dist2 = d * d,
                    });
                }
        }

        // ---- wall mass: greedy rectangles, inset from open sides by the depth of the pieces there
        if (_massMaterial == null)
            return;
        Array.Clear(_used, 0, w * h);
        for (int k = 0; k < w * h; k++)
            if ((_islandPieces.Count > 0 && _island[k]) || _obsMask[k]) _used[k] = true;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!_grid[y * w + x] || _used[y * w + x]) continue;
                int x2 = x;
                while (x2 + 1 < w && _grid[y * w + x2 + 1] && !_used[y * w + x2 + 1]) x2++;
                int y2 = y;
                while (y2 + 1 < h)
                {
                    bool full = true;
                    for (int k = x; k <= x2 && full; k++)
                        full = _grid[(y2 + 1) * w + k] && !_used[(y2 + 1) * w + k];
                    if (!full) break;
                    y2++;
                }
                for (int yy = y; yy <= y2; yy++)
                    for (int xx = x; xx <= x2; xx++)
                        _used[yy * w + xx] = true;

                float fullX = (x2 - x + 1) * cellX, fullZ = (y2 - y + 1) * cellY;
                bool rectThin = haveRows && Mathf.Min(fullX, fullZ) <= thin;
                float depth = rectThin ? _rowDepth : (mod != null ? mod.Depth : 0f);
                float height = rectThin ? _rowHeight - 0.05f : (mod != null ? mod.Height - 0.1f : _biome.blockHeight);

                bool OpenAlongX(int col) { for (int yy = y; yy <= y2; yy++) if (!Wall(col, yy)) return true; return false; }
                bool OpenAlongY(int row) { for (int xx = x; xx <= x2; xx++) if (!Wall(xx, row)) return true; return false; }
                float iw = OpenAlongX(x - 1) ? depth : 0f, ie = OpenAlongX(x2 + 1) ? depth : 0f;
                float isouth = OpenAlongY(y - 1) ? depth : 0f, inorth = OpenAlongY(y2 + 1) ? depth : 0f;
                float sx = fullX - iw - ie, sz = fullZ - isouth - inorth;
                if (sx < 0.3f || sz < 0.3f) continue;
                var c = CoordinateMapper.ToWorld3D((Center(x, y) + Center(x2, y2)) * 0.5f) + new Vector3((iw - ie) * 0.5f, 0f, (isouth - inorth) * 0.5f);
                // Distance to the rectangle, not its centre.
                var closest = new Vector3(Mathf.Clamp(player3D.x, c.x - sx * 0.5f, c.x + sx * 0.5f), c.y, Mathf.Clamp(player3D.z, c.z - sz * 0.5f, c.z + sz * 0.5f));
                float d2 = (closest - player3D).sqrMagnitude;
                if (d2 > rd * rd) continue;
                var pos = c + Vector3.up * (height * 0.5f);
                var scale = new Vector3(sx, height, sz);
                Upsert(Key(3, x0 + x, y0 + y, ((x2 - x) & 0xFF) << 8 | ((y2 - y) & 0xFF)), MassBox, new Want
                {
                    Pos = pos, Rot = Quaternion.identity, Scale = scale, Occluder = true,
                    World = new Bounds(pos, scale * 0.9f), Dist2 = d2,
                });
            }
    }

    bool[] _island = Array.Empty<bool>();
    int[] _comp = Array.Empty<int>();

    // Small freestanding obstacles (connected wall areas no bigger than islandMax both ways, not touching
    // the window edge): one furniture set each, chosen by footprint fit, aligned with the obstacle's long
    // side and scaled uniformly to stay inside its collision.
    void WantIslands(int w, int h, int x0, int y0, float cw, float ch, float cellX, float cellY, Vector3 player)
    {
        int n = w * h;
        if (_island.Length < n) { _island = new bool[n]; _comp = new int[n]; }
        Array.Clear(_island, 0, n);
        Array.Clear(_reach, 0, Math.Min(_reach.Length, n));
        if (_reach.Length < n) { _reach = new bool[n]; _queue = new int[n]; }
        float max = _biome.islandMax, rd = RenderDistance;
        for (int start = 0; start < n; start++)
        {
            if (!_grid[start] || _reach[start]) continue;
            int head = 0, tail = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            bool edge = false;
            _reach[start] = true; _queue[tail++] = start;
            while (head < tail)
            {
                int i = _queue[head++];
                int x = i % w, y = i / w;
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1) edge = true;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                if (x + 1 < w && _grid[i + 1] && !_reach[i + 1]) { _reach[i + 1] = true; _queue[tail++] = i + 1; }
                if (x > 0 && _grid[i - 1] && !_reach[i - 1]) { _reach[i - 1] = true; _queue[tail++] = i - 1; }
                if (y + 1 < h && _grid[i + w] && !_reach[i + w]) { _reach[i + w] = true; _queue[tail++] = i + w; }
                if (y > 0 && _grid[i - w] && !_reach[i - w]) { _reach[i - w] = true; _queue[tail++] = i - w; }
            }
            float sx = (maxX - minX + 1) * cellX, sz = (maxY - minY + 1) * cellY;
            bool small = !edge && sx <= max && sz <= max;
            // Kits without row pieces: thin rows (even ones running past the window) become a line of
            // furniture pieces, one per segment.
            bool row = !small && _rows.Count == 0 && Mathf.Min(sx, sz) <= _biome.thinRowMax;
            if (!small && !row) continue;
            bool hasObstacle = false;
            for (int k = 0; k < tail; k++) { _island[_queue[k]] = true; hasObstacle |= _obsMask[_queue[k]]; }
            if (hasObstacle) continue;
            if (row)
            {
                bool alongX = sx >= sz;
                float len = alongX ? sx : sz, across = alongX ? sz : sx;
                int segs = Mathf.Max(1, Mathf.CeilToInt(len / max));
                float segLen = len / segs;
                for (int sIdx = 0; sIdx < segs; sIdx++)
                {
                    float t = (sIdx + 0.5f) * segLen;
                    var sc2 = alongX
                        ? new Vector2((x0 + minX) * cw + t / CoordinateMapper.Scale, (y0 + (minY + maxY + 1) * 0.5f) * ch)
                        : new Vector2((x0 + (minX + maxX + 1) * 0.5f) * cw, (y0 + minY) * ch + t / CoordinateMapper.Scale);
                    var sc = CoordinateMapper.ToWorld3D(sc2);
                    float sd = Vector3.Distance(sc, player) - segLen * 0.5f;
                    if (sd > rd) continue;
                    PlaceIsland(Key(5, x0 + minX, y0 + minY, sIdx) * 41 + 7, sc, alongX ? segLen : across, alongX ? across : segLen,
                        Hash(x0 + minX + sIdx, y0 + minY, 23), Mathf.Max(sd, 0f));
                }
                continue;
            }

            var c2 = new Vector2((x0 + (minX + maxX + 1) * 0.5f) * cw, (y0 + (minY + maxY + 1) * 0.5f) * ch);
            var c = CoordinateMapper.ToWorld3D(c2);
            float d = Vector3.Distance(c, player) - Mathf.Max(sx, sz) * 0.5f;
            if (d > rd) continue;

            PlaceIsland(Key(5, x0 + minX, y0 + minY, ((maxX - minX) & 0xFF) << 8 | ((maxY - minY) & 0xFF)), c, sx, sz,
                Hash(x0 + minX, y0 + minY, 23), Mathf.Max(d, 0f));
        }
    }

    // One furniture set fitted into an sx-by-sz footprint (metres) centred on c: best footprint fit,
    // long sides aligned, uniform scale so it stays (mostly) inside the obstacle's collision.
    void PlaceIsland(long key, Vector3 c, float sx, float sz, uint hsh, float dist)
    {
        bool longX = sx >= sz;
        float islandLong = Mathf.Max(sx, sz), islandShort = Mathf.Max(0.1f, Mathf.Min(sx, sz));
        string best = null; float bestScore = float.MaxValue, bestScale = 1f;
        for (int k = 0; k < _islandPieces.Count; k++)
        {
            var b = _info[_islandPieces[k]].All;
            float pl = Mathf.Max(0.1f, Mathf.Max(b.size.x, b.size.z)), ps = Mathf.Max(0.1f, Mathf.Min(b.size.x, b.size.z));
            float s = Mathf.Min(islandLong / pl, islandShort / ps);
            // Prefer natural size (s close to 1) and filling the footprint; small random tie-break.
            float score = Mathf.Abs(Mathf.Log(Mathf.Max(s, 0.05f))) + (1f - (pl * ps * s * s) / (islandLong * islandShort)) * 0.5f
                          + ((Hash((int)hsh, k, 29) % 100) / 100f) * 0.25f;
            if (score < bestScore) { bestScore = score; best = _islandPieces[k]; bestScale = Mathf.Clamp(s, 0.5f, 1.25f); }
        }
        if (best == null) return;
        var bb = _info[best].All;
        bool pieceLongX = bb.size.x >= bb.size.z;
        float yaw = (pieceLongX == longX ? 0f : 90f) + ((hsh & 1) == 0 ? 0f : 180f);
        var rot = Quaternion.Euler(0f, yaw, 0f);
        var scale = Vector3.one * bestScale;
        var pos = c - rot * Vector3.Scale(new Vector3(bb.center.x, bb.min.y, bb.center.z), scale);
        Upsert(key, best, new Want { Pos = pos, Rot = rot, Scale = scale, Occluder = false, Dist2 = dist * dist });
    }

    // Window cells covered by obstacle groups (they get the obstacle's own prop instead of walls/islands).
    void MarkObstacles(int x0, int y0, int w, int h, float cw, float ch)
    {
        int n = w * h;
        if (_obsMask.Length < n) _obsMask = new bool[n];
        Array.Clear(_obsMask, 0, n);
        foreach (var g in _obstacles.Groups)
        {
            if (g.Tilemap == null || !g.Tilemap) continue;
            foreach (var c in g.Cells)
            {
                var wc = g.Tilemap.GetCellCenterWorld(c);
                int gx = Mathf.FloorToInt(wc.x / cw) - x0, gy = Mathf.FloorToInt(wc.y / ch) - y0;
                if (gx >= 0 && gy >= 0 && gx < w && gy < h) _obsMask[gy * w + gx] = true;
            }
        }
    }

    // Obstacle-layer objects: clear classes become Crawlers props, the rest keep their own art.
    void WantObstacles(Vector3 player)
    {
        float rd = RenderDistance;
        for (int i = 0; i < _obstacles.Groups.Count; i++)
        {
            var g = _obstacles.Groups[i];
            if (g.Tilemap == null || !g.Tilemap) continue;
            var fp = ObstacleProps.Footprint(g);
            float d = Vector3.Distance(fp.center, player) - Mathf.Max(fp.sx, fp.sz) * 0.5f;
            bool inRange = d <= rd;
            var prefabs = _assets != null ? ObstacleProps.PrefabsFor(g.Class) : null;
            if (prefabs == null)
            {
                var cs = g.Tilemap.cellSize; var ls = g.Tilemap.transform.lossyScale;
                _obstacles.UpdateArt(g, inRange, Mathf.Abs(cs.x * ls.x) * CoordinateMapper.Scale);
                continue;
            }
            if (!inRange) continue;
            uint hsh = Hash(g.Map * 7919 + g.MinX, g.MinY, 31);
            string path = null;
            for (int k = 0; k < prefabs.Length && path == null; k++)
            {
                var cand = prefabs[(int)((hsh + k) % (uint)prefabs.Length)];
                if (_info.ContainsKey(cand)) path = cand;
            }
            if (path == null) continue;

            // Face away from the side with the most wall next to it; freestanding pieces face south.
            var face = new Vector3(0, 0, -1);
            if (_gridValid)
            {
                var a = g.Tilemap.GetCellCenterWorld(new Vector3Int(g.MinX, g.MinY, 0));
                var b = g.Tilemap.GetCellCenterWorld(new Vector3Int(g.MaxX, g.MaxY, 0));
                int ax = Mathf.FloorToInt(a.x / _gcw) - _gx0, ay = Mathf.FloorToInt(a.y / _gch) - _gy0;
                int bx = Mathf.FloorToInt(b.x / _gcw) - _gx0, by = Mathf.FloorToInt(b.y / _gch) - _gy0;
                bool Solid(int x, int y) => x >= 0 && y >= 0 && x < _gw && y < _gh && _grid[y * _gw + x] && !_obsMask[y * _gw + x];
                int south = 0, north = 0, west = 0, east = 0;
                for (int x = ax; x <= bx; x++) { if (Solid(x, ay - 1)) south++; if (Solid(x, by + 1)) north++; }
                for (int y = ay; y <= by; y++) { if (Solid(ax - 1, y)) west++; if (Solid(bx + 1, y)) east++; }
                int best = Math.Max(Math.Max(south, north), Math.Max(west, east));
                if (best > 0)
                    face = best == north ? new Vector3(0, 0, -1) : best == south ? new Vector3(0, 0, 1) : best == west ? new Vector3(1, 0, 0) : new Vector3(-1, 0, 0);
            }
            var rot = Quaternion.Euler(0f, Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg, 0f);
            var pb = _info[path].All;
            bool sideways = Mathf.Abs(face.x) > 0.5f;
            float px = sideways ? pb.size.z : pb.size.x, pz = sideways ? pb.size.x : pb.size.z;
            float scale = ObstacleProps.KeepsNaturalSize(g.Class) ? 1f
                : Mathf.Clamp(Mathf.Min(fp.sx / Mathf.Max(px, 0.1f), fp.sz / Mathf.Max(pz, 0.1f)), 0.4f, 1.6f);
            var sc = Vector3.one * scale;
            Upsert(Key(7, g.Map, g.MinX, g.MinY & 0x3FFFF), path, new Want
            {
                Pos = fp.center - rot * Vector3.Scale(new Vector3(pb.center.x, pb.min.y, pb.center.z), sc),
                Rot = rot, Scale = sc, Occluder = false, Dist2 = Mathf.Max(d, 0f) * Mathf.Max(d, 0f),
            });
        }
    }

    // Classify the window (CellClassifier) from the collision layers and the wall art.
    // Returns whether any pit is in the window.
    bool ClassifyCells(int w, int h)
    {
        int n = w * h;
        if (_lowIds.Length < n) _lowIds = new int[n];
        var low = _art.Low;
        for (int i = 0; i < n; i++)
            _lowIds[i] = SpriteId(low[i]);
        _classifier.Run(w, h, _hitWall, _hitLow, _grid, _lowIds, _art.LowFromWalls,
                        id => SpriteOpacity.Get(_spriteList[id]).Coverage, id => SpriteOpacity.Packed(_spriteList[id]),
                        _kit.faceRows, 8, _kit.facadeRows);
        bool anyPit = _classifier.Pits > 0;
        string summary = $"regions {_classifier.Regions} (heights {_classifier.Heights.ToString().Trim()}), merged into walls {_classifier.MergedLow}, pits {_classifier.Pits} (bands {_classifier.PitDepths.ToString().Trim()}), low props {_classifier.LowProps}, merged blocks {_classifier.MergedBlocks}";
        if (summary != _lastSummary)
        {
            _lastSummary = summary;
            Plugin.Log.LogInfo($"Stage cells: {summary}");
        }
        return anyPit;
    }

    int SpriteId(Sprite s)
    {
        if (s == null) return -1;
        if (!_spriteIds.TryGetValue(s.Pointer, out var id))
        {
            id = _spriteList.Count;
            _spriteIds[s.Pointer] = id;
            _spriteList.Add(s);
        }
        return id;
    }

    // F10: the current window's classification as text (BepInEx/VS3D_grid.txt).
    public string DumpGrid()
    {
        if (!_gridValid || _classifier.Kind.Length < _gw * _gh) return null;
        int px = Mathf.FloorToInt(_lastPlayer2D.x / _gcw) - _gx0, py = Mathf.FloorToInt(_lastPlayer2D.y / _gch) - _gy0;
        return $"map '{_layers.MapName}', window x={_gx0} y={_gy0} {_gw}x{_gh}, {_lastSummary}\n" + _classifier.Dump(_gw, _gh, px, py);
    }

    int[] _queue = Array.Empty<int>();
    bool[] _reach = Array.Empty<bool>();

    // Open cells the player can't get to (e.g. beyond the Inlaid Library's outer shelves) count as solid,
    // so no wall fronts face them. Flood fill from the player's cell through open cells.
    void MarkUnreachable(int w, int h, int sx, int sy)
    {
        if (sx < 0 || sy < 0 || sx >= w || sy >= h || _grid[sy * w + sx])
            return;
        int n = w * h;
        if (_reach.Length < n) { _reach = new bool[n]; _queue = new int[n]; }
        Array.Clear(_reach, 0, n);
        int head = 0, tail = 0;
        _reach[sy * w + sx] = true;
        _queue[tail++] = sy * w + sx;
        while (head < tail)
        {
            int i = _queue[head++];
            int x = i % w, y = i / w;
            if (x + 1 < w && !_grid[i + 1] && !_reach[i + 1]) { _reach[i + 1] = true; _queue[tail++] = i + 1; }
            if (x > 0 && !_grid[i - 1] && !_reach[i - 1]) { _reach[i - 1] = true; _queue[tail++] = i - 1; }
            if (y + 1 < h && !_grid[i + w] && !_reach[i + w]) { _reach[i + w] = true; _queue[tail++] = i + w; }
            if (y > 0 && !_grid[i - w] && !_reach[i - w]) { _reach[i - w] = true; _queue[tail++] = i - w; }
        }
        for (int i = 0; i < n; i++)
            if (!_grid[i] && !_reach[i])
                _grid[i] = true;
    }

    // Thick walls: authored room-wall sections along the face line a->b (normal points into the floor).
    void WantWallRun(long runKey, Vector3 a, Vector3 b, Vector3 normal, Info info, Vector3 player)
    {
        float length = Vector3.Distance(a, b);
        if (length < info.Length * 0.3f)
            return; // short stubs: the wall mass covers them
        int n = Mathf.Max(1, Mathf.RoundToInt(length / info.Length));
        float stretch = length / (n * info.Length);
        var dir = (b - a) / length;
        float yaw = Mathf.Atan2(-normal.x, -normal.z) * Mathf.Rad2Deg - Mathf.Atan2(info.Back.x, info.Back.z) * Mathf.Rad2Deg;
        var rot = Quaternion.Euler(0f, yaw, 0f);
        var scale = new Vector3(Mathf.Abs(info.Along.x) > 0.5f ? stretch : 1f, 1f, Mathf.Abs(info.Along.z) > 0.5f ? stretch : 1f);
        var local = info.Back * info.FrontOffset + info.Along * info.AlongCenter + Vector3.up * info.FloorY;
        for (int i = 0; i < n; i++)
        {
            var p = a + dir * ((i + 0.5f) * length / n);
            // Every other section mirrored along the wall so the repetition is less obvious.
            var s = scale;
            if (((i + (int)(Hash((int)runKey, 0, 3) & 1)) & 1) == 1)
            {
                if (Mathf.Abs(info.Along.x) > 0.5f) s.x = -s.x; else s.z = -s.z;
            }
            var pos = p - rot * Vector3.Scale(local, s);
            Upsert(runKey * 31 + i, _biome.wallModule, new Want
            {
                Pos = pos, Rot = rot, Scale = s, Occluder = true,
                World = WorldBounds(info.Wall, pos, rot, s), Dist2 = (p - player).sqrMagnitude,
            });
        }
    }

    // Thin rows: freestanding bookcase runs (front = local +Z) along the face line, fitted to its length.
    void WantRowRun(long runKey, Vector3 a, Vector3 b, Vector3 normal, Vector3 player)
    {
        float length = Vector3.Distance(a, b);
        if (length < 0.8f)
            return;
        var dir = (b - a) / length;
        // Deterministic sequence of pieces that roughly fills the run.
        var seq = new List<string>(8);
        float sum = 0f;
        for (int k = 0; k < 64; k++)
        {
            var path = _rows[(int)(Hash((int)runKey, k, 11) % (uint)_rows.Count)];
            float len = _info[path].All.size.x;
            if (sum > 0f && sum + len > length * 1.15f) break;
            seq.Add(path);
            sum += len;
            if (sum >= length * 0.9f) break;
        }
        float stretch = length / sum;
        var rot = Quaternion.Euler(0f, Mathf.Atan2(normal.x, normal.z) * Mathf.Rad2Deg, 0f);   // local +Z -> floor
        float cursor = 0f;
        for (int i = 0; i < seq.Count; i++)
        {
            var bnd = _info[seq[i]].All;
            float len = bnd.size.x * stretch;
            var p = a + dir * (cursor + len * 0.5f);
            cursor += len;
            // Front-bottom-centre of the piece lands on the face line at floor level.
            var anchor = new Vector3(bnd.center.x, bnd.min.y, bnd.max.z);
            var scale = new Vector3(stretch, 1f, 1f);
            Upsert(runKey * 37 + i, seq[i], new Want
            {
                Pos = p - rot * Vector3.Scale(anchor, scale), Rot = rot, Scale = scale, Occluder = false,
                Dist2 = (p - player).sqrMagnitude,
            });
        }
    }

    static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-4f));
        return Vector3.Distance(p, a + ab * t);
    }

    // ------------------------------------------------------------------ placement + queue

    void Upsert(long key, string path, Want want)
    {
        if (_placed.TryGetValue(key, out var p) && p.Path != path)
        {
            Release(p);
            _placed.Remove(key);
            p = null;
        }
        if (p == null)
        {
            p = new Placed { Path = path };
            _placed[key] = p;
        }
        p.Want = want;
        p.Seen = _generation;
        if (p.Go != null)
        {
            Apply(p);
            return;
        }
        // Reuse a pooled instance right away (cheap); otherwise queue creation.
        var pooled = PopPool(path);
        if (pooled != null)
        {
            p.Go = pooled;
            Apply(p);
        }
        else if (_pendingSet.Add(key))
        {
            _pending.Add(key);
        }
    }

    void ProcessQueue()
    {
        if (_pending.Count == 0)
            return;
        _budget.Restart();
        int done = 0;
        while (done < _pending.Count)
        {
            var key = _pending[done++];
            _pendingSet.Remove(key);
            if (!_placed.TryGetValue(key, out var p) || p.Go != null)
                continue;
            p.Go = Create(p.Path);
            if (p.Go != null)
                Apply(p);
            if (_budget.Elapsed.TotalMilliseconds >= SpawnBudgetMs)
                break;
        }
        _pending.RemoveRange(0, done);
    }

    static void Apply(Placed p)
    {
        var t = p.Go.transform;
        t.localScale = p.Want.Scale;
        t.SetPositionAndRotation(p.Want.Pos, p.Want.Rot);
        if (!p.Hidden && !p.Go.activeSelf)
            p.Go.SetActive(true);
    }

    void Release(Placed p)
    {
        if (p.Go == null || !p.Go)
            return;
        p.Go.SetActive(false);
        p.Hidden = false;
        if (!_pool.TryGetValue(p.Path, out var stack))
            _pool[p.Path] = stack = new Stack<GameObject>();
        stack.Push(p.Go);
        p.Go = null;
    }

    GameObject PopPool(string path)
    {
        if (!_pool.TryGetValue(path, out var stack))
            return null;
        while (stack.Count > 0)
        {
            var go = stack.Pop();
            if (go != null && go)
                return go;
        }
        return null;
    }

    // Destroy pooled spares beyond PoolKeep, a few per frame.
    void TrimPools()
    {
        int budget = 4;
        foreach (var stack in _pool.Values)
            while (stack.Count > PoolKeep && budget-- > 0)
            {
                var go = stack.Pop();
                if (go != null && go)
                    UnityEngine.Object.Destroy(go);
            }
    }

    GameObject Create(string path)
    {
        if (path == MassBox)
            return CreateMassBox();
        var go = _assets.Spawn(path, _root, Vector3.zero, Quaternion.identity, 1f, _sortingLayer);
        if (go == null)
            return null;
        go.SetActive(false);
        bool wallModule = path == _biome.wallModule;
        Strip(go, wallModule, wallModule ? _info[path] : null);
        if (path == _biome.floor)
            MakeDoubleSided(go);
        return go;
    }

    GameObject CreateMassBox()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var col = go.GetComponent<UnityEngine.Collider>();
        if (col != null) UnityEngine.Object.Destroy(col);
        go.transform.SetParent(_root, false);
        go.layer = RenderLayer.Index;
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = _massMaterial;
        r.sortingLayerID = _sortingLayer;
        go.SetActive(false);
        return go;
    }

    // Floor tiles render double-sided where the shader allows it.
    void MakeDoubleSided(GameObject go)
    {
        var rs = go.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < rs.Length; i++)
        {
            var m = rs[i].sharedMaterial;
            if (m == null) continue;
            if (!_doubleSided.TryGetValue(m.Pointer, out var ds))
            {
                ds = null;
                if (m.HasProperty("_Cull"))
                {
                    ds = new Material(m);
                    ds.SetFloat("_Cull", 0f);
                }
                _doubleSided[m.Pointer] = ds;
            }
            if (ds != null)
                rs[i].sharedMaterial = ds;
        }
    }

    // ------------------------------------------------------------------ camera occlusion

    // Hide wall pieces between the camera and the player so the action stays visible.
    void UpdateOcclusion(Vector3 player, Vector3 cameraPos)
    {
        var eye = player + Vector3.up * 1.2f;
        var toCam = cameraPos - eye;
        float dist = toCam.magnitude;
        if (dist < 0.01f) return;
        var ray = new Ray(eye, toCam / dist);
        foreach (var p in _placed.Values)
        {
            if (!p.Want.Occluder || p.Go == null) continue;
            bool block = p.Want.World.IntersectRay(ray, out float hit) && hit < dist && !p.Want.World.Contains(eye);
            if (block != p.Hidden)
            {
                p.Hidden = block;
                p.Go.SetActive(!block);
            }
        }
    }

    // ------------------------------------------------------------------ measuring

    Info Measure(string path, bool wallModule = false)
    {
        if (_info.TryGetValue(path, out var info))
            return info;
        info = new Info { All = new Bounds(Vector3.zero, Vector3.one), Back = Vector3.forward, Along = Vector3.right, Length = 1f, Height = 3f };
        _info[path] = info;
        var go = _assets.Spawn(path, _root, Vector3.zero, Quaternion.identity, 1f, _sortingLayer);
        if (go == null)
            return info;
        if (wallModule)
        {
            info.FloorY = FindFloorY(go);
            _massMaterial ??= FindMassMaterial(go);
        }
        Strip(go, wallModule, null);
        info.All = EnabledBounds(go);
        if (wallModule)
        {
            // The wall stands on one edge of the footprint: the tall geometry above the module's floor
            // touches that edge and leaves the opposite side open (the room).
            var tall = EnabledBounds(go, minHeight: 1.5f, minY: info.FloorY - 0.2f);
            var all = info.All;
            float gxp = all.max.x - tall.max.x, gxn = tall.min.x - all.min.x;
            float gzp = all.max.z - tall.max.z, gzn = tall.min.z - all.min.z;
            float sxp = gxn - gxp, sxn = gxp - gxn, szp = gzn - gzp, szn = gzp - gzn;
            float best = Mathf.Max(Mathf.Max(sxp, sxn), Mathf.Max(szp, szn));
            bool backIsZ = best == szp || best == szn;
            float backSign = backIsZ ? (best == szp ? 1f : -1f) : (best == sxp ? 1f : -1f);
            info.Back = backIsZ ? new Vector3(0, 0, backSign) : new Vector3(backSign, 0, 0);
            info.Along = backIsZ ? Vector3.right : Vector3.forward;
            info.Length = backIsZ ? all.size.x : all.size.z;
            info.AlongCenter = backIsZ ? all.center.x : all.center.z;
            float tallMin = backIsZ ? tall.min.z : tall.min.x, tallMax = backIsZ ? tall.max.z : tall.max.x;
            info.FrontOffset = backSign > 0 ? tallMin : -tallMax;
            info.Depth = tallMax - tallMin;
            info.Height = Mathf.Max(1f, tall.max.y - info.FloorY);
            info.Wall = tall;
            Strip(go, true, info);   // now that the wall face is known, drop the furniture in front of it
        }
        go.SetActive(false);
        if (!_pool.TryGetValue(path, out var stack))
            _pool[path] = stack = new Stack<GameObject>();
        stack.Push(go);
        return info;
    }

    // Drop ceilings (the third-person camera looks down into the level). Wall modules also drop their own
    // floor (our floor tiles replace it) and, once measured, all furniture in front of the wall face.
    static void Strip(GameObject go, bool wallModule, Info info)
    {
        var origin = go.transform.position;
        var rs = go.GetComponentsInChildren<Renderer>(true);
        float floorY = info?.FloorY ?? 0f;
        for (int i = 0; i < rs.Length; i++)
        {
            var r = rs[i];
            if (IsCeiling(NameOf(r))) { r.enabled = false; continue; }
            if (!wallModule) continue;
            var b = r.bounds;
            float top = b.max.y - origin.y;
            bool flat = b.size.y < 0.3f && b.size.x * b.size.z > 4f;
            if (flat && Mathf.Abs(top - floorY) < 0.5f) { r.enabled = false; continue; }
            if (info != null && Vector3.Dot(b.center - origin, info.Back) < info.FrontOffset - 0.5f)
                r.enabled = false;   // in front of the wall face: would stand in walkable space
        }
    }

    static bool IsCeiling(string n) =>
        n.IndexOf("Ceiling", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Cieling", StringComparison.OrdinalIgnoreCase) >= 0 ||
        n.IndexOf("Roof", StringComparison.OrdinalIgnoreCase) >= 0;

    static string NameOf(Renderer r) => (r.sharedMaterial != null ? r.sharedMaterial.name : "") + "|" + r.gameObject.name;

    static float FindFloorY(GameObject go)
    {
        var rs = go.GetComponentsInChildren<Renderer>(true);
        float maxArea = 0f;
        for (int i = 0; i < rs.Length; i++)
        {
            var b = rs[i].bounds;
            if (b.size.y < 0.3f && !IsCeiling(NameOf(rs[i]))) maxArea = Mathf.Max(maxArea, b.size.x * b.size.z);
        }
        float y = float.PositiveInfinity;
        for (int i = 0; i < rs.Length; i++)
        {
            var b = rs[i].bounds;
            if (b.size.y < 0.3f && !IsCeiling(NameOf(rs[i])) && b.size.x * b.size.z >= maxArea * 0.5f)
                y = Mathf.Min(y, b.max.y - go.transform.position.y);
        }
        return float.IsInfinity(y) ? 0f : y;
    }

    static Material FindMassMaterial(GameObject go)
    {
        var rs = go.GetComponentsInChildren<Renderer>(true);
        Material wall = null;
        for (int i = 0; i < rs.Length; i++)
        {
            var m = rs[i].sharedMaterial;
            if (m == null) continue;
            if (IsCeiling(NameOf(rs[i])) && m.shader != null && m.shader.name.IndexOf("Triplanar", StringComparison.OrdinalIgnoreCase) >= 0)
                return m;
            if (wall == null && rs[i].gameObject.name.IndexOf("Wall", StringComparison.OrdinalIgnoreCase) >= 0)
                wall = m;
        }
        return wall ?? (rs.Length > 0 ? rs[0].sharedMaterial : null);
    }

    static Bounds EnabledBounds(GameObject go, float minHeight = 0f, float minY = float.NegativeInfinity)
    {
        var rs = go.GetComponentsInChildren<Renderer>(true);
        bool any = false;
        var b = new Bounds(go.transform.position, Vector3.zero);
        for (int i = 0; i < rs.Length; i++)
        {
            if (!rs[i].enabled) continue;
            var rb = rs[i].bounds;
            if (rb.size.y < minHeight) continue;
            if (rb.min.y - go.transform.position.y < minY) continue;
            if (!any) { b = rb; any = true; } else b.Encapsulate(rb);
        }
        b.center -= go.transform.position;
        return b;
    }

    static Bounds WorldBounds(Bounds local, Vector3 pos, Quaternion rot, Vector3 scale)
    {
        var c = pos + rot * Vector3.Scale(local.center, scale);
        var e = Vector3.Scale(local.extents, scale);
        var ax = rot * new Vector3(e.x, 0, 0); var az = rot * new Vector3(0, 0, e.z);
        var ext = new Vector3(Mathf.Abs(ax.x) + Mathf.Abs(az.x), e.y, Mathf.Abs(ax.z) + Mathf.Abs(az.z));
        return new Bounds(c, ext * 2f * 0.9f);
    }

    static long Key(int kind, int a, int b, int c) =>
        ((long)kind << 58) ^ ((long)(a & 0xFFFFF) << 38) ^ ((long)(b & 0xFFFFF) << 18) ^ (c & 0x3FFFF);

    static uint Hash(int x, int y, int salt)
    {
        unchecked
        {
            uint h = (uint)x * 73856093u ^ (uint)y * 19349663u ^ (uint)salt * 83492791u;
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return h;
        }
    }
}
