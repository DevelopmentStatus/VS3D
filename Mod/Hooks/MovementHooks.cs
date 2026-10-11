using System;
using HarmonyLib;
using UnityEngine;
using VS3D.Bootstrap;
using RewiredPlayer = Rewired.Player;

namespace VS3D.Hooks;

// Camera-relative movement: rotates the local player's movement input by the 3D camera yaw at the
// Rewired source, so "forward" means "away from the camera". Everything downstream (movement,
// facing, direction-based weapons) sees consistent input; collision and physics are untouched.
//
// The game's own HandlePlayerInput/ProcessRawDirection are inlined by IL2CPP, so the Rewired
// Player.GetAxis* family (id and name overloads) is patched instead; the game reads movement
// through GetAxis("Move Horizontal" / "Move Vertical").
[HarmonyPatch]
internal static class MovementHooks
{
    public static bool Enabled;              // set each frame by the runtime
    public static float Yaw;                 // camera yaw in degrees
    public static IntPtr LocalPlayerInput;   // Rewired.Player of the local character (may be zero)

    static int s_h = int.MinValue, s_v = int.MinValue;
    static bool s_reentrant;
    static bool s_confirmed;

    static void EnsureIds()
    {
        if (s_h != int.MinValue) return;
        s_h = RewiredConsts.Action.Move_Horizontal;
        s_v = RewiredConsts.Action.Move_Vertical;
    }

    // If the local player's Rewired.Player pointer is unknown, accept any player (single-player).
    static bool IsLocal(RewiredPlayer p) =>
        p != null && (LocalPlayerInput == IntPtr.Zero || p.Pointer == LocalPlayerInput);

    static Vector2 Rotate(Vector2 v, string via)
    {
        float r = Yaw * Mathf.Deg2Rad;
        float cos = Mathf.Cos(r), sin = Mathf.Sin(r);
        // Input (x = right, y = forward) onto the 2D plane as seen by the 3D camera.
        var result = new Vector2(v.x * cos + v.y * sin, -v.x * sin + v.y * cos);
        if (!s_confirmed && v.sqrMagnitude > 0.01f)
        {
            s_confirmed = true;
            Plugin.Log.LogInfo($"Camera-relative movement active via {via} ({v} -> {result} at yaw {Yaw:0})");
        }
        return result;
    }

    // ---- id overloads ----

    static void RotateSingleId(RewiredPlayer p, int id, ref float result, bool raw, string via)
    {
        EnsureIds();
        if (!Enabled || s_reentrant || (id != s_h && id != s_v) || !IsLocal(p))
            return;
        s_reentrant = true;
        try
        {
            float h = raw ? p.GetAxisRaw(s_h) : p.GetAxis(s_h);
            float v = raw ? p.GetAxisRaw(s_v) : p.GetAxis(s_v);
            var r = Rotate(new Vector2(h, v), via);
            result = id == s_h ? r.x : r.y;
        }
        catch (Exception ex) { Fail(ex); }
        finally { s_reentrant = false; }
    }

    [HarmonyPatch(typeof(RewiredPlayer), nameof(RewiredPlayer.GetAxis), typeof(int))]
    [HarmonyPostfix]
    static void GetAxisId(RewiredPlayer __instance, int actionId, ref float __result)
    {
        if (!s_reentrant) RotateSingleId(__instance, actionId, ref __result, false, "GetAxis(int)");
    }

    [HarmonyPatch(typeof(RewiredPlayer), nameof(RewiredPlayer.GetAxisRaw), typeof(int))]
    [HarmonyPostfix]
    static void GetAxisRawId(RewiredPlayer __instance, int actionId, ref float __result)
    {
        if (!s_reentrant) RotateSingleId(__instance, actionId, ref __result, true, "GetAxisRaw(int)");
    }

    [HarmonyPatch(typeof(RewiredPlayer), nameof(RewiredPlayer.GetAxis2D), typeof(int), typeof(int))]
    [HarmonyPostfix]
    static void GetAxis2DId(RewiredPlayer __instance, int xAxisActionId, int yAxisActionId, ref Vector2 __result)
    {
        if (s_reentrant) return;
        EnsureIds();
        if (Enabled && xAxisActionId == s_h && yAxisActionId == s_v && IsLocal(__instance))
            __result = Rotate(__result, "GetAxis2D(int,int)");
    }

    // ---- name overloads ----

    static bool IsMoveName(string name) =>
        name != null && name.IndexOf("UI", StringComparison.OrdinalIgnoreCase) < 0 &&
        (name.IndexOf("Horizontal", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Vertical", StringComparison.OrdinalIgnoreCase) >= 0);

    static void RotateSingleName(RewiredPlayer p, string name, ref float result, bool raw, string via)
    {
        if (!Enabled || s_reentrant || !IsMoveName(name) || !IsLocal(p))
            return;
        bool isH = name.IndexOf("Horizontal", StringComparison.OrdinalIgnoreCase) >= 0;
        string hName = isH ? name : name.Replace("Vertical", "Horizontal");
        string vName = isH ? name.Replace("Horizontal", "Vertical") : name;
        s_reentrant = true;
        try
        {
            float h = raw ? p.GetAxisRaw(hName) : p.GetAxis(hName);
            float v = raw ? p.GetAxisRaw(vName) : p.GetAxis(vName);
            var r = Rotate(new Vector2(h, v), via);
            result = isH ? r.x : r.y;
        }
        catch (Exception ex) { Fail(ex); }
        finally { s_reentrant = false; }
    }

    [HarmonyPatch(typeof(RewiredPlayer), nameof(RewiredPlayer.GetAxis), typeof(string))]
    [HarmonyPostfix]
    static void GetAxisName(RewiredPlayer __instance, string actionName, ref float __result)
    {
        if (!s_reentrant) RotateSingleName(__instance, actionName, ref __result, false, "GetAxis(string)");
    }

    [HarmonyPatch(typeof(RewiredPlayer), nameof(RewiredPlayer.GetAxisRaw), typeof(string))]
    [HarmonyPostfix]
    static void GetAxisRawName(RewiredPlayer __instance, string actionName, ref float __result)
    {
        if (!s_reentrant) RotateSingleName(__instance, actionName, ref __result, true, "GetAxisRaw(string)");
    }

    [HarmonyPatch(typeof(RewiredPlayer), nameof(RewiredPlayer.GetAxis2D), typeof(string), typeof(string))]
    [HarmonyPostfix]
    static void GetAxis2DName(RewiredPlayer __instance, string xAxisActionName, string yAxisActionName, ref Vector2 __result)
    {
        if (s_reentrant) return;
        if (Enabled && IsMoveName(xAxisActionName) && IsLocal(__instance))
            __result = Rotate(__result, "GetAxis2D(string,string)");
    }

    static void Fail(Exception ex)
    {
        Enabled = false;
        Plugin.Log.LogError($"Camera-relative movement disabled: {ex}");
    }
}
