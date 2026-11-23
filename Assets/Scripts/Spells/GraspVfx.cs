using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The character model's own arm (shoulder to hand), cut out of the body's skinned mesh: the same triangles, material
    /// and toon outline as a player, on bones of its own. Cut finer lengthways, so the hand (one solid piece on the model) can be bent
    /// round a leg. One per side, built from the first body there is.
    /// </summary>
    public static class GraspArmModel
    {
        public const int Upper = 0, Lower = 1, Hand = 2, Finger1 = 3, Finger2 = 4, BoneCount = 5;
        /// <summary>Where along the hand it bends.</summary>
        public const float Bend1 = 0.33f, Bend2 = 0.66f;
        /// <summary>The player model's own scale (what its bones measure in metres is for this).</summary>
        const float ModelScale = 0.5f;
        const int Cuts = 4; // each edge of each triangle is cut in this many

        public class Arm
        {
            public Mesh mesh;
            public Material[] materials;
            public float upper, lower, hand; // metres
            public float Length => upper + lower + hand;
        }

        static readonly Arm[] Built = new Arm[2];
        static Material _ink;

        /// <summary>The arm (side 0 right, 1 left), or null if there's no body to take it from.</summary>
        public static Arm Get(int side)
        {
            if (Built[side] != null && Built[side].mesh != null) return Built[side];
            foreach (var c in Combatant.Registry)
            {
                if (c == null || c.model == null) continue;
                foreach (var smr in c.model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var arm = Build(c, smr, side == 0 ? "Arm R" : "Arm L");
                    if (arm != null) return Built[side] = arm;
                }
            }
            return null;
        }

        static Arm Build(Combatant c, SkinnedMeshRenderer smr, string name)
        {
            var src = smr.sharedMesh;
            if (src == null || !src.isReadable) return null;
            int root = -1, b1 = -1, b2 = -1, b3 = -1;
            var bones = smr.bones;
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null) continue;
                if (bones[i].name == name) root = i;
                else if (bones[i].name == name + ".001") b1 = i;
                else if (bones[i].name == name + ".002") b2 = i;
                else if (bones[i].name == name + ".003") b3 = i;
            }
            var binds = src.bindposes;
            if (b1 < 0 || b2 < 0 || b3 < 0 || binds.Length <= Mathf.Max(b1, Mathf.Max(b2, b3))) return null;
            Vector3 Joint(int b) => binds[b].inverse.GetColumn(3);
            Vector3 j1 = Joint(b1), j2 = Joint(b2), j3 = Joint(b3);
            Vector3 d1 = (j2 - j1).normalized, d2 = (j3 - j2).normalized;
            float modelUpper = (j2 - j1).magnitude;
            float bodyScale = Mathf.Max(0.01f, c.model.lossyScale.y);
            // mesh units to metres, at a body's usual size
            float f = Vector3.Distance(bones[b1].position, bones[b2].position) * (ModelScale / bodyScale) / Mathf.Max(1e-6f, modelUpper);
            if (f <= 0f || float.IsNaN(f)) return null;

            var verts = src.vertices;
            var normals = src.normals;
            var uvs = src.uv;
            var weights = src.boneWeights;
            if (weights.Length != verts.Length) return null;

            // per vertex: how much of it is upper arm, forearm, hand (the shoulder counts as upper arm)
            var part = new Vector3[verts.Length];
            var keep = new bool[verts.Length];
            float handLen = 0f;
            for (int i = 0; i < verts.Length; i++)
            {
                var w = weights[i];
                Vector3 p = Vector3.zero;
                void Add(int bone, float k)
                {
                    if (k <= 0f) return;
                    if (bone == b1 || bone == root) p.x += k;
                    else if (bone == b2) p.y += k;
                    else if (bone == b3) p.z += k;
                }
                Add(w.boneIndex0, w.weight0); Add(w.boneIndex1, w.weight1); Add(w.boneIndex2, w.weight2); Add(w.boneIndex3, w.weight3);
                float sum = p.x + p.y + p.z;
                // all arm, and not up past the shoulder into the chest
                keep[i] = sum >= 0.5f && Vector3.Dot(verts[i] - j1, d1) > -0.26f * modelUpper;
                if (!keep[i]) continue;
                part[i] = p / sum;
                if (part[i].z > 0.5f) handLen = Mathf.Max(handLen, Vector3.Dot(verts[i] - j3, d2));
            }
            if (handLen <= 0f) return null;

            var pos = new List<Vector3>();
            var nrm = new List<Vector3>();
            var uv = new List<Vector2>();
            var bw = new List<BoneWeight>();
            var tri = new List<int>();
            var row = new int[Cuts + 1, Cuts + 1];
            for (int s = 0; s < src.subMeshCount; s++)
            {
                var t = src.GetTriangles(s);
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    int a = t[i], b = t[i + 1], e = t[i + 2];
                    if (!keep[a] || !keep[b] || !keep[e]) continue;
                    for (int x = 0; x <= Cuts; x++)
                    for (int y = 0; y <= Cuts - x; y++)
                    {
                        float kb = x / (float)Cuts, ke = y / (float)Cuts, ka = 1f - kb - ke;
                        Vector3 p = verts[a] * ka + verts[b] * kb + verts[e] * ke;
                        row[x, y] = pos.Count;
                        pos.Add((p - j1) * f);
                        nrm.Add(normals.Length == verts.Length ? (normals[a] * ka + normals[b] * kb + normals[e] * ke).normalized : Vector3.up);
                        uv.Add(uvs.Length == verts.Length ? uvs[a] * ka + uvs[b] * kb + uvs[e] * ke : Vector2.zero);
                        bw.Add(Weigh(part[a] * ka + part[b] * kb + part[e] * ke, Vector3.Dot(p - j3, d2) / handLen));
                    }
                    for (int x = 0; x < Cuts; x++)
                    for (int y = 0; y < Cuts - x; y++)
                    {
                        tri.Add(row[x, y]); tri.Add(row[x + 1, y]); tri.Add(row[x, y + 1]);
                        if (x + y >= Cuts - 1) continue;
                        tri.Add(row[x + 1, y]); tri.Add(row[x + 1, y + 1]); tri.Add(row[x, y + 1]);
                    }
                }
            }
            if (tri.Count == 0) return null;

            // the bones as they lie in the mesh: Y along the arm, Z the way it doesn't bend
            Vector3 hint = Mathf.Abs(d2.y) < Mathf.Abs(d2.z) ? Vector3.up : Vector3.forward;
            if (Vector3.Cross(hint, d2).sqrMagnitude < 0.1f) hint = Vector3.right;
            Matrix4x4 Bind(Vector3 at, Vector3 dir) => Matrix4x4.TRS((at - j1) * f, Frame(dir, hint), Vector3.one).inverse;
            var mesh = new Mesh { name = "Grasp " + name };
            mesh.SetVertices(pos);
            mesh.SetNormals(nrm);
            mesh.SetUVs(0, uv);
            mesh.boneWeights = bw.ToArray();
            mesh.SetTriangles(tri, 0);
            mesh.bindposes = new[]
            {
                Bind(j1, d1), Bind(j2, d2), Bind(j3, d2), Bind(j3 + d2 * (handLen * Bend1), d2), Bind(j3 + d2 * (handLen * Bend2), d2),
            };
            mesh.RecalculateBounds();

            if (_ink == null)
            {
                var shader = Shader.Find("SME/HullOutline");
                if (shader != null)
                {
                    _ink = new Material(shader) { name = "GraspInk" };
                    _ink.SetColor("_OutlineColor", Glow.Ink);
                    _ink.SetFloat("_Width", 2.5f);
                }
            }
            var skin = smr.sharedMaterials.Length > 0 ? smr.sharedMaterials[0] : null;
            return new Arm
            {
                mesh = mesh, materials = _ink != null ? new[] { skin, _ink } : new[] { skin },
                upper = modelUpper * f, lower = (j3 - j2).magnitude * f, hand = handLen * f,
            };
        }

        /// <summary>Bone weights; the hand's share goes to whichever stretch of it this is (<paramref name="along"/>: 0 the wrist, 1 the tips).</summary>
        static BoneWeight Weigh(Vector3 part, float along)
        {
            float f1 = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(Bend1 - 0.12f, Bend1 + 0.12f, along));
            float f2 = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(Bend2 - 0.12f, Bend2 + 0.12f, along));
            var w = new[] { part.x, part.y, part.z * (1f - f1), part.z * (f1 - f2), part.z * f2 };
            // the four heaviest (the upper arm never meets the fingers, so nothing is lost)
            var order = new[] { 0, 1, 2, 3, 4 };
            System.Array.Sort(order, (a, b) => w[b].CompareTo(w[a]));
            float sum = Mathf.Max(1e-6f, w[order[0]] + w[order[1]] + w[order[2]] + w[order[3]]);
            return new BoneWeight
            {
                boneIndex0 = order[0], weight0 = w[order[0]] / sum, boneIndex1 = order[1], weight1 = w[order[1]] / sum,
                boneIndex2 = order[2], weight2 = w[order[2]] / sum, boneIndex3 = order[3], weight3 = w[order[3]] / sum,
            };
        }

        /// <summary>Y along <paramref name="dir"/>, Z as near <paramref name="hint"/> as it can be.</summary>
        public static Quaternion Frame(Vector3 dir, Vector3 hint)
        {
            Vector3 z = Vector3.ProjectOnPlane(hint, dir);
            if (z.sqrMagnitude < 1e-6f) z = Vector3.ProjectOnPlane(Mathf.Abs(dir.y) < 0.9f ? Vector3.up : Vector3.right, dir);
            return Quaternion.LookRotation(z.normalized, dir);
        }
    }

    /// <summary>One of the character's arms coming up out of the ground, sickly green, posed as it's told.</summary>
    public class GraspArm
    {
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor"), ColorId = Shader.PropertyToID("_Color");
        static MaterialPropertyBlock _tint;

        /// <summary>At the shoulder, in the ground. Its scale makes the whole arm grow up out of there.</summary>
        public readonly Transform root;
        readonly GraspArmModel.Arm _arm;
        readonly Transform[] _bones = new Transform[GraspArmModel.BoneCount];
        readonly float _size;

        GraspArm(GraspArmModel.Arm arm, Transform parent, float size)
        {
            _arm = arm;
            _size = size;
            root = new GameObject("Arm") { layer = Layers.Fx }.transform;
            root.SetParent(parent, false);
            for (int i = 0; i < _bones.Length; i++)
            {
                _bones[i] = new GameObject("Bone") { layer = Layers.Fx }.transform;
                _bones[i].SetParent(root, false);
                _bones[i].localScale = Vector3.one * size;
            }
            var smr = root.gameObject.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = arm.mesh;
            smr.bones = _bones;
            smr.rootBone = _bones[0];
            smr.sharedMaterials = arm.materials;
            smr.updateWhenOffscreen = true;
            _tint ??= new MaterialPropertyBlock();
            _tint.SetColor(BaseColorId, SpellPlans.UndeadSkin);
            _tint.SetColor(ColorId, SpellPlans.UndeadSkin);
            smr.SetPropertyBlock(_tint, 0); // (the skin; the outline keeps its ink)
        }

        /// <summary>An arm (side 0 right, 1 left) <paramref name="size"/> times as big as a player's; null without a model to take it from.</summary>
        public static GraspArm Create(Transform parent, int side, float size)
        {
            var arm = GraspArmModel.Get(side);
            return arm != null ? new GraspArm(arm, parent, size) : null;
        }

        /// <summary>How tall an arm stands held straight up, at a player's size.</summary>
        public static float Reach
        {
            get { var a = GraspArmModel.Get(0); return a != null ? a.Length : 0.78f; }
        }

        public float ArmLength => (_arm.upper + _arm.lower) * _size;

        /// <summary>
        /// From the shoulder, the elbow out towards <paramref name="pole"/>, to the wrist; the hand along
        /// <paramref name="handDir"/>, each of its two bends turning it <paramref name="bend"/> degrees towards <paramref name="inward"/>.
        /// </summary>
        public void Pose(Vector3 shoulder, Vector3 wrist, Vector3 pole, Vector3 handDir, Vector3 inward, float bend, float grown)
        {
            root.localScale = Vector3.one;
            root.SetPositionAndRotation(shoulder, Quaternion.identity);
            float l1 = _arm.upper * _size, l2 = _arm.lower * _size;
            Vector3 to = wrist - shoulder;
            float d = Mathf.Clamp(to.magnitude, Mathf.Abs(l1 - l2) + 0.01f, l1 + l2 - 0.002f);
            Vector3 dir = to.sqrMagnitude > 1e-8f ? to.normalized : Vector3.up;
            wrist = shoulder + dir * d;
            float cos = Mathf.Clamp((l1 * l1 + d * d - l2 * l2) / (2f * l1 * d), -1f, 1f);
            Vector3 side = Vector3.ProjectOnPlane(pole, dir);
            side = side.sqrMagnitude > 1e-6f ? side.normalized : Vector3.ProjectOnPlane(Mathf.Abs(dir.x) < 0.9f ? Vector3.right : Vector3.forward, dir).normalized;
            Vector3 elbow = shoulder + dir * (l1 * cos) + side * (l1 * Mathf.Sqrt(1f - cos * cos));
            Vector3 z = Vector3.Cross(dir, side);

            handDir.Normalize();
            Vector3 axis = Vector3.Cross(handDir, inward);
            axis = axis.sqrMagnitude > 1e-6f ? axis.normalized : z;
            Vector3 hz = Vector3.Dot(axis, z) < 0f ? -axis : axis; // (the wrist twists no more than it has to)
            Vector3 f1 = Quaternion.AngleAxis(bend, axis) * handDir, f2 = Quaternion.AngleAxis(bend * 2f, axis) * handDir;
            float h = _arm.hand * _size;
            Vector3 p1 = wrist + handDir * (h * GraspArmModel.Bend1), p2 = p1 + f1 * (h * (GraspArmModel.Bend2 - GraspArmModel.Bend1));
            Set(GraspArmModel.Upper, shoulder, (elbow - shoulder).normalized, z);
            Set(GraspArmModel.Lower, elbow, (wrist - elbow).normalized, z);
            Set(GraspArmModel.Hand, wrist, handDir, hz);
            Set(GraspArmModel.Finger1, p1, f1, hz);
            Set(GraspArmModel.Finger2, p2, f2, hz);
            root.localScale = Vector3.one * Mathf.Max(0.001f, grown);
        }

        void Set(int bone, Vector3 at, Vector3 dir, Vector3 hint) => _bones[bone].SetPositionAndRotation(at, GraspArmModel.Frame(dir, hint));
    }

    /// <summary>The line of arms: all shooting up out of the ground at once, snatching shut, and going back.</summary>
    public class GraspView : MonoBehaviour
    {
        class One { public GraspArm arm; public Vector3 ground, inward; public float delay, sway; }
        readonly List<One> _arms = new();
        double _start;
        const float Shut = 0.28f, Linger = 0.35f, Sink = 0.3f;

        public static void Create(List<Vector3> spots, Vector3 dir, double start)
        {
            var v = new GameObject("UndyingGrasp") { layer = Layers.Fx }.AddComponent<GraspView>();
            v._start = start;
            Vector3 right = new(dir.z, 0f, -dir.x);
            float size = SpellPlans.GraspHeight / Mathf.Max(0.1f, GraspArm.Reach); // as tall as it reaches
            for (int i = 0; i < spots.Count; i++)
            {
                var arm = GraspArm.Create(v.transform, i % 2, size);
                if (arm != null)
                    v._arms.Add(new One
                    {
                        arm = arm, ground = spots[i],
                        // each snatches in across the line, all at slightly different angles
                        inward = Quaternion.Euler(0f, Random.Range(-35f, 35f), 0f) * (i % 2 == 0 ? -right : right),
                        delay = Random.Range(0f, 0.06f), sway = Random.Range(0f, 6f),
                    });
                EnergyBurst.Create(spots[i] + Vector3.up * 0.1f, new EnergyBurst.Settings
                {
                    flashSize = 0.4f, flashTime = 0.12f, sparkCount = 4, sparkSize = 0.18f, sparkSpeed = new Vector2(2f, 5f), sparkLife = 0.3f, colour = SpellPlans.NecroGlow,
                });
                if (i % 4 == 0) CameraRig.ShakeFrom(spots[i], 0.2f, SpellPlans.GraspRise);
            }
            v.LateUpdate();
        }

        void LateUpdate()
        {
            float t = (float)(WorldNet.Now - _start);
            float over = SpellPlans.GraspRise + Linger;
            if (t > over + Sink + 0.1f || WorldNet.Instance == null) { Destroy(gameObject); return; }
            foreach (var a in _arms)
            {
                // up with a little overshoot, snatching shut, then back down into the ground
                float u = Mathf.Clamp01((t - a.delay) / (SpellPlans.GraspRise - 0.06f)), k = 1f - u;
                float up = Mathf.Clamp(1f - k * k * k * Mathf.Cos(u * 4f), 0f, 1.08f);
                float down = 1f - Mathf.Clamp01((t - over - a.delay) / Sink);
                float shut = Mathf.SmoothStep(0f, 1f, (t - Shut) / 0.14f);
                Vector3 shoulder = a.ground + Vector3.down * 0.04f;
                Vector3 lean = new Vector3(Mathf.Sin(t * 9f + a.sway), 0f, Mathf.Cos(t * 8f + a.sway)) * 0.04f;
                // reaching straight up; then the elbow folds and the hand snatches over and in
                Vector3 wrist = shoulder + (Vector3.up * Mathf.Lerp(0.985f, 0.78f, shut) + a.inward * (0.12f * shut)) * a.arm.ArmLength + lean;
                Vector3 hand = Vector3.up + a.inward * Mathf.Lerp(-0.25f, 0.9f, shut);
                a.arm.Pose(shoulder, wrist, -a.inward, hand, a.inward + Vector3.down * (0.5f * shut), Mathf.Lerp(6f, 48f, shut),
                    up * down * down * (3f - 2f * down));
            }
        }
    }

    /// <summary>
    /// Held by Undying Grasp: two arms out of the ground at each leg, their hands wrapped round the shin, one by way of the
    /// front and one behind. They come up as it takes hold and shrink back the moment it lets go.
    /// </summary>
    [DefaultExecutionOrder(160)] // after the legs are posed
    public class GraspBodyFx : MonoBehaviour
    {
        class Hold { public GraspArm arm; public string knee, ankle; public float side; public bool front; }

        Combatant _c;
        Transform _root;
        readonly List<Hold> _holds = new();
        float _size;

        void Awake() => _c = GetComponent<Combatant>();

        void LateUpdate()
        {
            if (_c == null || !_c.IsSpawned) { Clear(); return; }
            bool on = _c.Grasped && _c.IsAlive && _c.Visible && !_c.HasLiveRagdoll;
            _size = Mathf.MoveTowards(_size, on ? 1f : 0f, Time.deltaTime / (on ? 0.14f : 0.22f));
            if (_size <= 0f) { Clear(); return; }
            if (_root == null) Build();
            float s = _size * _size * (3f - 2f * _size);
            foreach (var h in _holds)
            {
                if (on) Wrap(h, s);
                else h.arm.root.localScale = Vector3.one * Mathf.Max(0.001f, s); // let go: back into the ground where it was
            }
        }

        void Build()
        {
            _root = new GameObject("Grasp") { layer = Layers.Fx }.transform;
            float grow = Grow;
            for (int i = 0; i < 4; i++)
            {
                bool right = i % 2 == 1, front = i < 2;
                var arm = GraspArm.Create(_root, right == front ? 0 : 1, grow);
                if (arm == null) continue;
                string leg = right ? "Leg R" : "Leg L";
                _holds.Add(new Hold { arm = arm, knee = leg + ".002", ankle = leg + ".003", side = right ? 1f : -1f, front = front });
            }
            if (!Debris.Suppressed)
                EnergyBurst.Create(_c.FeetPosition + Vector3.up * 0.25f, new EnergyBurst.Settings
                {
                    flashSize = 0.6f, flashTime = 0.14f, sparkCount = 10, sparkSize = 0.2f, sparkSpeed = new Vector2(2f, 6f), sparkLife = 0.3f, colour = SpellPlans.NecroGlow,
                });
        }

        float Grow => SpellPlans.BodyGrow(_c) * _c.transform.localScale.y;

        void Wrap(Hold h, float s)
        {
            if (_c.Bones == null || !_c.Bones.TryGetValue(h.knee, out var kneeT) || !_c.Bones.TryGetValue(h.ankle, out var ankleT)) return;
            float grow = Grow;
            Quaternion yaw = Quaternion.Euler(0f, _c.ViewYaw, 0f);
            Vector3 knee = kneeT.position, ankle = ankleT.position;
            Vector3 axis = knee - ankle;
            axis = axis.sqrMagnitude > 1e-5f ? axis.normalized : Vector3.up;
            // where the hand takes hold: the outside of the shin, one hand a little behind it going round the front, the
            // other a little in front, going round the back, higher up
            Vector3 forward = Vector3.ProjectOnPlane(yaw * Vector3.forward, axis).normalized;
            Vector3 o = Vector3.ProjectOnPlane(yaw * new Vector3(h.side, 0f, h.front ? -0.35f : 0.9f), axis).normalized;
            Vector3 round = Vector3.Cross(axis, o);
            if (Vector3.Dot(round, forward) < 0f == h.front) round = -round;
            Vector3 at = Vector3.Lerp(ankle, knee, h.front ? 0.45f : 0.68f);
            float ring = 0.13f * grow; // (the leg, and half the hand's own thickness)
            Vector3 wrist = at + o * ring - round * (0.06f * grow);

            Vector3 flat = Vector3.ProjectOnPlane(o, Vector3.up).normalized;
            Vector3 ground = new Vector3(at.x, _c.FeetPosition.y - 0.04f, at.z) + flat * (0.32f * grow) - Vector3.ProjectOnPlane(round, Vector3.up) * (0.12f * grow);
            // the elbow stands out, away from the leg; the hand lies along the leg's side and bends round it
            h.arm.Pose(ground, wrist, flat + Vector3.up * 0.4f, round + o * 0.2f, -o, 46f, s);
        }

        void Clear()
        {
            _size = 0f;
            if (_root == null) return;
            Destroy(_root.gameObject);
            _root = null;
            _holds.Clear();
        }

        void OnDestroy() => Clear();
    }
}
