using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using VS3D.Bootstrap;

namespace VS3D.Camera;

// Render-loop hooks for the 3D camera.
// - Copy: the 3D camera renders into its own ordinary render texture; right after it renders, the
//   image is copied into the game's world texture (which the game's UI camera composites with the HUD).
//   Rendering straight into the game's texture came out with inverted face culling.
// - Invert: legacy fallback (debug panel) that flips culling while the 3D camera renders.
public static class CullingFix
{
    public static bool Invert;
    public static RenderTexture CopySource;
    public static RenderTexture CopyTarget;
    static IntPtr s_camera;
    static bool s_hooked;

    public static void Install(UnityEngine.Camera camera)
    {
        s_camera = camera.Pointer;
        if (s_hooked)
            return;
        try
        {
            RenderPipelineManager.add_beginCameraRendering(
                DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, UnityEngine.Camera>>(
                    new Action<ScriptableRenderContext, UnityEngine.Camera>(OnBegin)));
            RenderPipelineManager.add_endCameraRendering(
                DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, UnityEngine.Camera>>(
                    new Action<ScriptableRenderContext, UnityEngine.Camera>(OnEnd)));
            s_hooked = true;
            Plugin.Log.LogInfo("3D camera render hooks installed");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"3D camera render hooks unavailable: {ex.Message}");
        }
    }

    static void OnBegin(ScriptableRenderContext ctx, UnityEngine.Camera cam)
    {
        if (Invert && cam != null && cam.Pointer == s_camera)
            GL.invertCulling = true;
    }

    static void OnEnd(ScriptableRenderContext ctx, UnityEngine.Camera cam)
    {
        if (cam == null || cam.Pointer != s_camera)
            return;
        GL.invertCulling = false;
        if (CopySource != null && CopyTarget != null && CopySource && CopyTarget)
            Graphics.Blit(CopySource, CopyTarget);
    }
}
