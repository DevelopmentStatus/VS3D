using System.Collections.Generic;
using UnityEngine;
using VS3D.SimulationBridge;

namespace VS3D.Rendering.Entities;

// Mirrors tracked simulation sprites as SpriteRenderer proxies in the 3D world.
// Copying sprite/flip/colour/material each frame keeps the game's own animation and hit flashes.
public sealed class BillboardPool
{
    sealed class Proxy
    {
        public GameObject Go;
        public Transform T;
        public SpriteRenderer Sr;
        public Material LastMaterial;
    }

    readonly Stack<Proxy> _free = new(512);
    readonly VisualRegistry _registry;
    Transform _root;
    int _sortingLayerId;

    public int ActiveCount { get; private set; }
    public bool CategoryTint;
    public float PlayerAlpha = 1f;   // the player's own sprite (faded out in first person)

    public BillboardPool(VisualRegistry registry) => _registry = registry;

    public void Create(Transform worldRoot)
    {
        var go = new GameObject("VS3D_Billboards");
        go.transform.SetParent(worldRoot, false);
        RenderLayer.Apply(go);
        _root = go.transform;
    }

    public void SetSortingLayer(int id) => _sortingLayerId = id;

    public void Release(TrackedEntity e)
    {
        if (e.RenderData is not Proxy p)
            return;
        e.RenderData = null;
        if (p.Go != null)
        {
            p.Go.SetActive(false);
            _free.Push(p);
        }
        ActiveCount--;
    }

    Proxy Acquire()
    {
        while (_free.Count > 0)
        {
            var p = _free.Pop();
            if (p.Go != null)
            {
                p.Go.SetActive(true);
                return p;
            }
        }
        var go = new GameObject("proxy");
        RenderLayer.Apply(go);
        go.transform.SetParent(_root, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = 0;
        return new Proxy { Go = go, T = go.transform, Sr = sr };
    }

    public void Render(List<TrackedEntity> entities, float cameraYaw)
    {
        var billboardRot = Quaternion.Euler(0f, cameraYaw, 0f);
        var flatBase = Quaternion.Euler(90f, 0f, 0f);

        for (int i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            ref var s = ref e.Snapshot;
            var def = _registry.Get(s);

            if (!s.Visible || def.Mode == RenderMode.Hidden)
            {
                if (e.RenderData is Proxy hidden)
                    Release(e);
                continue;
            }

            if (e.RenderData is not Proxy p)
            {
                p = Acquire();
                p.Sr.sortingLayerID = _sortingLayerId;
                e.RenderData = p;
                ActiveCount++;
            }

            var sr = p.Sr;
            sr.sprite = s.Sprite;
            sr.flipX = s.FlipX;
            var color = CategoryTint ? TintFor(s.Category) : s.Color;
            if (s.Category == EntityCategory.Player)
                color.a *= PlayerAlpha;
            sr.color = color;
            if (s.Material != null && !ReferenceEquals(s.Material, p.LastMaterial))
            {
                sr.sharedMaterial = s.Material;
                p.LastMaterial = s.Material;
            }

            float sx = s.Scale.x * def.scale;
            float sy = s.Scale.y * def.scale;
            p.T.localScale = new Vector3(sx, sy, 1f);

            if (def.Mode == RenderMode.Flat)
            {
                var pos = s.Position + Vector3.up * def.yOffset;
                p.T.SetPositionAndRotation(pos, Quaternion.Euler(0f, -s.Angle, 0f) * flatBase);
            }
            else
            {
                // Stand the sprite on the ground: lift by the distance from pivot to sprite bottom.
                float bottom = s.Sprite.bounds.min.y * Mathf.Abs(sy);
                var pos = s.Position + Vector3.up * (def.yOffset - bottom);
                p.T.SetPositionAndRotation(pos, billboardRot);
            }
        }
    }

    public void ReleaseAll(List<TrackedEntity> entities)
    {
        for (int i = 0; i < entities.Count; i++)
            Release(entities[i]);
    }

    public void SetVisible(bool visible)
    {
        if (_root != null)
            _root.gameObject.SetActive(visible);
    }

    static Color TintFor(EntityCategory c) => c switch
    {
        EntityCategory.Player => Color.cyan,
        EntityCategory.Enemy => Color.red,
        EntityCategory.Boss => Color.magenta,
        EntityCategory.Projectile => Color.yellow,
        EntityCategory.Pickup => Color.green,
        _ => Color.white,
    };
}
