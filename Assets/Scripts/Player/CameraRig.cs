using UnityEngine;
using UnityEngine.InputSystem;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Drives Camera.main. In game: third person over the right shoulder, aimed by the mouse (the centre dot
    /// marks where spells go). Knocked down: follows the tumbling ragdoll. Eliminated: watches your body for a
    /// moment, then spectates the players still standing (click to switch). Menus: a fixed view.
    /// </summary>
    [DefaultExecutionOrder(300)]
    public class CameraRig : MonoBehaviour
    {
        [Tooltip("Shoulder offset from the pivot (x right, y up, z back is negative).")]
        public Vector3 shoulderOffset = new(0.6f, 0.2f, -3.1f);
        [Tooltip("Height of the pivot above the hips.")]
        public float pivotHeight = 0.7f;
        public float collisionRadius = 0.22f;
        public float spectateDistance = 4.5f;

        public static CameraRig Instance { get; private set; }
        /// <summary>The ray through the centre dot.</summary>
        public static Ray AimRay => Instance != null ? new Ray(Instance.transform.position, Instance.transform.forward) : new Ray(Vector3.zero, Vector3.forward);
        /// <summary>Who you're watching after being eliminated (null = your own body / nobody).</summary>
        public static PlayerController Spectating { get; private set; }

        static float _shakeStrength, _shakeTime, _shakeDuration;

        /// <summary>Shake the view: <paramref name="strength"/> metres, dying away over <paramref name="duration"/> seconds.</summary>
        public static void Shake(float strength, float duration)
        {
            // a bigger shake already going keeps going
            if (_shakeStrength * Mathf.Clamp01(_shakeTime / Mathf.Max(0.01f, _shakeDuration)) > strength) return;
            _shakeStrength = strength;
            _shakeDuration = _shakeTime = Mathf.Max(0.01f, duration);
        }

        /// <summary>Shake for something going off at <paramref name="point"/>: full strength close by, nothing beyond 25 m.</summary>
        public static void ShakeFrom(Vector3 point, float strength, float duration)
        {
            if (Instance == null) return;
            float k = 1f - Mathf.Clamp01((Instance.transform.position - point).magnitude / 25f);
            if (k > 0f) Shake(strength * k, duration);
        }

        void ApplyShake()
        {
            if (_shakeTime <= 0f) return;
            _shakeTime -= Time.deltaTime;
            float k = Mathf.Clamp01(_shakeTime / _shakeDuration);
            transform.position += Random.insideUnitSphere * (_shakeStrength * k * k);
        }

        Camera _cam;
        Vector3 _menuPos;
        Quaternion _menuRot;
        Vector3 _pivot;
        float _pivotY;
        float _deadTime = -1f, _specYaw, _specPitch = 15f;
        int _specIndex;

        void Awake()
        {
            Instance = this;
            _cam = GetComponent<Camera>();
            _menuPos = transform.position;
            _menuRot = transform.rotation;
            _cam.nearClipPlane = 0.05f;
        }

        void LateUpdate()
        {
            var pc = PlayerController.Local;
            float dt = Time.deltaTime;
            _cam.fieldOfView = HorizontalToVerticalFov(GameSettings.Fov, _cam.aspect);
            if (pc == null || !pc.IsSpawned)
            {
                transform.SetPositionAndRotation(_menuPos, _menuRot);
                _cam.fieldOfView = 60f;
                Spectating = null;
                _deadTime = -1f;
                return;
            }

            if (pc.Dead.Value)
            {
                Spectate(pc, dt);
                return;
            }
            _deadTime = -1f;
            Spectating = null;

            // pivot: the hips (or the tumbling ragdoll), smoothed vertically so steps don't jolt the view
            Transform rag = pc.RagdollFocus;
            Vector3 target = rag != null ? rag.position : pc.transform.position;
            if (rag != null) _pivot = Vector3.Lerp(_pivot, target, 1f - Mathf.Exp(-10f * dt));
            else
            {
                _pivotY = Mathf.Abs(_pivotY - target.y) > 2f ? target.y : Mathf.Lerp(_pivotY, target.y, 1f - Mathf.Exp(-18f * dt));
                _pivot = new Vector3(target.x, _pivotY, target.z);
            }
            Place(_pivot + Vector3.up * pivotHeight, pc.Motor.ViewRotation, shoulderOffset);
        }

        /// <summary>Puts the camera at pivot + rotation * offset, pulled in if something is in the way.</summary>
        void Place(Vector3 pivot, Quaternion rot, Vector3 offset)
        {
            Vector3 desired = pivot + rot * offset;
            Vector3 d = desired - pivot;
            float len = d.magnitude;
            if (len > 0.001f && Physics.SphereCast(pivot, collisionRadius, d / len, out var hit, len, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                desired = pivot + d / len * Mathf.Max(0.2f, hit.distance);
            transform.SetPositionAndRotation(desired, rot);
            ApplyShake();
        }

        void Spectate(PlayerController me, float dt)
        {
            if (_deadTime < 0f)
            {
                _deadTime = Time.time;
                _specYaw = me.Motor.Yaw;
                _specPitch = 20f;
            }
            Vector2 look = Mouse.current != null && Cursor.lockState == CursorLockMode.Locked
                ? Mouse.current.delta.ReadValue() * (GameSettings.Sensitivity * 0.022f) : Vector2.zero;
            if (GameSettings.InvertY) look.y = -look.y;
            _specYaw += look.x;
            _specPitch = Mathf.Clamp(_specPitch - look.y, -30f, 75f);

            Vector3 focus;
            if (Time.time - _deadTime < 2.5f || !AnyAlive())
            {
                // your own body
                Spectating = null;
                var bones = me.LastRagdoll != null ? Rig.MapBones(me.LastRagdoll.transform) : null;
                focus = bones != null && bones.TryGetValue("Bone", out var pelvis) ? pelvis.position : me.transform.position;
            }
            else
            {
                if (Spectating == null || !Spectating.IsAlive) Spectating = NextAlive(0);
                if (!InputMap.Blocked && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) Spectating = NextAlive(1);
                focus = Spectating != null ? Spectating.transform.position : me.transform.position;
            }
            Place(focus + Vector3.up * pivotHeight, Quaternion.Euler(_specPitch, _specYaw, 0), new Vector3(0f, 0.3f, -spectateDistance));
        }

        static bool AnyAlive()
        {
            foreach (var p in PlayerController.All) if (p != null && p.IsAlive) return true;
            return false;
        }

        PlayerController NextAlive(int step)
        {
            var all = PlayerController.All;
            if (all.Count == 0) return null;
            for (int k = 0; k < all.Count; k++)
            {
                _specIndex = ((_specIndex + (k == 0 ? step : 1)) % all.Count + all.Count) % all.Count;
                var p = all[_specIndex];
                if (p != null && p.IsAlive) return p;
            }
            return null;
        }

        static float HorizontalToVerticalFov(float hFov, float aspect)
        {
            // Settings FOV is horizontal at 16:9; Unity wants vertical.
            float h = hFov * Mathf.Deg2Rad;
            float v = 2f * Mathf.Atan(Mathf.Tan(h / 2f) / (16f / 9f));
            return v * Mathf.Rad2Deg;
        }
    }
}
