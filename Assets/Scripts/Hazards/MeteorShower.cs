using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Map hazard: meteors (big balls of flame) raining on random spots under this box, from <see cref="startAfter"/>
    /// seconds into the round, faster and faster up to <see cref="maxPerSecond"/> and bigger and
    /// bigger up to <see cref="fullSize"/>, until there's a winner. Each one's landing spot is marked first. The Box Collider: cover the arena's floor with it, its top over the highest roof.
    /// </summary>
    [RequireComponent(typeof(BoxCollider), typeof(VoxelIgnore))]
    [AddComponentMenu("Sudden Mage Encounter/Meteor Shower")]
    public class MeteorShower : MonoBehaviour
    {
        static readonly List<MeteorShower> All = new();

        [Tooltip("Seconds after FIGHT that the first one comes.")]
        public float startAfter = 30f;
        [Tooltip("Meteors a second at first...")]
        public float startPerSecond = 0.33f;
        [Tooltip("...and at most...")]
        public float maxPerSecond = 16f;
        [Tooltip("...reached this many seconds after the first.")]
        public float rampSeconds = 90f;
        [Tooltip("How much bigger they (and their blasts) are by then.")]
        public float fullSize = 2f;

        BoxCollider _box;
        Arena _arena;
        float _owed = 1f;

        void Awake()
        {
            _box = GetComponent<BoxCollider>();
            _box.isTrigger = true;
            gameObject.layer = 2;
            _arena = GetComponentInParent<Arena>();
            foreach (var c in GetComponents<Collider>()) if (c != _box) c.enabled = false;
            if (TryGetComponent<Renderer>(out var r)) r.enabled = false;
        }

        void OnEnable() => All.Add(this);
        void OnDisable() => All.Remove(this);

        float Rate(float t) => startPerSecond * Mathf.Pow(Mathf.Max(1f, maxPerSecond / Mathf.Max(0.01f, startPerSecond)), Mathf.Clamp01(t / Mathf.Max(1f, rampSeconds)));
        float Size(float t) => Mathf.Lerp(1f, fullSize, t / Mathf.Max(1f, rampSeconds));

        /// <summary>Server, every frame: drop whatever's due.</summary>
        public static void ServerTick(float dt)
        {
            foreach (var m in All) m.Tick(dt);
        }

        void Tick(float dt)
        {
            float t = MapClock.Fighting(_arena) ? MapClock.Since(_arena, startAfter) : -1f;
            if (t < 0f) { _owed = 1f; return; }
            for (_owed = Mathf.Min(_owed + Rate(t) * dt, 4f); _owed >= 1f; _owed -= 1f) Drop(Size(t));
        }

        void Drop(float size)
        {
            Vector3 h = _box.size * 0.5f;
            for (int tries = 0; tries < 6; tries++)
            {
                Vector3 top = transform.TransformPoint(_box.center + new Vector3(Random.Range(-h.x, h.x), Mathf.Abs(h.y), Random.Range(-h.z, h.z)));
                Vector2 lean = Random.insideUnitCircle * SpellPlans.MeteorLean;
                Vector3 dir = new Vector3(lean.x, -1f, lean.y).normalized;
                // (it leans: start it off to one side, so where it lands is still under the box)
                Vector3 from = top - dir * 60f;
                if (!FirstTouch(from, dir, out var at, out var normal, size)) continue;
                Vector3 l = transform.InverseTransformPoint(at) - _box.center;
                if (Mathf.Abs(l.x) > Mathf.Abs(h.x) || Mathf.Abs(l.z) > Mathf.Abs(h.z)) continue; // (leaned out of it)
                SpellServer.DropMeteor(at, normal, dir, null, size);
                return;
            }
        }

        /// <summary>What a meteor coming down this way meets first: the ground (a roof), or a lava or water surface.</summary>
        public static bool FirstTouch(Vector3 from, Vector3 dir, out Vector3 at, out Vector3 normal, float size = 1f)
        {
            at = default;
            normal = Vector3.up;
            float best = float.MaxValue;
            if (Physics.SphereCast(from, SpellPlans.MeteorRadius * size, dir, out var hit, 400f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
            {
                best = hit.distance;
                at = hit.point;
                normal = hit.normal;
            }
            foreach (var l in Liquid.All)
            {
                float d = (l.Level - from.y) / dir.y;
                if (d < 0f || d >= best || !l.Over(from + dir * d)) continue;
                best = d;
                at = from + dir * d;
                normal = Vector3.up;
            }
            return best < float.MaxValue;
        }

        void OnDrawGizmos()
        {
            var b = GetComponent<BoxCollider>();
            if (b == null) return;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.15f, 0.05f, 0.8f);
            Gizmos.DrawWireCube(b.center, b.size);
            Gizmos.color = new Color(1f, 0.15f, 0.05f, 0.1f);
            Gizmos.DrawCube(b.center + Vector3.up * (Mathf.Abs(b.size.y) * 0.5f), new Vector3(b.size.x, 0.01f, b.size.z));
        }
    }
}
