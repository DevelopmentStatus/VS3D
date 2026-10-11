using UnityEngine;

namespace VS3D.SimulationBridge;

// Maps the game's XY gameplay plane onto an XZ ground plane placed far away from
// the original 2D content, so the original camera never sees the 3D world.
public static class CoordinateMapper
{
    public static readonly Vector3 WorldOffset = new(0f, -5000f, 0f);

    public static float Scale = 1f;
    public static Vector2 Origin;

    public static Vector3 ToWorld3D(Vector2 p) =>
        new Vector3((p.x - Origin.x) * Scale, 0f, (p.y - Origin.y) * Scale) + WorldOffset;

    public static Vector3 ToWorld3D(Vector3 p) => ToWorld3D(new Vector2(p.x, p.y));
}
