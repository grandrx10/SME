using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Local aiming preview while an ability key is held: see-through boxes with outlines showing where the
    /// ground will move. White = good to cast, red = there's no ground to use there.
    /// </summary>
    public class SpellIndicator
    {
        static readonly Color Good = new(1f, 1f, 1f, 1f);
        static readonly Color Bad = new(1f, 0.18f, 0.12f, 1f);
        const float FillAlpha = 0.16f;

        class Box
        {
            public GameObject root;
            public MeshRenderer fill;
            public LineRenderer line;
        }

        readonly List<Box> _boxes = new();
        static Mesh _cube;
        static Material _fillMat, _lineMat;
        GameObject _root;
        int _used;

        void Begin()
        {
            if (_root == null)
            {
                _root = new GameObject("SpellIndicator");
                Object.DontDestroyOnLoad(_root);
            }
            _root.SetActive(true);
            _used = 0;
        }

        void End()
        {
            for (int i = _used; i < _boxes.Count; i++) _boxes[i].root.SetActive(false);
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        public void Destroy()
        {
            if (_root != null) Object.Destroy(_root);
        }

        void AddBox(Vector3 center, Quaternion rot, Vector3 size, Color c, float fillAlpha = FillAlpha)
        {
            if (_used >= _boxes.Count) _boxes.Add(MakeBox());
            var b = _boxes[_used++];
            b.root.SetActive(true);
            b.root.transform.SetPositionAndRotation(center, rot);
            b.fill.transform.localScale = size;
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor("_Color", new Color(c.r, c.g, c.b, fillAlpha));
            b.fill.SetPropertyBlock(mpb);
            b.line.startColor = b.line.endColor = new Color(c.r, c.g, c.b, 0.9f);
            Vector3 h = size * 0.5f;
            Vector3 P(float x, float y, float z) => center + rot * new Vector3(x * h.x, y * h.y, z * h.z);
            Vector3 b0 = P(-1, -1, -1), b1 = P(1, -1, -1), b2 = P(1, -1, 1), b3 = P(-1, -1, 1);
            Vector3 t0 = P(-1, 1, -1), t1 = P(1, 1, -1), t2 = P(1, 1, 1), t3 = P(-1, 1, 1);
            var pts = new[] { b0, b1, b2, b3, b0, t0, t1, b1, t1, t2, b2, t2, t3, b3, t3, t0 };
            b.line.positionCount = pts.Length;
            b.line.SetPositions(pts);
        }

        Box MakeBox()
        {
            _cube ??= MakeCube();
            _fillMat ??= new Material(Shader.Find("Sprites/Default")) { name = "IndicatorFill", renderQueue = 3100 };
            _lineMat ??= new Material(Shader.Find("Sprites/Default")) { name = "IndicatorLine", renderQueue = 3101 };
            var go = new GameObject("Box") { layer = 2 };
            go.transform.SetParent(_root.transform, false);
            var fillGo = new GameObject("Fill") { layer = 2 };
            fillGo.transform.SetParent(go.transform, false);
            fillGo.AddComponent<MeshFilter>().sharedMesh = _cube;
            var mr = fillGo.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _fillMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = _lineMat;
            lr.useWorldSpace = true;
            lr.widthMultiplier = 0.035f;
            lr.numCornerVertices = 2;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return new Box { root = go, fill = mr, line = lr };
        }

        // ------------------------------------------------------------------ spells

        public void ShowWall(SpellPlans.WallPlan plan)
        {
            Begin();
            float S = VoxelWorld.Size;
            var size = new Vector3(SpellPlans.WallLength * S, SpellPlans.WallHeight * S, SpellPlans.WallThick * S);
            var rot = Quaternion.Euler(0, plan.yaw, 0);
            Vector3 c = plan.center + Vector3.up * (size.y * 0.5f + 0.02f);
            AddBox(c, rot, size, plan.valid ? Good : Bad);
            End();
        }

        public void ShowFold(SpellPlans.FoldPlan plan, Vector3 target, float yaw)
        {
            Begin();
            float S = VoxelWorld.Size;
            Color col = plan.valid ? Good : Bad;
            if (plan.valid)
            {
                for (int s = 0; s < 2; s++)
                {
                    var b = plan.slabBounds[s];
                    AddBox(b.center + Vector3.up * 0.03f, Quaternion.identity, b.size + new Vector3(0, 0.06f, 0), col);
                }
                // the strip they slam together on
                var crush = plan.crush;
                Vector3 floor = new(crush.center.x, crush.min.y + 0.05f, crush.center.z);
                AddBox(floor, Quaternion.identity, new Vector3(crush.size.x, 0.08f, crush.size.z), col, 0.32f);
            }
            else
            {
                // no ground: show roughly where it would have gone
                Vector3 f = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
                bool alongX = Mathf.Abs(f.x) >= Mathf.Abs(f.z);
                Vector3 a = alongX ? Vector3.right : Vector3.forward, b = alongX ? Vector3.forward : Vector3.right;
                float len = SpellPlans.FoldLength * S, w = SpellPlans.FoldWidth * S, off = (SpellPlans.FoldGap * 0.5f + SpellPlans.FoldWidth * 0.5f) * S;
                Vector3 size = Abs(a * len + b * w + Vector3.up * SpellPlans.FoldThick * S);
                AddBox(target - b * off, Quaternion.identity, size, col);
                AddBox(target + b * off, Quaternion.identity, size, col);
            }
            End();
        }

        /// <summary>Shove: the gust's starting box, plus a faint corridor showing how far it goes.</summary>
        public void ShowShove(Vector3 origin, Vector3 dir)
        {
            Begin();
            var rot = Quaternion.LookRotation(dir);
            AddBox(origin, rot, SpellPlans.GustHalfSize * 2f, Good, 0.1f);
            float len = Cards.MaxRange;
            Vector3 corridor = new(SpellPlans.GustHalfSize.x * 2f, 0.02f, len);
            Vector3 floorMid = origin + dir * (len * 0.5f) + rot * Vector3.down * SpellPlans.GustHalfSize.y;
            AddBox(floorMid, rot, corridor, Good, 0.05f);
            End();
        }

        /// <summary>Tornado: a ring where it forms and the path it will barrel along.</summary>
        public void ShowTornado(Vector3 center, Vector3 dir)
        {
            Begin();
            AddRing(center + Vector3.up * 0.05f, SpellPlans.TornadoRadius, Good);
            AddRing(center + Vector3.up * SpellPlans.TornadoHeight, SpellPlans.TornadoRadius * 1.3f, new Color(1f, 1f, 1f, 0.5f));
            var rot = Quaternion.LookRotation(dir);
            float len = SpellPlans.TornadoTravel;
            AddBox(center + dir * (len * 0.5f) + Vector3.up * 0.05f, rot, new Vector3(SpellPlans.TornadoRadius * 2f, 0.02f, len), Good, 0.06f);
            End();
        }

        /// <summary>Arcane Judgment: the circle it will draw (outer and inner ring), on the ground.</summary>
        public void ShowJudgment(Vector3 center, float radius)
        {
            Begin();
            var blue = new Color(0.55f, 0.75f, 1f, 1f);
            AddRing(center + Vector3.up * 0.06f, radius, blue);
            AddRing(center + Vector3.up * 0.06f, radius * 0.66f, new Color(blue.r, blue.g, blue.b, 0.6f));
            End();
        }

        /// <summary>Tug: the gust where it starts, and the corridor it blows back along to you.</summary>
        public void ShowTug(Vector3 from, Vector3 to)
        {
            Begin();
            Vector3 dir = (to - from).normalized;
            var rot = Quaternion.LookRotation(dir);
            AddBox(from, rot, SpellPlans.GustHalfSize * 2f, Good, 0.1f);
            float len = (to - from).magnitude;
            Vector3 floorMid = from + dir * (len * 0.5f) + rot * Vector3.down * SpellPlans.GustHalfSize.y;
            AddBox(floorMid, rot, new Vector3(SpellPlans.GustHalfSize.x * 2f, 0.02f, len), Good, 0.05f);
            End();
        }

        void AddRing(Vector3 center, float radius, Color c)
        {
            if (_used >= _boxes.Count) _boxes.Add(MakeBox());
            var b = _boxes[_used++];
            b.root.SetActive(true);
            b.fill.transform.localScale = Vector3.zero; // outline only
            b.line.startColor = b.line.endColor = new Color(c.r, c.g, c.b, 0.9f * c.a);
            const int n = 48;
            var pts = new Vector3[n + 1];
            for (int i = 0; i <= n; i++)
            {
                float a = i / (float)n * Mathf.PI * 2f;
                pts[i] = center + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius;
            }
            b.line.positionCount = pts.Length;
            b.line.SetPositions(pts);
        }

        static Vector3 Abs(Vector3 v) => new(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        static Mesh MakeCube() => Resources.GetBuiltinResource<Mesh>("Cube.fbx");
    }
}
