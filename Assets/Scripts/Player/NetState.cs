using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>Arm/body animation a caster is playing (sent so everyone sees the same gestures).</summary>
    public enum SpellPose : byte
    {
        None = 0,
        HoldWall = 1,   // palms down, ready to lift
        CastWall = 2,   // heave upwards
        Gather = 3,     // rip the rock out, hold it, throw it
        HoldFold = 4,   // arms out wide, palms facing in
        CastFold = 5,   // buckle... clap
        Dash = 6,
        ShoveHold = 7,
        ShoveCast = 8,
        TornadoHold = 9,
        TornadoCast = 10,
        MissileCharge = 11,
        Barrier = 12,
        Siphon = 13,
        SiphonPunch = 14,
        FlameCharge = 15,    // hand above the head, then throw
        HellfireDraw = 16,   // pointing the line along the ground
        FlameHands = 17,     // glowing hands... clap
        JudgmentHold = 18,
        JudgmentCast = 19,
        TugHold = 20,
        TugCast = 21,
        Platform = 22,       // stamp the ground up
        FoolRoll = 23,       // a flick of the hand: a card appears
        Magician = 24,       // wand hand up, other hand down
        SunRaise = 25,       // holding the sun up... flash
        Prayer = 26,         // hands come together (Empress, Emperor)
        WaterHold = 27,      // hands cupped in front, holding the water
        WaterCast = 28,      // lifted out: the orb floats off them
        BubbleCast = 29,     // hands come together, and stay together while the bubble carries you
        TsunamiHold = 30,    // arms low and ready
        TsunamiCast = 31,    // drawn back behind the hips... shoved forward
        WarpHold = 32,       // hands close together in front of the chest
        WarpCast = 33,       // hands together... pulled apart, opening the rift
        PortalHold = 34,     // right hand out, palm forward, framing the portal
        PortalCast = 35,     // a sweep of the hand: the portal opens
        SwiftCast = 36,      // crouched, gathering... then the forced run, leaning in
        SeveranceDash = 37,  // rushing in, the sword held low on the left
        SeveranceSlash = 38, // the sword swept left to right
        FlowCharge = 39,     // down on one knee, sword held low on the left, charging
        FlowStrike = 40,     // after the teleport: right arm straight out to the side, sword pointing on
        AttractCast = 41,    // left hand raised... then held out, steering the ball of water
        RepelCast = 42,      // right hand raised... then held out, steering the ball of flame
        FuseCast = 43,       // both hands round the purple ball as it grows... then thrust out
        HierophantHold = 44, // both palms up and out, framing where the wall will stand
        HierophantCast = 45, // ...pushed out: the wall appears
        LoversBeam = 46,     // right hand thrown out, held out along the beam; left hand on the heart
        ChariotRun = 47,     // head down, arms flung back, charging
        HammerDrag = 48,     // Rising Hammer's rush: both hands on the hammer, its head dragged behind on the left
        HammerRise = 49,     // ...brought up from behind and swung upwards
        HammerLeap = 50,     // Descending Hammer: down on one knee, then leaping, the hammer held behind the head
        HammerSlam = 51,     // ...brought down vertically in front on landing
        SwarmRaise = 52,     // Rock Swarm: a scoop down, then both hands thrown up into the air
        IceCrouch = 53,      // Ice Spike: crouched, both palms on the ground... then standing up
        HermitKneel = 54,    // (no longer used)
        WheelSpin = 55,      // Wheel of Fortune: a flick of the right hand up at the die
        Clasp = 56,          // The Moon: hands clasped together in front of the chest
        LeftArmUp = 57,      // The Hanged Man: the left arm raised straight up
        HeadBack = 58,       // Death: the head tipped back... then forward again
        RightHandUp = 59,    // The Devil: the right hand raised
        TowerPray = 60,      // The Tower: kneeling, hands together in prayer
        NecroCrouch = 61,    // Undead Servitude / Summon Horde: crouched, hands out over the ground
        BiteCrouch = 62,     // Infectious Bite: crouched, ready to spring
        FlameSpew = 63,      // Flame Thrower: both hands clasped, held straight out in front
        GrapplePoint = 64,   // Grapple Hook: the left hand held out, pointing
        BreathLean = 65,     // Icy Breath: leaning forward, breathing out
        Flex = 66,           // Strength: both arms up, flexing
        ShotgunAim = 67,     // Rachmaninoff: the shotgun raised to the shoulder... fired
        RifleKneel = 68,     // Sebastian: down on one knee, aiming down the sights, charging
        RifleFire = 69,      // ...the kick, then standing up
        GraspKneel = 70,     // Undying Grasp: down on one knee, hands out over the ground
        SkinKneel = 71,      // Skinwalker, Justice: down on one knee over the corpse, hands out to it
        BombPlace = 72,      // Remote Flame Bomb: the right hand presses it onto the spot
        BombToss = 73,       // Flame Bomb Toss: both hands thrown forward
        NecroRaise = 74,     // Necromatic Blast: hauled up out of the ground over the head... then hurled forward
        HexPoint = 75,       // a Hex: the left hand pointing... then flicked at them
        ArcaneShield = 76,   // the arcane shield: the right hand held out along the aim
        DeathDash = 77,      // Death: the fist drawn back, charging (Mana Siphon's), through everyone
        PotHold = 78,        // the Botany cards: both hands out in front, a little bent, holding the pot
        PotThrow = 79,       // the Fly Trap thrown: both hands thrown forward
        WindCharge = 80,     // Compressed Shot charging: the right hand held out at the aim, wind round it
        WindShot = 81,       // ...and let go
        CloudStep = 82,      // Cloud Step: the leap
        SubDive = 83,        // Submerge: diving into the pool
        SubEmerge = 84,      // Submerge: coming out of the ground, spinning
        HammerSpinCharge = 85, // Hammer Spin: wound right back, the hammer out behind on the right, charging
        HammerSpin = 86,     // ...let go: spinning round and round, the hammer stretched out level, zooming along
        WolfgangFire = 87,   // Wolfgang: a pistol in each hand, both arms out at the aim, firing turn about
        HeavenCrouch = 88,   // Heavenly Slash: crouched, the sword low...
        HeavenHover = 89,    // ...up in the air, posing, the sword held out level
        HeavenSlash = 90,    // ...the cut
        ParryGuard = 91,     // Parry: the sword held point down in front, on guard
        ParryStab = 92,      // ...deflected: dashing in, a straight stab
        RangThrow = 93,      // Hammerang: the left hand held out... then flung forward, letting the hammer go
    }

    /// <summary>Owner-authoritative movement snapshot replicated to everyone.</summary>
    public struct NetState : INetworkSerializable, IEquatable<NetState>
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Yaw;
        public float Pitch;
        public byte Flags;
        public byte Teleport;   // increments on respawn so remotes snap instead of interpolating
        public byte Pose;       // SpellPose
        public double PoseStart; // server time the pose started
        /// <summary>Owner's real-time clock when this was written: remotes space the snapshots by when they were taken,
        /// not by when they happened to arrive (packets bunch up, which made other players stutter). Not part of equality,
        /// so standing still still sends nothing.</summary>
        public float Stamp;

        public const byte Grounded = 1, Ragdoll = 2, Crouch = 4;

        public bool Has(byte f) => (Flags & f) != 0;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Position);
            s.SerializeValue(ref Velocity);
            s.SerializeValue(ref Yaw);
            s.SerializeValue(ref Pitch);
            s.SerializeValue(ref Flags);
            s.SerializeValue(ref Teleport);
            s.SerializeValue(ref Pose);
            s.SerializeValue(ref PoseStart);
            s.SerializeValue(ref Stamp);
        }

        public bool Equals(NetState o) =>
            Position == o.Position && Velocity == o.Velocity && Mathf.Approximately(Yaw, o.Yaw) &&
            Mathf.Approximately(Pitch, o.Pitch) && Flags == o.Flags && Teleport == o.Teleport && Pose == o.Pose && PoseStart == o.PoseStart;

        public override bool Equals(object obj) => obj is NetState o && Equals(o);
        public override int GetHashCode() => Position.GetHashCode() ^ (Flags << 8);
    }

    /// <summary>Mana at a moment in time; it refills at 1 per second up to 10, so anyone can work out the current value.</summary>
    public struct ManaState : INetworkSerializable, IEquatable<ManaState>
    {
        public const float Max = 10f, PerSecond = 1f;
        /// <summary>
        /// <see cref="Flow"/>: refilling as normal / not refilling at all (The Chariot) / draining, not refilling (The
        /// Lovers' tether) / refilling, less what the Flame Thrower takes / refilling, less what Death's aura takes.
        /// </summary>
        public const byte Refill = 0, Held = 1, Draining = 2, Channel = 3, Aura = 4, Sunk = 5;
        public float Value;
        public double Time;
        /// <summary>The Magician: regeneration runs at double speed until this time.</summary>
        public double FastUntil;
        /// <summary>Justice: a little more on top until then (the killer was hurt); and a lot more until then (they were killed).</summary>
        public double GainUntil, SurgeUntil;
        /// <summary>Which way it's going: <see cref="Refill"/>, <see cref="Held"/>, <see cref="Draining"/>, <see cref="Channel"/> or <see cref="Aura"/>.</summary>
        public byte Flow;
        /// <summary>The most mana this player can hold (0 = the usual <see cref="Max"/>; Temperance raises it, the Undead lower it).</summary>
        public byte Cap;

        public float Limit => Cap > 0 ? Cap : Max;

        /// <summary>Mana a second, right then (before it's full or empty).</summary>
        public float RateAt(double t)
        {
            float r = Flow switch
            {
                Held => 0f,
                Draining => -SpellPlans.LoversDrain,
                Sunk => 0f, // under Submerge: no regeneration
                _ => PerSecond * (t < FastUntil ? 2f : 1f) - (Flow == Channel ? SpellPlans.ChannelDrain : Flow == Aura ? SpellPlans.DeathDrain : 0f),
            };
            if (t < GainUntil) r += SpellPlans.JusticeGain;
            if (t < SurgeUntil) r += SpellPlans.JusticeSurge / SpellPlans.JusticeSurgeTime;
            return r;
        }

        public float At(double now)
        {
            float max = Limit, v = Mathf.Clamp(Value, 0f, max);
            for (double t = Time; t < now;)
            {
                // up to whenever the rate next changes
                double next = now;
                if (FastUntil > t && FastUntil < next) next = FastUntil;
                if (GainUntil > t && GainUntil < next) next = GainUntil;
                if (SurgeUntil > t && SurgeUntil < next) next = SurgeUntil;
                v = Mathf.Clamp(v + RateAt(t) * (float)(next - t), 0f, max);
                t = next;
            }
            return v;
        }

        public bool Fast(double now) => now < FastUntil;

        /// <summary>A new mana amount from now on (keeps the running boosts, the flow and the cap).</summary>
        public ManaState With(double now, float value) { var m = this; m.Value = value; m.Time = now; return m; }

        /// <summary>The same mana, going the given way from now on.</summary>
        public ManaState WithFlow(double now, byte flow) { var m = With(now, At(now)); m.Flow = flow; return m; }

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Value);
            s.SerializeValue(ref Time);
            s.SerializeValue(ref FastUntil);
            s.SerializeValue(ref GainUntil);
            s.SerializeValue(ref SurgeUntil);
            s.SerializeValue(ref Flow);
            s.SerializeValue(ref Cap);
        }

        public bool Equals(ManaState o) => Value == o.Value && Time == o.Time && FastUntil == o.FastUntil && GainUntil == o.GainUntil && SurgeUntil == o.SurgeUntil && Flow == o.Flow && Cap == o.Cap;
        public override bool Equals(object obj) => obj is ManaState o && Equals(o);
        public override int GetHashCode() => Value.GetHashCode() ^ Time.GetHashCode();
    }

    /// <summary>
    /// Lasting states on a body, written by the server and drawn by everyone: The Hanged Man's float, Wheel of
    /// Fortune's results, Icy Breath's chill, and being (or turning into) one of the Undead.
    /// </summary>
    public struct BodyStatus : INetworkSerializable, IEquatable<BodyStatus>
    {
        /// <summary>The Hanged Man: upside down and floating from <see cref="HangFrom"/> until <see cref="HangUntil"/>.</summary>
        public double HangFrom, HangUntil;
        /// <summary>Wheel of Fortune 6: golden purple, nothing touches you (the dash and its punch), until then.</summary>
        public double GoldUntil;
        /// <summary>Wheel of Fortune 0: glowing red from <see cref="DoomFrom"/>; blows up at <see cref="DoomAt"/> (0 = not doomed).</summary>
        public double DoomFrom, DoomAt;
        /// <summary>Icy Breath: half speed until then.</summary>
        public double SlowUntil;
        /// <summary>Undying Grasp: held by the legs and silenced from <see cref="GraspFrom"/> until <see cref="GraspUntil"/>.</summary>
        public double GraspFrom, GraspUntil;
        /// <summary>A dead body turning green (bitten to death): it gets up as one of the Undead at InfectFrom + <see cref="SpellPlans.InfectTime"/>.</summary>
        public double InfectFrom;
        /// <summary>One of the Undead, on <see cref="Master"/>'s side (the master's object id).</summary>
        public bool Undead;
        public ulong Master;
        /// <summary>A small mindless one (Summon Horde), climbing out of the ground from <see cref="RiseFrom"/>.</summary>
        public bool Horde;
        public double RiseFrom;
        /// <summary>Strength: transformed (bigger, higher jumps, harder hits) from <see cref="StrongFrom"/> until <see cref="StrongUntil"/>.</summary>
        public double StrongFrom, StrongUntil;
        /// <summary>The Star: outlined through walls until then.</summary>
        public double LitUntil;
        /// <summary>The Moon: over the caster from <see cref="MoonFrom"/>, gone at <see cref="MoonAt"/> (0 = none); how full it is.</summary>
        public double MoonFrom, MoonAt;
        public float MoonFill;
        /// <summary>Justice: the killer this player is after (object id), outlined through walls on their own screen (0 = nobody).</summary>
        public ulong JusticeOn;
        /// <summary>Too much Water of Life: swelling up, bursting then (0 = not).</summary>
        public double BloatAt;
        /// <summary>Wheel of Fortune 1: thrown into the sky, tumbling, until then at most (0 = not).</summary>
        public double SpinUntil;
        /// <summary>The Sun: cast at <see cref="SunCast"/>; colours inverted from <see cref="SunFrom"/> until <see cref="SunUntil"/> (0 = none).</summary>
        public double SunCast, SunFrom, SunUntil;
        /// <summary>The World: crushed away from <see cref="WorldFrom"/>, standing at <see cref="WorldSpot"/> inside it from <see cref="WorldUntil"/> (0 = not taken).</summary>
        public double WorldFrom, WorldUntil;
        public Vector3 WorldSpot;
        public float WorldYaw;
        /// <summary>Who cast it (object id).</summary>
        public ulong WorldBy;
        /// <summary>Stasis Hex: frozen where they are, as they are, from <see cref="StasisFrom"/> until then (0 = not).</summary>
        public double StasisFrom, StasisUntil;
        /// <summary>Shatter Hex: lifted and held in the air, cased in glass, from <see cref="ShatterFrom"/> until it goes off (0 = not).</summary>
        public double ShatterFrom, ShatterUntil;
        /// <summary>Mind Hex: moving as <see cref="MindBy"/> (object id) moves, silenced, from <see cref="MindFrom"/> until then (0 = not).</summary>
        public double MindFrom, MindUntil;
        public ulong MindBy;
        /// <summary>Death: marked (for good, until they die). Struck again: doomed, vomiting bile from <see cref="BileFrom"/>, bursting at <see cref="BileAt"/> (0 = not).</summary>
        public bool DeathMarked;
        public double BileFrom, BileAt;
        /// <summary>Smallify Hex: small (half size, half speed, knocked over by anyone walking into them) from <see cref="SmallFrom"/> until then (0 = not).</summary>
        public double SmallFrom, SmallUntil;
        public ulong SmallBy;
        /// <summary>Fly Trap: inside the trap (pot id <see cref="EatenBy"/>) from <see cref="EatenFrom"/> until then (0 = not).</summary>
        public int EatenBy;
        public double EatenFrom, EatenUntil;
        /// <summary>Ephemerate: gone (unseen, untouchable, through anything) from <see cref="EphemFrom"/> until then (0 = not).</summary>
        public double EphemFrom, EphemUntil;
        /// <summary>Submerge: a pool from <see cref="SubFrom"/> (diving in before it) until then (0 = not).</summary>
        public double SubFrom, SubUntil;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref HangFrom); s.SerializeValue(ref HangUntil);
            s.SerializeValue(ref GoldUntil); s.SerializeValue(ref MoonFill); s.SerializeValue(ref JusticeOn); s.SerializeValue(ref BloatAt); s.SerializeValue(ref SpinUntil);
            s.SerializeValue(ref DoomFrom); s.SerializeValue(ref DoomAt);
            s.SerializeValue(ref SlowUntil); s.SerializeValue(ref InfectFrom);
            s.SerializeValue(ref GraspFrom); s.SerializeValue(ref GraspUntil);
            s.SerializeValue(ref Undead); s.SerializeValue(ref Master);
            s.SerializeValue(ref Horde); s.SerializeValue(ref RiseFrom);
            s.SerializeValue(ref StrongFrom); s.SerializeValue(ref StrongUntil);
            s.SerializeValue(ref LitUntil); s.SerializeValue(ref MoonFrom); s.SerializeValue(ref MoonAt);
            s.SerializeValue(ref SunCast); s.SerializeValue(ref SunFrom); s.SerializeValue(ref SunUntil);
            s.SerializeValue(ref WorldFrom); s.SerializeValue(ref WorldUntil); s.SerializeValue(ref WorldSpot); s.SerializeValue(ref WorldYaw); s.SerializeValue(ref WorldBy);
            s.SerializeValue(ref StasisFrom); s.SerializeValue(ref StasisUntil);
            s.SerializeValue(ref ShatterFrom); s.SerializeValue(ref ShatterUntil);
            s.SerializeValue(ref MindFrom); s.SerializeValue(ref MindUntil); s.SerializeValue(ref MindBy);
            s.SerializeValue(ref DeathMarked); s.SerializeValue(ref BileFrom); s.SerializeValue(ref BileAt);
            s.SerializeValue(ref SmallFrom); s.SerializeValue(ref SmallUntil); s.SerializeValue(ref SmallBy);
            s.SerializeValue(ref EatenBy); s.SerializeValue(ref EatenFrom); s.SerializeValue(ref EatenUntil);
            s.SerializeValue(ref EphemFrom); s.SerializeValue(ref EphemUntil); s.SerializeValue(ref SubFrom); s.SerializeValue(ref SubUntil);
        }

        public bool Equals(BodyStatus o) =>
            HangFrom == o.HangFrom && HangUntil == o.HangUntil && GoldUntil == o.GoldUntil &&
            MoonFill == o.MoonFill && JusticeOn == o.JusticeOn && BloatAt == o.BloatAt && SpinUntil == o.SpinUntil &&
            DoomFrom == o.DoomFrom && DoomAt == o.DoomAt && SlowUntil == o.SlowUntil && InfectFrom == o.InfectFrom && GraspFrom == o.GraspFrom && GraspUntil == o.GraspUntil &&
            Undead == o.Undead && Master == o.Master && Horde == o.Horde && RiseFrom == o.RiseFrom &&
            StrongFrom == o.StrongFrom && StrongUntil == o.StrongUntil && LitUntil == o.LitUntil &&
            MoonFrom == o.MoonFrom && MoonAt == o.MoonAt && SunCast == o.SunCast && SunFrom == o.SunFrom && SunUntil == o.SunUntil &&
            WorldFrom == o.WorldFrom && WorldUntil == o.WorldUntil && WorldSpot == o.WorldSpot && WorldYaw == o.WorldYaw && WorldBy == o.WorldBy &&
            StasisFrom == o.StasisFrom && StasisUntil == o.StasisUntil && ShatterFrom == o.ShatterFrom && ShatterUntil == o.ShatterUntil && MindFrom == o.MindFrom && MindUntil == o.MindUntil && MindBy == o.MindBy &&
            DeathMarked == o.DeathMarked && BileFrom == o.BileFrom && BileAt == o.BileAt &&
            SmallFrom == o.SmallFrom && SmallUntil == o.SmallUntil && SmallBy == o.SmallBy && EatenBy == o.EatenBy && EatenFrom == o.EatenFrom && EatenUntil == o.EatenUntil &&
            EphemFrom == o.EphemFrom && EphemUntil == o.EphemUntil && SubFrom == o.SubFrom && SubUntil == o.SubUntil;
        public override bool Equals(object obj) => obj is BodyStatus o && Equals(o);
        public override int GetHashCode() => HangUntil.GetHashCode() ^ Master.GetHashCode();

        public bool Hanging(double now) => now >= HangFrom && now < HangUntil;

        public bool Gold(double now) => now < GoldUntil;
        public bool Doomed => DoomAt > 0;
        public bool Slowed(double now) => now < SlowUntil;
        /// <summary>Doomed by Death: vomiting black bile until they burst.</summary>
        public bool Biling(double now) => BileAt > 0 && now < BileAt;
        /// <summary>Shrunk by Smallify Hex.</summary>
        public bool Small(double now) => now < SmallUntil;
        /// <summary>Inside a fly trap.</summary>
        public bool Eaten(double now) => now < EatenUntil;
        /// <summary>Gone with Ephemerate.</summary>
        public bool Ephemeral(double now) => now >= EphemFrom && now < EphemUntil;
        /// <summary>Under Submerge's pool (not just diving in).</summary>
        public bool Under(double now) => now >= SubFrom && now < SubUntil;
        /// <summary>Held by Undying Grasp: no moving, no casting.</summary>
        public bool Grasped(double now) => now < GraspUntil;
        /// <summary>Frozen by Stasis Hex.</summary>
        public bool Stasis(double now) => now < StasisUntil;
        /// <summary>About to shatter: held in the air.</summary>
        public bool Shattering(double now) => now < ShatterUntil;
        /// <summary>Under someone's control (Mind Hex).</summary>
        public bool Mind(double now) => now < MindUntil;
        public bool Strong(double now) => now >= StrongFrom && now < StrongUntil;
        public bool Lit(double now) => now < LitUntil;
        /// <summary>Swelling up with too much Water of Life.</summary>
        public bool Bloating => BloatAt > 0;
        /// <summary>The Moon is over them (not full yet).</summary>
        public bool Moon(double now) => MoonAt > 0 && now >= MoonFrom && now < MoonAt;
        /// <summary>The Sun: their colours are inverted, and looking at them hurts.</summary>
        public bool Sun(double now) => now >= SunFrom && now < SunUntil;
        /// <summary>Taken by The World: being crushed, or gone until it's ready (nothing touches them, they do nothing).</summary>
        public bool Taken(double now) => now >= WorldFrom && now < WorldUntil;
        /// <summary>Crushed away completely, not yet back.</summary>
        public bool Vanished(double now) => Taken(now) && now >= WorldFrom + SpellPlans.WorldCrush;
    }

    /// <summary>The 5 cards a player brought into the round (what the server lets them cast).</summary>
    public struct EquippedCards : INetworkSerializable, IEquatable<EquippedCards>
    {
        public byte C0, C1, C2, C3, C4;

        public CardId this[int i]
        {
            get => (CardId)(i switch { 0 => C0, 1 => C1, 2 => C2, 3 => C3, 4 => C4, _ => 0 });
            set
            {
                byte b = (byte)value;
                switch (i) { case 0: C0 = b; break; case 1: C1 = b; break; case 2: C2 = b; break; case 3: C3 = b; break; case 4: C4 = b; break; }
            }
        }

        public static EquippedCards From(Loadout l)
        {
            var e = new EquippedCards();
            for (int i = 0; i < Loadout.Slots; i++) e[i] = l.Get(i);
            return e;
        }

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref C0); s.SerializeValue(ref C1); s.SerializeValue(ref C2); s.SerializeValue(ref C3); s.SerializeValue(ref C4);
        }

        public bool Equals(EquippedCards o) => C0 == o.C0 && C1 == o.C1 && C2 == o.C2 && C3 == o.C3 && C4 == o.C4;
        public override bool Equals(object obj) => obj is EquippedCards o && Equals(o);
        public override int GetHashCode() => C0 | (C1 << 8) | (C2 << 16) | (C3 << 24) ^ C4;
    }

    /// <summary>A player's team in a Team Deathmatch, and its colour. Server writes.</summary>
    public struct TeamState : INetworkSerializable, IEquatable<TeamState>
    {
        /// <summary>1, 2, or 3 (the odd one out, alone); 0 = not in a Team Deathmatch.</summary>
        public byte Team;
        /// <summary>Palette index.</summary>
        public byte Color;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Team); s.SerializeValue(ref Color);
        }

        public bool Equals(TeamState o) => Team == o.Team && Color == o.Color;
        public override bool Equals(object obj) => obj is TeamState o && Equals(o);
        public override int GetHashCode() => Team | (Color << 8);
    }

    /// <summary>The skin a player is wearing (Skinwalker): whose colour, face and name, and their moves. Server writes.</summary>
    public struct SkinState : INetworkSerializable, IEquatable<SkinState>
    {
        public const byte Player = 0, Dummy = 1, Undead = 2;
        public bool On;
        /// <summary>A player's, a test dummy's, or one of the Undead's (green).</summary>
        public byte Kind;
        /// <summary>Palette index (a player's).</summary>
        public byte Color;
        /// <summary>Whose face (object id; 0 = none).</summary>
        public ulong Face;
        public FixedString32Bytes Name;
        public EquippedCards Hand;

        public void SetName(string name)
        {
            Name = new FixedString32Bytes();
            Name.CopyFromTruncated(name ?? "");
        }

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref On); s.SerializeValue(ref Kind); s.SerializeValue(ref Color); s.SerializeValue(ref Face);
            s.SerializeValue(ref Name); s.SerializeValue(ref Hand);
        }

        public bool Equals(SkinState o) => On == o.On && Kind == o.Kind && Color == o.Color && Face == o.Face && Name.Equals(o.Name) && Hand.Equals(o.Hand);
        public override bool Equals(object obj) => obj is SkinState o && Equals(o);
        public override int GetHashCode() => Face.GetHashCode() ^ Hand.GetHashCode();
    }
}
