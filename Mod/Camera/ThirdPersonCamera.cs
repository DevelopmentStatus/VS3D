using System;
using UnityEngine;
using VS3D.Bootstrap;
using VS3D.Config;
using VS3D.Rendering;

namespace VS3D.Camera;

// Presentation-only perspective camera orbiting the mapped player position.
// Deliberately not tagged MainCamera, so Camera.main still returns the original.
public sealed class ThirdPersonCamera
{
    public UnityEngine.Camera Cam { get; private set; }
    public float Yaw => _yaw;
    public float Pitch => _pitch;
    public float Distance => _distance;
    public bool CursorLocked { get; private set; }
    public bool FirstPerson { get; private set; }
    // Opacity for the player's own sprite: fades out while zooming into first person.
    public float PlayerAlpha => 1f - _fpBlend;

    // First person: zooming in past MinDistance puts the camera at the player's eyes.
    const float FirstPersonMinPitch = -60f, FirstPersonMaxPitch = 80f;
    float _fpBlend;         // 0 = third person, 1 = first person (smoothed)
    float _shownDistance;   // smoothed zoom

    float _yaw;
    float _targetYaw;
    float _pitch;
    float _distance;
    Vector3 _focus;
    bool _snap = true;
    bool _axisBroken;

    public void Create(Transform root)
    {
        var go = new GameObject("VS3D_Camera");
        go.transform.SetParent(root, false);
        Cam = go.AddComponent<UnityEngine.Camera>();
        Cam.orthographic = false;
        Cam.nearClipPlane = 0.1f;
        Cam.farClipPlane = 600f;
        Cam.cullingMask = RenderLayer.Mask;
        Cam.clearFlags = CameraClearFlags.SolidColor;
        Cam.backgroundColor = new Color(0.42f, 0.55f, 0.72f, 1f);
        Cam.transparencySortMode = TransparencySortMode.Perspective;
        Cam.enabled = false;
        _distance = ModConfig.Distance.Value;
        _shownDistance = _distance;
        _pitch = ModConfig.Pitch.Value;
    }

    public void Snap() => _snap = true;

    // playing = 3D view on, no menu/level-up/pause open, window focused.
    public void Update(Vector3 target, bool playing)
    {
        if (Cam == null)
            return;
        float dt = Time.unscaledDeltaTime;

        SetCursorLock(playing && ModConfig.MouseLook.Value);

        if (playing)
        {
            if (Input.GetKeyDown(KeyCode.Q)) _targetYaw -= ModConfig.RotateStep.Value;
            if (Input.GetKeyDown(KeyCode.E)) _targetYaw += ModConfig.RotateStep.Value;

            if (CursorLocked)
            {
                var delta = MouseDelta();
                float sens = ModConfig.MouseSensitivity.Value;
                // Mouse-look is applied directly (no smoothing) so it feels responsive.
                _targetYaw += delta.x * sens;
                _yaw += delta.x * sens;
                _pitch += (ModConfig.InvertY.Value ? delta.y : -delta.y) * sens;
            }

            float wheel = Input.mouseScrollDelta.y;
            if (wheel > 0f && !FirstPerson && _distance <= ModConfig.MinDistance.Value + 0.01f && ModConfig.FirstPerson.Value)
                FirstPerson = true;
            else if (wheel < 0f && FirstPerson)
            {
                FirstPerson = false;
                _distance = ModConfig.MinDistance.Value;
            }
            else if (wheel != 0f && !FirstPerson)
                _distance = Mathf.Clamp(_distance - wheel * 1.5f, ModConfig.MinDistance.Value, ModConfig.MaxDistance.Value);
        }
        if (FirstPerson)
            _pitch = Mathf.Clamp(_pitch, FirstPersonMinPitch, FirstPersonMaxPitch);
        else
            _pitch = Mathf.Clamp(_pitch, ModConfig.MinPitch.Value, ModConfig.MaxPitch.Value);

        float k = 1f - Mathf.Exp(-ModConfig.FollowSharpness.Value * dt);
        float zoomK = 1f - Mathf.Exp(-12f * dt);
        if (_snap)
        {
            _focus = target;
            _yaw = _targetYaw;
            _fpBlend = FirstPerson ? 1f : 0f;
            _shownDistance = _distance;
            _snap = false;
        }
        else
        {
            // The eyes are on the player, so first person follows exactly; third person trails.
            _focus = FirstPerson ? target : Vector3.Lerp(_focus, target, k);
            _yaw = Mathf.LerpAngle(_yaw, _targetYaw, k);
            _fpBlend = Mathf.MoveTowards(_fpBlend, FirstPerson ? 1f : 0f, dt * 5f);
            _shownDistance = Mathf.Lerp(_shownDistance, _distance, zoomK);
        }

        var rot = Quaternion.Euler(_pitch, _yaw, 0f);
        float eye = ModConfig.TargetPlayerHeight.Value * 0.85f;
        float t = Mathf.SmoothStep(0f, 1f, _fpBlend);
        var pivot = _focus + Vector3.up * Mathf.Lerp(ModConfig.LookAtHeight.Value, eye, t);
        float dist = Mathf.Lerp(_shownDistance, 0f, t);
        Cam.fieldOfView = ModConfig.Fov.Value;
        Cam.transform.SetPositionAndRotation(pivot - rot * Vector3.forward * dist, rot);
    }

    public void SetCursorLock(bool locked)
    {
        if (locked == CursorLocked)
            return;
        CursorLocked = locked;
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    Vector2 MouseDelta()
    {
        if (!_axisBroken)
        {
            try { return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")); }
            catch (Exception ex)
            {
                _axisBroken = true;
                Plugin.Log.LogWarning($"Mouse axes unavailable ({ex.Message}); using mousePositionDelta");
            }
        }
        var d = Input.mousePositionDelta;
        return new Vector2(d.x, d.y) * 0.1f;
    }
}
