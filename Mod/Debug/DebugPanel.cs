using UnityEngine;
using VS3D.Rendering;
using VS3D.SimulationBridge;
using Bridge = VS3D.SimulationBridge.SimulationBridge;

namespace VS3D.Debug;

// Plain IMGUI (no GUILayout / GUI.Window) to avoid IL2CPP delegate and params-array pitfalls.
public sealed class DebugPanel
{
    public bool Visible;
    public bool ShowGround = true;
    public bool CategoryTint;
    public bool InvertCulling;
    public bool Request3D;
    public bool RequestRestore;

    float _fps;
    readonly int[] _counts = new int[6];

    public void Tick()
    {
        float dt = Time.unscaledDeltaTime;
        if (dt > 0f)
            _fps = Mathf.Lerp(_fps, 1f / dt, 0.05f);
    }

    public void Draw(Bridge bridge, bool active3D, UnityEngine.Camera cam, float yaw, float distance, int proxies)
    {
        if (!Visible)
            return;

        System.Array.Clear(_counts, 0, _counts.Length);
        int visible = 0;
        var list = bridge.Entities;
        for (int i = 0; i < list.Count; i++)
        {
            _counts[(int)list[i].Snapshot.Category]++;
            if (list[i].Snapshot.Visible) visible++;
        }

        const float w = 330f;
        var r = new Rect(10, 10, w, 356);
        GUI.Box(r, "VS3D Renderer Debug");
        float y = 34;
        Line(ref y, $"FPS: {_fps:0.0}");
        Line(ref y, $"In stage: {bridge.InStage}   3D view: {active3D}");
        Line(ref y, $"Tracked: {list.Count} (visible {visible})   proxies: {proxies}");
        Line(ref y, $"Player {_counts[1]}  Enemy {_counts[2]}  Boss {_counts[3]}");
        Line(ref y, $"Projectile {_counts[4]}  Pickup {_counts[5]}  Other {_counts[0]}");
        Line(ref y, $"Discovered last scan: {bridge.DiscoveredLastScan}");
        Line(ref y, $"World scale: {CoordinateMapper.Scale:0.###}   layer: {RenderLayer.Index}");
        Line(ref y, $"Player 2D: {bridge.PlayerPosition2D}");
        if (cam != null)
            Line(ref y, $"Cam: {cam.transform.position}  yaw {yaw:0}  dist {distance:0.0}  cursor {Cursor.lockState}");

        y += 6;
        bool want3D = GUI.Toggle(new Rect(20, y, w - 20, 22), active3D, " Enable 3D renderer (F8)");
        if (want3D != active3D) Request3D = true;
        y += 24;
        ShowGround = GUI.Toggle(new Rect(20, y, w - 20, 22), ShowGround, " Show ground plane");
        y += 24;
        CategoryTint = GUI.Toggle(new Rect(20, y, w - 20, 22), CategoryTint, " Tint proxies by category");
        y += 24;
        InvertCulling = GUI.Toggle(new Rect(20, y, w - 20, 22), InvertCulling, " Invert mesh culling (fallback)");
        y += 28;
        if (GUI.Button(new Rect(20, y, w - 30, 26), "Restore original camera"))
            RequestRestore = true;
    }

    static void Line(ref float y, string text)
    {
        GUI.Label(new Rect(20, y, 320, 22), text);
        y += 20;
    }
}
