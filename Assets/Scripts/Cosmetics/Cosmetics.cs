using System;
using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>Saved and sent over the network: keep the numbers stable. A cosmetic's prefab is Resources/Cosmetics/(its enum name).</summary>
    public enum CosmeticId : byte
    {
        None = 0, Shell = 1, TopHat = 2, LongHair = 3, SoftHat = 4, RiceHat = 5, Charm = 6, Glasses = 7, PuffHair = 8, Goggles = 9,
        Tie = 10, FlatHat = 11, Whiskers = 12, ChefHat = 13, Belt = 14, Hair = 15, BunnyEars = 16, WizardHat = 17, WitchHat = 18,
    }

    public class CosmeticInfo
    {
        public CosmeticId id;
        public string name;
        public Rarity rarity;
    }

    /// <summary>Every cosmetic, and the colours and sizes one can roll. Purely for show: nothing here touches hitboxes.</summary>
    public static class Cosmetics
    {
        public static readonly CosmeticInfo[] All =
        {
            new() { id = CosmeticId.Shell, name = "Shell", rarity = Rarity.Common },
            new() { id = CosmeticId.TopHat, name = "Top Hat", rarity = Rarity.Common },
            new() { id = CosmeticId.LongHair, name = "Long Hair", rarity = Rarity.Common },
            new() { id = CosmeticId.SoftHat, name = "Soft Hat", rarity = Rarity.Common },
            new() { id = CosmeticId.RiceHat, name = "Rice Hat", rarity = Rarity.Common },
            new() { id = CosmeticId.Charm, name = "Charm", rarity = Rarity.Common },
            new() { id = CosmeticId.Glasses, name = "Glasses", rarity = Rarity.Common },
            new() { id = CosmeticId.PuffHair, name = "Puff Hair", rarity = Rarity.Common },
            new() { id = CosmeticId.Goggles, name = "Goggles", rarity = Rarity.Rare },
            new() { id = CosmeticId.Tie, name = "Tie", rarity = Rarity.Common },
            new() { id = CosmeticId.FlatHat, name = "Flat Hat", rarity = Rarity.Common },
            new() { id = CosmeticId.Whiskers, name = "Whiskers", rarity = Rarity.Common },
            new() { id = CosmeticId.ChefHat, name = "Chef Hat", rarity = Rarity.Common },
            new() { id = CosmeticId.Belt, name = "Belt", rarity = Rarity.Common },
            new() { id = CosmeticId.Hair, name = "Hair", rarity = Rarity.Common },
            new() { id = CosmeticId.BunnyEars, name = "Bunny Ears", rarity = Rarity.Epic },
            new() { id = CosmeticId.WizardHat, name = "Wizard Hat", rarity = Rarity.Common },
            new() { id = CosmeticId.WitchHat, name = "Witch Hat", rarity = Rarity.Rare },
        };

        public static CosmeticInfo Get(CosmeticId id)
        {
            foreach (var c in All) if (c.id == id) return c;
            return null;
        }

        // ---- the colours (saved and sent by index: only add at the end)

        public static readonly (string name, Color colour)[] Palette =
        {
            ("Crimson", Hex(0xD21F3C)), ("Scarlet", Hex(0xFF3B21)), ("Coral", Hex(0xFF7F6A)), ("Tangerine", Hex(0xFF8A1F)),
            ("Amber", Hex(0xFFB81C)), ("Lemon", Hex(0xFFE94A)), ("Lime", Hex(0xA8E22F)), ("Emerald", Hex(0x1FB65C)),
            ("Forest", Hex(0x2C6B3A)), ("Mint", Hex(0x9CF0C4)), ("Teal", Hex(0x16A096)), ("Turquoise", Hex(0x3FE0D8)),
            ("Sky", Hex(0x74C8FF)), ("Azure", Hex(0x2389FF)), ("Cobalt", Hex(0x2346C8)), ("Indigo", Hex(0x4B2FA8)),
            ("Violet", Hex(0x8B45E6)), ("Lavender", Hex(0xC9A7FF)), ("Magenta", Hex(0xE52FC0)), ("Rose", Hex(0xFF8FB8)),
            ("Chocolate", Hex(0x7A4526)), ("Ivory", Hex(0xFFF6E0)), ("Silver", Hex(0xAEB8C4)), ("Onyx", Hex(0x2A2A33)),
        };

        static Color Hex(int rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);

        public static Color Colour(int i) => Palette[Mathf.Clamp(i, 0, Palette.Length - 1)].colour;
        public static string ColourName(int i) => Palette[Mathf.Clamp(i, 0, Palette.Length - 1)].name;

        // ---- sizes: kept as a byte, 1x .. 1.5x in steps of 0.005 (100 is exactly 1x; nothing is smaller)

        public const byte SizeSteps = 200, Regular = 100;

        public static float Scale(byte size) => 0.5f + Mathf.Clamp(size, Regular, SizeSteps) / (float)SizeSteps;
        public static string SizeName(byte size) => size > Regular ? "Large" : "Regular";
    }

    /// <summary>One cosmetic as it came out of a crate.</summary>
    [Serializable]
    public class CosmeticItem
    {
        public int uid;
        public CosmeticId id;
        public byte colour;
        public byte size = Cosmetics.Regular;

        public CosmeticInfo Info => Cosmetics.Get(id);
        public Color Colour => Cosmetics.Colour(colour);
        public float Scale => Cosmetics.Scale(size);
        public string Name => Cosmetics.SizeName(size) + " " + Cosmetics.ColourName(colour) + " " + (Info != null ? Info.name : "");
    }

    /// <summary>A cosmetic being worn: which bone it hangs off and where, in that bone's own space (so it moves and grows with it).</summary>
    [Serializable]
    public class WornItem
    {
        public int uid;
        public byte bone;
        public Vector3 pos;
        public Quaternion rot = Quaternion.identity;
    }

    /// <summary>Opening crates: one cosmetic, its colour and its size.</summary>
    public static class Crates
    {
        public const float EpicChance = 0.05f, RareChance = 0.15f; // the rest Common
        public const float LargeChance = 0.05f;
        /// <summary>A large one is at least this much bigger (so it shows), up to half as big again.</summary>
        const int LeastStep = 20, MostStep = 100; // 0.1 .. 0.5, in size steps

        public static bool CanAfford => Wallet.Gold >= Rewards.CrateCost;

        /// <summary>Buys a crate: takes the gold and adds what's in it to the wardrobe right away. Null (nothing taken) when you can't afford it.</summary>
        public static CosmeticItem Open()
        {
            if (!Wallet.Spend(Rewards.CrateCost)) return null;
            var item = Roll();
            Wardrobe.Add(item);
            return item;
        }

        /// <summary>What a crate might have in it (also the reel's other tiles).</summary>
        public static CosmeticItem Roll()
        {
            float r = UnityEngine.Random.value;
            var rarity = r < EpicChance ? Rarity.Epic : r < EpicChance + RareChance ? Rarity.Rare : Rarity.Common;
            var pool = new List<CosmeticInfo>();
            foreach (var c in Cosmetics.All) if (c.rarity == rarity) pool.Add(c);
            bool large = UnityEngine.Random.value < LargeChance;
            int step = UnityEngine.Random.Range(LeastStep, MostStep + 1);
            return new CosmeticItem
            {
                id = pool[UnityEngine.Random.Range(0, pool.Count)].id,
                colour = (byte)UnityEngine.Random.Range(0, Cosmetics.Palette.Length),
                size = (byte)(large ? Cosmetics.Regular + step : Cosmetics.Regular),
            };
        }
    }
}
