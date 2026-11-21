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
    }

    /// <summary>Saved with cards: append new elements at the end, never reorder.</summary>
    public enum Element : byte { Flame, Arcane, Lightning, Wind, Ground, Tarot, Water }

    /// <summary>What a card costs to cast (shown top right: a bubble for mana, a heart for health).</summary>
    public enum CostType : byte { Mana, Health }

    public class CardInfo
    {
        public CardId id;
        public string name;
        public Element element;
        public CostType costType;
        public int cost;
        public string description;
        /// <summary>True: casts the moment the key goes down. False: hold to aim (indicator), cast on release.</summary>
        public bool castOnPress;
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
                description = "Summon a wall of Ground in front of you.", symbol = CardSymbols.SummonWall,
            },
            new()
            {
                id = CardId.RockBarrage, name = "Rock Barrage", element = Element.Ground, costType = CostType.Mana, cost = 2,
                description = "Gather a chunk of Ground and throw it.", symbol = CardSymbols.RockBarrage, castOnPress = true,
            },
            new()
            {
                id = CardId.FoldingHands, name = "Folding Hands", element = Element.Ground, costType = CostType.Mana, cost = 5,
                description = "Fold two chunks of Ground together.", symbol = CardSymbols.FoldingHands,
            },
            new()
            {
                id = CardId.Shove, name = "Shove", element = Element.Wind, costType = CostType.Mana, cost = 3,
                description = "Send a gust of Wind forward.", symbol = CardSymbols.Shove,
            },
            new()
            {
                id = CardId.Tornado, name = "Tornado", element = Element.Wind, costType = CostType.Mana, cost = 6,
                description = "Summon a tornado of Wind. After a brief delay, send it forward.", symbol = CardSymbols.Tornado,
            },
            new()
            {
                id = CardId.NomadicDash, name = "Nomadic Dash", element = Element.Wind, costType = CostType.Mana, cost = 2,
                description = "Dash in the direction you are moving.", symbol = CardSymbols.NomadicDash, castOnPress = true,
            },
            new()
            {
                id = CardId.ManaMissiles, name = "Mana Missiles", element = Element.Arcane, costType = CostType.Mana, cost = 4,
                description = "Launch tracking Arcane missiles at a targeted enemy.", symbol = CardSymbols.ManaMissiles, castOnPress = true,
            },
            new()
            {
                id = CardId.MagicalBarrier, name = "Magical Barrier", element = Element.Arcane, costType = CostType.Mana, cost = 4,
                description = "Summon a Barrier around yourself.", symbol = CardSymbols.MagicalBarrier, castOnPress = true,
            },
            new()
            {
                id = CardId.ManaSiphon, name = "Mana Siphon", element = Element.Arcane, costType = CostType.Mana, cost = 2,
                description = "Dash forward, striking and siphoning mana from enemies struck.", symbol = CardSymbols.ManaSiphon, castOnPress = true,
            },
            new()
            {
                id = CardId.BallOfFlame, name = "Ball of Flame", element = Element.Flame, costType = CostType.Mana, cost = 4,
                description = "Summon a ball of Flame that explodes on impact.", castOnPress = true,
            },
            new()
            {
                id = CardId.HellfireWall, name = "Hellfire Wall", element = Element.Flame, costType = CostType.Mana, cost = 5,
                description = "Summon a wall of Flame.", castOnPress = true,
            },
            new()
            {
                id = CardId.ExplosiveEscape, name = "Explosive Escape", element = Element.Flame, costType = CostType.Mana, cost = 3,
                description = "Summon a Flame explosion in front of you, propelling yourself away.", castOnPress = true,
            },
            new()
            {
                id = CardId.ArcaneJudgment, name = "Arcane Judgment", element = Element.Arcane, costType = CostType.Mana, cost = 10,
                description = "Create a large Arcane circle and destroy everything within it.",
            },
            new()
            {
                id = CardId.Tug, name = "Tug", element = Element.Wind, costType = CostType.Mana, cost = 3,
                description = "Send a gust of Wind towards yourself.",
            },
            new()
            {
                id = CardId.SummonPlatform, name = "Summon Platform", element = Element.Ground, costType = CostType.Mana, cost = 3,
                description = "Summon a block of Ground beneath you.", castOnPress = true,
            },
            new()
            {
                id = CardId.TheFool, name = "The Fool", element = Element.Tarot, costType = CostType.Mana, cost = 4,
                description = "Generate a free random single-use card from all possible cards.", castOnPress = true,
            },
            new()
            {
                id = CardId.TheMagician, name = "The Magician", element = Element.Tarot, costType = CostType.Mana, cost = 3,
                description = "Double Mana regeneration for a short period of time.", castOnPress = true,
            },
            new()
            {
                id = CardId.TheHighPriestess, name = "The High Priestess", element = Element.Tarot, costType = CostType.Mana, cost = 4,
                description = "Blind those looking at you.", castOnPress = true,
            },
            new()
            {
                id = CardId.TheEmpress, name = "The Empress", element = Element.Tarot, costType = CostType.Mana, cost = 8,
                description = "Create a domain around you that restrains enemies.", castOnPress = true,
            },
            new()
            {
                id = CardId.TheEmperor, name = "The Emperor", element = Element.Tarot, costType = CostType.Mana, cost = 6,
                description = "Create an aura around you that reduces damage taken.", castOnPress = true,
            },
            new()
            {
                id = CardId.WaterOfLife, name = "Water of Life", element = Element.Water, costType = CostType.Mana, cost = 5,
                description = "After a delay, summon a ball of healing Water.",
            },
            new()
            {
                id = CardId.BubbleAway, name = "Bubble Away", element = Element.Water, costType = CostType.Mana, cost = 5,
                description = "Encase yourself in a Water bubble.", castOnPress = true,
            },
            new()
            {
                id = CardId.Tsunami, name = "Tsunami", element = Element.Water, costType = CostType.Mana, cost = 8,
                description = "Summon a tidal wave of Water.",
            },
            new()
            {
                id = CardId.WarpSelf, name = "Warp Self", element = Element.Arcane, costType = CostType.Mana, cost = 3,
                description = "Warp to a position within view.",
            },
            new()
            {
                id = CardId.WarpSpace, name = "Warp Space", element = Element.Arcane, costType = CostType.Mana, cost = 5,
                description = "Summon a Warp between two spaces.",
            },
            new()
            {
                id = CardId.ArcaneSwiftness, name = "Arcane Swiftness", element = Element.Arcane, costType = CostType.Mana, cost = 4,
                description = "Become invulnerable and swift for a short duration.", castOnPress = true,
            },
        };

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
            CardId.FoldingHands => 0.4f,
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
            Element.Flame => new Color(0.86f, 0.25f, 0.2f),
            Element.Arcane => new Color(0.3f, 0.48f, 0.92f),
            Element.Lightning => new Color(0.98f, 0.85f, 0.25f),
            Element.Wind => new Color(0.68f, 0.7f, 0.72f),
            Element.Tarot => new Color(0.75f, 0.33f, 0.2f), // mahogany
            Element.Water => new Color(0.3f, 0.78f, 0.86f), // cyan / aqua
            _ => new Color(0.82f, 0.68f, 0.5f), // Ground: light brown
        };

        public static string ElementName(Element e) => e.ToString();
    }
}
