using System;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The cards the player owns (how many copies of each), saved on this PC. Everything is unlocked for now:
    /// every card starts with <see cref="StartingCopies"/> copies. A loadout may use a card as many times as
    /// you own copies of it.
    /// </summary>
    public static class Collection
    {
        public const int StartingCopies = 1;
        const string PrefKey = "sme_collection";

        [Serializable]
        class SaveData { public int[] copies; }

        static int[] _copies;
        public static event Action Changed;

        static int[] Copies
        {
            get
            {
                if (_copies == null) Load();
                return _copies;
            }
        }

        public static int CopiesOf(CardId id)
        {
            int i = (int)id;
            return i > 0 && i < Copies.Length ? Copies[i] : 0;
        }

        public static void SetCopies(CardId id, int n)
        {
            int i = (int)id;
            if (i <= 0 || i >= Copies.Length) return;
            Copies[i] = Mathf.Max(0, n);
            Save();
        }

        static void Load()
        {
            int max = 0;
            foreach (var c in Cards.All) max = Mathf.Max(max, (int)c.id);
            _copies = new int[max + 1];
            SaveData d = null;
            try { d = JsonUtility.FromJson<SaveData>(PlayerPrefs.GetString(PrefKey, "")); } catch { d = null; }
            foreach (var c in Cards.All)
            {
                int i = (int)c.id;
                int saved = d?.copies != null && i < d.copies.Length ? d.copies[i] : 0;
                _copies[i] = Mathf.Max(saved, StartingCopies); // everything unlocked for now
            }
        }

        static void Save()
        {
            PlayerPrefs.SetString(PrefKey, JsonUtility.ToJson(new SaveData { copies = Copies }));
            PlayerPrefs.Save();
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

        static void Load()
        {
            SaveData d = null;
            string json = PlayerPrefs.GetString(PrefKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                try { d = JsonUtility.FromJson<SaveData>(json); } catch { d = null; }
            }
            bool fresh = d == null;
            d ??= new SaveData();
            var arr = new Loadout[Count];
            for (int i = 0; i < Count; i++)
                arr[i] = Loadout.Fix(d.loadouts != null && i < d.loadouts.Length ? d.loadouts[i] : null);
            d.loadouts = arr;
            d.selected = Mathf.Clamp(d.selected, 0, Count - 1);
            if (fresh)
            {
                // a starter loadout so the first game isn't empty-handed
                arr[0].cards[0] = (int)CardId.RockBarrage;
                arr[0].cards[1] = (int)CardId.SummonWall;
                arr[0].cards[2] = (int)CardId.FoldingHands;
                Loadout.Fix(arr[0]);
            }
            _data = d;
        }

        static void Save()
        {
            PlayerPrefs.SetString(PrefKey, JsonUtility.ToJson(Data));
            PlayerPrefs.Save();
            Changed?.Invoke();
        }
    }
}
