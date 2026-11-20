using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SuddenMageEncounter
{
    /// <summary>Main menu, settings, pause menu, loadouts and the HUD — all built from code.</summary>
    public class GameUI : MonoBehaviour
    {
        enum UiScreen { Menu, Game }
        static readonly Color MenuBackground = new(0.1f, 0.1f, 0.13f, 1f);
        static readonly Color ManaColor = new(0.45f, 0.75f, 1f, 1f);
        static readonly Color ManaPartial = new(0.45f, 0.75f, 1f, 0.35f);
        static readonly Color DeniedRed = new(0.92f, 0.22f, 0.18f, 1f);

        Canvas _canvas;
        RectTransform _menu, _settings, _pause, _hud;
        Text _status;
        UiScreen _screen = UiScreen.Menu;
        bool _settingsOpen, _paused;
        LoadoutMenu _loadout;

        // rebinding
        GameAction? _rebinding;
        int _rebindFrame;
        readonly Dictionary<GameAction, Text> _bindLabels = new();

        Sprite _ring, _thinRing;

        // names & leaderboard
        readonly List<InputField> _nameFields = new();
        RectTransform _board;
        readonly List<Text[]> _boardRows = new();
        float _boardRefresh;

        void Awake()
        {
            // draw every card symbol now (menu and HUD sizes) rather than the moment a menu opens
            foreach (var card in Cards.All)
            {
                CardSymbols.Get(card.id);
                CardSymbols.Get(card.id, 128);
            }
            _ring = MakeRingSprite(256, 0.8f);
            _thinRing = MakeRingSprite(256, 0.9f);
            BuildCanvas();
            BuildMenu();
            BuildSettings();
            BuildPause();
            BuildHud();
            BuildLeaderboard();
            _loadout = new LoadoutMenu(_canvas.transform, RefreshVisibility);
            GameSettings.NameChanged += () => { foreach (var f in _nameFields) if (!f.isFocused) f.text = GameSettings.PlayerName; };
            SessionManager.Status += s => { if (_status) _status.text = s; };
            SessionManager.Left += () => SetScreen(UiScreen.Menu);
            PlayerController.LocalSpawned += _ => SetScreen(UiScreen.Game);
            PlayerController.LocalDespawned += _ => { if (SessionManager.Session == null) SetScreen(UiScreen.Menu); };
            Combatant.KillFeed += AddFeedEntry;
            AbilityCaster.Denied += slot => { if (slot >= 0 && slot < _denied.Length) _denied[slot] = 0.35f; };
            PlayerController.LocalHitConfirmed += OnHitConfirmed;
            AbilityCaster.NoTarget += () => _noTargetT = 1.2f;
            SetScreen(UiScreen.Menu);
        }

        void OnDestroy()
        {
            MusicNet.StateChanged -= OnSongState;
            PlayerController.LocalHitConfirmed -= OnHitConfirmed;
        }

        // ------------------------------------------------------------------ hits (damage numbers + hit marker)

        RectTransform _hitMarker;
        readonly Image[] _hitLines = new Image[4];
        float _hitT;
        Color _hitColor = Color.white;
        readonly List<(Text text, Vector3 world, float born)> _numbers = new();

        void BuildHitMarker()
        {
            _hitMarker = UIFactory.Rect("HitMarker", _hud);
            UIFactory.Place(_hitMarker, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(40, 40));
            _hitMarker.localRotation = Quaternion.Euler(0, 0, 45);
            var dirs = new[] { Vector2.up, Vector2.down, Vector2.left, Vector2.right };
            for (int i = 0; i < 4; i++)
            {
                bool vert = dirs[i].x == 0;
                _hitLines[i] = UIFactory.Image("L", _hitMarker, Color.white);
                _hitLines[i].raycastTarget = false;
                UIFactory.Place(_hitLines[i].rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), dirs[i] * 13f, vert ? new Vector2(3, 11) : new Vector2(11, 3));
            }
        }

        void OnHitConfirmed(Vector3 where, float damage, bool killed)
        {
            if (_hud == null) return;
            _hitT = killed ? 1.5f : 1f;
            _hitColor = killed ? new Color(1f, 0.2f, 0.15f) : Color.white;
            _hitMarker.localScale = Vector3.one * (killed ? 1.35f : 1f);
            if (damage < 0.5f) return;
            var t = UIFactory.Text("Damage", _hud, Mathf.RoundToInt(damage).ToString(), killed ? 46 : 36, TextAnchor.MiddleCenter,
                killed ? new Color(1f, 0.3f, 0.2f) : Color.white, FontStyle.Bold);
            t.rectTransform.sizeDelta = new Vector2(200, 60);
            Outlined(t, 2f);
            _numbers.Add((t, where + UnityEngine.Random.insideUnitSphere * 0.25f, Time.time));
        }

        Text _noTarget;
        float _noTargetT;

        void UpdateHits(float dt)
        {
            if (_noTarget == null)
            {
                _noTarget = UIFactory.Text("NoTarget", _hud, "No Target Found", 30, TextAnchor.MiddleCenter, new Color(1f, 0.35f, 0.3f), FontStyle.Bold);
                UIFactory.Place(_noTarget.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -70), new Vector2(600, 44));
                Outlined(_noTarget, 2f);
            }
            _noTargetT = Mathf.Max(0f, _noTargetT - dt);
            _noTarget.enabled = _noTargetT > 0f;
            var nc = _noTarget.color;
            nc.a = Mathf.Clamp01(_noTargetT / 0.3f);
            _noTarget.color = nc;

            _hitT = Mathf.MoveTowards(_hitT, 0f, dt * 4f);
            foreach (var l in _hitLines) l.color = new Color(_hitColor.r, _hitColor.g, _hitColor.b, Mathf.Clamp01(_hitT));

            var cam = Camera.main;
            float scale = _canvas.transform.lossyScale.x > 0f ? _canvas.transform.lossyScale.x : 1f;
            for (int i = _numbers.Count - 1; i >= 0; i--)
            {
                var (text, world, born) = _numbers[i];
                float age = Time.time - born;
                if (text == null || age > 1f || cam == null)
                {
                    if (text != null) Destroy(text.gameObject);
                    _numbers.RemoveAt(i);
                    continue;
                }
                Vector3 s = cam.WorldToScreenPoint(world + Vector3.up * age * 0.8f);
                text.enabled = s.z > 0f;
                text.rectTransform.anchorMin = text.rectTransform.anchorMax = Vector2.zero;
                text.rectTransform.anchoredPosition = new Vector2(s.x, s.y) / scale;
                var c = text.color;
                c.a = Mathf.Clamp01((1f - age) / 0.35f);
                text.color = c;
                text.rectTransform.localScale = Vector3.one * (1f + Mathf.Max(0f, 0.15f - age) * 3f);
            }
        }

        // ------------------------------------------------------------------ flow

        void SetScreen(UiScreen s)
        {
            _screen = s;
            _paused = false;
            _settingsOpen = false;
            _rebinding = null;
            _loadout?.Close();
            RefreshVisibility();
        }

        void RefreshVisibility()
        {
            if (_loadout == null) return;
            bool game = _screen == UiScreen.Game;
            _menu.gameObject.SetActive(!game && !_settingsOpen);
            _hud.gameObject.SetActive(game);
            _pause.gameObject.SetActive(game && _paused && !_settingsOpen);
            _settings.gameObject.SetActive(_settingsOpen);
            _loadout.SetShown(!_settingsOpen && !(game && _paused));
            _loadout.SetMusicVisible(game);
            _settings.GetComponent<Image>().color = game ? new Color(0, 0, 0, 0.6f) : MenuBackground;
            bool cursorFree = !game || _paused || _settingsOpen || _loadout.IsOpen;
            Cursor.lockState = cursorFree ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = cursorFree;
            InputMap.Blocked = cursorFree;
        }

        void Update()
        {
            if (_rebinding.HasValue) { PollRebind(); return; }

            if (InputMap.EscapeDown)
            {
                if (_settingsOpen) { _settingsOpen = false; RefreshVisibility(); }
                else if (!_paused && _loadout.IsOpen) _loadout.Back();
                else if (_screen == UiScreen.Game) { _paused = !_paused; RefreshVisibility(); }
            }
            if (_screen == UiScreen.Game && !_paused && !_settingsOpen && !LoadoutMenu.IsTyping && InputMap.DownRaw(GameAction.Loadout))
                _loadout.Toggle();
            // clicking back into the game re-locks the cursor (e.g. after alt-tab)
            if (_screen == UiScreen.Game && !_paused && !_settingsOpen && !_loadout.IsOpen && Cursor.lockState != CursorLockMode.Locked &&
                Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                RefreshVisibility();

            if (_screen == UiScreen.Game) UpdateHud();
            UpdateLeaderboard();
        }

        // ------------------------------------------------------------------ build

        void BuildCanvas()
        {
            var go = new GameObject("GameUI Canvas", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 10;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
        }

        RectTransform FullPanel(string name, Color c)
        {
            var img = UIFactory.Image(name, _canvas.transform, c);
            return UIFactory.Stretch(img.rectTransform);
        }

        void BuildMenu()
        {
            _menu = FullPanel("MainMenu", MenuBackground);
            var title = UIFactory.Text("Title", _menu, "SUDDEN MAGE ENCOUNTER", 96, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            UIFactory.Place(title.rectTransform, new Vector2(0.5f, 0.72f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1600, 140));

            var col = UIFactory.Rect("Buttons", _menu);
            UIFactory.Place(col, new Vector2(0.5f, 0.42f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(360, 240));
            UIFactory.VList(col, 16);
            UIFactory.Size(UIFactory.Button("Play", col, "PLAY", () => SessionManager.Play(), 34), 72);
            UIFactory.Size(UIFactory.Button("Loadout", col, "LOADOUT", () => _loadout.Open(), 30), 60);
            UIFactory.Size(UIFactory.Button("Settings", col, "SETTINGS", OpenSettings, 30), 60);

            _status = UIFactory.Text("Status", _menu, "", 26, TextAnchor.MiddleCenter, UIFactory.Accent);
            UIFactory.Place(_status.rectTransform, new Vector2(0.5f, 0.22f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1200, 40));

            var nameBox = UIFactory.Rect("Name", _menu);
            UIFactory.Place(nameBox, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-30, -30), new Vector2(360, 90));
            var nl = UIFactory.Text("Label", nameBox, "YOUR NAME", 18, TextAnchor.UpperLeft, UIFactory.Dim, FontStyle.Bold);
            UIFactory.Place(nl.rectTransform, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, new Vector2(340, 24));
            var nf = NameField(nameBox);
            UIFactory.Place((RectTransform)nf.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, -28), new Vector2(360, 54));
        }

        InputField NameField(Transform parent)
        {
            var f = UIFactory.Input(parent, "Enter a name...", GameSettings.PlayerName, 16, v => GameSettings.SetPlayerName(v));
            _nameFields.Add(f);
            return f;
        }

        // ------------------------------------------------------------------ leaderboard (hold Tab)

        void BuildLeaderboard()
        {
            var bg = UIFactory.Image("Leaderboard", _canvas.transform, new Color(0.04f, 0.05f, 0.07f, 0.88f));
            _board = bg.rectTransform;
            int rows = SessionManager.MaxPlayers;
            UIFactory.Place(_board, new Vector2(0.5f, 0.55f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(760, 80 + 40 * rows));

            Text[] Row(int i, Color c, FontStyle style, int size)
            {
                float y = -24 - i * 40;
                var name = UIFactory.Text("Name", _board, "", size, TextAnchor.MiddleLeft, c, style);
                UIFactory.Place(name.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(28, y), new Vector2(460, 38));
                var w = UIFactory.Text("Wins", _board, "", size, TextAnchor.MiddleCenter, c, FontStyle.Bold);
                UIFactory.Place(w.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(500, y), new Vector2(110, 38));
                var k = UIFactory.Text("Kills", _board, "", size, TextAnchor.MiddleCenter, c, style);
                UIFactory.Place(k.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(620, y), new Vector2(110, 38));
                return new[] { name, w, k };
            }
            var header = Row(0, UIFactory.Dim, FontStyle.Bold, 18);
            header[0].text = ""; header[1].text = "♔"; header[2].text = "✕"; // crown = wins, cross = eliminations
            for (int i = 0; i < rows; i++) _boardRows.Add(Row(i + 1, Color.white, FontStyle.Normal, 24));
            _board.gameObject.SetActive(false);
        }

        void UpdateLeaderboard()
        {
            bool show = _screen == UiScreen.Game && !_paused && !_settingsOpen && !_loadout.IsOpen &&
                        Keyboard.current != null && Keyboard.current.tabKey.isPressed;
            if (_board.gameObject.activeSelf != show) { _board.gameObject.SetActive(show); _boardRefresh = 0; }
            if (!show || (_boardRefresh -= Time.unscaledDeltaTime) > 0) return;
            _boardRefresh = 0.25f;

            var players = new List<PlayerController>(PlayerController.All);
            players.RemoveAll(p => p == null || !p.IsSpawned);
            players.Sort((a, b) => b.Wins.Value != a.Wins.Value ? b.Wins.Value.CompareTo(a.Wins.Value) : b.Kills.Value.CompareTo(a.Kills.Value));
            int r = 0;
            foreach (var p in players)
            {
                if (r >= _boardRows.Count) break;
                var row = _boardRows[r++];
                Color c = p.IsOwner ? UIFactory.Accent : Color.white;
                if (!p.IsAlive) c.a = 0.5f;
                row[0].supportRichText = true;
                row[0].text = $"<color=#{p.ColorHex}>■</color>  {p.DisplayName}";
                row[1].text = p.Wins.Value.ToString();
                row[2].text = p.Kills.Value.ToString();
                foreach (var t in row) { t.enabled = true; t.color = c; }
            }
            for (; r < _boardRows.Count; r++)
                foreach (var t in _boardRows[r]) t.enabled = false;
        }

        void OpenSettings()
        {
            _settingsOpen = true;
            RefreshVisibility();
        }

        void BuildPause()
        {
            _pause = FullPanel("Pause", new Color(0, 0, 0, 0.55f));
            var col = UIFactory.Rect("Buttons", _pause);
            UIFactory.Place(col, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(380, 420));
            UIFactory.VList(col, 14);
            UIFactory.Size(UIFactory.Button("Resume", col, "RESUME", () => { _paused = false; RefreshVisibility(); }), 56);
            UIFactory.Size(UIFactory.Button("Loadout", col, "LOADOUT", () => { _paused = false; _loadout.Open(); }), 56);
            UIFactory.Size(UIFactory.Button("Settings", col, "SETTINGS", OpenSettings), 56);
            var nl = UIFactory.Text("NameLabel", col, "YOUR NAME", 18, TextAnchor.LowerLeft, UIFactory.Dim, FontStyle.Bold);
            UIFactory.Size(nl, 26);
            UIFactory.Size(NameField(col), 50);
            UIFactory.Size(UIFactory.Button("MainMenu", col, "MAIN MENU", () => SessionManager.Leave()), 56);
        }

        void BuildSettings()
        {
            _settings = FullPanel("Settings", new Color(0, 0, 0, 0.6f));
            var win = UIFactory.Image("Window", _settings, UIFactory.PanelCol).rectTransform;
            UIFactory.Place(win, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(900, 760));

            var title = UIFactory.Text("Title", win, "SETTINGS", 40, TextAnchor.MiddleLeft, Color.white, FontStyle.Bold);
            UIFactory.Place(title.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(30, -20), new Vector2(400, 50));

            var tabs = UIFactory.Rect("Tabs", win);
            UIFactory.Place(tabs, new Vector2(0, 1), new Vector2(0, 1), new Vector2(30, -85), new Vector2(840, 50));
            var h = tabs.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 10; h.childControlWidth = true; h.childForceExpandWidth = true; h.childControlHeight = true;

            var pages = new List<RectTransform>();
            var tabButtons = new List<Button>();
            RectTransform Page(string name)
            {
                var p = UIFactory.Rect(name, win);
                UIFactory.Place(p, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -150), new Vector2(840, 520));
                pages.Add(p);
                return UIFactory.ScrollList(p, 6);
            }
            void Show(int i)
            {
                for (int k = 0; k < pages.Count; k++)
                {
                    pages[k].gameObject.SetActive(k == i);
                    UIFactory.Label(tabButtons[k]).color = k == i ? UIFactory.Accent : UIFactory.TextCol;
                }
            }
            string[] names = { "VIDEO", "AUDIO", "CONTROLS" };
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                tabButtons.Add(UIFactory.Button(names[i], tabs, names[i], () => Show(idx), 24));
            }

            var video = Page("Video");
            UIFactory.Cycler(video, "Resolution",
                () => Array.ConvertAll(GameSettings.Resolutions, r => $"{r.width} x {r.height}"),
                () => GameSettings.ResolutionIndex < 0 ? 0 : GameSettings.ResolutionIndex,
                i => { GameSettings.ResolutionIndex = i; GameSettings.Apply(); });
            UIFactory.Toggle(video, "Fullscreen", () => GameSettings.Fullscreen, v => { GameSettings.Fullscreen = v; GameSettings.Apply(); });
            UIFactory.Cycler(video, "Quality", () => QualitySettings.names,
                () => Mathf.Max(0, GameSettings.Quality), i => { GameSettings.Quality = i; GameSettings.Apply(false); });
            UIFactory.Toggle(video, "VSync", () => GameSettings.VSync, v => { GameSettings.VSync = v; GameSettings.Apply(false); });
            UIFactory.Cycler(video, "Max FPS",
                () => Array.ConvertAll(GameSettings.MaxFpsOptions, f => f <= 0 ? "Unlimited" : f.ToString()),
                () => GameSettings.MaxFpsIndex, i => { GameSettings.MaxFpsIndex = i; GameSettings.Apply(false); });
            UIFactory.Slider(video, "Field of View", 70, 120, GameSettings.Fov, v => { GameSettings.Fov = Mathf.Round(v); GameSettings.Apply(false); }, v => Mathf.Round(v).ToString());

            var audio = Page("Audio");
            UIFactory.Slider(audio, "Master Volume", 0, 1, GameSettings.MasterVolume, v => { GameSettings.MasterVolume = v; GameSettings.Apply(false); }, Pct);
            UIFactory.Slider(audio, "Effects Volume", 0, 1, GameSettings.SfxVolume, v => { GameSettings.SfxVolume = v; GameSettings.Apply(false); }, Pct);
            UIFactory.Slider(audio, "Music Volume", 0, GameSettings.MaxMusicVolume, GameSettings.MusicVolume, v => { GameSettings.MusicVolume = v; GameSettings.Apply(false); }, Pct);

            var controls = Page("Controls");
            UIFactory.Slider(controls, "Mouse Sensitivity", 0.1f, 10f, GameSettings.Sensitivity, v => { GameSettings.Sensitivity = v; GameSettings.Apply(false); }, v => v.ToString("0.00"));
            UIFactory.Toggle(controls, "Invert Y", () => GameSettings.InvertY, v => { GameSettings.InvertY = v; GameSettings.Apply(false); });
            foreach (GameAction a in Enum.GetValues(typeof(GameAction)))
            {
                var act = a;
                UIFactory.Row(controls, Nice(a), out var area);
                var b = UIFactory.Button("Bind", area, "", () => BeginRebind(act), 22);
                UIFactory.Stretch((RectTransform)b.transform);
                _bindLabels[a] = UIFactory.Label(b);
            }
            UIFactory.Size(UIFactory.Button("ResetBinds", controls, "Reset keys to default", () => { GameSettings.ResetBinds(); RefreshBinds(); }, 22), 40);
            RefreshBinds();

            var back = UIFactory.Button("Back", win, "BACK", () => { _settingsOpen = false; RefreshVisibility(); }, 26);
            UIFactory.Place((RectTransform)back.transform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-30, 25), new Vector2(200, 54));
            Show(0);
        }

        static string Pct(float v) => Mathf.RoundToInt(v * 100) + "%";

        static string Nice(GameAction a) => a switch
        {
            GameAction.Forward => "Move Forward",
            GameAction.Back => "Move Back",
            GameAction.Left => "Move Left",
            GameAction.Right => "Move Right",
            GameAction.Ability1 => "Ability 1",
            GameAction.Ability2 => "Ability 2",
            GameAction.Ability3 => "Ability 3",
            GameAction.Ability4 => "Ability 4",
            GameAction.Ability5 => "Ability 5",
            GameAction.Loadout => "Loadout",
            _ => a.ToString(),
        };

        void RefreshBinds()
        {
            foreach (var kv in _bindLabels)
                kv.Value.text = _rebinding == kv.Key ? "Press a key or mouse button... (Esc to cancel)" : GameSettings.Binds[kv.Key].DisplayName;
        }

        void BeginRebind(GameAction a)
        {
            _rebinding = a;
            _rebindFrame = Time.frameCount;
            RefreshBinds();
        }

        void PollRebind()
        {
            if (Time.frameCount == _rebindFrame) return;
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) { _rebinding = null; RefreshBinds(); return; }

            KeyBind? pressed = null;
            if (kb != null)
                foreach (var k in kb.allKeys)
                    if (k != null && k.wasPressedThisFrame) { pressed = k.keyCode; break; }
            var m = Mouse.current;
            if (pressed == null && m != null)
            {
                if (m.leftButton.wasPressedThisFrame) pressed = KeyBind.Mouse(KeyBind.MouseButton.Left);
                else if (m.rightButton.wasPressedThisFrame) pressed = KeyBind.Mouse(KeyBind.MouseButton.Right);
                else if (m.middleButton.wasPressedThisFrame) pressed = KeyBind.Mouse(KeyBind.MouseButton.Middle);
                else if (m.backButton.wasPressedThisFrame) pressed = KeyBind.Mouse(KeyBind.MouseButton.Back);
                else if (m.forwardButton.wasPressedThisFrame) pressed = KeyBind.Mouse(KeyBind.MouseButton.Forward);
            }
            if (pressed == null) return;

            GameSettings.Binds[_rebinding.Value] = pressed.Value;
            _rebinding = null;
            GameSettings.Apply(false);
            RefreshBinds();
            _hudKeysDirty = true;
        }

        // ------------------------------------------------------------------ HUD

        Image _damageFlash;
        float _damageT, _lastHealth = 100f;
        // mana + health rings (bottom left)
        Image[] _manaSegs;
        Text _manaText;
        Image _healthFill, _healthTrail;
        float _hpShown = 1f, _hpTrail = 1f, _hpTrailHold, _hpLastTarget = 1f;
        bool _hpHealing, _wasDead = true;
        // abilities
        class AbilityBox
        {
            public RectTransform root;
            public Image back, cooldown, frame;
            public RawImage symbol;
            public Text key;
            public RectTransform cost;
            public RawImage costIcon;
            public Text costText;
            public CardId shown = (CardId)255;
        }
        readonly AbilityBox[] _abilities = new AbilityBox[Loadout.Slots];
        readonly float[] _denied = new float[Loadout.Slots];
        bool _hudKeysDirty = true;
        // match
        RectTransform _aliveRow;
        readonly List<Image> _aliveDots = new();
        Text _roundWinner, _spectating;
        CanvasGroup _roundGroup;
        RectTransform _feed;
        readonly List<(CanvasGroup group, float born)> _feedEntries = new();
        const int FeedMax = 6;
        const float FeedLife = 6f;

        void BuildHud()
        {
            _hud = UIFactory.Stretch(UIFactory.Rect("HUD", _canvas.transform));

            _damageFlash = UIFactory.Image("DamageFlash", _hud, new Color(0.8f, 0, 0, 0));
            UIFactory.Stretch(_damageFlash.rectTransform);
            _damageFlash.raycastTarget = false;

            // centre dot
            var dotOutline = UIFactory.Image("DotOutline", _hud, new Color(0, 0, 0, 0.6f), CardView.RoundedSmall);
            dotOutline.type = Image.Type.Sliced;
            UIFactory.Place(dotOutline.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(8, 8));
            var dot = UIFactory.Image("Dot", _hud, Color.white, CardView.RoundedSmall);
            dot.type = Image.Type.Sliced;
            UIFactory.Place(dot.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(5, 5));

            BuildHitMarker();
            BuildRings();
            BuildAbilities();

            // players left (top centre): one dot each, filled while alive
            _aliveRow = UIFactory.Rect("Alive", _hud);
            UIFactory.Place(_aliveRow, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -24), new Vector2(800, 22));
            var hl = _aliveRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = 8;
            hl.childAlignment = TextAnchor.MiddleCenter;
            hl.childControlWidth = hl.childControlHeight = false;
            hl.childForceExpandWidth = hl.childForceExpandHeight = false;

            // round over
            var round = UIFactory.Rect("RoundOver", _hud);
            UIFactory.Place(round, new Vector2(0.5f, 0.68f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1400, 120));
            _roundGroup = round.gameObject.AddComponent<CanvasGroup>();
            _roundGroup.blocksRaycasts = false;
            _roundWinner = UIFactory.Text("Winner", round, "", 78, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            _roundWinner.supportRichText = true;
            UIFactory.Stretch(_roundWinner.rectTransform);
            Outlined(_roundWinner, 3f);

            // who you're watching
            _spectating = UIFactory.Text("Spectating", _hud, "", 30, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            _spectating.supportRichText = true;
            UIFactory.Place(_spectating.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 60), new Vector2(900, 44));
            Outlined(_spectating, 2f);

            // kill feed (top right)
            _feed = UIFactory.Rect("KillFeed", _hud);
            UIFactory.Place(_feed, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-30, -24), new Vector2(700, 400));
            var fv = _feed.gameObject.AddComponent<VerticalLayoutGroup>();
            fv.childAlignment = TextAnchor.UpperRight;
            fv.spacing = 6;
            fv.childControlWidth = fv.childControlHeight = false;
            fv.childForceExpandWidth = fv.childForceExpandHeight = false;

            BuildSongToast();
        }

        void BuildRings()
        {
            Vector2 c = new(130, 130);
            // health: thin outer ring
            var hp = UIFactory.Rect("HealthRing", _hud);
            UIFactory.Place(hp, Vector2.zero, new Vector2(0.5f, 0.5f), c, new Vector2(204, 204));
            UIFactory.Stretch(UIFactory.Image("Back", hp, new Color(0, 0, 0, 0.35f), _thinRing).rectTransform);
            _healthTrail = Radial(UIFactory.Image("Trail", hp, Color.white, _thinRing));
            UIFactory.Stretch(_healthTrail.rectTransform);
            _healthFill = Radial(UIFactory.Image("Fill", hp, Color.white, _thinRing));
            UIFactory.Stretch(_healthFill.rectTransform);

            // mana: 10 segments that fill smoothly
            var ring = UIFactory.Rect("ManaRing", _hud);
            UIFactory.Place(ring, Vector2.zero, new Vector2(0.5f, 0.5f), c, new Vector2(170, 170));
            UIFactory.Stretch(UIFactory.Image("Back", ring, new Color(0, 0, 0, 0.35f), _ring).rectTransform);
            int n = Mathf.RoundToInt(ManaState.Max);
            _manaSegs = new Image[n];
            for (int i = 0; i < n; i++)
            {
                var seg = Radial(UIFactory.Image("Seg" + i, ring, ManaColor, _ring));
                UIFactory.Stretch(seg.rectTransform);
                seg.rectTransform.localRotation = Quaternion.Euler(0, 0, -360f / n * i - 0.6f);
                _manaSegs[i] = seg;
            }
            _manaText = UIFactory.Text("Mana", ring, "0", 58, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            UIFactory.Stretch(_manaText.rectTransform);
            Outlined(_manaText, 2f, new Color(0, 0, 0, 0.6f));
        }

        static Image Radial(Image img)
        {
            img.type = Image.Type.Filled;
            img.fillMethod = Image.FillMethod.Radial360;
            img.fillOrigin = (int)Image.Origin360.Top;
            img.fillClockwise = true;
            return img;
        }

        void BuildAbilities()
        {
            const float size = 86f, gap = 12f, x0 = 262f, y = 40f;
            for (int i = 0; i < Loadout.Slots; i++)
            {
                var b = new AbilityBox();
                b.root = UIFactory.Rect("Ability" + (i + 1), _hud);
                UIFactory.Place(b.root, Vector2.zero, Vector2.zero, new Vector2(x0 + i * (size + gap), y + 30), new Vector2(size, size));
                b.frame = UIFactory.Image("Frame", b.root, Color.black, CardView.RoundedSmall);
                b.frame.type = Image.Type.Sliced;
                UIFactory.Stretch(b.frame.rectTransform, -3, -3, -3, -3);
                b.back = UIFactory.Image("Back", b.root, Color.white, CardView.RoundedSmall);
                b.back.type = Image.Type.Sliced;
                UIFactory.Stretch(b.back.rectTransform);
                b.symbol = UIFactory.Rect("Symbol", b.root).gameObject.AddComponent<RawImage>();
                b.symbol.raycastTarget = false;
                UIFactory.Stretch(b.symbol.rectTransform, 8, 8, 8, 8);
                b.cooldown = Radial(UIFactory.Image("Cooldown", b.root, new Color(0, 0, 0, 0.55f), CardView.RoundedSmall));
                UIFactory.Stretch(b.cooldown.rectTransform);
                // the card's cost, sitting on the box's top edge (same icon as on the card)
                b.cost = UIFactory.Rect("Cost", b.root);
                UIFactory.Place(b.cost, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0, 4), new Vector2(36, 36));
                b.costIcon = UIFactory.Rect("Icon", b.cost).gameObject.AddComponent<RawImage>();
                b.costIcon.raycastTarget = false;
                UIFactory.Stretch(b.costIcon.rectTransform);
                b.costText = UIFactory.Text("Amount", b.cost, "", 19, TextAnchor.MiddleCenter, Color.black, FontStyle.Bold);
                UIFactory.Stretch(b.costText.rectTransform);
                b.key = UIFactory.Text("Key", _hud, "", 20, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
                UIFactory.Place(b.key.rectTransform, Vector2.zero, Vector2.zero, new Vector2(x0 + i * (size + gap), y), new Vector2(size, 26));
                Outlined(b.key, 1.5f, new Color(0, 0, 0, 0.8f));
                _abilities[i] = b;
            }
        }

        void UpdateHud()
        {
            var pc = PlayerController.Local;
            float dt = Time.deltaTime;
            UpdateMatchHud(pc);
            UpdateHits(dt);
            UpdateFeed();
            UpdateSongToast(dt);
            if (pc == null || !pc.IsSpawned) return;

            // damage flash
            float hp = pc.Health.Value;
            if (hp < _lastHealth - 0.01f) _damageT = 1f;
            _lastHealth = hp;
            _damageT = Mathf.MoveTowards(_damageT, 0, dt * 2.5f);
            _damageFlash.color = new Color(0.8f, 0, 0, _damageT * 0.3f);
            UpdateHealthRing(pc, dt);

            // mana
            float mana = Mathf.Max(0f, pc.ManaNow - pc.Caster.PredictedSpend);
            if (pc.Dead.Value) mana = 0f;
            int n = _manaSegs.Length;
            for (int i = 0; i < n; i++)
            {
                float f = Mathf.Clamp01(mana - i);
                _manaSegs[i].fillAmount = (1f / n - 0.012f) * f;
                _manaSegs[i].color = f >= 1f ? ManaColor : ManaPartial;
            }
            _manaText.text = Mathf.FloorToInt(mana + 0.001f).ToString();

            // abilities
            for (int i = 0; i < _abilities.Length; i++)
            {
                var b = _abilities[i];
                var card = pc.Caster.CardIn(i);
                if (card != b.shown)
                {
                    b.shown = card;
                    b.symbol.texture = card != CardId.None ? CardSymbols.Get(card, 128) : null;
                    b.symbol.enabled = card != CardId.None;
                    var info = Cards.Get(card);
                    b.cost.gameObject.SetActive(info != null);
                    if (info != null)
                    {
                        b.costIcon.texture = CardSymbols.CostIcon(info.costType);
                        b.costText.text = pc.Caster.IsFree(i) ? "0" : info.cost.ToString(); // The Fool's card is free
                        b.costText.rectTransform.offsetMin = new Vector2(0, info.costType == CostType.Health ? 3 : 0);
                    }
                }
                if (_hudKeysDirty) b.key.text = GameSettings.Binds[GameSettings.AbilityAction(i)].ShortName;
                bool empty = card == CardId.None;
                float running = pc.Caster.RunningLeft(i); // The Magician still going: shown as a sweep, not red
                bool ready = !empty && (pc.Caster.Affordable(i) || running > 0f) && pc.IsAlive;
                _denied[i] = Mathf.Max(0f, _denied[i] - dt);
                Color back = empty ? new Color(1, 1, 1, 0.08f) : ready ? Color.white : DeniedRed;
                // a card The Fool rolled: shown over a Fool-coloured (mahogany) box
                if (!empty && pc.Caster.IsFree(i)) back = Color.Lerp(back, Cards.ElementColor(Element.Tarot), 0.45f);
                if (_denied[i] > 0f) back = Color.Lerp(back, DeniedRed, 0.6f);
                b.back.color = back;
                b.frame.color = pc.Caster.HoldingSlot == i ? UIFactory.Accent : new Color(0, 0, 0, empty ? 0.3f : 0.85f);
                float cd = Mathf.Max(pc.Caster.CooldownLeft(i) / Cards.Cooldown, running);
                b.cooldown.fillAmount = cd;
                b.cooldown.enabled = cd > 0f;
                float shake = _denied[i] > 0f ? Mathf.Sin(_denied[i] * 80f) * 4f * _denied[i] / 0.35f : 0f;
                b.symbol.rectTransform.anchoredPosition = new Vector2(shake, 0);
                float lift = pc.Caster.HoldingSlot == i ? 8f : 0f;
                b.root.anchoredPosition = new Vector2(b.root.anchoredPosition.x, Mathf.Lerp(b.root.anchoredPosition.y, 70f + lift, 1f - Mathf.Exp(-20f * dt)));
            }
            _hudKeysDirty = false;
        }

        void UpdateMatchHud(PlayerController me)
        {
            // one dot per player: filled = still standing
            var players = PlayerController.All;
            while (_aliveDots.Count < players.Count)
            {
                var d = UIFactory.Image("P", _aliveRow, Color.white, CardView.RoundedSmall);
                d.type = Image.Type.Sliced;
                d.rectTransform.sizeDelta = new Vector2(18, 18);
                _aliveDots.Add(d);
            }
            for (int i = 0; i < _aliveDots.Count; i++)
            {
                bool on = i < players.Count && players[i] != null && players[i].IsSpawned;
                _aliveDots[i].gameObject.SetActive(on);
                if (!on) continue;
                var p = players[i];
                Color c = p.PlayerColor;
                c.a = p.IsAlive ? 1f : 0.2f;
                _aliveDots[i].color = c;
                _aliveDots[i].rectTransform.sizeDelta = Vector2.one * (p == me ? 22f : 18f);
            }

            // round over: the winner
            var s = MatchManager.Current;
            bool over = s.phase == MatchPhase.RoundOver && !string.IsNullOrEmpty(s.winner) && !_loadout.IsOpen;
            _roundGroup.alpha = Mathf.MoveTowards(_roundGroup.alpha, over ? 1f : 0f, Time.deltaTime * 4f);
            if (over)
            {
                bool mine = me != null && s.winner == me.DisplayName;
                string hex = s.winnerColor >= 0 ? PlayerPalette.HexString(s.winnerColor) : "FFFFFF";
                _roundWinner.text = $"♔  <color=#{hex}>{s.winner.ToUpperInvariant()}</color>";
                _roundWinner.rectTransform.localScale = Vector3.one * (1f + Mathf.Max(0f, 0.25f - MatchManager.PhaseTime) * 1.5f);
                if (mine) _roundWinner.text = $"♔  <color=#{hex}>{s.winner.ToUpperInvariant()}</color>  ♔";
            }

            var spec = CameraRig.Spectating;
            _spectating.text = spec != null ? $"<color=#{spec.ColorHex}>{spec.DisplayName}</color>" : "";
        }

        static void Outlined(Text t, float px, Color? c = null)
        {
            var o = t.gameObject.AddComponent<Outline>();
            o.effectColor = c ?? new Color(0, 0, 0, 0.85f);
            o.effectDistance = new Vector2(px, -px);
        }

        void UpdateHealthRing(PlayerController pc, float dt)
        {
            float target = Mathf.Clamp01(pc.Health.Value / Mathf.Max(1f, pc.MaxHealth));
            bool dead = pc.Dead.Value;
            if (dead || _wasDead)
            {
                _hpShown = _hpTrail = target;
                _hpHealing = false;
                _wasDead = dead;
            }
            else
            {
                if (target < _hpLastTarget - 0.0005f)
                {
                    if (_hpHealing) _hpTrail = _hpShown;
                    _hpHealing = false;
                    _hpTrailHold = 0.45f;
                }
                if (target > _hpShown + 0.0005f)
                {
                    _hpHealing = true;
                    _hpTrail = target;
                }
                _hpShown = Mathf.Lerp(_hpShown, target, 1f - Mathf.Exp(-(_hpHealing ? 6f : 14f) * dt));
                if (Mathf.Abs(_hpShown - target) < 0.0005f) _hpShown = target;
                if (_hpHealing)
                {
                    _hpTrail = Mathf.Max(target, _hpShown);
                    if (_hpShown >= target) _hpHealing = false;
                }
                else
                {
                    _hpTrailHold -= dt;
                    if (_hpTrailHold <= 0f) _hpTrail = Mathf.Lerp(_hpTrail, _hpShown, 1f - Mathf.Exp(-5f * dt));
                    if (_hpTrail < _hpShown) _hpTrail = _hpShown;
                }
            }
            _hpLastTarget = target;
            float shownHp = _hpShown * pc.MaxHealth;
            _healthFill.fillAmount = _hpShown;
            _healthFill.color = shownHp > 50 ? Color.white : Color.Lerp(new Color(1f, 0.25f, 0.2f), UIFactory.Accent, shownHp / 50f);
            _healthTrail.fillAmount = _hpTrail;
            _healthTrail.enabled = _hpTrail > _hpShown + 0.001f;
            _healthTrail.color = _hpHealing ? new Color(0.35f, 1f, 0.45f, 0.9f) : new Color(1f, 0.2f, 0.15f, 0.9f);
        }

        // ------------------------------------------------------------------ kill feed

        void AddFeedEntry(string killer, int killerColor, string victim, int victimColor, bool self, DeathCause cause)
        {
            if (_feed == null) return;
            string Col(int c) => c >= 0 ? PlayerPalette.HexString(c) : "C8CCD2";
            var me = PlayerController.Local;
            string myName = me != null ? me.DisplayName : null;
            bool involvesMe = myName != null && (killer == myName || victim == myName);

            var bg = UIFactory.Image("Entry", _feed, involvesMe ? new Color(0.25f, 0.06f, 0.05f, 0.75f) : new Color(0f, 0f, 0f, 0.55f));
            bg.rectTransform.pivot = new Vector2(1, 1);
            var h = bg.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.padding = new RectOffset(12, 12, 5, 5);
            h.spacing = 10;
            h.childAlignment = TextAnchor.MiddleCenter;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = h.childForceExpandHeight = false;
            var fit = bg.gameObject.AddComponent<ContentSizeFitter>();
            fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var group = bg.gameObject.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;

            void Part(string s)
            {
                var t = UIFactory.Text("Text", bg.transform, s, 22, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
                t.supportRichText = true;
            }
            string icon = cause switch { DeathCause.Fall => "↓", DeathCause.Barrier => "☠", _ => "✦" };
            if (!self && !string.IsNullOrEmpty(killer)) Part($"<color=#{Col(killerColor)}>{killer}</color>");
            Part(icon);
            Part($"<color=#{Col(victimColor)}>{victim}</color>");

            bg.transform.SetAsFirstSibling();
            _feedEntries.Insert(0, (group, Time.time));
            while (_feedEntries.Count > FeedMax)
            {
                var old = _feedEntries[^1];
                _feedEntries.RemoveAt(_feedEntries.Count - 1);
                if (old.group) Destroy(old.group.gameObject);
            }
        }

        void UpdateFeed()
        {
            for (int i = _feedEntries.Count - 1; i >= 0; i--)
            {
                var (g, born) = _feedEntries[i];
                float age = Time.time - born;
                if (g == null || age > FeedLife)
                {
                    if (g) Destroy(g.gameObject);
                    _feedEntries.RemoveAt(i);
                    continue;
                }
                g.alpha = Mathf.Clamp01((FeedLife - age) / 0.6f) * Mathf.Clamp01(age / 0.12f);
            }
        }

        // ------------------------------------------------------------------ song toast (bottom right)

        const float ToastLife = 7f;
        const int ToastMaxTitle = 38;
        CanvasGroup _toast;
        Text _toastTitle, _toastBy;
        float _toastT;
        int _toastPlayId;

        void BuildSongToast()
        {
            var bg = UIFactory.Image("SongToast", _hud, new Color(0.04f, 0.05f, 0.07f, 0.85f));
            var r = bg.rectTransform;
            UIFactory.Place(r, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-30, 30), new Vector2(460, 112));
            bg.raycastTarget = false;
            var stripe = UIFactory.Image("Stripe", r, UIFactory.Accent);
            stripe.raycastTarget = false;
            UIFactory.Place(stripe.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), Vector2.zero, new Vector2(5, 112));
            var label = UIFactory.Text("Label", r, "♫", 22, TextAnchor.UpperLeft, UIFactory.Accent, FontStyle.Bold);
            UIFactory.Place(label.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(20, -10), new Vector2(420, 26));
            _toastTitle = UIFactory.Text("Title", r, "", 26, TextAnchor.UpperLeft, Color.white, FontStyle.Bold);
            _toastTitle.supportRichText = false;
            UIFactory.Place(_toastTitle.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(20, -38), new Vector2(425, 34));
            _toastBy = UIFactory.Text("By", r, "", 19, TextAnchor.UpperLeft, UIFactory.Dim);
            _toastBy.supportRichText = true;
            UIFactory.Place(_toastBy.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(20, -76), new Vector2(425, 26));
            _toast = bg.gameObject.AddComponent<CanvasGroup>();
            _toast.alpha = 0f;
            _toast.blocksRaycasts = false;
            MusicNet.StateChanged += OnSongState;
        }

        void OnSongState()
        {
            if (this == null) return;
            var c = MusicNet.Current;
            if (!c.Active || c.playId == _toastPlayId) return;
            _toastPlayId = c.playId;
            if (MusicNet.SongTime > 3.0) return;
            string title = c.title ?? "";
            _toastTitle.text = title.Length > ToastMaxTitle ? title.Substring(0, ToastMaxTitle - 1).TrimEnd() + "…" : title;
            string hex = "FFFFFF";
            foreach (var p in PlayerController.All)
                if (p != null && p.IsSpawned && p.OwnerClientId == c.owner) { hex = p.ColorHex; break; }
            _toastBy.text = MusicNet.IsMine ? "" : $"<color=#{hex}>{c.ownerName}</color>";
            _toastT = ToastLife;
        }

        void UpdateSongToast(float dt)
        {
            _toastT = Mathf.Max(0f, _toastT - dt);
            _toast.alpha = Mathf.Clamp01(_toastT) * Mathf.Clamp01((ToastLife - _toastT) / 0.25f);
        }

        static Sprite MakeRingSprite(int size, float inner)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            float aa = 2f / size;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01((1f - d) / aa) * Mathf.Clamp01((d - inner) / aa);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
