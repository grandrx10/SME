using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>A trigger capsule that follows one body segment. Bullets query these.</summary>
    public class Hitbox : MonoBehaviour
    {
        public Combatant Owner;
        public int SegmentIndex;
        public HitRegion Region;
        public Transform Start, End;
        public float Radius;
        /// <summary>The body grown (Strength): the capsules are this much thicker (their length follows the bones anyway).</summary>
        public float RadiusScale = 1f;
        bool _capsInside;
        CapsuleCollider _col;

        public void Init(Combatant owner, int index, Transform start, Transform end)
        {
            Owner = owner;
            SegmentIndex = index;
            var seg = Rig.Segments[index];
            Region = seg.region;
            Start = start;
            End = end;
            Radius = seg.radius;
            _capsInside = seg.capsInside;
            _col = gameObject.AddComponent<CapsuleCollider>();
            _col.isTrigger = true;
            _col.direction = 2; // Z
            _col.radius = Radius;
            gameObject.layer = Layers.Hitbox;
            Sync();
        }

        public void Sync()
        {
            Vector3 a = Start.position, b = End.position;
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 0.001f) { d = Vector3.up; len = 0.001f; }
            transform.SetPositionAndRotation(a, Quaternion.LookRotation(d / len));
            float r = Radius * RadiusScale;
            _col.radius = r;
            _col.center = new Vector3(0, 0, len * 0.5f);
            _col.height = _capsInside ? Mathf.Max(len, r * 2f) : len + r * 2f;
        }
    }

    /// <summary>Builds and updates all hitboxes for one player.</summary>
    public class HitboxSet
    {
        public readonly List<Hitbox> Boxes = new();
        readonly GameObject _root;

        public HitboxSet(Combatant owner, Transform playerRoot, Dictionary<string, Transform> bones)
        {
            _root = new GameObject("Hitboxes");
            _root.transform.SetParent(playerRoot, false);
            for (int i = 0; i < Rig.Segments.Length; i++)
            {
                var s = Rig.Segments[i];
                if (!bones.TryGetValue(s.start, out var a) || !bones.TryGetValue(s.end, out var b))
                {
                    continue;
                }
                var go = new GameObject("HB_" + s.name);
                go.transform.SetParent(_root.transform, false);
                var hb = go.AddComponent<Hitbox>();
                hb.Init(owner, i, a, b);
                Boxes.Add(hb);
            }
        }

        public void Sync()
        {
            for (int i = 0; i < Boxes.Count; i++) Boxes[i].Sync();
        }

        public void SetEnabled(bool on) => _root.SetActive(on);

        /// <summary>Strength: every capsule but the head's this much thicker.</summary>
        public void SetRadiusScale(float s)
        {
            foreach (var b in Boxes) b.RadiusScale = b.Region == HitRegion.Head ? 1f : s;
        }
    }
}
