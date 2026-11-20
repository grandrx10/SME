using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SuddenMageEncounter
{
    [Serializable]
    public class Song
    {
        public string id;
        public string title;
        public float duration;
        /// <summary>True: stays in the list after it plays. False (default): removed once it has played.</summary>
        public bool keep;
    }

    /// <summary>
    /// This player's song list, saved to music.json in persistentDataPath so it survives between sessions.
    /// MusicNet sends it to the host whenever it changes; the host picks what plays.
    /// </summary>
    public static class MusicLibrary
    {
        public const int MaxSongs = 200;

        public enum AddResult { Added, AlreadyInList, ListFull, TooLong, Invalid }

        [Serializable]
        class SaveData { public List<Song> songs = new(); }

        static SaveData _data;
        public static event Action Changed;

        static string FilePath => Path.Combine(Application.persistentDataPath, "music.json");

        static SaveData Data
        {
            get
            {
                if (_data == null) Load();
                return _data;
            }
        }

        public static IReadOnlyList<Song> Songs => Data.songs;
        public static int Count => Data.songs.Count;

        public static bool Contains(string id) => Find(id) != null;

        public static Song Find(string id)
        {
            foreach (var s in Data.songs) if (s.id == id) return s;
            return null;
        }

        public static AddResult Add(SongInfo info, bool keep)
        {
            var r = AddNoSave(info, keep);
            if (r == AddResult.Added) Save();
            return r;
        }

        /// <summary>Adds every song that fits; returns how many were added.</summary>
        public static int AddMany(IEnumerable<SongInfo> infos, bool keep)
        {
            int n = 0;
            foreach (var i in infos) if (AddNoSave(i, keep) == AddResult.Added) n++;
            if (n > 0) Save();
            return n;
        }

        static AddResult AddNoSave(SongInfo info, bool keep)
        {
            if (!MusicTools.IsValidId(info.id) || info.duration <= 0) return AddResult.Invalid;
            if (info.duration > MusicTools.MaxSongSeconds) return AddResult.TooLong;
            if (Contains(info.id)) return AddResult.AlreadyInList;
            if (Data.songs.Count >= MaxSongs) return AddResult.ListFull;
            Data.songs.Add(new Song { id = info.id, title = MusicTools.CleanTitle(info.title), duration = info.duration, keep = keep });
            return AddResult.Added;
        }

        public static void Remove(string id)
        {
            if (Data.songs.RemoveAll(s => s.id == id) > 0) Save();
        }

        public static void SetKeep(string id, bool keep)
        {
            var s = Find(id);
            if (s == null || s.keep == keep) return;
            s.keep = keep;
            Save();
        }

        /// <summary>One of this player's songs just started playing: drop it unless it's marked keep.</summary>
        public static void MarkPlayed(string id)
        {
            var s = Find(id);
            if (s != null && !s.keep) Remove(id);
        }

        static void Load()
        {
            SaveData d = null;
            try
            {
                if (File.Exists(FilePath)) d = JsonUtility.FromJson<SaveData>(File.ReadAllText(FilePath));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Music] couldn't read music.json: " + e.Message);
            }
            d ??= new SaveData();
            d.songs ??= new List<Song>();
            var seen = new HashSet<string>();
            d.songs.RemoveAll(s => s == null || !MusicTools.IsValidId(s.id) || s.duration <= 0 || !seen.Add(s.id));
            if (d.songs.Count > MaxSongs) d.songs.RemoveRange(MaxSongs, d.songs.Count - MaxSongs);
            foreach (var s in d.songs) s.title = MusicTools.CleanTitle(s.title);
            _data = d;
        }

        static void Save()
        {
            try { File.WriteAllText(FilePath, JsonUtility.ToJson(Data)); }
            catch (Exception e) { Debug.LogWarning("[Music] couldn't save music.json: " + e.Message); }
            Changed?.Invoke();
        }
    }
}
