using BepInEx;
using HarmonyLib;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using VS3D.Config;

namespace VS3D.Bootstrap;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "vs3d.renderer";
    public const string Name = "VS3D Third-Person Renderer";
    public const string Version = "0.1.2";

    internal new static ManualLogSource Log;

    public override void Load()
    {
        Log = base.Log;
        ModConfig.Bind(Config);

        new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
        ClassInjector.RegisterTypeInIl2Cpp<VS3DRuntime>();
        AddComponent<VS3DRuntime>();

        Log.LogInfo($"{Name} {Version} loaded. Toggle 3D: {ModConfig.ToggleKey.Value}, debug panel: {ModConfig.DebugKey.Value}");
    }
}
