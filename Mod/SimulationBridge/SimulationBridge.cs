using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using VampireSurvivors.Framework;
using VampireSurvivors.Objects.Characters;
using VampireSurvivors.Objects.Pickups;
using VampireSurvivors.Objects.Projectiles;
using VS3D.Bootstrap;
using VS3D.Config;
using GameCharacter = VampireSurvivors.Objects.Characters.CharacterController;

namespace VS3D.SimulationBridge;

public sealed class TrackedEntity
{
    public IntPtr Key;
    public ArcadeSprite Source;
    public GameObject GameObject;
    public Transform Transform;
    public SpriteRenderer Renderer;
    public EnemyController Enemy;
    public EntitySnapshot Snapshot;
    public int ListIndex;
    public object RenderData;   // owned by the renderer (proxy object)
}

// Single read-only adapter between the game's simulation objects and the renderer.
// It never writes to game state.
public sealed class SimulationBridge
{
    public GameManager Game { get; private set; }
    public GameCharacter Player { get; private set; }
    public bool InStage => Game != null && Player != null;

    public readonly List<TrackedEntity> Entities = new(2048);
    readonly Dictionary<IntPtr, TrackedEntity> _byKey = new(2048);

    public Vector3 PlayerPosition3D;
    public Vector2 PlayerPosition2D;
    public int DiscoveredLastScan;

    public event Action StageStarted;
    public event Action StageEnded;
    public event Action<TrackedEntity> EntityRemoved;

    int _frame;
    bool _scaleResolved;

    public void Tick()
    {
        _frame++;

        if (Game is null || Game.WasCollected || !Game)
        {
            // Reference check: a destroyed GameManager compares equal to null in Unity terms.
            if (Game is not null)
                EndStage();
            if (_frame % 30 == 0)
                TryFindGame();
            return;
        }

        if (Player == null || !Player)
        {
            Player = Game.PlayerOne;
            if (Player == null || !Player)
                return;
            BeginStage();
        }

        if (_frame % Math.Max(1, ModConfig.DiscoveryInterval.Value) == 0)
            Discover();

        UpdateSnapshots();
    }

    void TryFindGame()
    {
        var gm = UnityEngine.Object.FindFirstObjectByType<GameManager>();
        if (gm != null)
        {
            Game = gm;
            Plugin.Log.LogInfo("Bridge: GameManager found");
        }
    }

    void BeginStage()
    {
        Clear();
        _scaleResolved = false;
        var p = Player.CachedTrans.position;
        CoordinateMapper.Origin = new Vector2(p.x, p.y);
        ResolveScale();
        Plugin.Log.LogInfo($"Bridge: stage started, player at {p}, world scale {CoordinateMapper.Scale:0.###}");
        Discover();
        StageStarted?.Invoke();
    }

    void EndStage()
    {
        Plugin.Log.LogInfo("Bridge: stage ended");
        Clear();
        Game = null;
        Player = null;
        StageEnded?.Invoke();
    }

    void Clear()
    {
        for (int i = 0; i < Entities.Count; i++)
            EntityRemoved?.Invoke(Entities[i]);
        Entities.Clear();
        _byKey.Clear();
    }

    void ResolveScale()
    {
        if (ModConfig.WorldScale.Value > 0f)
        {
            CoordinateMapper.Scale = ModConfig.WorldScale.Value;
            _scaleResolved = true;
            return;
        }
        var rend = Player.Rend;
        var sprite = rend != null ? rend.sprite : null;
        if (sprite == null)
        {
            CoordinateMapper.Scale = 1f;
            return;
        }
        float h = sprite.bounds.size.y * Mathf.Abs(rend.transform.lossyScale.y);
        CoordinateMapper.Scale = h > 0.0001f ? ModConfig.TargetPlayerHeight.Value / h : 1f;
        _scaleResolved = true;
        Plugin.Log.LogInfo($"Bridge: player sprite height {h:0.###} units -> scale {CoordinateMapper.Scale:0.###}");
    }

