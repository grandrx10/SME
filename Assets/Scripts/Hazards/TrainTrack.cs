using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Map hazard: a track that trains come down, from <see cref="from"/> to <see cref="to"/> (both at rail height; put
    /// them out of sight, inside the tunnels: cars appear and vanish there). Each track is on its own timetable, worked
    /// out alike on every machine from the round's clock. A train kills whoever it touches and smashes any ground
    /// spells have put in its way, but never the map's own (it goes straight through that); nothing stops it. Its roof can be stood on.
    /// <see cref="TrainSignal"/>s show when one's coming.
    /// </summary>
    [DefaultExecutionOrder(-150)]
    [AddComponentMenu("Sudden Mage Encounter/Train Track")]
    public class TrainTrack : MonoBehaviour
    {
        public static readonly List<TrainTrack> All = new();

        [Tooltip("Where trains come in (rail height) and where they go out.")]
        public Transform from, to;

        [Header("Timetable")]
        [Tooltip("Seconds after FIGHT that the first train comes.")]
        public float firstAfter = 10f;
        [Tooltip("Seconds between one train leaving and the next coming (random).")]
        public float minGap = 3f, maxGap = 15f;
        [Tooltip("Seconds its signals are green before it comes.")]
        public float warning = 3f;

        [Header("Train")]
        public float speed = 32f;
        [Tooltip("Cars in a train (random).")]
        public int minCars = 3, maxCars = 6;
        [Tooltip("Car model (empty = the built-in voxel one): origin at the middle of its underside, +Z forward. Set Car Size to match.")]
        public GameObject carPrefab;
        [Tooltip("The car at the front, and at the back (empty = Car Prefab).")]
        public GameObject frontPrefab, backPrefab;
        [Tooltip("Width, height and length of a car (m): what kills, smashes and can be stood on.")]
        public Vector3 carSize = CarSize;
        [Tooltip("Between cars (m).")]
        public float carGap = CarGap;
        /// <summary>The built-in car (and the prefabs made of it).</summary>
        public static readonly Vector3 CarSize = new(8f, 10f, 16f);
        public const float CarGap = 1f;

        /// <summary>One train: when its nose reaches <see cref="from"/> (seconds after FIGHT), and how long it is.</summary>
        class Train { public double at; public int cars; public float length; public double gone; }

        readonly List<Train> _trains = new();
        System.Random _random;
        double _planned = -1;
        Arena _arena;

        void Awake() => _arena = GetComponentInParent<Arena>();
        void OnEnable() => All.Add(this);

        void OnDisable()
        {
            All.Remove(this);
            ClearCars();
        }

        public Vector3 Forward => (to.position - from.position).normalized;
        public float Distance => Vector3.Distance(from.position, to.position);
        public Vector3 Velocity => Forward * speed;
        bool Ready => from != null && to != null && MapClock.Live(_arena) && Distance > 1f;

        // ------------------------------------------------------------------ the timetable

        int Index
        {
            get
            {
                int i = 0;
                foreach (var t in _arena.GetComponentsInChildren<TrainTrack>(true))
                {
                    if (t == this) return i;
                    i++;
                }
                return i;
            }
        }

        /// <summary>Plans trains up to (and past) <paramref name="time"/>.</summary>
        void Plan(double time)
        {
            double start = MapClock.StartOf(_arena);
            if (_planned != start)
            {
                _planned = start;
                _trains.Clear();
                // (the same seed everywhere: when the clock started, to the millisecond, and which track this is)
                _random = new System.Random(unchecked((Index + 1) * 19349663 ^ (int)(long)(start * 1000.0)));
            }
            while (_trains.Count == 0 || _trains[^1].at < time + warning + 1.0)
            {
                bool first = _trains.Count == 0;
                double gap = minGap + _random.NextDouble() * Mathf.Max(0f, maxGap - minGap);
                int cars = _random.Next(Mathf.Max(1, minCars), Mathf.Max(minCars, maxCars) + 1);
                float length = cars * carSize.z + (cars - 1) * carGap;
                double at = !first ? _trains[^1].gone + gap : MapClock.ByHand(_arena) ? warning : firstAfter; // (set off by hand: the first comes at once)
                _trains.Add(new Train { at = at, cars = cars, length = length, gone = at + (Distance + length) / Mathf.Max(1f, speed) });
            }
        }

        Train Now(double t)
        {
            for (int i = _trains.Count - 1; i >= 0; i--)
                if (_trains[i].at <= t) return t < _trains[i].gone && _trains[i].at < MapClock.EndTime(_arena) ? _trains[i] : null;
            return null;
        }

        /// <summary>A train is about to come (its signals are green).</summary>
        public bool Warning
        {
            get
            {
                if (!Ready) return false;
                double t = MapClock.RunTime(_arena);
                if (t < 0) return false;
                Plan(t);
                foreach (var tr in _trains)
                    if (t < tr.at && t >= tr.at - warning && tr.at < MapClock.EndTime(_arena)) return true;
                return false;
            }
        }

        // ------------------------------------------------------------------ where it is

        /// <summary>The train on this track right now: how far along its nose and tail are (clipped to the track).</summary>
        public bool Running(out float tail, out float nose)
        {
            tail = nose = 0f;
            if (!Ready) return false;
            double t = MapClock.RunTime(_arena);
            if (t < 0) return false;
            Plan(t);
            var tr = Now(t);
            if (tr == null) return false;
            float head = (float)(t - tr.at) * speed, d = Distance;
            nose = Mathf.Min(head, d);
            tail = Mathf.Max(head - tr.length, 0f);
            return nose > tail;
        }

        const float BodyRadius = 0.45f, RoofSlack = 0.3f;

        /// <summary>Is a body (hips here) touching a train? (Not when it's on the roof.)</summary>
        public static bool Hits(Vector3 hips, out Vector3 velocity)
        {
            foreach (var t in All)
            {
                if (!t.Running(out float tail, out float nose)) continue;
                Vector3 f = t.Forward;
                Quaternion inv = Quaternion.Inverse(Quaternion.LookRotation(f, Vector3.up));
                for (int k = 0; k < 3; k++)
                {
                    Vector3 p = hips + Vector3.up * (k == 0 ? -0.6f : k == 1 ? 0f : 0.6f);
                    Vector3 l = inv * (p - t.from.position);
                    if (l.z < tail - BodyRadius || l.z > nose + BodyRadius || Mathf.Abs(l.x) > t.carSize.x * 0.5f + BodyRadius) continue;
                    if (l.y < -0.1f || l.y > t.carSize.y - RoofSlack) continue;
                    velocity = t.Velocity;
                    return true;
                }
            }
            velocity = default;
            return false;
        }

        // ------------------------------------------------------------------ server: smashing through

        /// <summary>Server, every frame: breaks any ground spells have put in the train's way (or against it). The map itself is left alone.</summary>
        public void ServerSmash()
        {
            var w = VoxelWorld.Current;
            if (w == null || w.SummonedCount == 0 || !Running(out float tail, out float nose)) return;
            Vector3 f = Forward, o = from.position;
            Quaternion inv = Quaternion.Inverse(Quaternion.LookRotation(f, Vector3.up));
            float ahead = Mathf.Min(nose + 0.75f, Distance), half = carSize.x * 0.5f;
            var cells = w.SummonedWhere(p =>
            {
                Vector3 l = inv * (p - o);
                return l.z >= tail && l.z <= ahead && Mathf.Abs(l.x) <= half && l.y >= 0f && l.y <= carSize.y;
            });
            cells.RemoveAll(c => w.IsIndestructible(w.Get(c)));
            if (cells.Count > 0) WorldNet.ServerWash(cells, f * 2.5f);
        }

        // ------------------------------------------------------------------ looks (and the solid cars)

        readonly List<TrainCar> _cars = new();
        Train _shown;
        float _nextShake;

        void Update()
        {
            Train tr = null;
            double t = MapClock.RunTime(_arena);
            if (Running(out _, out _)) tr = Now(t);
            if (tr != _shown)
            {
                ClearCars();
                _shown = tr;
                for (int i = 0; tr != null && i < tr.cars; i++) _cars.Add(TrainCar.Create(this, i == 0, i == tr.cars - 1));
            }
            if (tr == null) return;
            float head = (float)(t - tr.at) * speed, d = Distance;
            Quaternion rot = Quaternion.LookRotation(Forward, Vector3.up);
            for (int i = 0; i < _cars.Count; i++)
            {
                float front = head - i * (carSize.z + carGap), back = front - carSize.z;
                float a = Mathf.Clamp(back, 0f, d), b = Mathf.Clamp(front, 0f, d);
                // only the part of it that's on the track (it comes out of one end and goes into the other)
                _cars[i].Place(from.position + Forward * ((a + b) * 0.5f), rot, (b - a) / carSize.z);
            }
            if (Time.time < _nextShake) return;
            _nextShake = Time.time + 0.15f;
            CameraRig.ShakeFrom(from.position + Forward * Mathf.Clamp(head, 0f, d), 0.12f, 0.2f);
        }

        void ClearCars()
        {
            foreach (var c in _cars) if (c != null) Destroy(c.gameObject);
            _cars.Clear();
            _shown = null;
        }

        void OnDrawGizmos()
        {
            if (from == null || to == null) return;
            Vector3 f = to.position - from.position;
            if (f.sqrMagnitude < 0.01f) return;
            Gizmos.color = new Color(1f, 0.85f, 0.1f, 0.9f);
            Gizmos.DrawLine(from.position, to.position);
            Gizmos.matrix = Matrix4x4.TRS((from.position + to.position) * 0.5f, Quaternion.LookRotation(f, Vector3.up), Vector3.one);
            Gizmos.DrawWireCube(Vector3.up * (carSize.y * 0.5f), new Vector3(carSize.x, carSize.y, f.magnitude));
            Gizmos.matrix = Matrix4x4.identity;
            Vector3 n = f.normalized, side = Vector3.Cross(Vector3.up, n);
            for (int k = 1; k <= 3; k++)
            {
                Vector3 p = Vector3.Lerp(from.position, to.position, k / 4f);
                Gizmos.DrawLine(p, p - n * 1.5f + side);
                Gizmos.DrawLine(p, p - n * 1.5f - side);
            }
        }
    }
}
