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

        /// <summary>Any set of boxes (The Hermit's house, The Tower): white if it can be cast, red if not.</summary>
        public void ShowBoxes(IReadOnlyList<(Vector3 c, Quaternion r, Vector3 s)> boxes, bool ok)
        {
            Begin();
            foreach (var b in boxes) AddBox(b.c, b.r, b.s, ok ? Good : Bad);
            End();
        }

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

        /// <summary>Arcane Judgment, Judgement: the circle it will draw (outer and inner ring), on the ground.</summary>
        public void ShowJudgment(Vector3 center, float radius)
        {
            Begin();
            var blue = new Color(0.55f, 0.75f, 1f, 1f);
            AddRing(center + Vector3.up * 0.06f, radius, blue);
            AddRing(center + Vector3.up * 0.06f, radius * 0.66f, new Color(blue.r, blue.g, blue.b, 0.6f));
            End();
        }

        /// <summary>Judgement: the circle where it starts, where it ends up, and the lane it slides along (with arrows pointing the way).</summary>
        public void ShowSlidingJudgment(Vector3 center, Vector3 dir, float radius, float slide)
        {
            Begin();
            var red = new Color(1f, 0.55f, 0.38f, 1f);
            var faint = new Color(red.r, red.g, red.b, 0.6f);
            Vector3 up = Vector3.up * 0.06f, end = center + dir * slide, mid = center + dir * (slide * 0.5f);
            Vector3 right = Vector3.Cross(Vector3.up, dir);
            AddRing(center + up, radius, red);
            AddRing(center + up, radius * 0.66f, faint);
            AddRing(end + up, radius, faint);
            // its edges on the way, from the first circle to the last
            var along = Quaternion.LookRotation(dir);
            for (int s = -1; s <= 1; s += 2) AddBox(mid + up + right * (radius * s), along, new Vector3(0.06f, 0.02f, slide), red, 0.5f);
            AddBox(mid + up, along, new Vector3(radius * 2f, 0.01f, slide), red, 0.05f);
            // arrows down the middle
            for (int k = 0; k < 2; k++)
            {
                Vector3 tip = center + dir * (slide * (0.62f + 0.38f * k)) + up;
                for (int s = -1; s <= 1; s += 2)
                    AddBox(tip - dir * 0.28f + right * (0.28f * s), along * Quaternion.Euler(0f, -45f * s, 0f), new Vector3(0.08f, 0.02f, 0.8f), red, 0.6f);
            }
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

        /// <summary>Water of Life: a wire ball where the orb will float.</summary>
        public void ShowOrb(Vector3 center, float radius)
        {
            Begin();
            var cam = Camera.main;
            Vector3 right = cam != null ? cam.transform.right : Vector3.right, up = cam != null ? cam.transform.up : Vector3.up;
            AddCircle(center, right, up, radius, Good);                                     // its outline as you see it
            AddCircle(center, Vector3.right, Vector3.forward, radius, new Color(1f, 1f, 1f, 0.6f)); // the equator
            Vector3 side = Vector3.Cross(Vector3.up, right).normalized;
            AddCircle(center, Vector3.up, side, radius, new Color(1f, 1f, 1f, 0.35f));      // a meridian, for roundness
            End();
        }

        /// <summary>Tsunami: the wave where it rises, and the strip of floor it will run along.</summary>
        /// <summary>Ice Spike: a flat strip on the ground under each spike, following the ground (nothing: nowhere to raise them).</summary>
        public void ShowIceLine(List<SpellPlans.IceSlot> slots, Vector3 dir)
        {
            Begin();
            dir.y = 0f;
            dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.forward;
            var rot = Quaternion.LookRotation(dir);
            float width = 2f * (SpellPlans.IceJitter + SpellPlans.IceMinRadius);
            foreach (var s in slots)
                AddBox(s.ground + dir * (SpellPlans.IceLean * 0.5f) + Vector3.up * 0.05f, rot,
                    new Vector3(width, 0.02f, SpellPlans.IceSpacing * 0.8f), Good, 0.12f);
            End();
        }

        /// <summary>Undying Grasp: a flat strip on the ground by each hand, as wide as they reach, following the ground.</summary>
        public void ShowGraspLine(List<SpellPlans.GraspSlot> slots, Vector3 dir)
        {
            Begin();
            dir.y = 0f;
            dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.forward;
            var rot = Quaternion.LookRotation(dir);
            foreach (var s in slots)
                AddBox(s.middle + Vector3.up * 0.05f, rot, new Vector3(SpellPlans.GraspReach * 2f, 0.02f, SpellPlans.GraspSpacing), Good, 0.12f);
            End();
        }

        public void ShowTsunami(Vector3 feet, Vector3 dir)
        {
            Begin();
            dir.y = 0f;
            dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.forward;
            var rot = Quaternion.LookRotation(dir);
            float w = SpellPlans.TsunamiWidth, d = SpellPlans.TsunamiDepth, h = SpellPlans.TsunamiHeight;
            Vector3 wave = feet + dir * (SpellPlans.TsunamiAhead + d * 0.5f) + Vector3.up * (h * 0.5f + 0.02f);
            AddBox(wave, rot, new Vector3(w, h, d), Good, 0.1f);
            float len = SpellPlans.TsunamiTravel;
            Vector3 path = feet + dir * (SpellPlans.TsunamiAhead + d + len * 0.5f) + Vector3.up * 0.05f;
            AddBox(path, rot, new Vector3(w, 0.02f, len), Good, 0.06f);
            End();
        }

        /// <summary>Warp Self: a figure-sized box standing where you'll end up, and a ring at its feet.</summary>
        public void ShowWarp(Vector3 feet, bool ok)
        {
            Begin();
            Color c = ok ? Good : Bad;
            AddRing(feet + Vector3.up * 0.05f, 0.45f, c);
            var cam = Camera.main;
            float yaw = cam != null ? cam.transform.eulerAngles.y : 0f;
            AddBox(feet + Vector3.up * 0.95f, Quaternion.Euler(0f, yaw, 0f), new Vector3(0.7f, 1.9f, 0.7f), c, 0.1f);
            End();
        }

        /// <summary>Warp Space: the portal's frame where it will open, its front (the opening) facing you.</summary>
        public void ShowPortal(Vector3 center, float yaw, float pitch, bool ok)
        {
            Begin();
            Color c = ok ? Good : Bad;
            var rot = SpellPlans.PortalRotation(yaw, pitch);
            AddBox(center - rot * Vector3.forward * (SpellPlans.PortalDepth * 0.5f), rot,
                new Vector3(SpellPlans.PortalWidth, SpellPlans.PortalHeight, SpellPlans.PortalDepth), c, 0.22f);
            End();
        }

        /// <summary>Remote Flame Bomb: the bomb where it will go (red: nowhere there for it).</summary>
        public void ShowBomb(Vector3 center, Quaternion rot, bool ok)
        {
            Begin();
            AddBox(center, rot, new Vector3(SpellPlans.BombSize, SpellPlans.BombThick, SpellPlans.BombSize), ok ? Good : Bad, 0.3f);
            End();
        }

        /// <summary>The Hierophant: the wall's pane where it will stand, as tilted as it will be.</summary>
        public void ShowPane(Vector3 center, Quaternion rot, Vector3 size)
        {
            Begin();
            AddBox(center, rot, size, Good, 0.22f);
            End();
        }

        void AddRing(Vector3 center, float radius, Color c) => AddCircle(center, Vector3.right, Vector3.forward, radius, c);

        /// <summary>A circle (outline only) in the plane of axes <paramref name="a"/> and <paramref name="b"/>.</summary>
        void AddCircle(Vector3 center, Vector3 a, Vector3 b, float radius, Color c)
        {
            if (_used >= _boxes.Count) _boxes.Add(MakeBox());
            var box = _boxes[_used++];
            box.root.SetActive(true);
            box.fill.transform.localScale = Vector3.zero; // outline only
            box.line.startColor = box.line.endColor = new Color(c.r, c.g, c.b, 0.9f * c.a);
            const int n = 48;
            var pts = new Vector3[n + 1];
            for (int i = 0; i <= n; i++)
            {
                float ang = i / (float)n * Mathf.PI * 2f;
                pts[i] = center + (a * Mathf.Cos(ang) + b * Mathf.Sin(ang)) * radius;
            }
            box.line.positionCount = pts.Length;
            box.line.SetPositions(pts);
        }

        static Vector3 Abs(Vector3 v) => new(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        static Mesh MakeCube() => Resources.GetBuiltinResource<Mesh>("Cube.fbx");
    }
}