    void Discover()
    {
        var found = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<ArcadeSprite>(),
            FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        int added = 0;
        for (int i = 0; i < found.Length; i++)
        {
            var obj = found[i];
            if (obj == null)
                continue;
            var key = obj.Pointer;
            if (_byKey.ContainsKey(key))
                continue;
            var sprite = obj.Cast<ArcadeSprite>();
            var entity = Create(sprite, key);
            if (entity == null)
                continue;
            entity.ListIndex = Entities.Count;
            Entities.Add(entity);
            _byKey[key] = entity;
            added++;
        }
        DiscoveredLastScan = added;
    }

    static TrackedEntity Create(ArcadeSprite sprite, IntPtr key)
    {
        var rend = sprite.Rend;
        if (rend == null)
            rend = sprite.GetComponentInChildren<SpriteRenderer>();
        if (rend == null)
            return null;

        var e = new TrackedEntity
        {
            Key = key,
            Source = sprite,
            GameObject = sprite.gameObject,
            Transform = rend.transform,
            Renderer = rend,
        };
        e.Snapshot.Id = sprite.GetInstanceID();

        if (sprite.TryCast<GameCharacter>() != null)
            e.Snapshot.Category = EntityCategory.Player;
        else if (sprite.TryCast<EnemyController>() is { } enemy)
        {
            e.Enemy = enemy;
            e.Snapshot.Category = EntityCategory.Enemy;
            e.Snapshot.TypeId = (int)enemy.EnemyType;
        }
        else if (sprite.TryCast<Projectile>() != null)
            e.Snapshot.Category = EntityCategory.Projectile;
        else if (sprite.TryCast<Pickup>() != null)
            e.Snapshot.Category = EntityCategory.Pickup;
        else
            e.Snapshot.Category = EntityCategory.Other;
        return e;
    }

    void UpdateSnapshots()
    {
        if (!_scaleResolved)
            ResolveScale();

        var pp = Player.CachedTrans.position;
        PlayerPosition2D = new Vector2(pp.x, pp.y);
        PlayerPosition3D = CoordinateMapper.ToWorld3D(PlayerPosition2D);

        float scale = CoordinateMapper.Scale;
        for (int i = Entities.Count - 1; i >= 0; i--)
        {
            var e = Entities[i];
            if (e.Source == null || !e.Source || e.Renderer == null || !e.Renderer)
            {
                Remove(i);
                continue;
            }

            ref var s = ref e.Snapshot;
            var rend = e.Renderer;
            s.Visible = e.GameObject.activeInHierarchy && rend.enabled;
            if (!s.Visible)
                continue;
            s.Sprite = rend.sprite;
            if (s.Sprite == null)
            {
                s.Visible = false;
                continue;
            }

            var t = e.Transform;
            var prev = s.Position;
            s.Position = CoordinateMapper.ToWorld3D(t.position);
            s.Velocity = (s.Position - prev) / Mathf.Max(Time.deltaTime, 0.0001f);
            s.Angle = t.eulerAngles.z;
            var ls = t.lossyScale;
            s.Scale = new Vector2(ls.x * scale, ls.y * scale);
            s.FlipX = rend.flipX;
            s.Color = rend.color;
            s.Material = rend.sharedMaterial;

            if (e.Enemy != null)
            {
                if (e.Enemy.IsBoss)
                    s.Category = EntityCategory.Boss;
                if (e.Enemy.IsDead)
                    s.Visible = false;
            }
        }
    }

    void Remove(int index)
    {
        var e = Entities[index];
        _byKey.Remove(e.Key);
        EntityRemoved?.Invoke(e);
        int last = Entities.Count - 1;
        if (index != last)
        {
            Entities[index] = Entities[last];
            Entities[index].ListIndex = index;
        }
        Entities.RemoveAt(last);
    }
}
