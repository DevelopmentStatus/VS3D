using UnityEngine;
using VS3D.Bootstrap;

namespace VS3D.Camera;

// Switches presentation between the original 2D camera and the 3D camera.
// The original camera keeps its transform, ortho size and ProCamera2D control:
// Stage derives its spawn/despawn rects from it, so it must stay authoritative.
//
// Vampire Survivors renders the world through 'Main Camera' into a render texture, which the
// separate 'UI Camera' composites to the screen together with the HUD. So the 3D camera renders
// into that same texture, right after the original camera (whose culling mask is emptied).
// The game's own compositing, scaling and HUD then work unchanged.
// If the original camera ever renders straight to the screen, fall back to taking its place there
// and redirecting it into an offscreen texture of the same size.
public sealed class CameraToggle
{
    public bool Active { get; private set; }
    public UnityEngine.Camera Original { get; private set; }

    int _savedMask;
    float _savedDepth;
    RenderTexture _savedTarget;
    RenderTexture _sink;            // only used in the screen fallback
    RenderTexture _own;             // the 3D camera's own target, copied into the game's world texture
    bool _sharedTarget;
    readonly HudRelay _hud = new();

    public void Enable(UnityEngine.Camera thirdPerson)
    {
        if (Active)
            return;
        Original = UnityEngine.Camera.main;
        if (Original == null)
        {
            Plugin.Log.LogWarning("CameraToggle: no main camera; staying in 2D");
            return;
        }

        _savedMask = Original.cullingMask;
        _savedDepth = Original.depth;
        _savedTarget = Original.targetTexture;
        _sharedTarget = _savedTarget != null;

        Original.cullingMask = 0;
        if (_sharedTarget)
        {
            // Render into our own texture of the same size, then copy into the game's world texture.
            EnsureOwn(_savedTarget);
            thirdPerson.targetTexture = _own;
            thirdPerson.depth = _savedDepth + 0.25f;
            CullingFix.CopySource = _own;
            CullingFix.CopyTarget = _savedTarget;
            Plugin.Log.LogInfo($"3D view on: rendering {_own.width}x{_own.height}, copied into the game's world texture");
        }
        else
        {
            EnsureSink();
            Original.targetTexture = _sink;
            thirdPerson.targetTexture = null;
            thirdPerson.depth = _savedDepth - 0.5f;
            _hud.Enable(Original, thirdPerson);
            Plugin.Log.LogInfo($"3D view on: original camera renders to the screen; redirected offscreen {_sink.width}x{_sink.height}");
        }
        thirdPerson.enabled = true;
        Active = true;
    }

    // Call every frame while active: follows window resizes and texture swaps.
    public void Tick(UnityEngine.Camera thirdPerson)
    {
        if (!Active)
            return;
        if (Original == null || !Original)
        {
            Plugin.Log.LogInfo("CameraToggle: original camera destroyed");
            Disable(thirdPerson);
            return;
        }
        if (_sharedTarget)
        {
            // The game may recreate its world texture (resolution change); follow it.
            var current = Original.targetTexture;
            if (current != null && (current.Pointer != _savedTarget.Pointer || _own == null || !_own || _own.width != current.width || _own.height != current.height))
            {
                _savedTarget = current;
                thirdPerson.targetTexture = null;
                ReleaseOwn();
                EnsureOwn(current);
                thirdPerson.targetTexture = _own;
                CullingFix.CopySource = _own;
                CullingFix.CopyTarget = current;
            }
            return;
        }
        _hud.Tick(Original);
        if (_sink.width != Screen.width || _sink.height != Screen.height)
        {
            Original.targetTexture = null;
            ReleaseSink();
            EnsureSink();
            Original.targetTexture = _sink;
        }
    }

    public void Disable(UnityEngine.Camera thirdPerson)
    {
        if (!Active)
            return;
        if (thirdPerson != null)
        {
            thirdPerson.enabled = false;
            thirdPerson.targetTexture = null;
        }
        CullingFix.CopySource = null;
        CullingFix.CopyTarget = null;
        ReleaseOwn();
        if (!_sharedTarget)
            _hud.Disable();
        if (Original != null && Original)
        {
            Original.targetTexture = _savedTarget;
            Original.cullingMask = _savedMask;
            Original.depth = _savedDepth;
        }
        ReleaseSink();
        Original = null;
        Active = false;
        Plugin.Log.LogInfo("3D view off; original camera restored");
    }

    void EnsureOwn(RenderTexture like)
    {
        if (_own != null && _own)
            return;
        _own = new RenderTexture(like.width, like.height, 24, like.format);
        _own.name = "VS3D_View";
        _own.filterMode = like.filterMode;
        _own.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _own.Create();
    }

    void ReleaseOwn()
    {
        if (_own == null)
            return;
        if (_own)
        {
            _own.Release();
            Object.Destroy(_own);
        }
        _own = null;
    }

    void EnsureSink()
    {
        if (_sink != null && _sink)
            return;
        _sink = new RenderTexture(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height), 16);
        _sink.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _sink.Create();
    }

    void ReleaseSink()
    {
        if (_sink == null)
            return;
        if (_sink)
        {
            _sink.Release();
            Object.Destroy(_sink);
        }
        _sink = null;
    }
}
