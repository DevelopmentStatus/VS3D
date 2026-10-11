using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using VS3D.Bootstrap;

namespace VS3D.Camera;

// Keeps the game's HUD on screen while the original camera renders offscreen.
// - URP overlay cameras stacked on the original camera are moved to the 3D camera's stack.
// - Screen-space-camera canvases bound to the original camera are switched to overlay.
// Both are restored exactly when the 3D view turns off.
public sealed class HudRelay
{
    readonly List<UnityEngine.Camera> _movedOverlays = new();
    readonly Dictionary<System.IntPtr, Canvas> _convertedCanvases = new();
    UniversalAdditionalCameraData _origData;
    UniversalAdditionalCameraData _ourData;
    int _frame;
    bool _dumped;

    public void Enable(UnityEngine.Camera original, UnityEngine.Camera ours)
    {
        _origData = original.GetComponent<UniversalAdditionalCameraData>();
        _ourData = ours.GetComponent<UniversalAdditionalCameraData>() ?? ours.gameObject.AddComponent<UniversalAdditionalCameraData>();
        _ourData.renderType = CameraRenderType.Base;

        if (!_dumped)
        {
            _dumped = true;
            Dump(original);
        }

        _movedOverlays.Clear();
        var stack = _origData != null ? _origData.cameraStack : null;
        if (stack != null && stack.Count > 0)
        {
            for (int i = 0; i < stack.Count; i++)
            {
                var cam = stack[i];
                if (cam == null) continue;
                _movedOverlays.Add(cam);
                _ourData.cameraStack.Add(cam);
            }
            stack.Clear();
            Plugin.Log.LogInfo($"HUD: moved {_movedOverlays.Count} overlay camera(s) to the 3D camera stack");
        }
        ConvertCanvases(original);
    }

    // Catch HUD canvases created after the toggle (popups, level-up screens).
    public void Tick(UnityEngine.Camera original)
    {
        if (++_frame % 30 == 0 && original != null)
            ConvertCanvases(original);
    }

    public void Disable()
    {
        if (_ourData != null)
            _ourData.cameraStack.Clear();
        if (_origData != null && _origData)
            foreach (var cam in _movedOverlays)
                if (cam != null && cam)
                    _origData.cameraStack.Add(cam);
        _movedOverlays.Clear();

        foreach (var canvas in _convertedCanvases.Values)
        {
            if (canvas == null || !canvas)
                continue;
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
        }
        _convertedCanvases.Clear();
    }

    void ConvertCanvases(UnityEngine.Camera original)
    {
        var found = Object.FindObjectsByType(Il2CppType.Of<Canvas>(), FindObjectsInactive.Include, FindObjectsSortMode.None);
        int converted = 0;
        for (int i = 0; i < found.Length; i++)
        {
            var canvas = found[i]?.TryCast<Canvas>();
            if (canvas == null || !canvas.isRootCanvas || canvas.renderMode != RenderMode.ScreenSpaceCamera)
                continue;
            var wc = canvas.worldCamera;
            if (wc == null || wc.Pointer != original.Pointer)
                continue;
            if (_convertedCanvases.ContainsKey(canvas.Pointer))
                continue;
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _convertedCanvases[canvas.Pointer] = canvas;
            converted++;
        }
        if (converted > 0)
            Plugin.Log.LogInfo($"HUD: switched {converted} canvas(es) from the original camera to overlay");
    }

    static void Dump(UnityEngine.Camera original)
    {
        var sb = new StringBuilder("Scene camera/canvas layout:\n");
        var cams = Object.FindObjectsByType(Il2CppType.Of<UnityEngine.Camera>(), FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < cams.Length; i++)
        {
            var c = cams[i].TryCast<UnityEngine.Camera>();
            if (c == null) continue;
            var d = c.GetComponent<UniversalAdditionalCameraData>();
            sb.AppendLine($"  camera '{c.name}' enabled={c.enabled} depth={c.depth} ortho={c.orthographic} mask=0x{c.cullingMask:X8} " +
                          $"type={(d != null ? d.renderType.ToString() : "-")} stack={(d != null && d.cameraStack != null ? d.cameraStack.Count : 0)} main={c.Pointer == original.Pointer}");
        }
        var canvases = Object.FindObjectsByType(Il2CppType.Of<Canvas>(), FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < canvases.Length; i++)
        {
            var cv = canvases[i].TryCast<Canvas>();
            if (cv == null || !cv.isRootCanvas) continue;
            sb.AppendLine($"  canvas '{cv.name}' mode={cv.renderMode} cam={(cv.worldCamera != null ? cv.worldCamera.name : "-")} layer={cv.gameObject.layer} order={cv.sortingOrder} active={cv.gameObject.activeInHierarchy}");
        }
        Plugin.Log.LogInfo(sb.ToString());
    }
}
