using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VS3D.Rendering.Maps;

// Builds meshes out of the stage's own tile sprites (one submesh object per atlas texture).
// Used for extruded walls, standees and flat decals. Materials are unlit, double-sided clones.
public sealed class ArtMesh
{
    sealed class Batch
    {
        public readonly List<Vector3> V = new();
        public readonly List<Vector2> UV = new();
        public readonly List<int> T = new();
    }

    readonly Dictionary<System.IntPtr, (Texture tex, Batch batch)> _batches = new();

    public bool Empty => _batches.Count == 0;

    // Quad with corners bottom-left, bottom-right, top-right, top-left (as seen from its front).
    public void Quad(Vector3 bl, Vector3 br, Vector3 tr, Vector3 tl, Sprite s)
    {
        if (s == null) return;
        var tex = s.texture;
        if (tex == null) return;
        if (!_batches.TryGetValue(tex.Pointer, out var e))
            _batches[tex.Pointer] = e = (tex, new Batch());
        var r = s.textureRect;
        float u0 = r.xMin / tex.width, u1 = r.xMax / tex.width, v0 = r.yMin / tex.height, v1 = r.yMax / tex.height;
        var b = e.batch;
        int i = b.V.Count;
        b.V.Add(bl); b.V.Add(br); b.V.Add(tr); b.V.Add(tl);
        b.UV.Add(new Vector2(u0, v0)); b.UV.Add(new Vector2(u1, v0)); b.UV.Add(new Vector2(u1, v1)); b.UV.Add(new Vector2(u0, v1));
        b.T.Add(i); b.T.Add(i + 2); b.T.Add(i + 1);
        b.T.Add(i); b.T.Add(i + 3); b.T.Add(i + 2);
    }

    public void Clear() => _batches.Clear();

    // Writes the batches into child objects of `parent` (reusing existing children by index).
    // cutout: see-through art (standees, props, decals) drawn with the sprite material.
    public void Apply(Transform parent, ArtMaterials materials, int sortingLayer, int sortingOrder = 0, bool cutout = false)
    {
        int child = 0;
        foreach (var (tex, b) in _batches.Values)
        {
            GameObject go;
            if (child < parent.childCount)
                go = parent.GetChild(child).gameObject;
            else
            {
                go = new GameObject("art");
                go.transform.SetParent(parent, false);
                go.layer = RenderLayer.Index;
                go.AddComponent<MeshFilter>();
                go.AddComponent<MeshRenderer>();
            }
            child++;
            go.SetActive(true);
            var mf = go.GetComponent<MeshFilter>();
            var mesh = mf.sharedMesh;
            if (mesh == null)
            {
                mesh = new Mesh { name = "VS3D_Art" };
                mesh.indexFormat = IndexFormat.UInt32;
                mf.sharedMesh = mesh;
            }
            mesh.Clear();
            mesh.vertices = b.V.ToArray();
            mesh.uv = b.UV.ToArray();
            if (cutout)
            {
                // Sprite shaders tint by vertex colour.
                var white = new Color[b.V.Count];
                for (int k = 0; k < white.Length; k++) white[k] = Color.white;
                mesh.colors = white;
            }
            mesh.triangles = b.T.ToArray();
            mesh.RecalculateBounds();
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = materials.For(tex, cutout);
            mr.sortingLayerID = sortingLayer;
            mr.sortingOrder = sortingOrder;
        }
        for (int i = child; i < parent.childCount; i++)
            parent.GetChild(i).gameObject.SetActive(false);
    }
}

// One material per atlas texture. Solid art (walls, roofs): unlit, double-sided clone of a URP
// Unlit template. Cut-outs (standees, props, decals): clone of the game's sprite material, which
// blends see-through texels like the 2D view does.
public sealed class ArtMaterials
{
    readonly Material _template;
    readonly Material _spriteTemplate;
    readonly Dictionary<System.IntPtr, Material> _byTex = new();
    readonly Dictionary<System.IntPtr, Material> _cutoutByTex = new();

    public ArtMaterials(Material template, Material spriteTemplate = null)
    {
        _template = template;
        _spriteTemplate = spriteTemplate;
    }

    public Material For(Texture tex, bool cutout = false)
    {
        if (cutout && _spriteTemplate != null)
        {
            if (!_cutoutByTex.TryGetValue(tex.Pointer, out var c))
                _cutoutByTex[tex.Pointer] = c = new Material(_spriteTemplate) { mainTexture = tex };
            return c;
        }
        if (_byTex.TryGetValue(tex.Pointer, out var m))
            return m;
        m = new Material(_template) { mainTexture = tex };
        if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
        // Cut-out sprites (standees, machines): clip transparent texels instead of drawing them.
        try
        {
            if (m.HasProperty("_AlphaClip")) m.SetFloat("_AlphaClip", 1f);
            if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.5f);
            m.EnableKeyword("_ALPHATEST_ON");
        }
        catch (System.Exception) { }
        _byTex[tex.Pointer] = m;
        return m;
    }
}
