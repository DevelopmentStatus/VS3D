using System;
using System.IO;
using UnityEngine;
using VS3D.Camera;
using VS3D.Config;
using VS3D.Debug;
using VS3D.Hooks;
using VS3D.Rendering;
using VS3D.Rendering.Crawlers;
using VS3D.Rendering.Entities;
using VS3D.Rendering.Terrain;
using Bridge = VS3D.SimulationBridge.SimulationBridge;

namespace VS3D.Bootstrap;

// The single injected MonoBehaviour; drives every VS3D subsystem.
public class VS3DRuntime : MonoBehaviour
{
    public VS3DRuntime(IntPtr ptr) : base(ptr) { }

    bool _initialized;
    bool _inputBroken;
    GameObject _worldRoot;

    Bridge _bridge;
    ThirdPersonCamera _camera;
    CameraToggle _toggle;
    VisualRegistry _registry;
    BillboardPool _billboards;
    GroundPlane _ground;
    DebugPanel _panel;
    PlayerHealthBar _healthBar;
    bool _materialsConfigured;
    CrawlersAssets _crawlers;
    int _sortingLayerId;
    StageDresser _dresser;
    bool _dresserFailed;
    bool _envVisible = true;
    bool _crawlersFailed;

    void Init()
    {
        _initialized = true;
        RenderLayer.Resolve();

        _worldRoot = new GameObject("VS3D_World");
        DontDestroyOnLoad(_worldRoot);
        RenderLayer.Apply(_worldRoot);

        _bridge = new Bridge();
        _registry = new VisualRegistry();
        _registry.Load();
        _billboards = new BillboardPool(_registry);
        _billboards.Create(_worldRoot.transform);
        _ground = new GroundPlane();
        _ground.Create(_worldRoot.transform, Path.Combine(BepInEx.Paths.PluginPath, "VS3D", "bundles"));
        _camera = new ThirdPersonCamera();
        _camera.Create(_worldRoot.transform);
        CullingFix.Install(_camera.Cam);
        _toggle = new CameraToggle();
        _panel = new DebugPanel();
        _healthBar = new PlayerHealthBar();
        _healthBar.Create(_worldRoot.transform);

        _bridge.EntityRemoved += _billboards.Release;
        _bridge.StageStarted += OnStageStarted;
        _bridge.StageEnded += OnStageEnded;
        _worldRoot.SetActive(false);
    }

    void OnStageStarted()
    {
        _materialsConfigured = false;
        // New stage: drop any previous stage build so EnsureDresser builds this one.
        _dresser?.End();
        _dresserFailed = false;
        _camera.Snap();
        if (ModConfig.EnabledAtStart.Value)
            Set3D(true);
    }

    void OnStageEnded()
    {
        Set3D(false);
        _dresser?.End();
        _dresserFailed = false;
    }

    void Set3D(bool on)
    {
        if (on && !_bridge.InStage)
            return;
        if (on)
        {
            _worldRoot.SetActive(true);
            _camera.Snap();
            _toggle.Enable(_camera.Cam);
            if (!_toggle.Active)
                _worldRoot.SetActive(false);
            else
                _dresser?.SetViewActive(_envVisible);
        }
        else
        {
            _camera.SetCursorLock(false);
            _dresser?.SetViewActive(false);
            _toggle.Disable(_camera.Cam);
            _worldRoot.SetActive(false);
        }
    }

    bool KeyDown(KeyCode key)
    {
        if (_inputBroken)
            return false;
        try { return Input.GetKeyDown(key); }
        catch (Exception ex)
        {
            _inputBroken = true;
            Plugin.Log.LogError($"Legacy Input unavailable, hotkeys disabled: {ex.Message}");
            return false;
        }
    }

    void Update()
    {
        if (!_initialized)
            Init();

        if (KeyDown(ModConfig.DebugKey.Value))
            _panel.Visible = !_panel.Visible;
        if (KeyDown(ModConfig.DebugGridKey.Value))
            DumpGrid();
        if (KeyDown(ModConfig.ToggleKey.Value) || _panel.Request3D)
        {
            _panel.Request3D = false;
            Set3D(!_toggle.Active);
        }
        if (KeyDown(ModConfig.CrawlersTestKey.Value))
        {
            _envVisible = !_envVisible;
            _dresser?.SetVisible(_envVisible);
            Plugin.Log.LogInfo($"Crawlers environment {(_envVisible ? "shown" : "hidden")}");
        }
        if (_panel.RequestRestore)
        {
            _panel.RequestRestore = false;
            Set3D(false);
        }
        _panel.Tick();
    }

