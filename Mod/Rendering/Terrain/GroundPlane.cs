using System;
using System.IO;
using Il2CppInterop.Runtime;
using UnityEngine;
using VS3D.Bootstrap;

namespace VS3D.Rendering.Terrain;

// A large tiled ground sprite at Y=0 that follows the player in tile-sized steps.
// Placeholder until Phase 5 reconstructs the stage's own tilemaps.
public sealed class GroundPlane
{
    const float TileMetres = 4f;      // Unity caps tiled sprites at ~65k vertices: keep (Size/Tile)^2 small
    const float SizeMetres = 240f;

    static AssetBundle s_bundle;
    static Texture2D s_bundleTex;

    GameObject _go;
    SpriteRenderer _sr;

    public void Create(Transform worldRoot, string bundleDir)
    {
        var tex = LoadBundleTexture(bundleDir) ?? MakeChecker();
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Point;
        var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f),
            tex.width / TileMetres, 0, SpriteMeshType.FullRect);

        _go = new GameObject("VS3D_Ground");
        _go.transform.SetParent(worldRoot, false);
        RenderLayer.Apply(_go);
        _sr = _go.AddComponent<SpriteRenderer>();
        _sr.sprite = sprite;
        _sr.drawMode = SpriteDrawMode.Tiled;
        _sr.size = new Vector2(SizeMetres, SizeMetres);
        _sr.sortingOrder = -32000;
        _go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
    }

    public void Configure(Material material, int sortingLayerId)
    {
        if (_sr == null)
            return;
        if (material != null)
            _sr.sharedMaterial = material;
        _sr.sortingLayerID = sortingLayerId;
    }

    public void Follow(Vector3 player)
    {
        if (_go == null)
            return;
        float step = TileMetres * 2f;
        _go.transform.position = new Vector3(
            Mathf.Round(player.x / step) * step, player.y - 0.01f, Mathf.Round(player.z / step) * step);
    }

    public void SetVisible(bool visible)
    {
        if (_go != null)
            _go.SetActive(visible);
    }

    static Texture2D LoadBundleTexture(string bundleDir)
    {
        var path = Path.Combine(bundleDir, "vs3d_core");
        if (!File.Exists(path))
            return null;
        try
        {
            s_bundle ??= NativeAssetBundle.LoadFromFile(path);
            if (s_bundle == null)
                return null;
            s_bundleTex ??= NativeAssetBundle.LoadAsset<Texture2D>(s_bundle, "ground_checker");
            var tex = s_bundleTex;
            if (tex != null)
            {
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                Plugin.Log.LogInfo("Ground: texture loaded from vs3d_core bundle");
            }
            return tex;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Ground: bundle load failed; using generated texture\n{ex}");
            return null;
        }
    }

    static Texture2D MakeChecker()
    {
        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var a = new Color32(52, 74, 44, 255);
        var b = new Color32(60, 84, 50, 255);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                tex.SetPixel(x, y, ((x / 32) + (y / 32)) % 2 == 0 ? a : b);
        tex.Apply();
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return tex;
    }
}
