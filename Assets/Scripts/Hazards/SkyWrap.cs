using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Skyland: falling off the bottom doesn't kill. Anyone inside the area (x/z) who drops below <see cref="bottom"/>
    /// comes back in at <see cref="top"/>, straight above where they fell, still falling. Each fall adds <see cref="FallHeat"/> to the
    /// hex bar beside them, which drains from full to empty in <see cref="CoolTime"/> seconds; full (or past it) and they
    /// blow to bits. Going off a side of the area (front, back, left, right) brings them back in at the opposite
    /// side, at the same height: that doesn't fill the bar.
    /// All of it is relative to this object: drag the handles in the Scene view, or type the numbers.
    /// Add with Sudden Mage Encounter ▸ Hazards ▸ Add Sky Wrap.
    /// </summary>
    [AddComponentMenu("Sudden Mage Encounter/Sky Wrap")]
    public class SkyWrap : MonoBehaviour
    {
        /// <summary>How much of the bar each fall fills; how long the whole bar takes to drain (s). Full: dead.</summary>
        public const float FallHeat = 1f / 3f, CoolTime = 10f;

        /// <summary>One body's fall bar: fills <see cref="FallHeat"/> a fall, drains from full in <see cref="CoolTime"/> s.</summary>
        public struct Heat
        {
            float _v, _at;
            public float Now => Mathf.Max(0f, _v - (Time.time - _at) / CoolTime);
            /// <summary>A fall: true if that fills it (dead).</summary>
            public bool Add()
            {
                _v = Now + FallHeat;
                _at = Time.time;
                return _v >= 0.999f;
            }
            public void Clear() => _v = 0f;
        }
        /// <summary>Wind round a body after it wraps lasts until it lands: at least the first, at most the second (s).</summary>
        public const float WindMin = 0.35f, WindMax = 10f;

        [Tooltip("Width (x) and depth (z) of the area, centred on the map. 0 = fit to the map.")]
        public Vector2 size;
        [Tooltip("Height you come back in at, from the middle of the map.")]
        public float top = 40f;
        [Tooltip("Fall below this height (from the middle of the map) and you wrap.")]
        public float bottom = -25f;
        [Tooltip("The bottom shows the top, like a portal (one more render of the scene while it's on screen).")]
        public bool bottomWindow = true;
        /// <summary>The middle of the map's meshes, from this object (kept up to date by the editor).</summary>
        [SerializeField, HideInInspector] Vector3 mapCentre;

        /// <summary>The middle of the map: everything is measured from here.</summary>
        public Vector3 Origin => transform.position + mapCentre;

        static readonly List<SkyWrap> All = new();

        void OnEnable() => All.Add(this);
        void OnDisable() => All.Remove(this);

        // the floor shows the top, like a portal (looks only)
        void Start()
        {
            if (Application.isBatchMode) return;
            if (bottomWindow) SkyWindow.Create(this, SkyWindow.Face.Bottom);
        }

        public bool Unset => size.x <= 0f || size.y <= 0f;
        public float TopY => Origin.y + top;
        public float BottomY => Origin.y + bottom;

        /// <summary>Is <paramref name="p"/> over the area (x/z)?</summary>
        public bool Covers(Vector3 p)
        {
            if (Unset) return true;
            Vector3 l = p - Origin;
            return Mathf.Abs(l.x) <= size.x * 0.5f && Mathf.Abs(l.z) <= size.y * 0.5f;
        }

        /// <summary>The Sky Wrap of the arena (or practice chamber) <paramref name="p"/> is in, over its area or not.</summary>
        public static SkyWrap Near(Vector3 p)
        {
            if (All.Count == 0) return null;
            int slot = Practice.SlotAt(p);
            SkyWrap any = null;
            foreach (var s in All)
            {
                if (s == null || Practice.SlotAt(s.transform.position) != slot) continue;
                if (s.Covers(p)) return s;
                any ??= s;
            }
            return any;
        }

        /// <summary>
        /// Gone off a side of the area (front, back, left or right): where they come back in, just inside the opposite
        /// side, at the same height.
        /// </summary>
        public bool Across(Vector3 p, out Vector3 to)
        {
            to = p;
            if (Unset) return false;
            Vector3 l = p - Origin;
            float hx = size.x * 0.5f, hz = size.y * 0.5f;
            bool moved = false;
            if (Mathf.Abs(l.x) > hx) { l.x = -Mathf.Sign(l.x) * (hx - Inset); moved = true; }
            if (Mathf.Abs(l.z) > hz) { l.z = -Mathf.Sign(l.z) * (hz - Inset); moved = true; }
            to = Origin + l;
            return moved;
        }

        /// <summary>How far inside the opposite side a body comes back in (m).</summary>
        const float Inset = 0.3f;
        /// <summary>The Sky Wrap over <paramref name="p"/> (in its arena or practice chamber), if any.</summary>
        public static SkyWrap At(Vector3 p)
        {
            if (All.Count == 0) return null;
            int slot = Practice.SlotAt(p);
            foreach (var s in All)
                if (s != null && Practice.SlotAt(s.transform.position) == slot && s.Covers(p)) return s;
            return null;
        }

        /// <summary>Where a body that fell out at <paramref name="p"/> comes back in: same x and z (kept inside the area), at the top.</summary>
        public Vector3 TopAbove(Vector3 p)
        {
            if (Unset) return new Vector3(p.x, TopY, p.z);
            Vector3 o = Origin;
            float hx = Mathf.Max(0f, size.x * 0.5f - 1f), hz = Mathf.Max(0f, size.y * 0.5f - 1f);
            return new Vector3(o.x + Mathf.Clamp(p.x - o.x, -hx, hx), TopY, o.z + Mathf.Clamp(p.z - o.z, -hz, hz));
        }

        /// <summary>Editor: fit the area round the map's meshes, the top 6 m over them and the bottom at the arena's kill height.</summary>
        public void FitToMap()
        {
            var arena = GetComponentInParent<Arena>();
            if (arena == null || !MapBounds(out var b)) return;
            mapCentre = b.center - transform.position;
            size = new Vector2(b.size.x + 8f, b.size.z + 8f);
            top = b.extents.y + 6f;
            bottom = arena.KillY - b.center.y;
        }

        /// <summary>Editor: the map's meshes, all together.</summary>
        public bool MapBounds(out Bounds b)
        {
            b = new Bounds(transform.position, Vector3.zero);
            var arena = GetComponentInParent<Arena>();
            if (arena == null) return false;
            bool any = false;
            foreach (var mf in Arena.VoxelSources(arena.transform))
            {
                var r = mf.GetComponent<MeshRenderer>();
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            return any;
        }

        /// <summary>Editor: follow the map's middle if it moved, keeping the top and bottom at the same heights. True if anything changed.</summary>
        public bool Recentre()
        {
            if (!MapBounds(out var b)) return false;
            Vector3 c = b.center - transform.position;
            if ((c - mapCentre).sqrMagnitude < 1e-4f) return false;
            float dy = c.y - mapCentre.y;
            top -= dy;
            bottom -= dy;
            mapCentre = c;
            return true;
        }

        /// <summary>Scene view: the bottom (fall below it: wrap) in red, the top (where you come back in) in sky blue.</summary>
        void OnDrawGizmos()
        {
            if (Unset) return;
            var c = Origin;
            Vector3 s = new(size.x, 0f, size.y);
            Plane(new Vector3(c.x, BottomY, c.z), s, new Color(1f, 0.25f, 0.15f), "Bottom: fall past it and you wrap");
            Plane(new Vector3(c.x, TopY, c.z), s, new Color(0.45f, 0.8f, 1f), "Top: you come back in here");
            // the sides of the area
            Gizmos.color = new Color(1f, 1f, 1f, 0.35f);
            Gizmos.DrawWireCube(new Vector3(c.x, (TopY + BottomY) * 0.5f, c.z), new Vector3(size.x, TopY - BottomY, size.y));
        }

        static void Plane(Vector3 c, Vector3 size, Color col, string label)
        {
            Gizmos.color = new Color(col.r, col.g, col.b, 0.12f);
            Gizmos.DrawCube(c, size);
            Gizmos.color = col;
            Gizmos.DrawWireCube(c, size);
#if UNITY_EDITOR
            UnityEditor.Handles.color = col;
            UnityEditor.Handles.Label(c + new Vector3(-size.x * 0.5f, 0.5f, -size.z * 0.5f), label);
#endif
        }
    }
    /// <summary>Inked wind round a body that just wrapped: rings whipping round it and streaks rushing up past it (it's falling).</summary>
    public class SkyWrapWind : MonoBehaviour
    {
        class Stroke { public ToonLine line; public Vector3 offset; public float born, life, length; }

        Combatant _c;
        Transform _root;
        readonly ToonLine[] _rings = new ToonLine[3];
        readonly List<Stroke> _lines = new();
        readonly List<Vector3> _pts = new(), _part = new();
        float _from, _endAt, _owed;

        public static void Play(Combatant c)
        {
            if (c == null) return;
            var w = c.GetComponent<SkyWrapWind>();
            if (w == null) w = c.gameObject.AddComponent<SkyWrapWind>();
            w._c = c;
            w._from = Time.time;
            w._endAt = -1f;
        }

        void LateUpdate()
        {
            float now = Time.time;
            float age = now - _from;
            // blowing until it lands (or dies)
            if (_endAt < 0f && (_c == null || !_c.IsAlive || age >= SkyWrap.WindMax
                || age >= SkyWrap.WindMin && (_c.ViewFlags & NetState.Grounded) != 0)) _endAt = now;
            bool active = _endAt < 0f;
            // rings pop out, whip round, and shrink away once it's landed
            float ring = active ? Mathf.Clamp01(age / 0.08f) : Mathf.Clamp01(1f - (now - _endAt) / 0.2f);
            if (!active && ring <= 0f && _lines.Count == 0)
            {
                if (_root != null) { Destroy(_root.gameObject); _root = null; }
                return;
            }
            if (_root == null)
            {
                _root = new GameObject("SkyWrapWind") { layer = Layers.Fx }.transform;
                for (int i = 0; i < _rings.Length; i++) _rings[i] = new ToonLine(_root, 0.06f);
            }
            Vector3 hips = transform.position;
            for (int i = 0; i < _rings.Length; i++)
            {
                if (ring <= 0f) { _rings[i].Hide(); continue; }
                float spin = age * (i % 2 == 0 ? 20f : -16f) + i * 2f;
                float h = (i - 1) * 0.55f, r = (i == 1 ? 0.7f : 0.55f) * ring;
                Swoosh.Arc(_pts, hips + Vector3.up * h, r, spin, Mathf.Deg2Rad * 230f, 24, 0.2f);
                _rings[i].Set(_pts);
            }
            if (active)
            {
                _owed += 18f * Time.deltaTime;
                while (_owed >= 1f)
                {
                    _owed -= 1f;
                    float a = Random.Range(0f, Mathf.PI * 2f), d = Random.Range(0.45f, 0.9f);
                    _lines.Add(new Stroke
                    {
                        line = new ToonLine(_root, Random.Range(0.04f, 0.07f)),
                        offset = new Vector3(Mathf.Cos(a) * d, Random.Range(-0.9f, 0.6f), Mathf.Sin(a) * d),
                        born = now, life = Random.Range(0.22f, 0.32f), length = Random.Range(1.2f, 2.2f),
                    });
                }
            }
            for (int i = _lines.Count - 1; i >= 0; i--)
            {
                var s = _lines[i];
                float u = (now - s.born) / s.life;
                if (u >= 1f) { s.line.Destroy(); _lines.RemoveAt(i); continue; }
                // streaming straight up off the falling body
                _pts.Clear();
                _pts.Add(hips + s.offset);
                _pts.Add(hips + s.offset + Vector3.up * s.length);
                Swoosh.Window(u, out float tail, out float head);
                Swoosh.Part(_pts, tail, head, _part);
                s.line.Set(_part);
            }
        }

        void OnDestroy()
        {
            if (_root != null) Destroy(_root.gameObject);
        }
    }
}