    void LateUpdate()
    {
        if (!_initialized)
            return;
        try
        {
            _bridge.Tick();
            _toggle.Tick(_camera.Cam);
            MovementHooks.Enabled = _toggle.Active && _bridge.InStage && ModConfig.CameraRelativeMovement.Value;
            if (!_toggle.Active || !_bridge.InStage)
            {
                _healthBar.Hide();
                _camera.SetCursorLock(false);
                return;
            }

            if (!_materialsConfigured)
                ConfigureMaterials();

            _camera.Update(_bridge.PlayerPosition3D, IsPlaying());
            MovementHooks.Yaw = _camera.Yaw;
            var input = _bridge.Player._player;
            MovementHooks.LocalPlayerInput = input != null ? input.Pointer : System.IntPtr.Zero;
            if (_materialsConfigured)
                EnsureDresser();
            bool crawlersFloor = _dresser != null && _dresser.Active && _dresser.HasFloor && _envVisible;
            _dresser?.Tick(_bridge.PlayerPosition2D, _bridge.PlayerPosition3D, _camera.Cam.transform.position, _camera.Yaw);
            _ground.SetVisible(_panel.ShowGround && !crawlersFloor);
            _ground.Follow(_bridge.PlayerPosition3D);
            _billboards.CategoryTint = _panel.CategoryTint;
            CullingFix.Invert = _panel.InvertCulling;
            _billboards.PlayerAlpha = _camera.PlayerAlpha;
            _billboards.Render(_bridge.Entities, _camera.Yaw);
            _healthBar.Update(_camera.Cam, _bridge.Player, _bridge.PlayerPosition3D, GameRunning(), _camera.FirstPerson);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"VS3D frame error, reverting to 2D: {ex}");
            Set3D(false);
        }
    }

    // True while the player is actually in control: no pause/level-up/chest UI, window focused,
    // debug panel closed. Drives the centre mouse lock.
    bool IsPlaying() => GameRunning() && Application.isFocused && !_panel.Visible;

    // False while the game is paused or a level-up/chest/menu screen holds it.
    bool GameRunning()
    {
        var gm = _bridge.Game;
        return !gm.IsInPauseGameState && !gm._isPaused && gm.IsGameRunning && Time.timeScale != 0f;
    }

    void OnApplicationFocus(bool focused)
    {
        if (!focused && _camera != null)
            _camera.SetCursorLock(false);
    }

    // Proxies share the player's sorting layer and material so the game's 2D lights hit them.
    void ConfigureMaterials()
    {
        var rend = _bridge.Player.Rend;
        if (rend == null)
            return;
        _sortingLayerId = rend.sortingLayerID;
        _billboards.SetSortingLayer(rend.sortingLayerID);
        _healthBar.SetSortingLayer(rend.sortingLayerID);
        _ground.Configure(rend.sharedMaterial, rend.sortingLayerID);
        _materialsConfigured = true;
        Plugin.Log.LogInfo($"Materials: sorting layer '{rend.sortingLayerName}', material '{rend.sharedMaterial?.name}' / shader '{rend.sharedMaterial?.shader?.name}'");
    }

    // Builds the stage from Vampire Crawlers' 3D kit once a stage runs in 3D.
    void EnsureDresser()
    {
        if (!ModConfig.CrawlersEnabled.Value || _dresserFailed || (_dresser != null && _dresser.Active))
            return;
        try
        {
            if (_crawlers == null && ModConfig.CrawlersAssetsOn && !_crawlersFailed)
            {
                _crawlers = new CrawlersAssets();
                if (!_crawlers.Init())
                {
                    // Fall back to the stage's own art.
                    _crawlers = null;
                    _crawlersFailed = true;
                    Plugin.Log.LogWarning("Crawlers assets unavailable; building stages from Vampire Survivors' own art");
                }
            }
            _dresser ??= new StageDresser(_crawlers);
            if (!_dresser.Begin(_worldRoot.transform, _bridge.Game, _bridge.Game.Stage, _sortingLayerId))
                _dresserFailed = true;
            else
                _camera.Cam.backgroundColor = _dresser.Background;
        }
        catch (Exception ex)
        {
            _dresserFailed = true;
            Plugin.Log.LogError($"Crawlers stage build failed: {ex}");
        }
    }

    // Writes the 3D stage's cell classification around the player to BepInEx/VS3D_grid.txt.
    void DumpGrid()
    {
        try
        {
            var text = _dresser?.DumpGrid();
            if (text == null) { Plugin.Log.LogInfo("Grid dump: no 3D stage built yet"); return; }
            var path = Path.Combine(BepInEx.Paths.BepInExRootPath, "VS3D_grid.txt");
            File.WriteAllText(path, text);
            Plugin.Log.LogInfo($"Grid dump: {path}");
        }
        catch (Exception ex) { Plugin.Log.LogError($"Grid dump failed: {ex.Message}"); }
    }

    void OnGUI()
    {
        if (!_initialized)
            return;
        if (!_panel.Visible)
            return;
        _panel.Draw(_bridge, _toggle.Active, _camera.Cam, _camera.Yaw, _camera.Distance, _billboards.ActiveCount);
    }
}
