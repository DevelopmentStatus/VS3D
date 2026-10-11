using UnityEngine;
using VS3D.Bootstrap;

namespace VS3D.Rendering;

// The layer every VS3D object lives on; only the 3D camera draws it.
public static class RenderLayer
{
    public static int Index { get; private set; } = 31;
    public static int Mask => 1 << Index;

    public static void Resolve()
    {
        for (int i = 31; i >= 8; i--)
        {
            if (string.IsNullOrEmpty(LayerMask.LayerToName(i)))
            {
                Index = i;
                Plugin.Log.LogInfo($"VS3D render layer: {i}");
                return;
            }
        }
        Plugin.Log.LogWarning("No unnamed layer found; using layer 31");
        Index = 31;
    }

    public static void Apply(GameObject go) => go.layer = Index;
}
