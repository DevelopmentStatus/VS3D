using BepInEx.Configuration;
using UnityEngine;

namespace VS3D.Config;

internal static class ModConfig
{
    public static ConfigEntry<bool> EnabledAtStart;
    public static ConfigEntry<KeyCode> ToggleKey;
    public static ConfigEntry<KeyCode> DebugKey;
    public static ConfigEntry<KeyCode> DebugGridKey;

    public static ConfigEntry<float> Distance;
    public static ConfigEntry<float> LookAtHeight;
    public static ConfigEntry<float> MinPitch;
    public static ConfigEntry<float> MaxPitch;
    public static ConfigEntry<bool> MouseLook;
    public static ConfigEntry<float> MouseSensitivity;
    public static ConfigEntry<bool> InvertY;
    public static ConfigEntry<float> Pitch;
    public static ConfigEntry<float> Fov;
    public static ConfigEntry<float> MinDistance;
    public static ConfigEntry<bool> FirstPerson;
    public static ConfigEntry<float> MaxDistance;
    public static ConfigEntry<float> FollowSharpness;
    public static ConfigEntry<float> RotateStep;

    public static ConfigEntry<float> WorldScale;
    public static ConfigEntry<float> TargetPlayerHeight;
    public static ConfigEntry<int> DiscoveryInterval;
    public static ConfigEntry<string> OriginalCameraKeepLayers;
    public static ConfigEntry<bool> CameraRelativeMovement;
    public static ConfigEntry<string> CrawlersPath;
    public static ConfigEntry<KeyCode> CrawlersTestKey;
    public static ConfigEntry<bool> CrawlersEnabled;
    public static ConfigEntry<bool> UseCrawlersAssets;
#if VS_ONLY
    public const bool VsOnlyBuild = true;
    public static bool CrawlersAssetsOn => false;
#else
    public const bool VsOnlyBuild = false;
    public static bool CrawlersAssetsOn => UseCrawlersAssets.Value;
#endif
    public static ConfigEntry<float> RenderDistance;

    public static void Bind(ConfigFile cfg)
    {
        EnabledAtStart = cfg.Bind("General", "EnabledAtStart", true, "Switch to the 3D view automatically when a stage starts.");
        ToggleKey = cfg.Bind("General", "ToggleKey", KeyCode.F8, "Toggle between the original 2D view and the 3D view.");
        DebugKey = cfg.Bind("General", "DebugKey", KeyCode.F9, "Show or hide the debug panel.");
        DebugGridKey = cfg.Bind("General", "DebugGridKey", KeyCode.F10, "Write the 3D stage's wall/pit classification around the player to BepInEx/VS3D_grid.txt.");

        Distance = cfg.Bind("Camera", "Distance", 12f, "Distance behind the player, in metres.");
        LookAtHeight = cfg.Bind("Camera", "LookAtHeight", 1f, "Height above the player's feet the camera orbits around, in metres. With Distance 12 and Pitch 25 the camera sits about 5 m up.");
        Pitch = cfg.Bind("Camera", "Pitch", 25f, "Initial downward pitch, in degrees.");
        MinPitch = cfg.Bind("Camera", "MinPitch", 5f, "Lowest mouse-look pitch.");
        MaxPitch = cfg.Bind("Camera", "MaxPitch", 75f, "Highest mouse-look pitch.");
        MouseLook = cfg.Bind("Camera", "MouseLook", true, "Lock the cursor to the centre and orbit the camera with the mouse while playing in 3D (released whenever a menu/level-up/pause is open).");
        MouseSensitivity = cfg.Bind("Camera", "MouseSensitivity", 2f, "Degrees per mouse-axis unit.");
        InvertY = cfg.Bind("Camera", "InvertY", false, "Invert vertical mouse-look.");
        Fov = cfg.Bind("Camera", "FieldOfView", 70f, "Vertical field of view, in degrees.");
        MinDistance = cfg.Bind("Camera", "MinDistance", 4f, "Closest mouse-wheel zoom.");
        FirstPerson = cfg.Bind("Camera", "FirstPerson", true, "Zooming in past MinDistance switches to first person (camera at the player's eyes, own sprite hidden). Zoom out to leave it.");
        MaxDistance = cfg.Bind("Camera", "MaxDistance", 30f, "Farthest mouse-wheel zoom.");
        FollowSharpness = cfg.Bind("Camera", "FollowSharpness", 10f, "Higher values follow the player more tightly.");
        RotateStep = cfg.Bind("Camera", "RotateStep", 45f, "Degrees the camera turns per Q/E press.");

        WorldScale = cfg.Bind("World", "WorldScale", 0f, "Metres per original Unity unit. 0 = derive from the player sprite and TargetPlayerHeight.");
        TargetPlayerHeight = cfg.Bind("World", "TargetPlayerHeight", 1.8f, "Player height in metres used when WorldScale is 0.");
        DiscoveryInterval = cfg.Bind("World", "DiscoveryInterval", 10, "Frames between scans for newly spawned sprites.");
        OriginalCameraKeepLayers = cfg.Bind("World", "OriginalCameraKeepLayers", "UI", "Comma-separated layers the original camera keeps drawing (HUD) while 3D is on.");
#if VS_ONLY
        RenderDistance = cfg.Bind("Stage", "RenderDistance", 60f, "Metres around the player that get 3D walls, pits and props.");
        CrawlersEnabled = cfg.Bind("Stage", "Enabled", true, "Rebuild stages in 3D (walls, pits, props) instead of only a flat ground.");
        CrawlersTestKey = cfg.Bind("Stage", "ToggleKey", KeyCode.F7, "Show or hide the 3D stage.");
#else
        CrawlersPath = cfg.Bind("Crawlers", "InstallPath", "", @"Your Vampire Crawlers install folder (e.g. C:\Program Files (x86)\Steam\steamapps\common\Vampire Crawlers); its 3D environment bundles are loaded from here at runtime (never copied).");
        RenderDistance = cfg.Bind("Crawlers", "RenderDistance", 60f, "Metres around the player that get Crawlers environment pieces; further pieces are despawned.");
        CrawlersEnabled = cfg.Bind("Crawlers", "Enabled", true, "Rebuild stages in 3D (walls, pits, props) instead of only a flat ground.");
        UseCrawlersAssets = cfg.Bind("Crawlers", "UseCrawlersAssets", !VsOnlyBuild, "Use Vampire Crawlers' 3D kits for some stages (Library, Forest, Chapel, Tower) and props. false = build every stage only from Vampire Survivors' own art; no Crawlers install needed. (Ignored by the VS-only build.)");
        CrawlersTestKey = cfg.Bind("Crawlers", "ToggleKey", KeyCode.F7, "Show or hide the Crawlers environment.");
#endif
        CameraRelativeMovement = cfg.Bind("Input", "CameraRelativeMovement", true, "In the 3D view, movement input is relative to the camera (W = away from the camera).");
    }
}
