using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SuddenMageEncounter
{
    /// <summary>Rebindable actions. Order = order in Settings > Controls. Saved by name, so reordering is safe.</summary>
    public enum GameAction { Forward, Back, Left, Right, Jump, Crouch, Ability1, Ability2, Ability3, Ability4, Ability5, Loadout, PracticeReset, PracticeDummy, PracticeLeave, PracticeRounds, SkipVote, PracticeCorpse, PracticeMap, PracticeHazard }

    /// <summary>All user settings, persisted in PlayerPrefs.</summary>
    public static class GameSettings
    {
        // Video
        public static int ResolutionIndex = -1;
        public static bool Fullscreen = true;
        public static int Quality = -1;
        public static bool VSync = false;
        public static int MaxFpsIndex = 0;
        public static float Fov = 90f;

        // Audio
        public static float MasterVolume = 1f;
        public static float SfxVolume = 1f;
        /// <summary>Music slider value, 0..1. 1 (100%) = MusicBaseGain, i.e. 25% of the stream's full loudness.</summary>
        public static float MusicVolume = 1f;
        public const float MaxMusicVolume = 1f;
        public const float MusicBaseGain = 0.25f;

        /// <summary>Final linear gain for the music stream: slider * cap * master. Music applies master itself
        /// (its AudioSource ignores AudioListener.volume) so master always affects it, exactly once.</summary>
        public static float MusicGain => Mathf.Clamp01(MusicVolume) * MusicBaseGain * Mathf.Clamp01(MasterVolume);

        // Profile
        public static string PlayerName = "";
        public static event Action NameChanged;
        public static int PlayerColor = -1;          // preferred palette index (-1 = pick for me)
        public static event Action ColorChanged;

        public static void SetPlayerColor(int index)
        {
            PlayerColor = Mathf.Clamp(index, -1, PlayerPalette.Count - 1);
            PlayerPrefs.SetInt("sme_color", PlayerColor);
            PlayerPrefs.Save();
            ColorChanged?.Invoke();
        }

        public static void SetPlayerName(string name)
        {
            PlayerName = SanitizeName(name);
            PlayerPrefs.SetString("sme_name", PlayerName);
            PlayerPrefs.Save();
            NameChanged?.Invoke();
        }

        /// <summary>Trim, strip control chars, max 16 chars (fits a FixedString32Bytes).</summary>
        public static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char ch in name.Trim())
            {
                if (char.IsControl(ch)) continue;
                sb.Append(ch);
                if (sb.Length >= 16) break;
            }
            string s = sb.ToString();
            while (System.Text.Encoding.UTF8.GetByteCount(s) > 29) s = s.Substring(0, s.Length - 1);
            return s;
        }

        // Hits
        /// <summary>Damage numbers: one running total per target (true), or a number for every hit.</summary>
        public static bool DamageTotals = false;

        // Controls
        public static float Sensitivity = 2f;
        public static bool InvertY = false;

        public static readonly int[] MaxFpsOptions = { 0, 60, 120, 144, 165, 240, 360 };

        public static readonly Dictionary<GameAction, KeyBind> DefaultBinds = new()
        {
            { GameAction.Forward, Key.W },
            { GameAction.Back, Key.S },
            { GameAction.Left, Key.A },
            { GameAction.Right, Key.D },
            { GameAction.Jump, Key.Space },
            { GameAction.Crouch, Key.LeftCtrl },
            { GameAction.Ability1, KeyBind.Mouse(KeyBind.MouseButton.Left) },
            { GameAction.Ability2, Key.E },
            { GameAction.Ability3, Key.Q },
            { GameAction.Ability4, Key.F },
            { GameAction.Ability5, Key.LeftShift },
            { GameAction.Loadout, Key.B },
            { GameAction.PracticeReset, Key.J },
            { GameAction.PracticeDummy, Key.K },
            { GameAction.PracticeLeave, Key.L },
            { GameAction.PracticeRounds, Key.H },
            { GameAction.SkipVote, Key.N },
            { GameAction.PracticeCorpse, Key.I },
            { GameAction.PracticeMap, Key.O },
            { GameAction.PracticeHazard, Key.P },
        };

        /// <summary>The rebindable key for ability slot 0..4.</summary>
        public static GameAction AbilityAction(int slot) => GameAction.Ability1 + slot;

        public static readonly Dictionary<GameAction, KeyBind> Binds = new(DefaultBinds);

        public static event Action Changed;

        static bool _loaded;
        static Resolution[] _resolutions;

        public static Resolution[] Resolutions
        {
            get
            {
                if (_resolutions == null)
                {
                    var list = new List<Resolution>();
                    var seen = new HashSet<long>();
                    var all = Screen.resolutions;
                    for (int i = all.Length - 1; i >= 0; i--) // highest first
                    {
                        long key = ((long)all[i].width << 32) | (uint)all[i].height;
                        if (seen.Add(key)) list.Add(all[i]);
                    }
                    if (list.Count == 0) list.Add(Screen.currentResolution);
                    _resolutions = list.ToArray();
                }
                return _resolutions;
            }
        }

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            ResolutionIndex = PlayerPrefs.GetInt("sme_res", -1);
            PlayerName = SanitizeName(PlayerPrefs.GetString("sme_name", ""));
            PlayerColor = Mathf.Clamp(PlayerPrefs.GetInt("sme_color", -1), -1, PlayerPalette.Count - 1);
            Fullscreen = PlayerPrefs.GetInt("sme_fullscreen", 1) == 1;
            Quality = PlayerPrefs.GetInt("sme_quality", QualitySettings.GetQualityLevel());
            VSync = PlayerPrefs.GetInt("sme_vsync", 0) == 1;
            MaxFpsIndex = PlayerPrefs.GetInt("sme_maxfps", 0);
            Fov = PlayerPrefs.GetFloat("sme_fov", 90f);
            MasterVolume = PlayerPrefs.GetFloat("sme_master", 1f);
            SfxVolume = PlayerPrefs.GetFloat("sme_sfx", 1f);
            // new key: older "sme_music"/"sme_music2" values used different scales
            MusicVolume = Mathf.Clamp(PlayerPrefs.GetFloat("sme_music3", 1f), 0f, MaxMusicVolume);
            Sensitivity = PlayerPrefs.GetFloat("sme_sens", 2f);
            InvertY = PlayerPrefs.GetInt("sme_inverty", 0) == 1;
            DamageTotals = PlayerPrefs.GetInt("sme_damagetotals", 0) == 1;
            foreach (var kv in DefaultBinds)
            {
                int k = PlayerPrefs.GetInt("sme_bind_" + kv.Key, kv.Value.Code);
                Binds[kv.Key] = KeyBind.IsValidCode(k) ? new KeyBind(k) : kv.Value;
            }
            MaxFpsIndex = Mathf.Clamp(MaxFpsIndex, 0, MaxFpsOptions.Length - 1);
        }

        public static void Save()
        {
            PlayerPrefs.SetInt("sme_res", ResolutionIndex);
            PlayerPrefs.SetInt("sme_fullscreen", Fullscreen ? 1 : 0);
            PlayerPrefs.SetInt("sme_quality", Quality);
            PlayerPrefs.SetInt("sme_vsync", VSync ? 1 : 0);
            PlayerPrefs.SetInt("sme_maxfps", MaxFpsIndex);
            PlayerPrefs.SetFloat("sme_fov", Fov);
            PlayerPrefs.SetFloat("sme_master", MasterVolume);
            PlayerPrefs.SetFloat("sme_sfx", SfxVolume);
            PlayerPrefs.SetFloat("sme_music3", MusicVolume);
            PlayerPrefs.SetFloat("sme_sens", Sensitivity);
            PlayerPrefs.SetInt("sme_inverty", InvertY ? 1 : 0);
            PlayerPrefs.SetInt("sme_damagetotals", DamageTotals ? 1 : 0);
            foreach (var kv in Binds) PlayerPrefs.SetInt("sme_bind_" + kv.Key, kv.Value.Code);
            PlayerPrefs.Save();
        }

        /// <summary>Apply settings to the engine, then save and notify listeners.</summary>
        public static void Apply(bool applyResolution = true)
        {
            if (applyResolution && !Application.isEditor)
            {
                var mode = Fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
                if (ResolutionIndex >= 0 && ResolutionIndex < Resolutions.Length)
                {
                    var r = Resolutions[ResolutionIndex];
                    Screen.SetResolution(r.width, r.height, mode);
                }
                else Screen.fullScreenMode = mode;
            }
            if (Quality >= 0 && Quality < QualitySettings.names.Length && Quality != QualitySettings.GetQualityLevel())
                QualitySettings.SetQualityLevel(Quality, true);
            QualitySettings.vSyncCount = VSync ? 1 : 0;
            int fps = MaxFpsOptions[Mathf.Clamp(MaxFpsIndex, 0, MaxFpsOptions.Length - 1)];
            Application.targetFrameRate = fps <= 0 ? -1 : fps;
            AudioListener.volume = MasterVolume;
            Save();
            Changed?.Invoke();
        }

        public static void ResetBinds()
        {
            foreach (var kv in DefaultBinds) Binds[kv.Key] = kv.Value;
            Apply(false);
        }
    }
}
