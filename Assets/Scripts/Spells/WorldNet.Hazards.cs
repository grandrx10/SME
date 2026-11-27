using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>The op stream's part for the map hazards: meteors (the rest only depends on the round's clock, see <see cref="MapClock"/>).</summary>
    public partial class WorldNet
    {
        enum HazardOp : byte { Meteor = 131, Practice = 132, Crater = 150, Wall = 170 }

        /// <summary>A meteor on its way down, and its mark where it'll land.</summary>
        public static int ServerMeteor(Vector3 origin, Vector3 velocity, Vector3 at, Vector3 normal, ulong owner = 0, float size = 1f)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp((Op)(byte)HazardOp.Meteor, 96);
            w.WriteValueSafe(id); w.WriteValueSafe(origin); w.WriteValueSafe(velocity); w.WriteValueSafe(at); w.WriteValueSafe(normal); w.WriteValueSafe(Now); w.WriteValueSafe(owner); w.WriteValueSafe(size);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <summary>A meteor's crater: the voxels are just gone (no debris: they come by the dozen, and what they cut off goes too), and what's round it is singed.</summary>
        public static void ServerMeteorCrater(Vector3Int center, int r4, int singe4)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp((Op)(byte)HazardOp.Crater, 16);
            WriteCell(ref w, center);
            w.WriteValueSafe((byte)Mathf.Clamp(r4, 1, 255)); w.WriteValueSafe((byte)Mathf.Clamp(singe4, 1, 255));
            i.Broadcast(Finish(ref w));
        }

        /// <summary>The rising wall has reached layer <paramref name="y"/>: every voxel in it (inside x0..x1, z0..z1, cells) goes.</summary>
        public static void ServerWallLayer(int y, int x0, int x1, int z0, int z1)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp((Op)(byte)HazardOp.Wall, 24);
            w.WriteValueSafe(y); w.WriteValueSafe(x0); w.WriteValueSafe(x1); w.WriteValueSafe(z0); w.WriteValueSafe(z1);
            i.Broadcast(Finish(ref w));
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

        bool ApplyHazards(Op op, ref FastBufferReader r, VoxelWorld world)
        {
            if ((HazardOp)(byte)op == HazardOp.Crater)
            {
                var c = ReadCell(ref r);
                r.ReadValueSafe(out byte r4); r.ReadValueSafe(out byte singe4);
                world.RemoveSphere(c, r4, null, null);
                world.Singe(c, singe4);
                return true;
            }
            if ((HazardOp)(byte)op == HazardOp.Wall)
            {
                r.ReadValueSafe(out int y); r.ReadValueSafe(out int x0); r.ReadValueSafe(out int x1); r.ReadValueSafe(out int z0); r.ReadValueSafe(out int z1);
                RisingWall.ApplyLayer(world, y, x0, x1, z0, z1);
                return true;
            }
            if ((HazardOp)(byte)op == HazardOp.Practice)
            {
                r.ReadValueSafe(out byte slot); r.ReadValueSafe(out double start);
                Practice.SetHazardsLocal(slot, start);
                return true;
            }
            if ((HazardOp)(byte)op != HazardOp.Meteor) return false;
            var fx = new FireballFx { meteor = true, launched = true };
            r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.origin); r.ReadValueSafe(out fx.velocity);
            r.ReadValueSafe(out Vector3 at); r.ReadValueSafe(out Vector3 normal); r.ReadValueSafe(out fx.start); r.ReadValueSafe(out fx.owner); r.ReadValueSafe(out fx.size);
            fx.launchTime = fx.start;
            Fireballs[fx.id] = fx;
            if (Debris.Suppressed) return true;
            fx.view = FireballView.Create(fx);
            MeteorMark.Create(fx, at, normal);
            return true;
        }
    }
}
