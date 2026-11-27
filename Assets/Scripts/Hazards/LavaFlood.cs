using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Map hazard: a lava pool that floods. <see cref="startAfter"/> seconds into the round lava pours from the sky at
    /// <see cref="fallPoint"/> for <see cref="fallSeconds"/> (only a look), then the pool rises to <see cref="riseTo"/>
    /// and stays there. Touching it (at any level) sets you alight and launches you upwards, once a second at most
    /// (SpellServer.Hazards). The Box Collider's top is the pool's starting level (see <see cref="Liquid"/>).
    /// </summary>
    [AddComponentMenu("Sudden Mage Encounter/Lava Flood")]
    public class LavaFlood : Liquid
    {
        [Tooltip("Seconds after FIGHT that the lava fall starts.")]
        public float startAfter = 55f;
        [Tooltip("How long lava pours from the sky before the pool starts rising.")]
        public float fallSeconds = 5f;
        [Tooltip("The level the lava rises to.")]
        public Transform riseTo;
        [Tooltip("Seconds it takes to get there.")]
        public float riseSeconds = 40f;

        [Header("Lava fall")]
        [Tooltip("Where the lava falls from (up in the sky). Empty = no lava fall.")]
        public Transform fallPoint;
        [Tooltip("How wide the lava fall is (m).")]
        public Vector2 fallSize = new(4f, 4f);
        [Tooltip("Lava voxels a second.")]
        public float fallRate = 160f;

        protected override float Wait => startAfter;
        protected override float LevelAt(float t)
        {
            if (riseTo == null) return BaseY;
            float u = Mathf.Clamp01((t - fallSeconds) / Mathf.Max(0.1f, riseSeconds));
            return Mathf.Lerp(BaseY, riseTo.position.y, u);
        }

        public static bool InAny(Vector3 p, float pad)
        {
            foreach (var l in All) if (l is LavaFlood && l.Contains(p, pad)) return true;
            return false;
        }

        /// <summary>Nothing but lava underneath this point.</summary>
        public static bool Below(Vector3 p)
        {
            foreach (var l in All)
            {
                if (l is not LavaFlood || !l.Over(p)) continue;
                float down = p.y - l.Level;
                if (down >= 0f && !Physics.Raycast(p, Vector3.down, down, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ looks

        static Material _mat;

        protected override Material Look
        {
            get
            {
                if (_mat != null) return _mat;
                return _mat = new Material(Shader.Find("SME/Lava") ?? Shader.Find("Universal Render Pipeline/Unlit")) { name = "Lava" };
            }
        }

        protected override void Entered(Vector3 at, float speed) => FireVfx.Burst(at, 0.6f);
        protected override Color GizmoColour => new(1f, 0.4f, 0.05f);

        float _owed;
        LavaFallView _fall;

        protected override void LateUpdate()
        {
            base.LateUpdate();
            if (fallPoint == null || !MapClock.Fighting(Home)) return;
            float t = MapClock.Since(Home, startAfter);
            if (t < 0 || t > fallSeconds) return;
            if (_fall == null) _fall = LavaFallView.Create(Home.transform);
            _owed = Mathf.Min(_owed + fallRate * Time.deltaTime, 40f); // (a hitch: don't drop them all at once)
            for (; _owed >= 1f; _owed -= 1f)
            {
                Vector3 p = fallPoint.position + fallPoint.rotation * new Vector3(Random.Range(-0.5f, 0.5f) * fallSize.x, Random.Range(-0.5f, 0.5f), Random.Range(-0.5f, 0.5f) * fallSize.y);
                float ground = BottomY;
                if (Physics.Raycast(p, Vector3.down, out var hit, 300f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) ground = hit.point.y;
                if (Over(p)) ground = Mathf.Max(ground, Level);
                _fall.Add(p, ground);
            }
        }

        void OnDrawGizmosSelected()
        {
            if (riseTo != null) DrawLevel(riseTo.position.y);
            if (fallPoint == null) return;
            Gizmos.color = GizmoColour;
            Gizmos.matrix = Matrix4x4.TRS(fallPoint.position, fallPoint.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(fallSize.x, 1f, fallSize.y));
            Gizmos.DrawLine(Vector3.zero, Vector3.down * 40f);
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
