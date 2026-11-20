using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Builds a physics ragdoll from a snapshot of a player's current pose.
    /// If a custom ragdoll prefab is assigned (customPrefab) it is used instead:
    /// it must contain the same bone names; its pose is copied and rigidbodies are kept.
    /// </summary>
    public static class RagdollFactory
    {
        /// <summary>Optional: assign your own ragdoll prefab here (e.g. from GameBootstrap).</summary>
        public static GameObject CustomPrefab;
        public static float Lifetime = 20f;
        static PhysicsMaterial _slippery;

        /// <summary>Throws a ragdoll: every part gains <paramref name="knock"/> (a velocity, m/s: the same throw a live body gets).</summary>
        public static void Push(GameObject ragdoll, Vector3 knock)
        {
            if (ragdoll == null || knock.sqrMagnitude < 1e-4f) return;
            foreach (var rb in ragdoll.GetComponentsInChildren<Rigidbody>()) rb.linearVelocity += knock;
        }

        /// <param name="lifetime">Seconds before it's removed; 0 = never (a live ragdoll that is removed by its owner).</param>
        public static GameObject Spawn(Combatant player, Vector3 velocity, int segment, Vector3 point, Vector3 impulse, float lifetime)
        {
            var go = Spawn(player, velocity, segment, point, impulse);
            if (lifetime <= 0f)
            {
                var ttl = go.GetComponent<RagdollLifetime>();
                if (ttl != null) Object.Destroy(ttl);
            }
            return go;
        }

        public static GameObject Spawn(Combatant player, Vector3 velocity, int segment, Vector3 point, Vector3 impulse)
        {
            Transform src = player.model;
            GameObject go;
            bool custom = CustomPrefab != null;
            if (custom)
            {
                go = Object.Instantiate(CustomPrefab, src.position, src.rotation);
                go.transform.localScale = src.lossyScale;
                CopyPose(src, go.transform);
            }
            else
            {
                go = Object.Instantiate(src.gameObject, src.position, src.rotation);
                go.transform.localScale = src.lossyScale;
                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) Object.Destroy(mb);
            }
            go.name = player.name + "_Ragdoll";
            Layers.SetLayerRecursive(go, Layers.Ragdoll);
            var srcRenderers = src.GetComponentsInChildren<Renderer>(true);
            var mpb = new MaterialPropertyBlock();
            int ri = 0;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                // keep the player's colour tint
                if (!custom && ri < srcRenderers.Length && srcRenderers[ri].HasPropertyBlock())
                {
                    srcRenderers[ri].GetPropertyBlock(mpb);
                    r.SetPropertyBlock(mpb);
                }
                ri++;
                r.enabled = true;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; // local body was shadows-only
                if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
            }

            var bones = Rig.MapBones(go.transform);
            if (bones.TryGetValue("Bone.001", out var head)) head.localScale = Vector3.one; // local player hides its head

            Rigidbody[] bodies = custom ? BodiesFromCustom(bones) : BuildBodies(bones);

            // carry the body's momentum: every part starts with the player's velocity
            if (_slippery == null)
                _slippery = new PhysicsMaterial("RagdollSlide")
                {
                    dynamicFriction = 0.18f, staticFriction = 0.25f, bounciness = 0.05f,
                    frictionCombine = PhysicsMaterialCombine.Minimum, bounceCombine = PhysicsMaterialCombine.Average,
                };
            Vector3 tumble = Vector3.Cross(Vector3.up, velocity) * 0.35f;
            foreach (var rb in bodies)
            {
                if (!rb) continue;
                rb.linearVelocity = velocity;
                rb.angularVelocity = tumble;
                rb.maxDepenetrationVelocity = 4f;
                foreach (var c in rb.GetComponents<Collider>()) c.sharedMaterial = _slippery;
            }

            // the killing hit's knock (a velocity: the same throw a live body would have got) carries the whole body,
            // and the struck part gets a little extra, so it tumbles
            if (impulse.sqrMagnitude > 1e-4f)
            {
                foreach (var rb in bodies) if (rb) rb.linearVelocity += impulse;
                Rigidbody hitBody = segment >= 0 && segment < bodies.Length ? bodies[segment] : null;
                if (hitBody == null && bodies.Length > 0) hitBody = bodies[0];
                if (hitBody) hitBody.AddForceAtPosition(impulse * (hitBody.mass * 0.3f), point, ForceMode.Impulse);
            }

            go.AddComponent<RagdollLifetime>().dieAt = Time.time + Lifetime;
            return go;
        }

        /// <summary>The pelvis body (what a live ragdoll is steered by).</summary>
        public static Rigidbody Pelvis(GameObject ragdoll)
        {
            if (ragdoll == null) return null;
            var bones = Rig.MapBones(ragdoll.transform);
            return bones.TryGetValue("Bone", out var p) ? p.GetComponent<Rigidbody>() : ragdoll.GetComponentInChildren<Rigidbody>();
        }

        static void CopyPose(Transform from, Transform to)
        {
            var src = Rig.MapBones(from);
            foreach (var t in to.GetComponentsInChildren<Transform>(true))
                if (t != to && src.TryGetValue(t.name, out var s))
                {
                    t.localPosition = s.localPosition;
                    t.localRotation = s.localRotation;
                }
        }

        static Rigidbody[] BodiesFromCustom(System.Collections.Generic.Dictionary<string, Transform> bones)
        {
            var result = new Rigidbody[Rig.Segments.Length];
            for (int i = 0; i < Rig.Segments.Length; i++)
                if (bones.TryGetValue(Rig.Segments[i].start, out var t)) result[i] = t.GetComponent<Rigidbody>();
            return result;
        }

        static Rigidbody[] BuildBodies(System.Collections.Generic.Dictionary<string, Transform> bones)
        {
            // Hip bones sit under the armature root, not the spine; re-parent so they follow the pelvis.
            if (bones.TryGetValue("Bone", out var pelvis))
            {
                if (bones.TryGetValue("Leg L", out var ll)) ll.SetParent(pelvis, true);
                if (bones.TryGetValue("Leg R", out var lr)) lr.SetParent(pelvis, true);
            }

            var segs = Rig.Segments;
            var bodies = new Rigidbody[segs.Length];
            for (int i = 0; i < segs.Length; i++)
            {
                var s = segs[i];
                if (!bones.TryGetValue(s.start, out var a) || !bones.TryGetValue(s.end, out var b)) continue;

                var rb = a.gameObject.AddComponent<Rigidbody>();
                rb.mass = s.mass;
                rb.linearDamping = 0.05f;
                rb.angularDamping = 0.8f;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                rb.solverIterations = 12;

                var col = a.gameObject.AddComponent<CapsuleCollider>();
                Vector3 lp = a.InverseTransformPoint(b.position);
                float scale = Mathf.Max(0.0001f, a.lossyScale.x);
                int axis = MaxAxis(lp);
                col.direction = axis;
                col.center = lp * 0.5f;
                col.radius = s.radius / scale;
                col.height = Mathf.Abs(lp[axis]) + col.radius * 2f;
                bodies[i] = rb;
            }

            for (int i = 0; i < segs.Length; i++)
            {
                if (bodies[i] == null || segs[i].parent < 0 || bodies[segs[i].parent] == null) continue;
                var j = bodies[i].gameObject.AddComponent<CharacterJoint>();
                j.connectedBody = bodies[segs[i].parent];
                j.axis = Vector3.right;
                j.swingAxis = Vector3.forward;
                bool spine = segs[i].region != HitRegion.Limb;
                j.lowTwistLimit = new SoftJointLimit { limit = spine ? -25f : -60f };
                j.highTwistLimit = new SoftJointLimit { limit = spine ? 25f : 60f };
                j.swing1Limit = new SoftJointLimit { limit = spine ? 30f : 70f };
                j.swing2Limit = new SoftJointLimit { limit = spine ? 30f : 50f };
                j.enableProjection = true;
                j.enablePreprocessing = false;
            }
            return bodies;
        }

        static int MaxAxis(Vector3 v)
        {
            float x = Mathf.Abs(v.x), y = Mathf.Abs(v.y), z = Mathf.Abs(v.z);
            return x > y && x > z ? 0 : (y > z ? 1 : 2);
        }
    }
}
