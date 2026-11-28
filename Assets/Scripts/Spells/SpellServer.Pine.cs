using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for the Pine Tree (a Botany pot): the sapling grows slowly for 6 s, shaking hard over the last second,
    /// then is suddenly 3x its size. Everyone inside the big tree then (its caster too) takes 30, thrown and ragdolled. It
    /// stands 2.5 s more, then it's gone. Pressed again it's thrown; knocked down while holding it, it's dropped (it
    /// still grows and pops).
    /// </summary>
    /// <summary>Server: tells whoever's listening when this physics body hits something.</summary>
    public class PotHits : MonoBehaviour
    {
        public System.Action<Collision> hit;
        void OnCollisionEnter(Collision c) => hit?.Invoke(c);
    }

    public static partial class SpellServer
    {
        static void PineTick(PotState p, WorldNet.PotFx fx, double now, float t)
        {
            // thrown or dropped: its physics body is as big as it's drawn
            if (p.body != null && p.body.TryGetComponent<BoxCollider>(out var box))
            {
                var b0 = PotModel.Bounds(SpellPlans.PotPine);
                float s = SpellPlans.PinePotScale(t);
                box.center = b0.center * s;
                box.size = b0.size * s;
            }
            if (p.popped || t < SpellPlans.PineBurstAt) return;
            p.popped = true;

            // still in the hands: set down on the ground under it, upright. Dropped or thrown: it stays however it lies
            // (come to rest, it's held there, so the growing pot isn't shoved about by the ground it grows into)
            Vector3 at = p.body != null ? p.body.position : WorldNet.PotPosition(fx, now);
            Quaternion rot = p.body != null ? p.body.rotation : WorldNet.PotRotation(fx, now);
            if (p.body != null && p.body.linearVelocity.magnitude < 2f)
            {
                p.body.linearVelocity = Vector3.zero;
                p.body.angularVelocity = Vector3.zero;
                p.body.isKinematic = true;
            }
            bool held = p.phase == WorldNet.PotHeld;
            if (held && Physics.Raycast(at + Vector3.up * 0.5f, Vector3.down, out var ground, 3f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
            {
                float yaw = WorldNet.PotYaw(fx);
                p.phase = WorldNet.PotGround;
                at = ground.point;
                rot = Quaternion.Euler(0f, yaw, 0f);
                WorldNet.ServerPotDrop(p.id, at, yaw);
            }
            else if (held) StartBody(p, fx, now, p.caster != null ? p.caster.ViewVelocity : Vector3.zero, Vector3.zero);

            // everyone inside the big tree
            Vector3 root = at + rot * (PotModel.PlantOffset(SpellPlans.PotPine) * SpellPlans.PinePotBig); // (the pot's grown too)
            var b = PotModel.PlantBounds(SpellPlans.PotPine);
            foreach (var o in Combatant.Registry.ToArray())
            {
                if (o == null || !o.IsAlive || Folded(o) || o != p.caster && Combatant.Allies(p.caster, o)) continue;
                if (!InPine(o, root, rot, b)) continue;
                Vector3 away = o.transform.position - root;
                away.y = 0f;
                away = away.sqrMagnitude > 0.01f ? away.normalized : Quaternion.Euler(0f, o.ViewYaw + 180f, 0f) * Vector3.forward;
                Hit(o, p.caster, SpellPlans.PineDamage, o.transform.position, away * SpellPlans.PineKnock + Vector3.up * SpellPlans.PineKnockUp, SpellPlans.PineRagdoll, from: root);
            }
        }

        static PhysicsMaterial _pineBounce;

        /// <summary>A thrown (or dropped) pine pot's body: it bounces off the ground, breaking a bite of it each hard hit.</summary>
        static void PineBody(PotState p, Rigidbody rb)
        {
            _pineBounce ??= new PhysicsMaterial("PineBounce")
            {
                bounciness = SpellPlans.PineBounce, bounceCombine = PhysicsMaterialCombine.Maximum,
                dynamicFriction = 0.4f, staticFriction = 0.4f,
            };
            foreach (var c in rb.GetComponents<Collider>()) c.sharedMaterial = _pineBounce;
            rb.gameObject.AddComponent<PotHits>().hit = c => PineSmash(p, c);
        }

        /// <summary>Hit the ground hard: a bite of it (as big as the pot is now, not all the way through) breaks away; it bounces off.</summary>
        static void PineSmash(PotState p, Collision c)
        {
            double now = WorldNet.Now;
            var w = VoxelWorld.Current;
            if (w == null || now < p.nextSmash || !PotsS.Contains(p) || c.contactCount == 0 || !VoxelWorld.IsVoxelCollider(c.collider)) return;
            if (c.relativeVelocity.magnitude < SpellPlans.PineSmashSpeed) return;
            p.nextSmash = now + SpellPlans.PineSmashEvery;
            var cp = c.GetContact(0);
            float r = Mathf.Min(SpellPlans.PineSmash * SpellPlans.PinePotScale((float)(now - p.start)), SpellPlans.PineSmashMax);
            var cell = w.CellOf(cp.point - cp.normal * 0.1f); // (the normal points out of the ground)
            WorldNet.ServerRemoveSphere(cell, SpellPlans.R4(r), 3f, cp.normal * 3f);
        }

        /// <summary>
        /// Is any of this body (feet, hips, head) inside the grown tree? Its shape: the model's box at <see cref="SpellPlans.PineBig"/>
        /// times, narrowing to a point at the top (a cone, as a pine is).
        /// </summary>
        static bool InPine(Combatant o, Vector3 root, Quaternion rot, Bounds b)
        {
            float s = SpellPlans.PineBig, pad = 0.35f;
            Vector3 feet = o.FeetPosition;
            Quaternion back = Quaternion.Inverse(rot);
            float wide = Mathf.Max(b.extents.x, b.extents.z) * s;
            foreach (var p in new[] { feet + Vector3.up * 0.15f, o.transform.position, feet + Vector3.up * 1.7f * o.transform.localScale.y })
            {
                Vector3 q = back * (p - root);
                float y0 = b.min.y * s, y1 = b.max.y * s;
                if (q.y < y0 - pad || q.y > y1 + pad) continue;
                float up = Mathf.Clamp01((q.y - y0) / Mathf.Max(0.01f, y1 - y0));
                float r = wide * (1f - 0.8f * up) + pad;
                Vector2 off = new(q.x - b.center.x * s, q.z - b.center.z * s);
                if (off.magnitude <= r) return true;
            }
            return false;
        }
    }
}
