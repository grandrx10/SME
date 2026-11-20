using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Describes the "fellas" rig: body segments used for hitboxes and the ragdoll.
    /// Segment index is sent over the network, so keep the order stable.
    /// </summary>
    public enum HitRegion : byte { Body, Head, Limb }

    public static class Rig
    {
        public struct Segment
        {
            public string name, start, end;
            public float radius;      // world-space metres
            public HitRegion region;
            public int parent;        // ragdoll joint parent (-1 = root)
            public float mass;
            /// <summary>Hitbox end caps stay inside the bone instead of sticking out past each end.</summary>
            public bool capsInside;
            public Segment(string name, string start, string end, float radius, HitRegion region, int parent, float mass, bool capsInside = false)
            { this.name = name; this.start = start; this.end = end; this.radius = radius; this.region = region; this.parent = parent; this.mass = mass; this.capsInside = capsInside; }
        }

        public static readonly Segment[] Segments =
        {
            new("Abdomen",   "Bone",      "Bone.003",      0.17f, HitRegion.Body, -1, 10f, capsInside: true),
            new("Chest",     "Bone.003",  "Bone.001",      0.19f, HitRegion.Body,  0, 10f, capsInside: true),
            new("Head",      "Bone.001",  "Bone.001_end",  0.17f, HitRegion.Head,  1, 4f,  capsInside: true),
            new("UpperArmL", "Arm L.001", "Arm L.002",     0.07f, HitRegion.Limb,  1, 2f),
            new("ForearmL",  "Arm L.002", "Arm L.003_end", 0.06f, HitRegion.Limb,  3, 1.5f),
            new("UpperArmR", "Arm R.001", "Arm R.002",     0.07f, HitRegion.Limb,  1, 2f),
            new("ForearmR",  "Arm R.002", "Arm R.003_end", 0.06f, HitRegion.Limb,  5, 1.5f),
            new("ThighL",    "Leg L.001", "Leg L.002",     0.10f, HitRegion.Limb,  0, 5f),
            new("ShinL",     "Leg L.002", "Leg L.003_end", 0.08f, HitRegion.Limb,  7, 3f),
            new("ThighR",    "Leg R.001", "Leg R.002",     0.10f, HitRegion.Limb,  0, 5f),
            new("ShinR",     "Leg R.002", "Leg R.003_end", 0.08f, HitRegion.Limb,  9, 3f),
        };

        public static Dictionary<string, Transform> MapBones(Transform root)
        {
            var map = new Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (!map.ContainsKey(t.name)) map[t.name] = t;
            return map;
        }
    }
}
