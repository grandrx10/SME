using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The pot and its plants: the user's ProBuilder models (Resources/VFX/Pot, Sunflower, Cactus, FlyTrap), each rebuilt
    /// once into a clean template the way the sword and hammer are (meshes copied out, no colliders, every part on
    /// SME/ToonFade so it phases in and out in a dither). Each template's origin is the bottom middle of the model. The
    /// models were made standing together in one place, so where a plant stood over the pot there is where it grows.
    /// The cactus came as a body and one spike: the spike is copied all round the body here.
    /// </summary>
    public static class PotModel
    {
        class Template
        {
            public GameObject go;
            public Vector3 bottomWorld;  // (in the scene the user made it in)
            public Vector3 head;         // the plant's head, from the template's origin
            public Bounds bounds;        // everything in it, from its origin
            public bool any;
            public readonly List<string> parts = new();
        }

        static Template _pot;
        static readonly Dictionary<byte, Template> Plants = new();

        static string NameOf(byte kind) => kind switch
        {
            SpellPlans.PotSunflower => "Sunflower",
            SpellPlans.PotCactus => "Cactus",
            _ => "FlyTrap",
        };

        public static GameObject SpawnPot() => Spawn(Pot().go, "Pot");
        public static GameObject SpawnPlant(byte kind) => Spawn(Plant(kind).go, NameOf(kind));

        static GameObject Spawn(GameObject template, string name)
        {
            var go = Object.Instantiate(template);
            go.name = name;
            go.SetActive(true);
            return go;
        }

        /// <summary>Where the plant's own origin (its bottom middle) sits, from the pot's.</summary>
        public static Vector3 PlantOffset(byte kind) => Plant(kind).bottomWorld - Pot().bottomWorld + Vector3.up * SpellPlans.PlantLift;

        /// <summary>The pot and its plant together, from the pot's origin (a thrown pot's body).</summary>
        public static Bounds Bounds(byte kind)
        {
            var b = Pot().bounds;
            var p = Plant(kind);
            if (p.any) b.Encapsulate(new Bounds(p.bounds.center + PlantOffset(kind), p.bounds.size));
            if (b.size.sqrMagnitude < 1e-4f) b = new Bounds(Vector3.up * 0.4f, new Vector3(0.4f, 0.8f, 0.4f));
            return b;
        }

        /// <summary>The plant's head (sunflower face, cactus middle, fly trap mouth), from the pot's origin (the bottom of the pot).</summary>
        public static Vector3 Head(byte kind) => PlantOffset(kind) + Plant(kind).head;

        static Template Pot()
        {
            if (_pot != null) return _pot;
            _pot = Build("Pot", null);
            return _pot;
        }

        static Template Plant(byte kind)
        {
            if (Plants.TryGetValue(kind, out var t)) return t;
            t = Build(NameOf(kind), kind);
            Plants[kind] = t;
            return t;
        }

        static Template Build(string name, byte? kind)
        {
            var t = new Template();
            t.go = new GameObject(name + " (template)") { layer = Layers.Fx, hideFlags = HideFlags.HideAndDontSave };
            t.go.SetActive(false);
            Object.DontDestroyOnLoad(t.go);
            var prefab = Resources.Load<GameObject>("VFX/" + name);
            if (prefab == null)
            {
                Debug.LogWarning("[SME] Resources/VFX/" + name + " is missing.");
                t.head = Vector3.up * 0.5f;
                return t;
            }
            // instantiated (active) so ProBuilder builds the meshes, copied out, then thrown away
            var src = Object.Instantiate(prefab);
            src.transform.SetPositionAndRotation(prefab.transform.position, Quaternion.identity); // (where the user made it)
            var filters = new List<MeshFilter>();
            foreach (var mf in src.GetComponentsInChildren<MeshFilter>(true)) if (mf.sharedMesh != null) filters.Add(mf);
            MeshFilter spike = kind == SpellPlans.PotCactus ? filters.Find(f => f.name == "Spike") : null;

            // the bottom middle of the model (the cactus's spike left out: it's copied round the body below)
            Bounds b = default;
            bool any = false;
            foreach (var mf in filters)
            {
                if (mf == spike) continue;
                var r = mf.GetComponent<Renderer>();
                if (r == null) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            t.bottomWorld = any ? new Vector3(b.center.x, b.min.y, b.center.z) : src.transform.position;
            // the plants face the other way from how they were made (turned about their own middle)
            Quaternion turn = kind.HasValue ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
            Matrix4x4 toLocal = Matrix4x4.Rotate(turn) * Matrix4x4.Translate(-t.bottomWorld);

            Bounds head = default;
            bool anyHead = false;
            foreach (var mf in filters)
            {
                if (mf == spike) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                var mats = mr != null ? mr.sharedMaterials : null;
                var part = AddPart(t, mf.name, Object.Instantiate(mf.sharedMesh), mats, toLocal * mf.transform.localToWorldMatrix);

                // the head: the sunflower's yellow face, the fly trap's two jaws
                bool isHead = kind == SpellPlans.PotSunflower ? IsYellow(mats) : kind == SpellPlans.PotFlyTrap && IsJaw(mf.name);
                if (isHead && mr != null)
                {
                    if (!anyHead) { head = mr.bounds; anyHead = true; } else head.Encapsulate(mr.bounds);
                }
            }
            if (kind == SpellPlans.PotCactus && spike != null) Spikes(t, spike, b, toLocal);
            t.head = turn * (anyHead ? head.center - t.bottomWorld : (any ? b.center - t.bottomWorld : Vector3.up * 0.4f));
            Object.Destroy(src);
            return t;
        }

        static Transform AddPart(Template t, string name, Mesh mesh, Material[] mats, Matrix4x4 m)
        {
            SwordModel.Part(t.go.transform, mesh, mats, m);
            var part = t.go.transform.GetChild(t.go.transform.childCount - 1);
            part.name = name;
            t.parts.Add(name);
            // (what it all takes up, for a thrown pot's body)
            var mb = mesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 p = m.MultiplyPoint3x4(c);
                if (!t.any) { t.bounds = new Bounds(p, Vector3.zero); t.any = true; } else t.bounds.Encapsulate(p);
            }
            return part;
        }

        /// <summary>
        /// A fly trap jaw's teeth: a row of small outlined spikes along its rim (the long edge away from where it's hinged),
        /// pointing out. They're children of the jaw, so they snap shut with it.
        /// </summary>
        static void Teeth(Transform jaw, Bounds mb)
        {
            Vector3 e = mb.extents;
            int[] order = { 0, 1, 2 };
            System.Array.Sort(order, (a, b) => e[b].CompareTo(e[a]));
            int len = order[0], wid = order[1], thick = order[2];
            Vector3 L = Vector3.zero, W = Vector3.zero, T = Vector3.zero;
            L[len] = 1f; W[wid] = 1f; T[thick] = 1f;
            // the rim: the edge farther from the hinge (the jaw's own origin)
            float side = Mathf.Abs(mb.center[wid] + e[wid]) >= Mathf.Abs(mb.center[wid] - e[wid]) ? 1f : -1f;
            Vector3 rim = mb.center + W * (side * e[wid]);
            Vector3 dir = W * side;
            float tooth = Mathf.Clamp(e[wid] * 0.45f, 0.04f, 0.14f);
            const int n = 7;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = rim + L * Mathf.Lerp(-e[len] * 0.85f, e[len] * 0.85f, i / (n - 1f));
                float k = 1f - 0.35f * Mathf.Abs(i / (n - 1f) * 2f - 1f); // longest in the middle
                SwordModel.Part(jaw, Glow.PrismMesh, new[] { ToothMaterial }, Matrix4x4.TRS(p + dir * (tooth * k * 0.5f), Quaternion.LookRotation(dir, T), new Vector3(tooth * 0.4f, tooth * 0.4f, tooth * k)));
                jaw.GetChild(jaw.childCount - 1).name = "Tooth";
            }
        }

        static Material _tooth;
        /// <summary>The teeth: a pale, creamy green.</summary>
        static Material ToothMaterial
        {
            get
            {
                if (_tooth != null) return _tooth;
                var shader = Shader.Find("SME/Toon");
                _tooth = shader != null ? new Material(shader) : new Material(Glow.Shared);
                _tooth.name = "Tooth";
                if (_tooth.HasProperty("_BaseColor")) _tooth.SetColor("_BaseColor", new Color(0.93f, 0.95f, 0.8f));
                return _tooth;
            }
        }

        /// <summary>The fly trap's two jaws (in the user's model).</summary>
        public static bool IsJaw(string part) => part == "Cube (2)" || part == "Cube (3)";

        /// <summary>A sunflower part with the user's yellow (the face that glows).</summary>
        static bool IsYellow(Material[] mats)
        {
            if (mats == null) return false;
            foreach (var m in mats) if (m != null && m.name.StartsWith("Yellow")) return true;
            return false;
        }

        /// <summary>A part of a spawned sunflower that glows (yellow): spawned parts keep the template's names, plus a "Glow" mark on the face.</summary>
        public static bool Glows(Renderer r) => r != null && r.sharedMaterial != null && r.sharedMaterial.name.StartsWith("Yellow");

        /// <summary>
        /// The cactus: the user's one spike, copied all round the body. Each copy stands out from the body's axis as far as
        /// the original does, turned so it points straight out the way the original points out where it is; a few on top.
        /// </summary>
        static void Spikes(Template t, MeshFilter spike, Bounds body, Matrix4x4 toLocal)
        {
            var mr = spike.GetComponent<MeshRenderer>();
            var mats = mr != null ? mr.sharedMaterials : null;
            Vector3 axis = new(body.center.x, 0f, body.center.z);
            Vector3 p0 = spike.transform.position;
            Vector3 out0 = new Vector3(p0.x, 0f, p0.z) - axis;
            float r0 = out0.magnitude;
            out0 = r0 > 1e-3f ? out0 / r0 : Vector3.forward;
            Quaternion rot0 = spike.transform.rotation;
            Vector3 scale = spike.transform.lossyScale;
            float lo = Mathf.Max(body.min.y + body.size.y * 0.15f, Mathf.Min(p0.y, body.max.y)), hi = body.max.y - body.size.y * 0.12f;
            int side = SpellPlans.CactusSpikes - 3;
            for (int i = 0; i < SpellPlans.CactusSpikes; i++)
            {
                Vector3 pos, dir;
                if (i < side)
                {
                    float a = i * 137.5f * Mathf.Deg2Rad;
                    dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    float y = Mathf.Lerp(lo, hi, (i + 0.5f) / side);
                    pos = new Vector3(axis.x, y, axis.z) + dir * r0;
                }
                else
                {
                    // on top, tipped out a little
                    float a = (i - side) * 120f * Mathf.Deg2Rad;
                    Vector3 flat = new(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    dir = (Vector3.up + flat * 0.45f).normalized;
                    pos = new Vector3(axis.x, body.max.y - Mathf.Max(0f, body.extents.x - r0), axis.z) + flat * (body.extents.x * 0.35f);
                }
                var rot = Quaternion.FromToRotation(out0, dir) * rot0;
                AddPart(t, "Spike", Object.Instantiate(spike.sharedMesh), mats, toLocal * Matrix4x4.TRS(pos, rot, scale));
            }
        }
    }

    /// <summary>
    /// A pot on screen: it phases into its holder's hands (or wherever it is), the plant grows up out of it, and it follows
    /// its holder, stands on the ground, or flies. The Sunflower glows brighter and brighter, then its beam goes where the
    /// holder aims; the Fly Trap's jaws snap shut on whoever it's eaten. Dithers away at the end.
    /// </summary>
    /// <summary>
    /// Inside a fly trap: the body's own collider is off (it would shove whoever holds the trap away), back on when it's let
    /// out. On every peer, so nobody's movement bumps into it.
    /// </summary>
    public class EatenBodyFx : MonoBehaviour
    {
        Combatant _c;
        bool _off;
        Collider[] _cols;

        void Awake() => _c = GetComponent<Combatant>();

        void LateUpdate()
        {
            // (and gone with Ephemerate: no collisions at all)
            bool eaten = _c != null && _c.IsSpawned && _c.IsAlive && (_c.Status.Value.Eaten(WorldNet.Now) || _c.Status.Value.Ephemeral(WorldNet.Now));
            if (eaten == _off) return;
            _off = eaten;
            _cols ??= GetComponents<Collider>();
            foreach (var c in _cols) if (c != null && !c.isTrigger) c.enabled = !eaten;
        }
    }

    public class PotView : MonoBehaviour
    {
        static readonly int FadeId = Shader.PropertyToID("_Fade");
        static readonly int FlashId = Shader.PropertyToID("_FlashColor");

        WorldNet.PotFx _fx;
        Transform _pot, _plant;
        Renderer[] _renderers;
        readonly List<Renderer> _glow = new();
        readonly List<(Transform t, Quaternion rest, float sign)> _jaws = new();
        MaterialPropertyBlock _mpb;
        Transform _beam, _beamCore, _beamInk;
        float _bite, _walk;
        Vector3 _plantAt, _tilt;

        public static PotView Create(WorldNet.PotFx fx)
        {
            var go = new GameObject("Pot") { layer = Layers.Fx };
            var v = go.AddComponent<PotView>();
            v._fx = fx;
            v._pot = PotModel.SpawnPot().transform;
            v._pot.SetParent(go.transform, false);
            v._plant = PotModel.SpawnPlant(fx.kind).transform;
            v._plant.SetParent(go.transform, false);
            v._plantAt = PotModel.PlantOffset(fx.kind);
            v._plant.localPosition = v._plantAt;
            v._renderers = go.GetComponentsInChildren<Renderer>(true);
            foreach (Transform part in v._plant)
            {
                if (fx.kind == SpellPlans.PotSunflower && PotModel.Glows(part.GetComponent<Renderer>())) v._glow.Add(part.GetComponent<Renderer>());
                if (fx.kind == SpellPlans.PotFlyTrap && PotModel.IsJaw(part.name)) v._jaws.Add((part, part.localRotation, v._jaws.Count == 0 ? -1f : 1f));
            }
            if (fx.kind == SpellPlans.PotSunflower)
            {
                v._beamInk = JudgmentView.Tube(go.transform, "BeamOutline", Glow.Ink, true);
                v._beam = JudgmentView.Tube(go.transform, "Beam", SpellPlans.FlowerBeam, false);
                v._beamCore = JudgmentView.Tube(go.transform, "BeamCore", SpellPlans.FlowerCore, false);
                v._beam.localScale = v._beamCore.localScale = v._beamInk.localScale = Vector3.zero;
            }
            v._mpb = new MaterialPropertyBlock();
            v.LateUpdate();
            return v;
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            float t = (float)(now - _fx.start);
            Vector3 pos = WorldNet.PotPosition(_fx, now);
            Quaternion rot = WorldNet.PotRotation(_fx, now);
            var holder = WorldNet.FindCombatant(_fx.caster);
            // (held: it's in the hands, wherever the animation has them: see SpellPlans.PotPoint)
            transform.SetPositionAndRotation(pos, rot);
            float size = _fx.phase == WorldNet.PotHeld && holder != null ? SpellPlans.BodyGrow(holder) * SpellPlans.SmallScale(holder) : 1f;
            transform.localScale = Vector3.one * size;

            // phasing in, and out at the end
            float fade = Mathf.Clamp01(t / SpellPlans.PotAppear);
            if (_fx.ended >= 0)
            {
                fade = 1f - Mathf.Clamp01((float)(now - _fx.ended) / SpellPlans.PotVanish);
                if (fade <= 0f) { Destroy(gameObject); return; }
            }
            // the plant grows up out of the pot
            float g = Mathf.Clamp01((t - SpellPlans.PotAppear * 0.5f) / SpellPlans.PotGrow);
            g = 1f - (1f - g) * (1f - g) * (1f - g);
            _plant.localScale = Vector3.one * Mathf.Max(0.001f, g * (1f + 0.12f * Mathf.Sin(g * Mathf.PI)));
            // the stem sways gently (the cactus barely)
            float sway = _fx.kind == SpellPlans.PotCactus ? 1.2f : 4.5f;
            _plant.localRotation = Quaternion.Euler(Mathf.Sin(t * 2.3f) * sway, Mathf.Sin(t * 1.1f) * sway * 0.6f, Mathf.Sin(t * 1.7f + 1f) * sway * 0.8f);

            // the Sunflower: brighter and brighter, then the beam
            float glow = 0f;
            if (_fx.kind == SpellPlans.PotSunflower)
            {
                glow = Mathf.Clamp01((t - SpellPlans.PotGrow) / SpellPlans.FlowerGlow);
                bool beam = _fx.ended < 0 && _fx.phase == WorldNet.PotHeld && t >= SpellPlans.FlowerLaserStart && t < SpellPlans.FlowerLaserEnd && holder != null;
                if (beam) Beam(holder, now, t);
                else HideBeam();
            }
            // the Fly Trap: jaws shut on whoever's inside, else gaping (a little snap now and then)
            if (_jaws.Count > 0)
            {
                bool full = Eating(now);
                float want = full ? 1f : 0.12f * Mathf.Max(0f, Mathf.Sin(t * 5f)) ;
                _bite = Mathf.MoveTowards(_bite, want, Time.deltaTime / (full ? 0.06f : 0.2f));
                foreach (var (jaw, rest, sign) in _jaws) jaw.localRotation = Quaternion.Euler(sign * 22f * _bite, 0f, 0f) * rest;
            }

            foreach (var r in _renderers)
            {
                if (r == null || r.transform.IsChildOf(transform) == false || _beam != null && (r.transform == _beam || r.transform == _beamCore || r.transform == _beamInk)) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetFloat(FadeId, fade);
                Color f = SpellPlans.FlowerBeam;
                _mpb.SetColor(FlashId, new Color(f.r, f.g, f.b, _glow.Contains(r) ? glow * glow * 0.85f : 0f));
                r.SetPropertyBlock(_mpb);
            }
        }

        /// <summary>Someone is inside this fly trap right now.</summary>
        bool Eating(double now)
        {
            foreach (var c in Combatant.Registry)
                if (c != null && c.IsSpawned && c.Status.Value.EatenBy == _fx.id && c.Status.Value.Eaten(now)) return true;
            return false;
        }

        void Beam(Combatant holder, double now, float t)
        {
            Vector3 from = WorldNet.PotHead(_fx, now);
            Vector3 dir = SpellPlans.FlowerAim(holder, from);
            float reach = SpellPlans.FlowerReach(from, dir);
            // it bursts out, then thins away at the end
            float r = SpellPlans.FlowerBeamRadius * Mathf.Clamp01((t - SpellPlans.FlowerLaserStart) / 0.08f) * Mathf.Clamp01((SpellPlans.FlowerLaserEnd - t) / 0.15f);
            r *= 1f + 0.06f * Mathf.Sin(Time.time * 50f);
            var rot = Quaternion.FromToRotation(Vector3.up, dir);
            foreach (var (tube, k) in new[] { (_beamInk, 1.28f), (_beam, 1f), (_beamCore, 0.45f) })
            {
                tube.SetPositionAndRotation(from, rot);
                Vector3 s = new(r * k, reach, r * k);
                // (parented under the pot: undo its scale)
                tube.localScale = new Vector3(s.x / transform.lossyScale.x, s.y / transform.lossyScale.y, s.z / transform.lossyScale.z);
            }
            if (Random.value < Time.deltaTime * 14f) CameraRig.ShakeFrom(from, 0.05f, 0.06f);
        }

        void HideBeam()
        {
            if (_beam == null) return;
            _beam.localScale = _beamCore.localScale = _beamInk.localScale = Vector3.zero;
        }
    }
}
