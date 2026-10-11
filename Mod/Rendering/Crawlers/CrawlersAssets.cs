using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using VS3D.Bootstrap;
using VS3D.Config;

namespace VS3D.Rendering.Crawlers;

// Loads Vampire Crawlers' Addressables bundles straight from the user's own install
// (same Unity 6000.0.62 build, so they are binary-compatible). Nothing is copied.
// Bundles are loaded by file name, dependencies first; the Addressables catalog isn't needed.
public sealed class CrawlersAssets
{
    static readonly string[] CoreBundles =
    {
        "d087183013b85773d5651fd7400d79c5_unitybuiltinassets.bundle",
        "d087183013b85773d5651fd7400d79c5_monoscripts.bundle",
        "biome-sharedshaders_assets_all.bundle",
        "biome-shared_assets_all.bundle",
        "tilemapmaterials_assets_all.bundle",
    };

    readonly Dictionary<string, AssetBundle> _bundles = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, GameObject> _prefabs = new(StringComparer.OrdinalIgnoreCase);
    string _dir;

    public bool Available { get; private set; }

    public bool Init()
    {
        _dir = Path.Combine(ModConfig.CrawlersPath.Value, "Vampire Crawlers_Data", "StreamingAssets", "aa", "StandaloneWindows64");
        if (!Directory.Exists(_dir))
        {
            Plugin.Log.LogWarning($"Crawlers: bundle folder not found: {_dir}");
            return false;
        }
        foreach (var name in CoreBundles)
            if (Load(name) == null)
                return false;
        Available = true;
        Plugin.Log.LogInfo("Crawlers: core bundles loaded");
        return true;
    }

    // biome: "inlaidlibrary", "madforest", "gallotower", "dairyplant", "capellamagna", "tinybridge", ...
    public bool LoadBiome(string biome) => Available && Load($"biome-{biome}_assets_all.bundle") != null;

    AssetBundle Load(string file)
    {
        if (_bundles.TryGetValue(file, out var b))
            return b;
        var path = Path.Combine(_dir, file);
        try
        {
            b = File.Exists(path) ? NativeAssetBundle.LoadFromFile(path) : null;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Crawlers: failed to load {file}: {ex.Message}");
            b = null;
        }
        if (b == null)
        {
            Plugin.Log.LogError($"Crawlers: could not load {file}");
            return null;
        }
        b.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _bundles[file] = b;
        Plugin.Log.LogInfo($"Crawlers: loaded {file}");
        return b;
    }

    // assetPath as listed in Tooling/Documentation/CrawlersAssets.md, e.g.
    // "Assets/_GameAssets/Art/Levels/InlaidLibrary/Prefabs/Props/Bookshelves_Set_01.prefab"
    public GameObject GetPrefab(string assetPath)
    {
        if (_prefabs.TryGetValue(assetPath, out var cached))
            return cached;
        GameObject found = null;
        foreach (var b in _bundles.Values)
        {
            found = NativeAssetBundle.LoadAsset<GameObject>(b, assetPath);
            if (found != null)
                break;
        }
        if (found == null)
            Plugin.Log.LogWarning($"Crawlers: prefab not found: {assetPath}");
        _prefabs[assetPath] = found;
        return found;
    }

    // Crawlers prefabs carry Crawlers' own MonoBehaviours (procedural-generation IDs etc.). Their scripts
    // don't exist in this game, so every Instantiate logs "referenced script is missing" warnings.
    // Instead, each prefab is copied once into a clean template holding only transforms, meshes,
    // materials and sprites, and instances are cloned from that.
    static GameObject s_templateRoot;
    readonly Dictionary<string, GameObject> _templates = new(StringComparer.OrdinalIgnoreCase);

    GameObject GetTemplate(string assetPath)
    {
        if (_templates.TryGetValue(assetPath, out var t))
            return t;
        var prefab = GetPrefab(assetPath);
        if (prefab != null)
        {
            if (s_templateRoot == null || !s_templateRoot)
            {
                s_templateRoot = new GameObject("VS3D_CrawlersTemplates");
                s_templateRoot.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(s_templateRoot);
            }
            t = CopyClean(prefab.transform, s_templateRoot.transform);
        }
        _templates[assetPath] = t;
        return t;
    }

    static GameObject CopyClean(Transform src, Transform parent)
    {
        var go = new GameObject(src.name);
        var t = go.transform;
        t.SetParent(parent, false);
        t.localPosition = src.localPosition;
        t.localRotation = src.localRotation;
        t.localScale = src.localScale;
        go.SetActive(src.gameObject.activeSelf);

        var mf = src.GetComponent<MeshFilter>();
        var mr = src.GetComponent<MeshRenderer>();
        if (mf != null && mr != null && mf.sharedMesh != null)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = mr.sharedMaterials;
            r.enabled = mr.enabled;
        }
        var sr = src.GetComponent<SpriteRenderer>();
        if (sr != null && sr.sprite != null)
        {
            var r = go.AddComponent<SpriteRenderer>();
            r.sprite = sr.sprite;
            r.color = sr.color;
            r.flipX = sr.flipX;
            r.flipY = sr.flipY;
            r.sharedMaterial = sr.sharedMaterial;
            r.enabled = sr.enabled;
        }
        for (int i = 0; i < src.childCount; i++)
            CopyClean(src.GetChild(i), t);
        return go;
    }

    // Instantiates a (clean) prefab copy into the VS3D world: render layer + shared sorting layer.
    public GameObject Spawn(string assetPath, Transform parent, Vector3 position, Quaternion rotation, float scale, int sortingLayerId)
    {
        var template = GetTemplate(assetPath);
        if (template == null)
            return null;
        var go = UnityEngine.Object.Instantiate(template, parent).Cast<GameObject>();
        go.SetActive(true);
        go.transform.SetPositionAndRotation(position, rotation);
        go.transform.localScale = Vector3.one * scale;
        PrepareInstance(go, sortingLayerId);
        return go;
    }

    public static void PrepareInstance(GameObject go, int sortingLayerId)
    {
        var all = go.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
            all[i].gameObject.layer = RenderLayer.Index;
        var renderers = go.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
            renderers[i].sortingLayerID = sortingLayerId;
    }

    public static Bounds RendererBounds(GameObject go)
    {
        var rs = go.GetComponentsInChildren<Renderer>(true);
        if (rs.Length == 0)
            return new Bounds(go.transform.position, Vector3.zero);
        var b = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++)
            b.Encapsulate(rs[i].bounds);
        return b;
    }
}
