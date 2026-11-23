using System;
using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>Gold amounts: what things pay and what a pack costs.</summary>
    public static class Rewards
    {
        public const int Kill = 20;      // knocking out another player
        public const int RoundWin = 50;  // last one standing
        public const int Death = 10;     // dying in a round
        public const int FirstRound = 100; // once ever, at the end of your first round (with a nudge to the shop)
        public const int Admin = 100;    // the admin panel's gold button (for testing)
        public const int PackCost = 100;
        public const int PackSize = 3;
    }

    /// <summary>The player's gold, saved on this PC (like the card collection).</summary>
    public static class Wallet
    {
        const string PrefKey = "sme_gold";
        static int _gold = -1;

        /// <summary>Gold changed (earned or spent).</summary>
        public static event Action Changed;
        /// <summary>Gold was earned (amount) — for the "+20" pop-up.</summary>
        public static event Action<int> Earned;

        public static int Gold
        {
            get
            {
                if (_gold < 0) _gold = Mathf.Max(0, PlayerPrefs.GetInt(PrefKey, 0));
                return _gold;
            }
        }

        public static void Earn(int amount)
        {
            if (amount <= 0) return;
            Set(Gold + amount);
            Earned?.Invoke(amount);
        }

        /// <summary>Takes the gold if there's enough. Returns false (and takes nothing) when there isn't.</summary>
        public static bool Spend(int amount)
        {
            if (amount < 0 || Gold < amount) return false;
            Set(Gold - amount);
            return true;
        }

        static void Set(int v)
        {
            _gold = Mathf.Max(0, v);
            PlayerPrefs.SetInt(PrefKey, _gold);
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        // ---- first round: a one-time bonus, then a nudge to the shop until it's opened

        const string FirstRoundKey = "sme_first_round_paid", ShopHintKey = "sme_shop_hint";

        /// <summary>The first-round bonus was just paid (show the "go to the shop" nudge).</summary>
        public static event Action FirstRoundPaid;

        /// <summary>A round you played in just ended: pays <see cref="Rewards.FirstRound"/> the first time ever (saved on this PC).</summary>
        public static void RoundPlayed()
        {
            if (PlayerPrefs.GetInt(FirstRoundKey, 0) != 0) return;
            PlayerPrefs.SetInt(FirstRoundKey, 1);
            PlayerPrefs.SetInt(ShopHintKey, 1);
            Earn(Rewards.FirstRound);
            FirstRoundPaid?.Invoke();
        }

        /// <summary>Admin panel (fresh inventory): the next round you play pays the first-round bonus and nudge again.</summary>
        public static void ResetFirstRound()
        {
            PlayerPrefs.DeleteKey(FirstRoundKey);
            PlayerPrefs.DeleteKey(ShopHintKey);
            PlayerPrefs.Save();
        }

        /// <summary>The "go to the shop" nudge is still owed (cleared the first time the shop opens).</summary>
        public static bool ShopHint
        {
            get => PlayerPrefs.GetInt(ShopHintKey, 0) != 0;
            set
            {
                if (ShopHint == value) return;
                PlayerPrefs.SetInt(ShopHintKey, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }
    }

    /// <summary>One card out of a pack, and the one that came along with it (None: nothing, or you have it already).</summary>
    public struct PackCard { public CardId id, with; }

    public static class Packs
    {
        /// <summary>Odds per card: Epic 5%, Rare 20%, the rest Common (Starter cards count as Common).</summary>
        public const float EpicChance = 0.05f, RareChance = 0.20f;

        /// <summary>Enough gold, and there's still a card you don't own.</summary>
        public static bool CanAfford => Wallet.Gold >= Rewards.PackCost && Collection.Unowned().Count > 0;

        /// <summary>
        /// Buys and opens a pack: takes the gold, picks <see cref="Rewards.PackSize"/> different cards you don't own
        /// yet (fewer if that's all that's left) and adds them to the collection right away, so closing the shop
        /// mid-reveal loses nothing. Returns null (and takes nothing) when you can't afford it or own everything.
        /// Each card rolls its rarity (<see cref="EpicChance"/>, <see cref="RareChance"/>, else Common); every pack
        /// has at least one Rare or better. A tier with nothing left to give is dropped and the odds re-rolled
        /// over the tiers that still have cards.
        /// </summary>
        public static PackCard[] Open()
        {
            var unowned = Collection.Unowned();
            if (unowned.Count == 0 || !Wallet.Spend(Rewards.PackCost)) return null;

            var pools = new Dictionary<Rarity, List<CardId>>
            {
                { Rarity.Common, new List<CardId>() }, { Rarity.Rare, new List<CardId>() }, { Rarity.Epic, new List<CardId>() },
            };
            foreach (var id in unowned) pools[Cards.PackTier(Cards.Get(id).rarity)].Add(id);

            var cards = new CardId[Mathf.Min(Rewards.PackSize, unowned.Count)];
            bool gotRare = false;
            for (int i = 0; i < cards.Length; i++)
            {
                // the last card makes good on the guarantee if nothing Rare or better came out yet
                bool mustBeRare = i == cards.Length - 1 && !gotRare;
                var tier = Roll(pools, mustBeRare);
                var pool = pools[tier];
                int k = UnityEngine.Random.Range(0, pool.Count);
                cards[i] = pool[k];
                pool.RemoveAt(k);
                if (tier != Rarity.Common) gotRare = true;
            }
            // the guaranteed card doesn't always come last
            int g = UnityEngine.Random.Range(0, cards.Length);
            (cards[g], cards[cards.Length - 1]) = (cards[cards.Length - 1], cards[g]);

            if (Collection.OnlyStarters) Collection.LoadoutHint = true; // first pack: send them to try the new cards
            // a card that comes along with one of them (Remote Detonation), once ever
            var opened = new PackCard[cards.Length];
            for (int i = 0; i < cards.Length; i++)
            {
                var with = Cards.CompanionOf(cards[i]);
                bool had = with == CardId.None || Collection.Owns(with) || Array.Exists(opened, o => o.with == with);
                opened[i] = new PackCard { id = cards[i], with = had ? CardId.None : with };
            }
            Collection.Add(cards);
            return opened;
        }

        /// <summary>Rolls a tier that still has cards. Rare-or-better only when <paramref name="rareOrBetter"/> (unless none are left).</summary>
        static Rarity Roll(Dictionary<Rarity, List<CardId>> pools, bool rareOrBetter)
        {
            float common = rareOrBetter ? 0f : 1f - EpicChance - RareChance;
            float rare = RareChance, epic = EpicChance;
            if (pools[Rarity.Common].Count == 0) common = 0f;
            if (pools[Rarity.Rare].Count == 0) rare = 0f;
            if (pools[Rarity.Epic].Count == 0) epic = 0f;
            float total = common + rare + epic;
            if (total <= 0f) return pools[Rarity.Rare].Count > 0 ? Rarity.Rare : pools[Rarity.Epic].Count > 0 ? Rarity.Epic : Rarity.Common;
            float r = UnityEngine.Random.value * total;
            if (r < epic) return Rarity.Epic;
            if (r < epic + rare) return Rarity.Rare;
            return Rarity.Common;
        }
    }
}
