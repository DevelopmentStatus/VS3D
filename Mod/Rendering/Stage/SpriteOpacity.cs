using System.Collections.Generic;
using UnityEngine;
using VS3D.Bootstrap;

namespace VS3D.Rendering.Maps;

// Pixel facts about tile sprites, measured once per sprite:
//  - Opaque: solid art (a floor/pit/wall surface) vs a cut-out with see-through parts (a barrel, a tank);
//  - edge masks: which of 16 positions along each edge have visible pixels, used to tell whether two
//    neighbouring tiles are parts of the same drawn object (their pixels continue across the edge).
// The atlases aren't CPU-readable, so each sprite is blitted to a tiny render texture and read back.
public static class SpriteOpacity
{
    const int Size = 16;

    public struct Info
    {
        public bool Opaque;
        public float Coverage;   // share of visible pixels
        public ushort Top, Bottom, Left, Right;
    }

    static readonly Dictionary<System.IntPtr, Info> Cache = new();
    static RenderTexture _rt;
    static Texture2D _read;
    static bool _failed;

    public static bool IsOpaque(Sprite s) => s != null && Get(s).Opaque;

    // Edge masks packed as top | bottom << 16 | left << 32 | right << 48 (CellClassifier format).
    public static ulong Packed(Sprite s)
    {
        var i = Get(s);
        return i.Top | (ulong)i.Bottom << 16 | (ulong)i.Left << 32 | (ulong)i.Right << 48;
    }

    public static Info Get(Sprite s)
    {
        if (s == null) return default;
        if (Cache.TryGetValue(s.Pointer, out var v)) return v;
        v = Measure(s);
        Cache[s.Pointer] = v;
        return v;
    }

    static Info Measure(Sprite s)
    {
        var solid = new Info { Opaque = true, Coverage = 1f, Top = 0xFFFF, Bottom = 0xFFFF, Left = 0xFFFF, Right = 0xFFFF };
        if (_failed) return solid;
        try
        {
            var tex = s.texture;
            if (tex == null) return solid;
            if (_rt == null || !_rt)
            {
                _rt = new RenderTexture(Size, Size, 0, RenderTextureFormat.ARGB32) { name = "VS3D_Opacity" };
                _rt.Create();
            }
            if (_read == null || !_read)
                _read = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            var r = s.textureRect;
            var scale = new Vector2(r.width / tex.width, r.height / tex.height);
            var offset = new Vector2(r.x / tex.width, r.y / tex.height);
            Graphics.Blit(tex, _rt, scale, offset);
            var prev = RenderTexture.active;
            RenderTexture.active = _rt;
            _read.ReadPixels(new Rect(0, 0, Size, Size), 0, 0, false);
            RenderTexture.active = prev;
            var px = _read.GetPixels32();   // row 0 = bottom of the sprite
            int count = 0;
            var info = new Info();
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    byte a = px[y * Size + x].a;
                    if (a <= 100) continue;   // visible from half alpha (translucent water counts as solid)
                    count++;
                    if (y == 0) info.Bottom |= (ushort)(1 << x);
                    if (y == Size - 1) info.Top |= (ushort)(1 << x);
                    if (x == 0) info.Left |= (ushort)(1 << y);
                    if (x == Size - 1) info.Right |= (ushort)(1 << y);
                }
            info.Coverage = count / (float)px.Length;
            info.Opaque = count >= px.Length * 0.8f;   // thin grout lines still count as solid
            return info;
        }
        catch (System.Exception ex)
        {
            _failed = true;
            Plugin.Log.LogWarning($"Sprite pixel check unavailable ({ex.Message}); treating tiles as solid");
            return solid;
        }
    }
}
