using UnityEngine;
using GameCharacter = VampireSurvivors.Objects.Characters.CharacterController;

namespace VS3D.Rendering.Entities;

// The player's health bar in the 3D view, right under the character like in the top-down view.
// Built from sprite renderers with unlit materials, so the stage's 2D lights don't tint it.
// It is placed just in front of the camera on the line to the player's feet and scaled down to
// match, so it lands exactly under the player on screen, faces the camera, and nothing in the
// world can come between it and the camera. In first person it sits at the bottom centre instead.
public sealed class PlayerHealthBar
{
    const float WorldWidth = 1.1f;      // metres, at the player
    const float HeightRatio = 0.14f;    // bar height / width
    const float Border = 0.03f;         // of the width
    const float MinWidth = 0.05f, MaxWidth = 0.2f, FirstPersonWidth = 0.22f;   // fractions of view height
    const float NearDistance = 0.5f;    // metres in front of the camera

    static readonly Color Back = new(0.08f, 0.08f, 0.08f, 0.85f);
    static readonly Color Empty = new(0.35f, 0.05f, 0.05f, 0.9f);
    static readonly Color Fill = new(0.9f, 0.1f, 0.1f, 1f);
    static readonly Color Lost = new(1f, 0.6f, 0.6f, 0.9f);

    GameObject _root;
    SpriteRenderer _back, _empty, _fill, _lost;
    float _shown = 1f;
    readonly float[] _z = new float[4];

    public void Create(Transform worldRoot)
    {
        _root = new GameObject("VS3D_HealthBar");
        _root.transform.SetParent(worldRoot, false);
        RenderLayer.Apply(_root);
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "VS3D_White", filterMode = FilterMode.Point };
        tex.SetPixel(0, 0, Color.white);
        tex.Apply();
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        // 1x1 unit sprite, pivot on its left edge so x scale = filled width.
        var sprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0f, 0.5f), 1f);
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _back = Part("back", sprite, Back, 0);
        _empty = Part("empty", sprite, Empty, 1);
        _fill = Part("fill", sprite, Fill, 2);
        _lost = Part("lost", sprite, Lost, 3);
        _root.SetActive(false);
    }

    SpriteRenderer Part(string name, Sprite sprite, Color color, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root.transform, false);
        RenderLayer.Apply(go);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = color;
        sr.sortingOrder = 32000 + order;
        // Unlit and opaque: exact colours whatever the stage lighting. Later parts sit slightly closer
        // to the camera so they never z-fight with the ones behind.
        var unlit = Shader.Find("Universal Render Pipeline/Unlit");
        if (unlit != null)
        {
            var m = new Material(unlit) { name = "VS3D_HealthBar_" + name };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", new Color(color.r, color.g, color.b, 1f));
            m.hideFlags = HideFlags.DontUnloadUnusedAsset;
            sr.sharedMaterial = m;
        }
        _z[order] = -0.002f * order;
        return sr;
    }

    public void SetSortingLayer(int sortingLayer)
    {
        foreach (var sr in new[] { _back, _empty, _fill, _lost })
            sr.sortingLayerID = sortingLayer;
    }

    public void Hide()
    {
        if (_root != null && _root.activeSelf) _root.SetActive(false);
    }

    // Call after the camera has moved this frame. show = false hides it (menus).
    public void Update(UnityEngine.Camera cam, GameCharacter player, Vector3 feet, bool show, bool firstPerson)
    {
        if (_root == null || !show || cam == null || player == null || !player) { Hide(); return; }
        float max = player.MaxHp();
        if (max <= 0f) { Hide(); return; }
        float frac = Mathf.Clamp01(player.CurrentHp / max);
        _shown = Mathf.MoveTowards(_shown, frac, Time.unscaledDeltaTime * 2f);

        var ct = cam.transform;
        float tanHalf = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        Vector3 dir;
        float widthFrac;   // bar width as a fraction of the view height
        if (firstPerson)
        {
            dir = cam.ViewportPointToRay(new Vector3(0.5f, 0.1f, 0f)).direction;
            widthFrac = FirstPersonWidth;
        }
        else
        {
            float dist = Vector3.Dot(feet - ct.position, ct.forward);
            if (dist <= 0.05f) { Hide(); return; }
            float viewH = 2f * dist * tanHalf;
            widthFrac = Mathf.Clamp(WorldWidth / viewH, MinWidth, MaxWidth);
            // Just below the feet on screen.
            var below = feet - ct.up * (widthFrac * viewH * HeightRatio * 0.9f);
            dir = (below - ct.position).normalized;
        }
        // Along that line, NearDistance in front of the camera (measured along its forward axis).
        float along = NearDistance / Mathf.Max(0.05f, Vector3.Dot(dir, ct.forward));
        float width = widthFrac * 2f * NearDistance * tanHalf;

        if (!_root.activeSelf) _root.SetActive(true);
        var t = _root.transform;
        t.SetPositionAndRotation(ct.position + dir * along, ct.rotation);
        t.localScale = new Vector3(width, width, 1f);

        float h = HeightRatio;
        Set(_back, -0.5f - Border, 1f + Border * 2f, h + Border * 2f, _z[0]);
        Set(_empty, -0.5f, 1f, h, _z[1]);
        Set(_fill, -0.5f, Mathf.Min(frac, _shown), h, _z[2]);
        Set(_lost, -0.5f + frac, Mathf.Max(0f, _shown - frac), h, _z[3]);
    }

    static void Set(SpriteRenderer sr, float x, float w, float h, float z)
    {
        bool on = w > 0.0001f;
        if (sr.enabled != on) sr.enabled = on;
        if (!on) return;
        var t = sr.transform;
        t.localPosition = new Vector3(x, 0f, z);
        t.localScale = new Vector3(w, h, 1f);
    }
}
