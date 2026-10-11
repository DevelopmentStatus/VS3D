using UnityEngine;

namespace VS3D.SimulationBridge;

public enum EntityCategory : byte
{
    Other,
    Player,
    Enemy,
    Boss,
    Projectile,
    Pickup,
}

// Read-only view of one simulation object, refreshed every frame by the bridge.
public struct EntitySnapshot
{
    public int Id;
    public int TypeId;

    public Vector3 Position;      // mapped 3D position on the ground plane
    public Vector3 Velocity;
    public float Angle;           // original 2D rotation around Z, degrees
    public Vector2 Scale;         // original lossy scale, already multiplied by world scale

    public float HealthNormalized;
    public byte AnimationState;
    public EntityCategory Category;

    public bool Visible;
    public Sprite Sprite;
    public bool FlipX;
    public Color Color;
    public Material Material;
}
