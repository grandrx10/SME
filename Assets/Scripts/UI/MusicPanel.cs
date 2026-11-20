using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SuddenMageEncounter
{
    /// <summary>
    /// MUSIC panel shown next to the loadout menu (B):
    ///  - now playing + skip / vote skip, music volume
    ///  - search YouTube or paste a video/playlist link, add results to your list
    ///  - your list: toggle KEEP (stays after it plays) / ONCE (removed after it plays), remove songs
    /// </summary>
    public class MusicPanel : MonoBehaviour
    {
        const float Pad = 30f;
        const float RowHeight = 56f;

        /// <summary>Whether newly added songs stay in the list after playing. Default: play once.</summary>
        static bool _addAsKeep;

        Vector2 _size;
        Text _npTitle, _npInfo, _status, _listTitle, _skipLabel;
        Button _skip, _addAll, _back, _addMode;
        InputField _search;
        Slider _volume;
        RectTransform _list;
        List<SongInfo> _results; // null = showing my songs
        bool _searching, _dirty;
        string _message = "";
        float _npRefresh;

        public static MusicPanel Create(Transform parent, Vector2 pos, Vector2 size)
        {
            var bg = UIFactory.Image("MusicPanel", parent, UIFactory.PanelCol);
            UIFactory.Place(bg.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), pos, size);
            var p = bg.gameObject.AddComponent<MusicPanel>();
            p.Build(bg.rectTransform, size);
            return p;
        }

        static RectTransform TopLeft(RectTransform r, float x, float y, float w, float h) =>
            UIFactory.Place(r, new Vector2(0, 1), new Vector2(0, 1), new Vector2(x, -y), new Vector2(w, h));

        void Build(RectTransform root, Vector2 size)
        {
            _size = size;
            float w = size.x - Pad * 2;

            var title = UIFactory.Text("Title", root, "MUSIC", 40, TextAnchor.MiddleLeft, Color.white, FontStyle.Bold);
            TopLeft(title.rectTransform, Pad, 20, 400, 50);

            // ---- now playing ----
            var np = UIFactory.Image("NowPlaying", root, new Color(0, 0, 0, 0.3f)).rectTransform;
            TopLeft(np, Pad, 82, w, 100);
            var npLabel = UIFactory.Text("Label", np, "NOW PLAYING", 16, TextAnchor.UpperLeft, UIFactory.Dim, FontStyle.Bold);
            TopLeft(npLabel.rectTransform, 16, 10, 300, 20);
            _npTitle = UIFactory.Text("Song", np, "", 24, TextAnchor.UpperLeft, Color.white, FontStyle.Bold);
            _npTitle.supportRichText = false;
            _npTitle.horizontalOverflow = HorizontalWrapMode.Wrap;
            _npTitle.verticalOverflow = VerticalWrapMode.Truncate;
            TopLeft(_npTitle.rectTransform, 16, 34, w - 210, 30);
            _npInfo = UIFactory.Text("Info", np, "", 18, TextAnchor.UpperLeft, UIFactory.Dim);
            _npInfo.supportRichText = true;
            TopLeft(_npInfo.rectTransform, 16, 68, w - 210, 24);
            _skip = UIFactory.Button("Skip", np, "SKIP", MusicNet.RequestSkip, 20);
            UIFactory.Place((RectTransform)_skip.transform, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-16, 0), new Vector2(170, 52));
            _skipLabel = UIFactory.Label(_skip);

            // ---- volume ----
            var vol = UIFactory.Rect("Volume", root);
            TopLeft(vol, Pad, 192, w, 46);
            UIFactory.VList(vol, 0);
            _volume = UIFactory.Slider(vol, "Music Volume", 0, GameSettings.MaxMusicVolume, GameSettings.MusicVolume,
                v => { GameSettings.MusicVolume = v; GameSettings.Apply(false); }, v => Mathf.RoundToInt(v * 100) + "%");

            // ---- search ----
            _search = UIFactory.Input(root, "Search YouTube or paste a video / playlist link...", "", 300, OnSearchEndEdit, 20);
            TopLeft((RectTransform)_search.transform, Pad, 250, w - 150, 50);
            var go = UIFactory.Button("Search", root, "SEARCH", DoSearch, 22);
            UIFactory.Place((RectTransform)go.transform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-Pad, -250), new Vector2(140, 50));

            _status = UIFactory.Text("Status", root, "", 18, TextAnchor.MiddleLeft, UIFactory.Accent);
            TopLeft(_status.rectTransform, Pad, 304, w, 26);

            // ---- list header ----
            _listTitle = UIFactory.Text("ListTitle", root, "", 22, TextAnchor.MiddleLeft, Color.white, FontStyle.Bold);
            TopLeft(_listTitle.rectTransform, Pad, 338, 200, 36);
            _back = UIFactory.Button("Back", root, "BACK", () => { _results = null; _message = ""; Rebuild(); }, 18);
            UIFactory.Place((RectTransform)_back.transform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-Pad, -338), new Vector2(96, 36));
            _addAll = UIFactory.Button("AddAll", root, "ADD ALL", AddAll, 18);
            UIFactory.Place((RectTransform)_addAll.transform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-Pad - 106, -338), new Vector2(116, 36));
            _addMode = UIFactory.Button("AddMode", root, "", () => { _addAsKeep = !_addAsKeep; RefreshAddMode(); }, 18);
            UIFactory.Place((RectTransform)_addMode.transform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-Pad - 232, -338), new Vector2(200, 36));

            // ---- list ----
            var listRoot = UIFactory.Rect("List", root);
            float top = 384, bottom = Pad;
            TopLeft(listRoot, Pad, top, w, size.y - top - bottom);
            _list = UIFactory.ScrollList(listRoot, 6);
        }

        void OnEnable()
        {
            MusicLibrary.Changed += MarkDirty;
            MusicNet.StateChanged += RefreshNowPlaying;
            if (_list == null) return;
            if (_volume != null) _volume.value = GameSettings.MusicVolume;
            Rebuild();
            RefreshNowPlaying();
        }

        void OnDisable()
        {
            MusicLibrary.Changed -= MarkDirty;
            MusicNet.StateChanged -= RefreshNowPlaying;
        }

        void MarkDirty() => _dirty = true;

        void Update()
        {
            if (_dirty) { _dirty = false; Rebuild(); }
            if ((_npRefresh -= Time.unscaledDeltaTime) <= 0f) { _npRefresh = 0.25f; RefreshNowPlaying(); }
        }

        // ------------------------------------------------------------------ now playing

        void RefreshNowPlaying()
        {
            if (_npTitle == null) return;
            var c = MusicNet.Current;
            bool active = MusicNet.Online && c.Active;
            _skip.gameObject.SetActive(active);
            if (!MusicNet.Online)
            {
                _npTitle.text = "Not connected";
                _npInfo.text = "";
                return;
            }
            if (!c.Active)
            {
                _npTitle.text = "Nothing playing";
                _npInfo.text = "";
                return;
            }

            _npTitle.text = c.title;
            double t = MusicNet.SongTime;
            string time = t < 0 ? "starting..." : $"{MusicTools.FormatTime(t)} / {MusicTools.FormatTime(c.duration)}";
            string who = MusicNet.IsMine ? "you" : $"<color=#{OwnerHex(c.owner)}>{c.ownerName}</color>";
            string status = MusicNet.PlaybackStatus;
            _npInfo.text = $"queued by {who}   ·   {time}" + (status.Length > 0 ? $"   ·   <color=#{ColorUtility.ToHtmlStringRGB(UIFactory.Accent)}>{status}</color>" : "");

            if (MusicNet.CanSkipInstantly)
            {
                _skipLabel.text = "SKIP";
                _skip.interactable = true;
            }
            else
            {
                bool voted = MusicNet.HasVoted;
                _skipLabel.text = voted ? $"VOTED {c.votes}/{c.needed}" : $"VOTE SKIP {c.votes}/{c.needed}";
                _skip.interactable = !voted;
            }
        }

        static string OwnerHex(ulong client)
        {
            foreach (var p in PlayerController.All)
                if (p != null && p.IsSpawned && p.OwnerClientId == client) return p.ColorHex;
            return "FFFFFF";
        }

        // ------------------------------------------------------------------ search

        void OnSearchEndEdit(string _)
        {
            var kb = Keyboard.current;
            if (kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)) DoSearch();
        }

        async void DoSearch()
        {
            string q = _search.text;
            if (_searching || string.IsNullOrWhiteSpace(q)) return;
            _searching = true;
            _message = MusicTools.LooksLikeUrl(q.Trim()) ? "Reading link..." : "Searching...";
            RefreshStatus();

            var (songs, error) = await MusicTools.Search(q);
            _searching = false;
            if (this == null) return; // UI destroyed while waiting
            if (error != null)
            {
                _message = error;
                RefreshStatus();
                return;
            }
            _results = songs;
            _message = songs.Count == 0 ? "No results" : "";
            Rebuild();
        }

        void AddAll()
        {
            if (_results == null) return;
            int before = MusicLibrary.Count;
            int added = MusicLibrary.AddMany(_results, _addAsKeep);
            _message = added > 0 ? $"Added {added} song{(added == 1 ? "" : "s")}" :
                before >= MusicLibrary.MaxSongs ? $"Your list is full ({MusicLibrary.MaxSongs} songs)" : "Nothing new to add";
            RefreshStatus();
        }

        void AddOne(SongInfo s)
        {
            var r = MusicLibrary.Add(s, _addAsKeep);
            _message = r switch
            {
                MusicLibrary.AddResult.Added => "Added: " + s.title,
                MusicLibrary.AddResult.ListFull => $"Your list is full ({MusicLibrary.MaxSongs} songs)",
                MusicLibrary.AddResult.TooLong => $"Too long (max {MusicTools.FormatTime(MusicTools.MaxSongSeconds)})",
                MusicLibrary.AddResult.AlreadyInList => "Already in your list",
                _ => "Can't add that one",
            };
            RefreshStatus();
        }

        // ------------------------------------------------------------------ list

        void RefreshStatus()
        {
            if (_status != null) _status.text = _message;
        }

        void RefreshAddMode()
        {
            UIFactory.Label(_addMode).text = _addAsKeep ? "ADD AS: KEEP" : "ADD AS: ONCE";
            UIFactory.Label(_addMode).color = _addAsKeep ? UIFactory.Accent : UIFactory.TextCol;
        }

        void Rebuild()
        {
            if (_list == null) return;
            for (int i = _list.childCount - 1; i >= 0; i--) Destroy(_list.GetChild(i).gameObject);

            bool results = _results != null;
            _back.gameObject.SetActive(results);
            _addAll.gameObject.SetActive(results && _results.Count > 1);
            RefreshAddMode();
            RefreshStatus();

            if (results)
            {
                _listTitle.text = $"RESULTS ({_results.Count})";
                foreach (var s in _results) ResultRow(s);
            }
            else
            {
                _listTitle.text = $"YOUR SONGS ({MusicLibrary.Count})";
                if (MusicLibrary.Count == 0)
                {
                    var empty = UIFactory.Text("Empty", _list, "No songs yet. Search above to add some.", 20, TextAnchor.MiddleLeft, UIFactory.Dim);
                    UIFactory.Size(empty, 50);
                }
                foreach (var s in MusicLibrary.Songs) SongRow(s);
            }
        }

        RectTransform Row(string title, float duration, float buttonsWidth)
        {
            var row = UIFactory.Image("Row", _list, new Color(1, 1, 1, 0.04f)).rectTransform;
            UIFactory.Size(row, RowHeight);
            var t = UIFactory.Text("Title", row, title, 19, TextAnchor.MiddleLeft);
            t.supportRichText = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            UIFactory.Stretch(t.rectTransform, 14, 4, buttonsWidth + 76, 4);
            var d = UIFactory.Text("Length", row, MusicTools.FormatTime(duration), 17, TextAnchor.MiddleRight, UIFactory.Dim);
            d.rectTransform.anchorMin = new Vector2(1, 0);
            d.rectTransform.anchorMax = new Vector2(1, 1);
            d.rectTransform.pivot = new Vector2(1, 0.5f);
            d.rectTransform.sizeDelta = new Vector2(64, 0);
            d.rectTransform.anchoredPosition = new Vector2(-buttonsWidth - 8, 0);
            return row;
        }

        static Button RowButton(RectTransform row, string label, float right, float width, UnityEngine.Events.UnityAction onClick)
        {
            var b = UIFactory.Button(label, row, label, onClick, 18);
            UIFactory.Place((RectTransform)b.transform, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-right, 0), new Vector2(width, RowHeight - 12));
            return b;
        }

        void SongRow(Song s)
        {
            string id = s.id;
            var row = Row(s.title, s.duration, 10 + 110 + 6 + 46);
            var keep = RowButton(row, s.keep ? "KEEP" : "ONCE", 10 + 46 + 6, 110, () =>
            {
                var cur = MusicLibrary.Find(id);
                if (cur != null) MusicLibrary.SetKeep(id, !cur.keep);
            });
            UIFactory.Label(keep).color = s.keep ? UIFactory.Accent : UIFactory.TextCol;
            var x = RowButton(row, "X", 10, 46, () => MusicLibrary.Remove(id));
            UIFactory.Label(x).color = new Color(1f, 0.45f, 0.4f);
        }

        void ResultRow(SongInfo s)
        {
            var song = s;
            var row = Row(s.title, s.duration, 10 + 110);
            bool tooLong = s.duration > MusicTools.MaxSongSeconds;
            bool inList = MusicLibrary.Contains(s.id);
            var b = RowButton(row, tooLong ? "TOO LONG" : inList ? "ADDED" : "ADD", 10, 110, () => AddOne(song));
            b.interactable = !tooLong && !inList;
            if (!b.interactable) UIFactory.Label(b).color = UIFactory.Dim;
        }
    }
}
