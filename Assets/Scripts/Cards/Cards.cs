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
    }

    public enum Element : byte { Flame, Arcane, Lightning, Wind, Ground, Tarot }

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
            _ => new Color(0.82f, 0.68f, 0.5f), // Ground: light brown
        };

        public static string ElementName(Element e) => e.ToString();
    }
}
