using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>Card ids are saved and sent over the network: append new cards at the end, never reorder.</summary>
    public enum CardId : byte
    {
        None = 0, SummonWall = 1, RockBarrage = 2, FoldingHands = 3,
        Shove = 4, Tornado = 5, NomadicDash = 6, ManaMissiles = 7, MagicalBarrier = 8, ManaSiphon = 9,
        BallOfFlame = 10, HellfireWall = 11, ExplosiveEscape = 12, ArcaneJudgment = 13, Tug = 14, SummonPlatform = 15,
        TheFool = 16, TheMagician = 17, TheHighPriestess = 18, TheEmpress = 19, TheEmperor = 20,
        WaterOfLife = 21, BubbleAway = 22, Tsunami = 23,
        WarpSelf = 24, WarpSpace = 25, ArcaneSwiftness = 26,
        Severance = 27, FlowingCut = 28,
        Attraction = 29, Repulsion = 30,
        TheHierophant = 31, TheLovers = 32, TheChariot = 33,
        RisingHammer = 34, DescendingHammer = 35,
        RockSwarm = 36, IceSpike = 37,
        TheHermit = 38, WheelOfFortune = 39, Justice = 40, TheHangedMan = 41, Death = 42, Temperance = 43, TheDevil = 44, TheTower = 45,
        UndeadServitude = 46, InfectiousBite = 47, FlameThrower = 48, GrappleHook = 49, SummonHorde = 50, IcyBreath = 51,
        Strength = 52,
        TheStar = 53, TheMoon = 54, TheSun = 55, Judgement = 56, TheWorld = 57, Undying = 58,
        Rachmaninoff = 59, Sebastian = 60, UndyingGrasp = 61,
        Skinwalker = 62, RemoteFlameBomb = 63, RemoteDetonation = 64, FlameBombToss = 65,
        NecromaticBlast = 66, ShatterHex = 67, StasisHex = 68, MindHex = 69,
        SmallifyHex = 70, Sunflower = 71, Cactus = 72, FlyTrap = 73,
        CompressedShot = 74, Ephemerate = 75, CloudStep = 76, Submerge = 77, BubbleBomb = 78,
        HammerSpin = 79, Wolfgang = 80, HeavenlySlash = 81, Parry = 82, SpeedDemon = 83,
    }

    /// <summary>Saved with cards: append new elements at the end, never reorder. (Weapon is retired: no card is one any more.)</summary>
    public enum Element : byte { Flame, Arcane, Lightning, Wind, Ground, Tarot, Water, Weapon, Necromancy, Hex, Botany, Blade, Gun, Hammer }

    /// <summary>What a card costs to cast (shown top right: a bubble for mana, a heart for health, a P for a passive card that is never cast).</summary>
    public enum CostType : byte { Mana, Health, Passive }

    /// <summary>
    /// How rare a card is: its border colour, its odds in a pack, and how big its reveal is. Starter cards count as Common
    /// in packs. Secret cards never come out of one (The World: see <see cref="Collection.SecretReady"/>).
    /// </summary>
    public enum Rarity : byte { Starter, Common, Rare, Epic, Secret }

    public class CardInfo
    {
        public CardId id;
        public string name;
        public Element element;
        public CostType costType;
        public int cost;
        public Rarity rarity;
        public string description;
        /// <summary>True: casts the moment the key goes down. False: hold to aim (indicator), cast on release.</summary>
        public bool castOnPress;
        /// <summary>Never in a pack (or rolled by The Fool): it comes along with the cards it goes with (<see cref="Cards.CompanionOf"/>).</summary>
        public bool companion;
        /// <summary>Line drawing in the middle of the card (and on the HUD).</summary>
        public CardSymbols.Draw symbol;
    }

    public static class Cards
    {
        /// <summary>Max distance (metres) any spell reaches, unless a card says otherwise.</summary>
        public const float MaxRange = 20f;
        /// <summary>Seconds before the same ability can be cast again.</summary>
        public const float Cooldown = 1f;

        public static readonly CardInfo[] All =
        {
            new()
            {
                id = CardId.SummonWall, name = "Summon Wall", element = Element.Ground, costType = CostType.Mana, cost = 3,
                rarity = Rarity.Starter,
                description = "Summon a wall of Ground in front of you.", symbol = CardSymbols.SummonWall,
            },
            new()
            {
                id = CardId.RockBarrage, name = "Rock Barrage", element = Element.Ground, costType = CostType.Mana, cost = 2,
                rarity = Rarity.Starter,
                description = "Gather a chunk of Ground and throw it.", symbol = CardSymbols.RockBarrage, castOnPress = true,
            },
            new()
            {
                id = CardId.FoldingHands, name = "Folding Hands", element = Element.Ground, costType = CostType.Mana, cost = 5,
                rarity = Rarity.Common,
                description = "Fold two chunks of Ground together.", symbol = CardSymbols.FoldingHands,
            },
            new()
            {
                id = CardId.Shove, name = "Shove", element = Element.Wind, costType = CostType.Mana, cost = 3,
                rarity = Rarity.Starter,
                description = "Send a gust of Wind forward.", symbol = CardSymbols.Shove,
            },
            new()
            {
                id = CardId.Tornado, name = "Tornado", element = Element.Wind, costType = CostType.Mana, cost = 5,
                rarity = Rarity.Rare,
                description = "Summon a tornado of Wind. After a brief delay, send it forward.", symbol = CardSymbols.Tornado,
            },
            new()
            {
                id = CardId.NomadicDash, name = "Nomadic Dash", element = Element.Wind, costType = CostType.Mana, cost = 2,
                rarity = Rarity.Common,
                description = "Dash in the direction you are moving.", symbol = CardSymbols.NomadicDash, castOnPress = true,
            },
            new()
            {
                id = CardId.ManaMissiles, name = "Mana Missiles", element = Element.Arcane, costType = CostType.Mana, cost = 3,
                rarity = Rarity.Common,
                description = "Launch tracking Arcane missiles at a targeted enemy.", symbol = CardSymbols.ManaMissiles, castOnPress = true,
            },
            new()
            {
                id = CardId.MagicalBarrier, name = "Magical Barrier", element = Element.Arcane, costType = CostType.Mana, cost = 3,
                rarity = Rarity.Common,
                description = "Summon a Barrier around yourself.", symbol = CardSymbols.MagicalBarrier, castOnPress = true,
            },
            new()
            {
                id = CardId.ManaSiphon, name = "Mana Siphon", element = Element.Arcane, costType = CostType.Mana, cost = 4,
                rarity = Rarity.Common,
                description = "Dash forward, striking and siphoning mana from enemies struck.", symbol = CardSymbols.ManaSiphon, castOnPress = true,
            },
            new()
            {
                id = CardId.BallOfFlame, name = "Ball of Flame", element = Element.Flame, costType = CostType.Mana, cost = 4,
                rarity = Rarity.Common,
                description = "Summon a ball of Flame that explodes on impact.", castOnPress = true,
            },
            new()
            {
                id = CardId.HellfireWall, name = "Hellfire Wall", element = Element.Flame, costType = CostType.Mana, cost = 4,
                rarity = Rarity.Common,
                description = "Summon a wall of Flame.", castOnPress = true,
            },
            new()
            {
                id = CardId.ExplosiveEscape, name = "Explosive Escape", element = Element.Flame, costType = CostType.Mana, cost = 2,
                rarity = Rarity.Rare,
                description = "Summon a Flame explosion in front of you. Can be held to increase blast size.",
            },
            new()
            {
                id = CardId.ArcaneJudgment, name = "Arcane Judgment", element = Element.Arcane, costType = CostType.Mana, cost = 10,
                rarity = Rarity.Epic,
                description = "Create a large Arcane circle and destroy everything within it.",
            },
            new()
            {
                id = CardId.Tug, name = "Tug", element = Element.Wind, costType = CostType.Mana, cost = 3,
                rarity = Rarity.Common,
                description = "Send a gust of Wind towards yourself.",
            },
            new()
            {
                id = CardId.SummonPlatform, name = "Summon Platform", element = Element.Ground, costType = CostType.Mana, cost = 3,
                rarity = Rarity.Common,
                description = "Summon a block of Ground beneath you.", castOnPress = true,
            },
            new()
            {
                id = CardId.TheFool, name = "The Fool", element = Element.Tarot, costType = CostType.Mana, cost = 4,
                rarity = Rarity.Epic,
                description = "Generate a free random single-use card from all possible cards.", castOnPress = true,
            },
            new()
            {
                id = CardId.TheMagician, name = "The Magician", element = Element.Tarot, costType = CostType.Mana, cost = 3,
                rarity = Rarity.Rare,
                description = "Double Mana regeneration for a short period of time.", castOnPress = true,
            },
            new()
            {
                id = CardId.TheHighPriestess, name = "The High Priestess", element = Element.Tarot, costType = CostType.Mana, cost = 4,
                rarity = Rarity.Common,
                description = "Blind those looking at you.", castOnPress = true,
            },
            new()
            {
                id = CardId.TheEmpress, name = "The Empress", element = Element.Tarot, costType = CostType.Mana, cost = 7,
                rarity = Rarity.Common,
                description = "Create a domain around you that silences enemies.", castOnPress = true,
            },
            new()
            {
                id = CardId.TheEmperor, name = "The Emperor", element = Element.Tarot, costType = CostType.Mana, cost = 5,
                rarity = Rarity.Common,
                description = "Create an aura around you that reduces damage taken.", castOnPress = true,
            },
            new()
            {
                id = CardId.WaterOfLife, name = "Water of Life", element = Element.Water, costType = CostType.Mana, cost = 7,
                rarity = Rarity.Common,
                description = "After a delay, summon a ball of healing Water. Consuming more than 2 is dangerous.",
            },
            new()
            {
                id = CardId.BubbleAway, name = "Bubble Away", element = Element.Water, costType = CostType.Mana, cost = 5,
                rarity = Rarity.Rare,
                description = "Encase yourself in a Water bubble. Can be cast while in Ragdoll.", castOnPress = true,
            },
            new()
            {
                id = CardId.Tsunami, name = "Tsunami", element = Element.Water, costType = CostType.Mana, cost = 8,
                rarity = Rarity.Rare,
                description = "Summon a tidal wave of Water.",
            },
            new()
            {
                id = CardId.WarpSelf, name = "Warp Self", element = Element.Arcane, costType = CostType.Mana, cost = 3,
                rarity = Rarity.Common,
                description = "Warp to a position within view.",
            },
            new()
            {
                id = CardId.WarpSpace, name = "Warp Space", element = Element.Arcane, costType = CostType.Mana, cost = 4,
                rarity = Rarity.Epic,
                description = "Summon a Warp between two spaces.",
            },
            new()
            {
                id = CardId.ArcaneSwiftness, name = "Arcane Swiftness", element = Element.Arcane, costType = CostType.Mana, cost = 4,
                rarity = Rarity.Common,
                description = "Become invulnerable and swift for a short duration.", castOnPress = true,
            },
            new()
            {
                id = CardId.Severance, name = "Severance", element = Element.Blade, costType = CostType.Mana, cost = 3,
                rarity = Rarity.Common,
                description = "Dash forward and Cut.", castOnPress = true,
            },
            new()
            {
                id = CardId.FlowingCut, name = "Flowing Cut", element = Element.Blade, costType = CostType.Mana, cost = 5,
                rarity = Rarity.Rare,
                description = "Charge a dash that cuts enemies in the path.",
            },
            new()
            {
                id = CardId.Attraction, name = "Attraction", element = Element.Water, costType = CostType.Mana, cost = 4,
                rarity = Rarity.Rare,
                description = "Summon a ball of Water that attracts nearby objects. Can be combined with Repulsion.", castOnPress = true,
            },
            new()
            {
                id = CardId.Repulsion, name = "Repulsion", element = Element.Flame, costType = CostType.Mana, cost = 6,
                rarity = Rarity.Common,
                description = "Summon a ball of Flame that explodes on contact, repelling and burning everything nearby. Can be combined with Attraction.", castOnPress = true,
            },
            new()
            {
                id = CardId.TheHierophant, name = "The Hierophant", element = Element.Tarot, costType = CostType.Mana, cost = 4,
                rarity = Rarity.Rare,
                description = "Create a wall that reflects projectiles.",
            },
            new()
            {
                id = CardId.TheLovers, name = "The Lovers", element = Element.Tarot, costType = CostType.Mana, cost = 1,
                rarity = Rarity.Common,
                description = "Healing beam to restore health. Costs 1 Mana per second.", castOnPress = true,
            },
            new()
            {
                id = CardId.TheChariot, name = "The Chariot", element = Element.Tarot, costType = CostType.Mana, cost = 5,
                rarity = Rarity.Epic,
                description = "Recklessly charge forward.", castOnPress = true,
            },
            new()
            {
                id = CardId.RisingHammer, name = "Rising Hammer", element = Element.Hammer, costType = CostType.Mana, cost = 4,
                rarity = Rarity.Common,
                description = "Dash forward, striking an enemy into the air.", castOnPress = true,
            },
            new()
            {
                id = CardId.DescendingHammer, name = "Descending Hammer", element = Element.Hammer, costType = CostType.Mana, cost = 6,
                rarity = Rarity.Rare,
                description = "Leap into the air and damage a large area upon landing.", castOnPress = true,
            },
            new()
            {
                id = CardId.RockSwarm, name = "Rock Swarm", element = Element.Ground, costType = CostType.Mana, cost = 6,
                rarity = Rarity.Rare,
                description = "Create rotating Ground around you that will deal damage and protect you.", castOnPress = true,
            },
            new()
            {
                id = CardId.IceSpike, name = "Ice Spike", element = Element.Water, costType = CostType.Mana, cost = 5,
                rarity = Rarity.Common,
                description = "Summon a straight path of spikes.",
            },
            new()
            {
                id = CardId.TheHermit, name = "The Hermit", element = Element.Tarot, costType = CostType.Mana, cost = 5,
                rarity = Rarity.Common,
                description = "Summon walls of Ground around you. Can be cast while in Ragdoll.",
            },
            new()
            {
                id = CardId.WheelOfFortune, name = "Wheel of Fortune", element = Element.Tarot, costType = CostType.Mana, cost = 6,
                rarity = Rarity.Epic,
                description = "Something random will happen!", castOnPress = true,
            },
            new()
            {
                id = CardId.Justice, name = "Justice", element = Element.Tarot, costType = CostType.Mana, cost = 3,
                rarity = Rarity.Rare,
                description = "Target a corpse. Gain mana when damaging the killer.", castOnPress = true,
            },
            new()
            {
                id = CardId.TheHangedMan, name = "The Hanged Man", element = Element.Tarot, costType = CostType.Health, cost = 10,
                rarity = Rarity.Common,
                description = "A noose yanks you high into the air. Can be cast while in Ragdoll.", castOnPress = true,
            },
            new()
            {
                id = CardId.Death, name = "Death", element = Element.Tarot, costType = CostType.Mana, cost = 9,
                rarity = Rarity.Common,
                description = "Dash forward, applying Death to enemies hit. Applying Death to an enemy marked with Death causes them to die.", castOnPress = true,
            },
            new()
            {
                id = CardId.Temperance, name = "Temperance", element = Element.Tarot, costType = CostType.Passive, cost = 0,
                rarity = Rarity.Common,
                description = "Increase your maximum mana at the cost of your max health.", castOnPress = true,
            },
            new()
            {
                id = CardId.TheDevil, name = "The Devil", element = Element.Tarot, costType = CostType.Health, cost = 20,
                rarity = Rarity.Common,
                description = "Sacrifice life for mana.", castOnPress = true,
            },
            new()
            {
                id = CardId.TheTower, name = "The Tower", element = Element.Tarot, costType = CostType.Mana, cost = 9,
                rarity = Rarity.Epic,
                description = "Summon a Massive Tower made of Ground to collapse.",
            },
            new()
            {
                id = CardId.UndeadServitude, name = "Undead Servitude", element = Element.Necromancy, costType = CostType.Mana, cost = 8,
                rarity = Rarity.Epic,
                description = "Revive a corpse into an Undead.", castOnPress = true,
            },
            new()
            {
                id = CardId.InfectiousBite, name = "Infectious Bite", element = Element.Necromancy, costType = CostType.Mana, cost = 3,
                rarity = Rarity.Common,
                description = "Lunge forward and bite. Killing an enemy transforms them into an Undead.", castOnPress = true,
            },
            new()
            {
                id = CardId.FlameThrower, name = "Flame Thrower", element = Element.Flame, costType = CostType.Mana, cost = 2,
                rarity = Rarity.Rare,
                description = "Spew out Flames and drain your mana continuously.", castOnPress = true,
            },
            new()
            {
                id = CardId.GrappleHook, name = "Grapple Hook", element = Element.Gun, costType = CostType.Mana, cost = 3,
                rarity = Rarity.Rare,
                description = "Summon a grapple hook that pulls enemies in and pulls you towards surfaces.", castOnPress = true,
            },
            new()
            {
                id = CardId.SummonHorde, name = "Summon Horde", element = Element.Necromancy, costType = CostType.Mana, cost = 8,
                rarity = Rarity.Rare,
                description = "Summon 2 Mindless Undead to your aid.", castOnPress = true,
            },
            new()
            {
                id = CardId.IcyBreath, name = "Icy Breath", element = Element.Water, costType = CostType.Mana, cost = 4,
                rarity = Rarity.Common,
                description = "Turn surfaces into Ice.", castOnPress = true,
            },
            new()
            {
                id = CardId.Strength, name = "Strength", element = Element.Tarot, costType = CostType.Mana, cost = 5,
                rarity = Rarity.Common,
                description = "Become stronger temporarily.", castOnPress = true,
            },
            new()
            {
                id = CardId.TheStar, name = "The Star", element = Element.Tarot, costType = CostType.Mana, cost = 6,
                rarity = Rarity.Rare,
                description = "Throw stars forward. Landing a hit adds a star to your next throw.", castOnPress = true,
            },
            new()
            {
                id = CardId.TheMoon, name = "The Moon", element = Element.Tarot, costType = CostType.Mana, cost = 4,
                rarity = Rarity.Rare,
                description = "Deal damage to heal.", castOnPress = true,
            },
            new()
            {
                id = CardId.TheSun, name = "The Sun", element = Element.Tarot, costType = CostType.Mana, cost = 7,
                rarity = Rarity.Rare,
                description = "Damage those that dare look at you.", castOnPress = true,
            },
            new()
            {
                id = CardId.Judgement, name = "Judgement", element = Element.Tarot, costType = CostType.Mana, cost = 6,
                rarity = Rarity.Rare,
                description = "Create a Tarot circle and flatten everything within it.",
            },
            new()
            {
                id = CardId.TheWorld, name = "The World", element = Element.Tarot, costType = CostType.Mana, cost = 10,
                rarity = Rarity.Secret,
                description = "Encase you and your enemy in The World, and fight to the death.", castOnPress = true,
            },
            new()
            {
                id = CardId.Undying, name = "Undying", element = Element.Necromancy, costType = CostType.Passive, cost = 0,
                rarity = Rarity.Common,
                description = "After dying, revive as an Undead. Your max health is reduced.", castOnPress = true,
            },
            new()
            {
                id = CardId.Rachmaninoff, name = "Rachmaninoff", element = Element.Gun, costType = CostType.Mana, cost = 5,
                rarity = Rarity.Rare,
                description = "Fire a burst of Bullets.", castOnPress = true,
            },
            new()
            {
                id = CardId.Sebastian, name = "Sebastian", element = Element.Gun, costType = CostType.Mana, cost = 7,
                rarity = Rarity.Epic,
                description = "Charge a heavy piercing Bullet.",
            },
            new()
            {
                id = CardId.UndyingGrasp, name = "Undying Grasp", element = Element.Necromancy, costType = CostType.Mana, cost = 4,
                rarity = Rarity.Common,
                description = "Summon a line of hands that hold your enemies in place.",
            },
            new()
            {
                id = CardId.Skinwalker, name = "Skinwalker", element = Element.Necromancy, costType = CostType.Mana, cost = 0,
                rarity = Rarity.Epic,
                description = "Take over the identity of a corpse.", castOnPress = true,
            },
            new()
            {
                id = CardId.RemoteFlameBomb, name = "Remote Flame Bomb", element = Element.Flame, costType = CostType.Mana, cost = 3,
                rarity = Rarity.Common,
                description = "Place a Flame bomb. Can be activated with Remote Detonation.",
            },
            new()
            {
                id = CardId.RemoteDetonation, name = "Remote Detonation", element = Element.Flame, costType = CostType.Mana, cost = 1,
                rarity = Rarity.Common, companion = true,
                description = "Detonate all of your Flame bombs.", castOnPress = true,
            },
            new()
            {
                id = CardId.FlameBombToss, name = "Flame Bomb Toss", element = Element.Flame, costType = CostType.Mana, cost = 9,
                rarity = Rarity.Common,
                description = "Toss out 4 Flame bombs in front of you. Can be activated with Remote Detonation.", castOnPress = true,
            },
            new()
            {
                id = CardId.NecromaticBlast, name = "Necromatic Blast", element = Element.Necromancy, costType = CostType.Mana, cost = 3,
                rarity = Rarity.Common,
                description = "Launch a corrupted chunk that heals you on hit.", castOnPress = true,
            },
            new()
            {
                id = CardId.ShatterHex, name = "Shatter Hex", element = Element.Hex, costType = CostType.Mana, cost = 3,
                rarity = Rarity.Common,
                description = "Apply Shatter Hex to an enemy. Dealing damage will Shatter them.", castOnPress = true,
            },
            new()
            {
                id = CardId.StasisHex, name = "Stasis Hex", element = Element.Hex, costType = CostType.Mana, cost = 3,
                rarity = Rarity.Common,
                description = "Apply Stasis Hex to an enemy. Dealing damage will Stasis them.", castOnPress = true,
            },
            new()
            {
                id = CardId.MindHex, name = "Mind Hex", element = Element.Hex, costType = CostType.Mana, cost = 7,
                rarity = Rarity.Rare,
                description = "Apply Mind Hex to an enemy. Staying nearby grants control of their body.", castOnPress = true,
            },
            new()
            {
                id = CardId.SmallifyHex, name = "Smallify Hex", element = Element.Hex, costType = CostType.Mana, cost = 5,
                rarity = Rarity.Common,
                description = "Apply Smallify Hex to an enemy. Staying nearby makes them small.", castOnPress = true,
            },
            new()
            {
                id = CardId.Sunflower, name = "Sunflower", element = Element.Botany, costType = CostType.Mana, cost = 6,
                rarity = Rarity.Common,
                description = "Summon a sunflower that releases a powerful beam.", castOnPress = true,
            },
            new()
            {
                id = CardId.Cactus, name = "Cactus", element = Element.Botany, costType = CostType.Mana, cost = 3,
                rarity = Rarity.Common,
                description = "Summon a cactus that hurts enemies on touch.", castOnPress = true,
            },
            new()
            {
                id = CardId.FlyTrap, name = "Fly Trap", element = Element.Botany, costType = CostType.Mana, cost = 5,
                rarity = Rarity.Rare,
                description = "Summon a fly trap that consumes an enemy. Cast again to throw.", castOnPress = true,
            },
            new()
            {
                id = CardId.CompressedShot, name = "Compressed Shot", element = Element.Wind, costType = CostType.Mana, cost = 5,
                rarity = Rarity.Common,
                description = "Channel the air around you to fire a high powered Wind shot. Headshots deal increased damage.",
            },
            new()
            {
                id = CardId.Ephemerate, name = "Ephemerate", element = Element.Wind, costType = CostType.Mana, cost = 4,
                rarity = Rarity.Rare,
                description = "Briefly phase out of existence, then reappear.", castOnPress = true,
            },
            new()
            {
                id = CardId.CloudStep, name = "Cloud Step", element = Element.Wind, costType = CostType.Mana, cost = 2,
                rarity = Rarity.Common,
                description = "Perform a leap.", castOnPress = true,
            },
            new()
            {
                id = CardId.Submerge, name = "Submerge", element = Element.Water, costType = CostType.Mana, cost = 5,
                rarity = Rarity.Epic,
                description = "Hold to dive into the ground and regenerate health. Release to ambush your enemies.", castOnPress = true,
            },
            new()
            {
                id = CardId.BubbleBomb, name = "Bubble Bomb", element = Element.Water, costType = CostType.Mana, cost = 2,
                rarity = Rarity.Common,
                description = "Create a proximity explosive bubble.",
            },
            new()
            {
                id = CardId.HammerSpin, name = "Hammer Spin", element = Element.Hammer, costType = CostType.Mana, cost = 5,
                rarity = Rarity.Common,
                description = "Spin in a direction with a hammer.",
            },
            new()
            {
                id = CardId.Wolfgang, name = "Wolfgang", element = Element.Gun, costType = CostType.Mana, cost = 7,
                rarity = Rarity.Common,
                description = "Continuously fire Bullets.", castOnPress = true,
            },
            new()
            {
                id = CardId.HeavenlySlash, name = "Heavenly Slash", element = Element.Blade, costType = CostType.Mana, cost = 8,
                rarity = Rarity.Rare,
                description = "Leap into the air, then send out a devastating cut.", castOnPress = true,
            },
            new()
            {
                id = CardId.Parry, name = "Parry", element = Element.Blade, costType = CostType.Mana, cost = 6,
                rarity = Rarity.Common,
                description = "Deflect an attack and retaliate.", castOnPress = true,
            },
            new()
            {
                id = CardId.SpeedDemon, name = "Panic Winds", element = Element.Wind, costType = CostType.Passive, cost = 0,
                rarity = Rarity.Common,
                description = "Gain speed as you lose health. Lose max shield health.", castOnPress = true,
            },
        };

        /// <summary>The card that comes along with this one (None: nothing does): any card that places flame bombs brings Remote Detonation.</summary>
        public static CardId CompanionOf(CardId id) => id switch
        {
            CardId.RemoteFlameBomb or CardId.FlameBombToss => CardId.RemoteDetonation,
            _ => CardId.None,
        };

        /// <summary>
        /// Where a card sits in the inventory within its element: the Tarot by their arcana number (Strength, VIII, after The
        /// Chariot, VII), everything else in the order it was added.
        /// </summary>
        public static int Order(CardId id) => id switch
        {
            CardId.TheFool => 0, CardId.TheMagician => 1, CardId.TheHighPriestess => 2, CardId.TheEmpress => 3, CardId.TheEmperor => 4,
            CardId.TheHierophant => 5, CardId.TheLovers => 6, CardId.TheChariot => 7, CardId.Strength => 8, CardId.TheHermit => 9,
            CardId.WheelOfFortune => 10, CardId.Justice => 11, CardId.TheHangedMan => 12, CardId.Death => 13, CardId.Temperance => 14,
            CardId.TheDevil => 15, CardId.TheTower => 16, CardId.TheStar => 17, CardId.TheMoon => 18, CardId.TheSun => 19,
            CardId.Judgement => 20, CardId.TheWorld => 21,
            _ => 100 + (int)id,
        };

        /// <summary>Temperance and other cards that are never cast: they work just by being in the loadout.</summary>
        public static bool IsPassive(CardId id) => Get(id)?.costType == CostType.Passive;


        /// <summary>How many of a card are in these five slots.</summary>
        public static int CountOf(EquippedCards e, CardId id)
        {
            int n = 0;
            for (int i = 0; i < Loadout.Slots; i++) if (e[i] == id) n++;
            return n;
        }

        /// <summary>How many Temperance cards are in these five slots.</summary>
        public static int TemperanceCount(EquippedCards e) => CountOf(e, CardId.Temperance);

        /// <summary>
        /// How much a loadout's passive cards change its owner's maximum health: each Temperance and each Undying takes some.
        /// </summary>
        public static float HealthBonus(EquippedCards e) =>
            -SpellPlans.TemperanceHealth * CountOf(e, CardId.Temperance) - SpellPlans.UndyingHealth * CountOf(e, CardId.Undying);

        /// <summary>Attraction and Repulsion: cast together (within <see cref="SpellPlans.FuseWindow"/>), they fuse.</summary>
        public static bool Fuses(CardId a, CardId b) =>
            a == CardId.Attraction && b == CardId.Repulsion || a == CardId.Repulsion && b == CardId.Attraction;

        /// <summary>
        /// Seconds the caster is busy casting this card, counted from the cast (key release, or press for press cards).
        /// No other card can be cast until it's over. Being knocked down (ragdolled, caught by a tornado) during it
        /// stops the spell: whatever hasn't left the caster's hands yet never happens.
        /// Cards whose effect appears the moment they're cast only lock for their short gesture.
        /// </summary>
        public static float CastTime(CardId id) => id switch
        {
            CardId.SummonWall => SpellPlans.WallRise,
            CardId.RockBarrage => SpellPlans.RockGather + SpellPlans.RockHold, // gathered, held, thrown
            CardId.FoldingHands => 0.2f,
            CardId.Shove => 0.3f,
            CardId.Tornado => 0.4f,
            CardId.NomadicDash => SpellPlans.DashTime,
            CardId.ManaMissiles => SpellPlans.MissileCharge,                   // the orb charges in your hand
            CardId.MagicalBarrier => SpellPlans.BarrierWindup,
            CardId.ManaSiphon => SpellPlans.SiphonArm,
            CardId.BallOfFlame => SpellPlans.FireballCharge,                   // the ball forms, then it's thrown
            CardId.HellfireWall => SpellPlans.HellfireDraw,                    // drawing the line
            CardId.ExplosiveEscape => SpellPlans.EscapeDelay,                  // the clap
            CardId.ArcaneJudgment => SpellPlans.JudgmentDraw,                  // drawing the circle
            CardId.Tug => 0.3f,
            CardId.SummonPlatform => 0.3f,
            CardId.TheFool => 0.3f,
            CardId.TheMagician => 0.3f,
            CardId.TheHighPriestess => SpellPlans.PriestessDelay,              // the sun, until the flash
            CardId.TheEmpress or CardId.TheEmperor => SpellPlans.DomainDelay,  // hands together
            CardId.WaterOfLife => 0.6f,
            CardId.BubbleAway => SpellPlans.BubbleWindup,
            CardId.Tsunami => SpellPlans.TsunamiWindup,                        // hands back, shoved forward
            CardId.WarpSelf => SpellPlans.WarpCharge,                          // hands together, pulled apart
            CardId.WarpSpace => 0f,                                            // the second portal can follow at once
            CardId.ArcaneSwiftness => SpellPlans.SwiftPrep,                    // crouched, gathering
            CardId.Severance => SpellPlans.SeveranceTime + 0.3f,               // the rush, then the cut
            CardId.Attraction or CardId.Repulsion => SpellPlans.OrbRaise + SpellPlans.ForceLife, // steering the ball the whole time
            CardId.FlowingCut => SpellPlans.FlowTime + 0.3f,                   // the teleport, then the strike pose
            CardId.TheHierophant => SpellPlans.HierophantDelay,                // palms out until the wall appears
            CardId.TheLovers => 0.3f,                                          // the throw (the beam then runs on its own)
            CardId.TheChariot => SpellPlans.ChariotMaxTime + 1f,               // the whole charge (ended early when it stops)
            CardId.RisingHammer => SpellPlans.RisingTime + 0.3f,               // the rush, then the upswing
            CardId.DescendingHammer => SpellPlans.HammerCrouch + 0.1f,         // the crouch (the leap itself keeps you busy until you land)
            CardId.RockSwarm => SpellPlans.SwarmGather,                        // hands up while the rocks are torn out
            CardId.IceSpike => SpellPlans.IceCrouch,                           // crouched, hands on the ground
            CardId.TheHermit => 0f,                                            // (the house goes up at once)
            CardId.WheelOfFortune => SpellPlans.WheelBusy,                     // the die is over your head
            CardId.Justice => SpellPlans.JusticeCast,                          // down on one knee over the corpse
            CardId.TheHangedMan => SpellPlans.HangedDrop + SpellPlans.HangedYank, // the noose coming down, then the yank
            CardId.Death => SpellPlans.DeathArm,                               // the fist drawn back (the charge then keeps you busy)
            CardId.Temperance => 0f,
            CardId.SpeedDemon => 0f,
            CardId.TheDevil => SpellPlans.DevilRaise,                          // the right hand goes up
            CardId.TheTower => SpellPlans.TowerPray,                           // kneeling in prayer
            CardId.UndeadServitude => SpellPlans.ReviveTime,                   // crouched over the corpse
            CardId.InfectiousBite => SpellPlans.BiteCrouch,                    // the crouch before the lunge
            CardId.FlameThrower => SpellPlans.FlameStart,                                       // (held: no other card while it spews)
            CardId.GrappleHook => SpellPlans.HookCast,                         // the hand points
            CardId.SummonHorde => SpellPlans.HordeCrouch,                      // hands out over the ground
            CardId.IcyBreath => SpellPlans.BreathLean + SpellPlans.IceBreathTime, // leaning in, then breathing
            CardId.Strength => SpellPlans.StrengthFlex,                        // flexing
            CardId.TheStar => SpellPlans.StarRaise,                            // the right hand goes up
            CardId.TheMoon => SpellPlans.MoonClasp,                            // hands clasped (the moon then fills on its own)
            CardId.TheSun => SpellPlans.SunRaise,                              // hands up (the rest comes on its own)
            CardId.Judgement => SpellPlans.TarotJudgmentDraw,                  // writing the circle
            CardId.TheWorld => SpellPlans.WorldClasp,                          // hands clasped
            CardId.Undying => 0f,
            CardId.Rachmaninoff => SpellPlans.RachDelay + 0.1f,                // the shotgun comes out, then fires
            CardId.Sebastian => 0f,                                            // (charged as long as it's held: busy meanwhile)
            CardId.UndyingGrasp => SpellPlans.GraspCast,                       // down on one knee
            CardId.Skinwalker => SpellPlans.SkinTime,                          // down on one knee over the corpse
            CardId.RemoteFlameBomb => SpellPlans.BombPlace,                    // the right hand puts it there
            CardId.RemoteDetonation => SpellPlans.DetonateTime,                // the left hand goes up
            CardId.FlameBombToss => SpellPlans.TossTime,                       // both hands thrown forward
            CardId.NecromaticBlast => SpellPlans.NecroCharge,                  // the ball gathers, then it's thrown
            CardId.ShatterHex or CardId.StasisHex or CardId.MindHex or CardId.SmallifyHex => SpellPlans.HexCast, // the left hand points
            CardId.Sunflower or CardId.Cactus or CardId.FlyTrap => SpellPlans.PotGrow, // the pot phases in, the plant grows (then held: no other card meanwhile)
            CardId.CompressedShot => 0.2f,                                     // the flick of the hand (the charge was held before)
            CardId.Ephemerate => SpellPlans.EphemTime,                         // gone
            CardId.CloudStep => 0.15f,
            CardId.Submerge => SpellPlans.SubDive,                             // diving in (then under: no other card meanwhile)
            CardId.BubbleBomb => 0.3f,
            CardId.HammerSpin => 0.2f,                                         // (the spin itself keeps you busy: the server times it by its length)
            CardId.Wolfgang => SpellPlans.WolfDraw + SpellPlans.WolfFireTime,  // drawn, then firing
            CardId.HeavenlySlash => SpellPlans.HeavenRelease + 0.2f,           // the crouch, the leap, the pose, the cut
            CardId.Parry => SpellPlans.ParryWindow,                            // on guard (cut short if it deflects)
            _ => 0.3f,
        };

        static Dictionary<CardId, CardInfo> _byId;

        public static CardInfo Get(CardId id)
        {
            if (_byId == null)
            {
                _byId = new Dictionary<CardId, CardInfo>();
                foreach (var c in All) _byId[c.id] = c;
            }
            return _byId.TryGetValue(id, out var info) ? info : null;
        }

        public static bool IsValid(int id) => id > 0 && Get((CardId)id) != null;

        public static Color ElementColor(Element e) => e switch
        {
            Element.Flame => new Color(0.74f, 0.29f, 0.27f), // a softer brick red (pure red was hard on the eyes)
            Element.Arcane => new Color(0.3f, 0.48f, 0.92f),
            Element.Lightning => new Color(0.98f, 0.85f, 0.25f),
            Element.Wind => new Color(0.68f, 0.7f, 0.72f),
            Element.Tarot => new Color(0.75f, 0.33f, 0.2f), // mahogany
            Element.Water => new Color(0.3f, 0.78f, 0.86f), // cyan / aqua
            Element.Weapon => new Color(0.87f, 0.9f, 0.95f), // steel silver-white
            Element.Necromancy => new Color(0.56f, 0.72f, 0.36f), // sickly green
            Element.Hex => new Color(0.58f, 0.36f, 0.82f), // purple
            Element.Botany => new Color(0.32f, 0.76f, 0.3f), // leaf green
            Element.Blade => new Color(0.87f, 0.9f, 0.95f), // steel silver-white
            Element.Gun => new Color(0.5f, 0.53f, 0.6f), // gunmetal
            Element.Hammer => new Color(0.66f, 0.6f, 0.56f), // iron
            _ => new Color(0.82f, 0.68f, 0.5f), // Ground: light brown
        };

        public static string ElementName(Element e) => e.ToString();

        /// <summary>Card border colour: grey for Starter and Common, blue for Rare, purple for Epic, black for Secret.</summary>
        public static Color RarityColor(Rarity r) => r switch
        {
            Rarity.Rare => new Color(0.2f, 0.55f, 1f),
            Rarity.Epic => new Color(0.68f, 0.3f, 0.95f),
            Rarity.Secret => Color.black,
            _ => new Color(0.6f, 0.62f, 0.66f),
        };

        /// <summary>Starter counts as Common in packs.</summary>
        public static Rarity PackTier(Rarity r) => r == Rarity.Starter ? Rarity.Common : r;
    }
}
