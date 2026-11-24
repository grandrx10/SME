using System;
using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>The cosmetics you own (every copy is kept) and the ones you're wearing, saved on this PC.</summary>
    public static class Wardrobe
    {
        /// <summary>As many as can be worn at once (everyone's are drawn and sent to everyone).</summary>
        public const int MaxWorn = 16;

        const string PrefKey = "sme_cosmetics";

        [Serializable]
        class SaveData
        {
            public int next = 1;
            public List<CosmeticItem> items = new();
            public List<WornItem> worn = new();
        }

        static SaveData _data;
        static bool _dirty;

        /// <summary>Something was won, put on, taken off or moved.</summary>
        public static event Action Changed;

        static SaveData Data
        {
            get
            {
                if (_data != null) return _data;
                string json = PlayerPrefs.GetString(PrefKey, "");
                if (!string.IsNullOrEmpty(json))
                {
                    try { _data = JsonUtility.FromJson<SaveData>(json); } catch { _data = null; }
                }
                _data ??= new SaveData();
                _data.items ??= new List<CosmeticItem>();
                _data.worn ??= new List<WornItem>();
                _data.items.RemoveAll(i => i == null || i.Info == null);
                foreach (var i in _data.items) if (i.size < Cosmetics.Regular) i.size = Cosmetics.Regular; // (there used to be small ones)
                _data.worn.RemoveAll(w => w == null || Find(_data, w.uid) == null);
                return _data;
            }
        }

        public static IReadOnlyList<CosmeticItem> Items => Data.items;
        public static IReadOnlyList<WornItem> Worn => Data.worn;

        static CosmeticItem Find(SaveData d, int uid)
        {
            foreach (var i in d.items) if (i.uid == uid) return i;
            return null;
        }

        public static CosmeticItem Find(int uid) => Find(Data, uid);

        public static WornItem FindWorn(int uid)
        {
            foreach (var w in Data.worn) if (w.uid == uid) return w;
            return null;
        }

        public static void Add(CosmeticItem item)
        {
            item.uid = Data.next++;
            Data.items.Add(item);
            Save();
            Changed?.Invoke();
        }

        /// <summary>Puts it on, where it goes by default. Null if you're wearing as many as you can.</summary>
        public static WornItem PutOn(int uid)
        {
            var had = FindWorn(uid);
            if (had != null) return had;
            var item = Find(uid);
            if (item == null || Data.worn.Count >= MaxWorn) return null;
            var w = new WornItem { uid = uid };
            CosmeticLook.DefaultSpot(item.id, w);
            Data.worn.Add(w);
            Save();
            Changed?.Invoke();
            return w;
        }

        public static void TakeOff(int uid)
        {
            if (Data.worn.RemoveAll(w => w.uid == uid) == 0) return;
            Save();
            Changed?.Invoke();
        }

        /// <summary>A worn one was moved: everyone sees it now, and it's saved on the next <see cref="Flush"/>.</summary>
        public static void Moved()
        {
            _dirty = true;
            Changed?.Invoke();
        }

        /// <summary>A copy of what's worn and where, to go back to (<see cref="Restore"/>).</summary>
        public static List<WornItem> Snapshot()
        {
            var list = new List<WornItem>();
            foreach (var w in Data.worn) list.Add(new WornItem { uid = w.uid, bone = w.bone, pos = w.pos, rot = w.rot });
            return list;
        }

        public static void Restore(List<WornItem> worn)
        {
            Data.worn.Clear();
            foreach (var w in worn) if (Find(w.uid) != null) Data.worn.Add(w);
            Save();
            Changed?.Invoke();
        }

        public static void Flush()
        {
            if (_dirty) Save();
        }

        /// <summary>Admin panel: one of every cosmetic.</summary>
        public static void AddAll()
        {
            foreach (var c in Cosmetics.All)
            {
                var item = Crates.Roll();
                item.id = c.id;
                item.uid = Data.next++;
                Data.items.Add(item);
            }
            Save();
            Changed?.Invoke();
        }

        /// <summary>Admin panel "Fresh Inventory": nothing owned, nothing worn.</summary>
        public static void Clear()
        {
            _data = new SaveData();
            Save();
            Changed?.Invoke();
        }

        static void Save()
        {
            _dirty = false;
            PlayerPrefs.SetString(PrefKey, JsonUtility.ToJson(Data));
            PlayerPrefs.Save();
        }

        public static List<OutfitPiece> Outfit()
        {
            var list = new List<OutfitPiece>();
            foreach (var w in Data.worn)
            {
                var i = Find(w.uid);
                if (i != null) list.Add(new OutfitPiece { id = i.id, colour = i.colour, size = i.size, bone = w.bone, pos = w.pos, rot = w.rot });
            }
            return list;
        }

        /// <summary>What you're wearing, as sent to everyone.</summary>
        public static string Encoded() => OutfitPiece.Encode(Outfit());
    }

    /// <summary>One worn cosmetic as it's sent: 16 bytes.</summary>
    public struct OutfitPiece
    {
        public CosmeticId id;
        public byte colour, size, bone;
        public Vector3 pos;
        public Quaternion rot;

        const int Bytes = 16;
        /// <summary>Furthest a piece can be from its bone, in bone units (half a metre each on a body of normal size).</summary>
        public const float Reach = 1.6f;

        public static string Encode(List<OutfitPiece> list)
        {
            int n = Mathf.Min(list.Count, Wardrobe.MaxWorn);
            if (n == 0) return "";
            var b = new byte[n * Bytes];
            for (int i = 0; i < n; i++)
            {
                var p = list[i];
                int o = i * Bytes;
                b[o] = (byte)p.id;
                b[o + 1] = p.colour;
                b[o + 2] = p.size;
                b[o + 3] = p.bone;
                var q = p.rot.w < 0f ? new Quaternion(-p.rot.x, -p.rot.y, -p.rot.z, -p.rot.w) : p.rot;
                for (int k = 0; k < 3; k++)
                {
                    Put(b, o + 4 + k * 2, p.pos[k] / Reach);
                    Put(b, o + 10 + k * 2, q[k]);
                }
            }
            return Convert.ToBase64String(b);
        }

        /// <summary>Whatever isn't valid is dropped or pulled back in range (it came from another player).</summary>
        public static List<OutfitPiece> Decode(string s)
        {
            var list = new List<OutfitPiece>();
            if (string.IsNullOrEmpty(s)) return list;
            byte[] b;
            try { b = Convert.FromBase64String(s); } catch (FormatException) { return list; }
            for (int o = 0; o + Bytes <= b.Length && list.Count < Wardrobe.MaxWorn; o += Bytes)
            {
                var p = new OutfitPiece { id = (CosmeticId)b[o], colour = b[o + 1], size = b[o + 2], bone = b[o + 3] };
                if (Cosmetics.Get(p.id) == null || p.colour >= Cosmetics.Palette.Length || p.bone >= CosmeticLook.Bones.Length) continue;
                p.size = (byte)Mathf.Clamp(p.size, Cosmetics.Regular, Cosmetics.SizeSteps);
                p.pos = Vector3.ClampMagnitude(new Vector3(Take(b, o + 4), Take(b, o + 6), Take(b, o + 8)) * Reach, Reach);
                var v = new Vector3(Take(b, o + 10), Take(b, o + 12), Take(b, o + 14));
                p.rot = new Quaternion(v.x, v.y, v.z, Mathf.Sqrt(Mathf.Max(0f, 1f - v.sqrMagnitude))).normalized;
                list.Add(p);
            }
            return list;
        }

        static void Put(byte[] b, int o, float v)
        {
            short s = (short)Mathf.RoundToInt(Mathf.Clamp(v, -1f, 1f) * 32767f);
            b[o] = (byte)s;
            b[o + 1] = (byte)(s >> 8);
        }

        static float Take(byte[] b, int o) => Mathf.Clamp((short)(b[o] | (b[o + 1] << 8)) / 32767f, -1f, 1f);
    }
}
