using System;
using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The cards the player owns, saved on this PC. One of each at most: no duplicates. A new player (no save yet)
    /// owns just the starter cards; after that the save is what you own (N clears it, "," gives you everything).
    /// Starter cards are always owned, even after a reset, so packs never give them.
    /// </summary>
    public static class Collection
    {
        const string PrefKey = "sme_collection";

        [Serializable]
        class SaveData { public int[] copies; }

        static int[] _owned; // 0 or 1 per CardId
        public static event Action Changed;

        static int[] Owned
        {
            get
            {
                if (_owned == null) Load();
                return _owned;
            }
        }

        public static bool Owns(CardId id)
        {
            int i = (int)id;
            if (Cards.Get(id)?.rarity == Rarity.Starter) return true; // everyone always has the starters
            return i > 0 && i < Owned.Length && Owned[i] > 0;
        }

        /// <summary>1 if you own it, 0 if not (a loadout can use a card as many times as this).</summary>
        public static int CopiesOf(CardId id) => Owns(id) ? 1 : 0;

        /// <summary>Every card packs can give that you don't own yet (never a secret one).</summary>
        public static List<CardId> Unowned()
        {
            var list = new List<CardId>();
            foreach (var c in Cards.All) if (c.rarity != Rarity.Secret && !c.companion && !Owns(c.id)) list.Add(c.id);
            return list;
        }

        /// <summary>
        /// The World can be found: you own every other Tarot card, and not it yet. A blacked-out card waits in the
        /// inventory until then (see <see cref="SecretReveal"/>).
        /// </summary>
        public static bool SecretReady
        {
            get
            {
                if (Owns(CardId.TheWorld)) return false;
                foreach (var c in Cards.All) if (c.element == Element.Tarot && c.id != CardId.TheWorld && !Owns(c.id)) return false;
                return true;
            }
        }

        /// <summary>Adds the cards listed (a pack's cards). Ones you already own change nothing.</summary>
        public static void Add(IEnumerable<CardId> cards)
        {
            foreach (var id in cards)
            {
                int i = (int)id;
                if (i > 0 && i < Owned.Length) Owned[i] = 1;
                int with = (int)Cards.CompanionOf(id); // (Remote Detonation, with a card that places flame bombs)
                if (with > 0) Owned[with] = 1;
            }
            Save();
        }

        /// <summary>Admin panel: own every card packs give (secret ones still have to be found).</summary>
        public static void AddAll()
        {
            foreach (var c in Cards.All) if (c.rarity != Rarity.Secret) Owned[(int)c.id] = 1;
            Save();
        }

        /// <summary>
        /// Admin panel "Fresh Inventory": back to a brand-new player. Own only the starters, every loadout is the
        /// starter loadout again (loadout 1 selected), and the next pack counts as your first.
        /// </summary>
        public static void Clear()
        {
            Array.Clear(Owned, 0, Owned.Length);
            // Starter cards stay: Owns() always says yes to them
            LoadoutHint = false;
            Save();
            Loadouts.ResetToStarters();
        }

        static void Load()
        {
            int max = 0;
            foreach (var c in Cards.All) max = Mathf.Max(max, (int)c.id);
            _owned = new int[max + 1];
            SaveData d = null;
            string json = PlayerPrefs.GetString(PrefKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                try { d = JsonUtility.FromJson<SaveData>(json); } catch { d = null; }
            }
            foreach (var c in Cards.All)
            {
                int i = (int)c.id;
                // no save yet: a new player owns only the starters (Owns() covers those). Old saves counted copies; any copy means you own it.
                _owned[i] = d?.copies != null && i < d.copies.Length && d.copies[i] > 0 ? 1 : 0;
            }
        }

        /// <summary>You own nothing but the starter cards (so the next pack is your first).</summary>
        public static bool OnlyStarters
        {
            get
            {
                foreach (var c in Cards.All) if (c.rarity != Rarity.Starter && Owns(c.id)) return false;
                return true;
            }
        }

        const string LoadoutHintKey = "sme_loadout_hint";

        /// <summary>Your first pack was just opened: point you at the loadout screen until you've been there.</summary>
        public static bool LoadoutHint
        {
            get => PlayerPrefs.GetInt(LoadoutHintKey, 0) != 0;
            set
            {
                if (LoadoutHint == value) return;
                PlayerPrefs.SetInt(LoadoutHintKey, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        static void Save()
        {
            PlayerPrefs.SetString(PrefKey, JsonUtility.ToJson(new SaveData { copies = Owned }));
            PlayerPrefs.Save();
            Loadouts.DropUnowned();
            Changed?.Invoke();
        }
    }

    [Serializable]
    public class Loadout
    {
        public const int Slots = 5;
        /// <summary>CardId per ability slot (0 = empty).</summary>
        public int[] cards = new int[Slots];

        public CardId Get(int slot) => cards != null && slot >= 0 && slot < cards.Length && Cards.IsValid(cards[slot]) ? (CardId)cards[slot] : CardId.None;

        /// <summary>How many slots hold this card.</summary>
        public int Uses(CardId id)
        {
            int n = 0;
            for (int i = 0; i < Slots; i++) if (Get(i) == id) n++;
            return n;
        }

        /// <summary>Make sure there are 5 slots and no card is used more often than it's owned.</summary>
        public static Loadout Fix(Loadout l)
        {
            l ??= new Loadout();
            if (l.cards == null || l.cards.Length != Slots)
            {
                var old = l.cards ?? Array.Empty<int>();
                l.cards = new int[Slots];
                for (int i = 0; i < Slots && i < old.Length; i++) l.cards[i] = old[i];
            }
            for (int i = 0; i < Slots; i++)
            {
                if (!Cards.IsValid(l.cards[i])) { l.cards[i] = 0; continue; }
                int used = 0;
                for (int k = 0; k <= i; k++) if (l.cards[k] == l.cards[i]) used++;
                if (used > Collection.CopiesOf((CardId)l.cards[i])) l.cards[i] = 0;
            }
            return l;
        }
    }

    /// <summary>The player's 5 saved loadouts (PlayerPrefs, JSON) and which one is selected.</summary>
    public static class Loadouts
    {
        public const int Count = 5;
        const string PrefKey = "sme_loadouts";

        [Serializable]
        class SaveData
        {
            public int selected;
            public Loadout[] loadouts;
        }

        static SaveData _data;
        public static event Action Changed;

        static SaveData Data
        {
            get
            {
                if (_data == null) Load();
                return _data;
            }
        }

        public static int SelectedIndex => Data.selected;
        public static Loadout Selected => Data.loadouts[Data.selected];
        public static Loadout Get(int i) => Data.loadouts[Mathf.Clamp(i, 0, Count - 1)];

        public static void Select(int i)
        {
            Data.selected = Mathf.Clamp(i, 0, Count - 1);
            Save();
        }

        /// <summary>Copies of this card still free in the given loadout.</summary>
        public static int Available(int loadout, CardId id) => Collection.CopiesOf(id) - Get(loadout).Uses(id);

        /// <summary>Puts a card in a slot (if a copy is free). Returns false when it can't.</summary>
        public static bool Equip(int loadout, int slot, CardId id)
        {
            var l = Get(loadout);
            if (slot < 0 || slot >= Loadout.Slots) return false;
            if (id != CardId.None && l.Get(slot) != id && Available(loadout, id) <= 0) return false;
            l.cards[slot] = (int)id;
            Save();
            return true;
        }

        /// <summary>Swaps two slots (dragging one equipped card onto another slot).</summary>
        public static void Swap(int loadout, int a, int b)
        {
            var l = Get(loadout);
            if (a < 0 || b < 0 || a >= Loadout.Slots || b >= Loadout.Slots) return;
            (l.cards[a], l.cards[b]) = (l.cards[b], l.cards[a]);
            Save();
        }

        /// <summary>The collection changed: take cards you no longer own out of every loadout.</summary>
        public static void DropUnowned()
        {
            if (_data == null) return; // not loaded yet; Load fixes them
            bool changed = false;
            foreach (var l in _data.loadouts)
            {
                var before = (int[])l.cards.Clone();
                FillIfBlank(Loadout.Fix(l));
                for (int i = 0; i < Loadout.Slots; i++) changed |= before[i] != l.cards[i];
            }
            if (changed) Save();
        }

        /// <summary>Every loadout back to the starter loadout, the first one selected (a fresh inventory).</summary>
        public static void ResetToStarters()
        {
            for (int i = 0; i < Count; i++) Data.loadouts[i] = FillIfBlank(new Loadout());
            Data.selected = 0;
            Save();
        }

        static void Load()
        {
            SaveData d = null;
            string json = PlayerPrefs.GetString(PrefKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                try { d = JsonUtility.FromJson<SaveData>(json); } catch { d = null; }
            }
            d ??= new SaveData();
            var arr = new Loadout[Count];
            for (int i = 0; i < Count; i++)
                arr[i] = FillIfBlank(Loadout.Fix(d.loadouts != null && i < d.loadouts.Length ? d.loadouts[i] : null));
            d.loadouts = arr;
            d.selected = Mathf.Clamp(d.selected, 0, Count - 1);
            _data = d;
        }

        /// <summary>
        /// A blank loadout gets the starter cards, so nobody goes into a round empty-handed:
        /// Rock Barrage on ability 1 (LMB), Summon Wall on ability 2 (E), Shove on ability 5 (Shift).
        /// </summary>
        static Loadout FillIfBlank(Loadout l)
        {
            for (int i = 0; i < Loadout.Slots; i++) if (l.cards[i] != 0) return l;
            l.cards[0] = (int)CardId.RockBarrage;
            l.cards[1] = (int)CardId.SummonWall;
            l.cards[4] = (int)CardId.Shove;
            return Loadout.Fix(l);
        }

        static void Save()
        {
            PlayerPrefs.SetString(PrefKey, JsonUtility.ToJson(Data));
            PlayerPrefs.Save();
            Changed?.Invoke();
        }
    }
}
