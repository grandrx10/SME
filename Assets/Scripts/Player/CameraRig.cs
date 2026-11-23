using UnityEngine;
using UnityEngine.InputSystem;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Drives Camera.main. In game: third person over the right shoulder, aimed by the mouse (the centre dot
    /// marks where spells go). Knocked down: follows the tumbling ragdoll. Eliminated: watches your body for a
    /// moment, then spectates the players still standing (left / right click: next / previous). While a new arena
    /// builds itself: circles it. Menus: a fixed view.
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
        float _pivotY, _ads;
        float _deadTime = -1f, _specYaw, _specPitch = 15f;
        int _specIndex;

        void Awake()
        {
            Instance = this;
            _cam = GetComponent<Camera>();
            _menuPos = transform.position;
            _menuRot = transform.rotation;
            _cam.nearClipPlane = 0.05f;
            // practice chambers sit 384 m apart along +x: stop short of seeing the next one on the horizon
            _cam.farClipPlane = Practice.ViewDistance;
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

            // the new arena building itself: everyone in the match watches it go up from a slow circle around it
            // (and the same while waiting for enough players to start a round)
            if ((MatchManager.BuildingArena || MatchManager.WaitingForPlayers && pc.Dead.Value) && !pc.InPractice && VoxelWorld.Current != null)
            {
                Orbit(VoxelWorld.Current);
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
                // (never lagging more than a step behind: a hard fall used to stretch the lag past 2 m, snap, and
                // stretch again, so the view jumped down in bursts)
                _pivotY = Mathf.Abs(_pivotY - target.y) > 6f ? target.y : Mathf.Lerp(_pivotY, target.y, 1f - Mathf.Exp(-18f * dt));
                _pivotY = Mathf.Clamp(_pivotY, target.y - 0.35f, target.y + 0.35f);
                _pivot = new Vector3(target.x, _pivotY, target.z);
            }
            // crouched: the view comes down with the head
            float crouch = rag != null ? 0f : pc.Motor.CrouchBlend * PlayerMotor.CrouchDrop;
            // Strength: the head is up where the grown body puts it, and the camera stands back a little
            float big = pc.Animator != null ? pc.Animator.StrongWeight : 0f;
            big = big * big * (3f - 2f * big) * (SpellPlans.StrengthSize - 1f);
            // aiming down Sebastian's sights: in close over the rifle (so near its barrel that the centre dot is inside the shot)
            _ads = Mathf.MoveTowards(_ads, rag == null && pc.Caster != null && pc.Caster.AimingDownSights ? 1f : 0f, dt / 0.2f);
            float ads = _ads * _ads * (3f - 2f * _ads);
            _cam.fieldOfView *= Mathf.Lerp(1f, 0.72f, ads);
            float height = Mathf.Lerp(pivotHeight, 0.52f, ads);
            Place(_pivot + Vector3.up * (height - crouch + 0.95f * big), pc.Motor.ViewRotation, Vector3.Lerp(shoulderOffset, new Vector3(0.36f, 0.1f, -1.5f), ads) * (1f + 0.5f * big));
        }

        /// <summary>Puts the camera at pivot + rotation * offset, pulled in if something is in the way.</summary>
        void Place(Vector3 pivot, Quaternion rot, Vector3 offset, float roll = 0f)
        {
            Vector3 desired = pivot + rot * offset;
            Vector3 d = desired - pivot;
            float len = d.magnitude;
            if (len > 0.001f && Physics.SphereCast(pivot, collisionRadius, d / len, out var hit, len, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                desired = pivot + d / len * Mathf.Max(0.2f, hit.distance);
            transform.SetPositionAndRotation(desired, roll != 0f ? rot * Quaternion.Euler(0f, 0f, roll) : rot);
            ApplyShake();
        }

        /// <summary>Circles the arena, looking down on it, while it builds.</summary>
        void Orbit(VoxelWorld w)
        {
            Spectating = null;
            _deadTime = -1f;
            var point = Arena.Current != null ? Arena.Current.BuildCamera : null;
            if (point != null)
            {
                point.Pose(MatchManager.PhaseTime, out var p, out var r);
                transform.SetPositionAndRotation(p, r);
                ApplyShake();
                return;
            }
            var b = w.ArenaBounds;
            float dist = Mathf.Max(b.extents.x, b.extents.z) * 1.35f + 6f;
            float angle = MatchManager.Current.round * 67f + MatchManager.PhaseTime * 14f;
            Vector3 focus = b.center + Vector3.down * (b.extents.y * 0.3f);
            Vector3 pos = b.center + Quaternion.Euler(0f, angle, 0f) * new Vector3(0f, 0f, -dist) + Vector3.up * (b.extents.y + dist * 0.45f);
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(focus - pos));
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
            if ((Time.time - _deadTime < 2.5f && me.LastRagdoll != null) || me.InPractice || !AnyAlive())
            {
                // your own body
                Spectating = null;
                var bones = me.LastRagdoll != null ? Rig.MapBones(me.LastRagdoll.transform) : null;
                focus = bones != null && bones.TryGetValue("Bone", out var pelvis) ? pelvis.position : me.transform.position;
            }
            else
            {
                if (Spectating == null || !Watchable(Spectating)) Spectating = NextAlive(0);
                if (!InputMap.Blocked && Mouse.current != null)
                {
                    if (Mouse.current.leftButton.wasPressedThisFrame) Spectating = NextAlive(1);        // next player
                    else if (Mouse.current.rightButton.wasPressedThisFrame) Spectating = NextAlive(-1); // previous one
                }
                focus = Spectating != null ? Spectating.transform.position : me.transform.position;
            }
            Place(focus + Vector3.up * pivotHeight, Quaternion.Euler(_specPitch, _specYaw, 0), new Vector3(0f, 0.3f, -spectateDistance));
        }

        /// <summary>Someone fighting in the match (not dead, not off practising in their own chamber).</summary>
        static bool Watchable(PlayerController p) => p != null && p.IsAlive && !p.InPractice;

        static bool AnyAlive()
        {
            foreach (var p in PlayerController.All) if (Watchable(p)) return true;
            return false;
        }

        PlayerController NextAlive(int step)
        {
            var all = PlayerController.All;
            if (all.Count == 0) return null;
            int dir = step < 0 ? -1 : 1;
            for (int k = 0; k < all.Count; k++)
            {
                _specIndex = ((_specIndex + (k == 0 ? step : dir)) % all.Count + all.Count) % all.Count;
                var p = all[_specIndex];
                if (Watchable(p)) return p;
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
