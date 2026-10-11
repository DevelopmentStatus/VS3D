using System.Collections.Generic;
using UnityEngine;
using VS3D.Bootstrap;
using VS3D.SimulationBridge;

namespace VS3D.Rendering.Maps;

// The stage's own ground art in 3D: a top-down orthographic camera renders the map's ground layers
// (Floor, FloorOverlay, Decals, Shadows, or the TilingBackground) into a render texture, which is laid
// on a ground plane under the player. Exact 2D look, including animated tiles, tint and map tiling.
// The ground layer GameObjects are moved to a spare render layer while active and restored after.
public sealed class GroundCapture
{
    const int Resolution = 2048;

    readonly List<GameObject> _objects = new();
    readonly Dictionary<System.IntPtr, int> _savedLayers = new();
    UnityEngine.Camera _cam;
    RenderTexture _rt;
    GameObject _quad;
    // With pits in view the ground is a cell mesh with holes instead of the quad. Its UVs are 2D world
    // units; the material's texture scale/offset maps them into the capture each frame.
    GameObject _holed;
    Material _holedMat;
    Mesh _holedMesh;
    bool _useHoled;
    int _layer = -1;
    float _radius;
    int _frame;
    StageLayers _layers;

    public bool Active { get; private set; }

    public void Begin(Transform worldRoot, StageLayers layers, float radiusMetres, Material unlitTemplate, int sortingLayer)
    {
        End();
        _layers = layers;
        _layer = FindFreeLayer();
        if (_layer < 0 || unlitTemplate == null)
        {
            Plugin.Log.LogWarning("Ground capture: no free layer or material; skipped");
            return;
        }
        _radius = radiusMetres;
        _rt = new RenderTexture(Resolution, Resolution, 16) { name = "VS3D_Ground", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        _rt.Create();

        var camGo = new GameObject("VS3D_GroundCapture");
        Object.DontDestroyOnLoad(camGo);
        _cam = camGo.AddComponent<UnityEngine.Camera>();
        _cam.orthographic = true;
        _cam.cullingMask = 1 << _layer;
        _cam.clearFlags = CameraClearFlags.SolidColor;
        _cam.backgroundColor = Color.black;
        _cam.targetTexture = _rt;
        _cam.nearClipPlane = 0.01f;
        _cam.farClipPlane = 1000f;
        _cam.depth = -60f;

        _quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        var col = _quad.GetComponent<UnityEngine.Collider>();
        if (col != null) Object.Destroy(col);
        _quad.name = "VS3D_GroundPlane";
        _quad.transform.SetParent(worldRoot, false);
        _quad.layer = RenderLayer.Index;
        var mat = new Material(unlitTemplate) { mainTexture = _rt };
        var r = _quad.GetComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.sortingLayerID = sortingLayer;
        r.sortingOrder = -32000;
        _quad.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        _holed = new GameObject("VS3D_GroundHoled");
        _holed.transform.SetParent(worldRoot, false);
        _holed.layer = RenderLayer.Index;
        _holedMesh = new Mesh { name = "VS3D_GroundHoled", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        _holed.AddComponent<MeshFilter>().sharedMesh = _holedMesh;
        _holedMat = new Material(unlitTemplate) { mainTexture = _rt };
        if (_holedMat.HasProperty("_Cull")) _holedMat.SetFloat("_Cull", 0f);
        var hr = _holed.AddComponent<MeshRenderer>();
        hr.sharedMaterial = _holedMat;
        hr.sortingLayerID = sortingLayer;
        hr.sortingOrder = -32000;
        _holed.SetActive(false);
        _useHoled = false;

        MoveLayers();
        Active = true;
        Plugin.Log.LogInfo($"Ground capture: {_objects.Count} ground object(s) on layer {_layer}");
    }

    public void End()
    {
        RestoreLayers();
        if (_cam != null && _cam) Object.Destroy(_cam.gameObject);
        if (_quad != null && _quad) Object.Destroy(_quad);
        if (_holed != null && _holed) Object.Destroy(_holed);
        if (_holedMesh != null && _holedMesh) Object.Destroy(_holedMesh);
        _holed = null; _holedMesh = null; _useHoled = false;
        if (_rt != null && _rt) { _rt.Release(); Object.Destroy(_rt); }
        _cam = null; _quad = null; _rt = null;
        Active = false;
    }

    public void SetEnabled(bool on)
    {
        if (!Active) return;
        if (on) MoveLayers(); else RestoreLayers();
        _cam.enabled = on;
        _quad.SetActive(on && !_useHoled);
        _holed.SetActive(on && _useHoled);
    }

    public void Tick(Vector2 player2D)
    {
        if (!Active || !_cam.enabled) return;
        if (++_frame % 120 == 0) MoveLayers();   // pick up map copies the game creates later

        float units = _radius / Mathf.Max(CoordinateMapper.Scale, 0.0001f);
        float texel = units * 2f / Resolution;
        // Snap to whole texels so the ground doesn't shimmer while walking.
        var c = new Vector2(Mathf.Round(player2D.x / texel) * texel, Mathf.Round(player2D.y / texel) * texel);
        _cam.orthographicSize = units;
        _cam.transform.SetPositionAndRotation(new Vector3(c.x, c.y, -500f), Quaternion.identity);

        var center3D = CoordinateMapper.ToWorld3D(c);
        _quad.transform.position = new Vector3(center3D.x, center3D.y - 0.02f, center3D.z);
        _quad.transform.localScale = new Vector3(_radius * 2f, _radius * 2f, 1f);
        if (_useHoled)
        {
            // uv (2D units) -> capture texture coordinates.
            float sc = 1f / (2f * units);
            _holedMat.mainTextureScale = new Vector2(sc, sc);
            _holedMat.mainTextureOffset = new Vector2(-(c.x - units) * sc, -(c.y - units) * sc);
        }
    }

    // Ground for the window [x0, x0+w) x [y0, y0+h) of 2D cells (cw, ch = cell size in 2D units) with
    // the cells marked in `hole` left out. hole == null: plain quad again.
    public void SetHoles(bool[] hole, int x0, int y0, int w, int h, float cw, float ch)
    {
        if (!Active) return;
        _useHoled = hole != null;
        if (_useHoled)
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            for (int y = 0; y < h; y++)
            {
                int x = 0;
                while (x < w)
                {
                    if (hole[y * w + x]) { x++; continue; }
                    int x1 = x;
                    while (x1 + 1 < w && !hole[y * w + x1 + 1]) x1++;
                    var a = new Vector2((x0 + x) * cw, (y0 + y) * ch);
                    var b = new Vector2((x0 + x1 + 1) * cw, (y0 + y + 1) * ch);
                    int i = v.Count;
                    AddVertex(v, uv, a.x, a.y); AddVertex(v, uv, b.x, a.y); AddVertex(v, uv, b.x, b.y); AddVertex(v, uv, a.x, b.y);
                    t.Add(i); t.Add(i + 2); t.Add(i + 1);
                    t.Add(i); t.Add(i + 3); t.Add(i + 2);
                    x = x1 + 1;
                }
            }
            _holedMesh.Clear();
            _holedMesh.vertices = v.ToArray();
            _holedMesh.uv = uv.ToArray();
            _holedMesh.triangles = t.ToArray();
            _holedMesh.RecalculateBounds();
            _holed.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        }
        bool on = _cam.enabled;
        _quad.SetActive(on && !_useHoled);
        _holed.SetActive(on && _useHoled);
    }

    static void AddVertex(List<Vector3> v, List<Vector2> uv, float x2, float y2)
    {
        var p = CoordinateMapper.ToWorld3D(new Vector2(x2, y2));
        v.Add(new Vector3(p.x, p.y - 0.02f, p.z));
        uv.Add(new Vector2(x2, y2));
    }

    void MoveLayers()
    {
        if (_layers == null || _layer < 0) return;
        _objects.Clear();
        _objects.AddRange(_layers.GroundObjects());
        foreach (var go in _objects)
        {
            if (go == null || !go) continue;
            if (!_savedLayers.ContainsKey(go.Pointer))
                _savedLayers[go.Pointer] = go.layer;
            go.layer = _layer;
        }
    }

    void RestoreLayers()
    {
        foreach (var go in _objects)
            if (go != null && go && _savedLayers.TryGetValue(go.Pointer, out var l))
                go.layer = l;
        _savedLayers.Clear();
    }

    static int FindFreeLayer()
    {
        for (int i = 30; i >= 8; i--)
            if (i != RenderLayer.Index && string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                return i;
        return -1;
    }
}
