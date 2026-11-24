using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>The op stream's part for the map hazards: meteors (the rest only depends on the round's clock, see <see cref="MapClock"/>).</summary>
    public partial class WorldNet
    {
        enum HazardOp : byte { Meteor = 131, Practice = 132 }

        /// <summary>A meteor on its way down, and its mark where it'll land.</summary>
        public static int ServerMeteor(Vector3 origin, Vector3 velocity, Vector3 at, Vector3 normal)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp((Op)(byte)HazardOp.Meteor, 72);
            w.WriteValueSafe(id); w.WriteValueSafe(origin); w.WriteValueSafe(velocity); w.WriteValueSafe(at); w.WriteValueSafe(normal); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <summary>The hazards in the practice chamber in this slot: set off now, or put back to how they were.</summary>
        public static void ServerPracticeHazards(int slot, bool on)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp((Op)(byte)HazardOp.Practice, 16);
            w.WriteValueSafe((byte)slot); w.WriteValueSafe(on ? Now : 0.0);
            i.Broadcast(Finish(ref w));
        }

        bool ApplyHazards(Op op, ref FastBufferReader r)
        {
            if ((HazardOp)(byte)op == HazardOp.Practice)
            {
                r.ReadValueSafe(out byte slot); r.ReadValueSafe(out double start);
                Practice.SetHazardsLocal(slot, start);
                return true;
            }
            if ((HazardOp)(byte)op != HazardOp.Meteor) return false;
            var fx = new FireballFx { meteor = true, launched = true };
            r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.origin); r.ReadValueSafe(out fx.velocity);
            r.ReadValueSafe(out Vector3 at); r.ReadValueSafe(out Vector3 normal); r.ReadValueSafe(out fx.start);
            fx.launchTime = fx.start;
            Fireballs[fx.id] = fx;
            if (Debris.Suppressed) return true;
            fx.view = FireballView.Create(fx);
            MeteorMark.Create(fx, at, normal);
            return true;
        }
    }
}
