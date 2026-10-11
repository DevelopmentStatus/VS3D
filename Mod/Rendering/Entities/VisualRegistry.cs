using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;
using VS3D.Bootstrap;
using VS3D.SimulationBridge;

namespace VS3D.Rendering.Entities;

public enum RenderMode
{
    Billboard,   // upright, camera-facing (yaw only)
    Flat,        // lying on the ground, keeps the original 2D rotation
    Hidden,
    Mesh,        // reserved for Phase 6 model replacements
}

public sealed class VisualDef
{
    public string renderMode { get; set; } = "billboard";
    public float scale { get; set; } = 1f;
    public float yOffset { get; set; } = 0f;
    public string model { get; set; }

    public RenderMode Mode = RenderMode.Billboard;
}

// Data-driven visual overrides, loaded from BepInEx/config/VS3D/visuals.json.
// Keys: a category ("Enemy", "Projectile", ...) or "enemy:<EnemyType>" for one enemy type.
public sealed class VisualRegistry
{
    readonly Dictionary<string, VisualDef> _defs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, VisualDef> _enemyCache = new();
    readonly VisualDef[] _byCategory = new VisualDef[Enum.GetValues(typeof(EntityCategory)).Length];

    static readonly string DefaultJson = """
{
  "Player":     { "renderMode": "billboard", "scale": 1.0 },
  "Enemy":      { "renderMode": "billboard", "scale": 1.0 },
  "Boss":       { "renderMode": "billboard", "scale": 1.0 },
  "Pickup":     { "renderMode": "billboard", "scale": 1.0, "yOffset": 0.1 },
  "Projectile": { "renderMode": "flat",      "scale": 1.0, "yOffset": 0.3 },
  "Other":      { "renderMode": "flat",      "scale": 1.0, "yOffset": 0.05 }
}
""";

    public void Load()
    {
        var dir = Path.Combine(Paths.ConfigPath, "VS3D");
        var path = Path.Combine(dir, "visuals.json");
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, DefaultJson);
            }
            Parse(File.ReadAllText(path));
            Plugin.Log.LogInfo($"Visual registry: {_defs.Count} entries from {path}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Visual registry: failed to read {path}: {ex.Message}; using defaults");
            Parse(DefaultJson);
        }
    }

    void Parse(string json)
    {
        _defs.Clear();
        _enemyCache.Clear();
        var map = JsonSerializer.Deserialize<Dictionary<string, VisualDef>>(json,
            new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        foreach (var kv in map)
        {
            kv.Value.Mode = Enum.TryParse<RenderMode>(kv.Value.renderMode, true, out var m) ? m : RenderMode.Billboard;
            _defs[kv.Key] = kv.Value;
        }
        foreach (EntityCategory c in Enum.GetValues(typeof(EntityCategory)))
            _byCategory[(int)c] = _defs.TryGetValue(c.ToString(), out var d) ? d : new VisualDef();
    }

    public VisualDef Get(in EntitySnapshot s)
    {
        if (s.Category is EntityCategory.Enemy or EntityCategory.Boss)
        {
            if (_enemyCache.TryGetValue(s.TypeId, out var cached))
                return cached;
            var key = "enemy:" + (VampireSurvivors.Data.EnemyType)s.TypeId;
            var def = _defs.TryGetValue(key, out var d) ? d : _byCategory[(int)s.Category];
            _enemyCache[s.TypeId] = def;
            return def;
        }
        return _byCategory[(int)s.Category];
    }
}
