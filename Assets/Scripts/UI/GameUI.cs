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
        ShopMenu _shop;
        AdminMenu _admin;

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
            BuildConfirmQuit();
            BuildHud();
            BuildLeaderboard();
            _shop = ShopMenu.Create(_canvas.transform, RefreshVisibility, () => _loadout.Open());
            _loadout = new LoadoutMenu(_canvas.transform, RefreshVisibility, () => _shop.Open());
            _admin = new AdminMenu(_canvas.transform, RefreshVisibility);
            Wallet.Earned += OnGoldEarned;
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

        /// <summary>
        /// The first time a screen opens it pays for its layout, its text glyphs and its first draw, which showed as a
        /// hitch on the first click. So every screen is opened once on the first frame, invisibly, and closed again.
        /// </summary>
        System.Collections.IEnumerator Start()
        {
            var hide = _canvas.gameObject.AddComponent<CanvasGroup>();
            hide.alpha = 0f;
            hide.blocksRaycasts = false;
            // opening the shop and loadout clears their "come look" hints; keep them
            bool shopHint = Wallet.ShopHint, loadoutHint = Collection.LoadoutHint;
            _loadout.Open();
            _shop.Open();
            RefreshVisibility();
            _settings.gameObject.SetActive(true);
            _pause.gameObject.SetActive(true);
            foreach (var p in _settingsPages) p.gameObject.SetActive(true);
            _confirm.gameObject.SetActive(true);
            Canvas.ForceUpdateCanvases();
            yield return null; // one drawn frame
            yield return new WaitForEndOfFrame();

            _shop.CloseQuietly();
            _loadout.Close();
            _showSettingsTab?.Invoke(0);
            Wallet.ShopHint = shopHint;
            Collection.LoadoutHint = loadoutHint;
            _confirm.gameObject.SetActive(false);
            Destroy(hide);
            RefreshVisibility();
        }

        void OnDestroy()
        {
            MusicNet.StateChanged -= OnSongState;
            PlayerController.LocalHitConfirmed -= OnHitConfirmed;
            Wallet.Earned -= OnGoldEarned;
            Wallet.Changed -= OnGoldChanged;
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

        /// <summary>Damage Totals: all we've done to one target without letting up for <see cref="TotalReset"/> s, shown over it.</summary>
        class HitTotal { public Text text; public float total, at = -10f; public Vector3 world; }
        readonly Dictionary<ulong, HitTotal> _totals = new();
        readonly List<ulong> _totalsOver = new();
        /// <summary>Half a second, and a little for the network (and so one burn, which bites every 0.5 s, still adds up).</summary>
        const float TotalReset = 0.6f;

        void ClearTotals()
        {
            foreach (var t in _totals.Values) if (t.text != null) Destroy(t.text.gameObject);
            _totals.Clear();
        }

        Text DamageText(float damage, bool killed)
        {
            var t = UIFactory.Text("Damage", _hud, Mathf.RoundToInt(damage).ToString(), killed ? 46 : 36, TextAnchor.MiddleCenter,
                killed ? new Color(1f, 0.3f, 0.2f) : Color.white, FontStyle.Bold);
            t.rectTransform.sizeDelta = new Vector2(200, 60);
            Outlined(t, 2f);
            return t;
        }

        void AddTotal(ulong victim, Vector3 where, float damage, bool killed)
        {
            if (!_totals.TryGetValue(victim, out var h)) _totals[victim] = h = new HitTotal();
            h.total += damage;
            h.world = where;
            h.at = Time.time;
            if (killed)
            {
                // the final total floats off like any killing number, and it starts over
                if (h.text != null) Destroy(h.text.gameObject);
                _totals.Remove(victim);
                _numbers.Add((DamageText(h.total, true), where, Time.time));
                return;
            }
            if (h.total < 0.5f) return;
            if (h.text == null) h.text = DamageText(h.total, false);
            else h.text.text = Mathf.RoundToInt(h.total).ToString();
        }

        void UpdateTotals(Camera cam, float scale)
        {
            if (_totals.Count == 0) return;
            if (!GameSettings.DamageTotals || cam == null) { ClearTotals(); return; }
            _totalsOver.Clear();
            foreach (var kv in _totals)
            {
                var h = kv.Value;
                float age = Time.time - h.at;
                if (age > TotalReset)
                {
                    // let up: the number floats off, and the next hit starts a new total
                    _totalsOver.Add(kv.Key);
                    if (h.text != null) _numbers.Add((h.text, h.world + Vector3.down * 0.32f, Time.time - 0.4f));
                    continue;
                }
                if (h.text == null) continue;
                var c = WorldNet.FindCombatant(kv.Key);
                if (c != null && c.Visible)
                    h.world = c.RagdollFocus != null ? c.RagdollFocus.position + Vector3.up * 0.7f : c.transform.position + Vector3.up * 1.15f;
                PlaceNumber(h.text, cam.WorldToScreenPoint(h.world), scale, 1f, age);
            }
            foreach (var id in _totalsOver) _totals.Remove(id);
        }

        static void PlaceNumber(Text text, Vector3 screen, float scale, float alpha, float age)
        {
            text.enabled = screen.z > 0f;
            text.rectTransform.anchorMin = text.rectTransform.anchorMax = Vector2.zero;
            text.rectTransform.anchoredPosition = new Vector2(screen.x, screen.y) / scale;
            var c = text.color;
            c.a = alpha;
            text.color = c;
            text.rectTransform.localScale = Vector3.one * (1f + Mathf.Max(0f, 0.15f - age) * 3f);
        }

        void OnHitConfirmed(ulong victim, Vector3 where, float damage, bool killed)
        {
            if (_hud == null) return;
            _hitT = killed ? 1.5f : 1f;
            _hitColor = killed ? new Color(1f, 0.2f, 0.15f) : Color.white;
            _hitMarker.localScale = Vector3.one * (killed ? 1.35f : 1f);
            if (GameSettings.DamageTotals) { AddTotal(victim, where, damage, killed); return; }
            if (damage < 0.5f) return;
            _numbers.Add((DamageText(damage, killed), where + UnityEngine.Random.insideUnitSphere * 0.25f, Time.time));
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
                PlaceNumber(text, cam.WorldToScreenPoint(world + Vector3.up * age * 0.8f), scale, Mathf.Clamp01((1f - age) / 0.35f), age);
            }
            UpdateTotals(cam, scale);
        }

        // ------------------------------------------------------------------ flow

        void SetScreen(UiScreen s)
        {
            _screen = s;
            _paused = false;
            _settingsOpen = false;
            _rebinding = null;
            _loadout?.Close();
            _shop?.CloseQuietly();
            if (_confirm != null) _confirm.gameObject.SetActive(false);
            RefreshVisibility();
        }

        void RefreshVisibility()
        {
            if (_loadout == null || _shop == null || _admin == null) return;
            bool game = _screen == UiScreen.Game;
            _menu.gameObject.SetActive(!game && !_settingsOpen);
            _hud.gameObject.SetActive(game);
            _pause.gameObject.SetActive(game && _paused && !_settingsOpen);
            if (_pausePracticeLabel != null)
            {
                var me = PlayerController.Local;
                _pausePracticeLabel.text = me != null && me.InPractice ? "LEAVE PRACTICE" : "PRACTICE";
            }
            _settings.gameObject.SetActive(_settingsOpen);
            _loadout.SetShown(!_settingsOpen && !(game && _paused));
            _loadout.SetMusicVisible(game);
            _shop.SetShown(!_settingsOpen && !(game && _paused));
            _settings.GetComponent<Image>().color = game ? new Color(0.02f, 0.03f, 0.08f, 0.6f) : MenuBackground;
            bool cursorFree = !game || _paused || _settingsOpen || _loadout.IsOpen || _shop.IsOpen || _admin.IsOpen;
            Cursor.lockState = cursorFree ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = cursorFree;
            InputMap.Blocked = cursorFree;
        }

        void Update()
        {
            if (_rebinding.HasValue) { PollRebind(); return; }

            if (InputMap.EscapeDown)
            {
                if (_confirm.gameObject.activeSelf) _confirm.gameObject.SetActive(false);
                else if (_admin.IsOpen) _admin.Close();
                else if (_servers != null && _servers.gameObject.activeInHierarchy) CloseServers();
                else if (_settingsOpen) { _settingsOpen = false; RefreshVisibility(); }
                else if (!_paused && _shop.IsOpen) _shop.Close();
                else if (!_paused && _loadout.IsOpen) _loadout.Back();
                else if (_screen == UiScreen.Game) { _paused = !_paused; RefreshVisibility(); }
            }
            if (_screen == UiScreen.Game && !_paused && !_settingsOpen && !_shop.IsOpen && !_admin.IsOpen && !LoadoutMenu.IsTyping && InputMap.DownRaw(GameAction.Loadout))
                _loadout.Toggle();
            // clicking back into the game re-locks the cursor (e.g. after alt-tab)
            if (_screen == UiScreen.Game && !_paused && !_settingsOpen && !_loadout.IsOpen && !_shop.IsOpen && !_admin.IsOpen && Cursor.lockState != CursorLockMode.Locked &&
                Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                RefreshVisibility();

            // admin panel (for testing the shop): M opens it, password-locked until unlocked once this run
            if (!LoadoutMenu.IsTyping && Keyboard.current != null && Keyboard.current.mKey.wasPressedThisFrame)
                _admin.Toggle();
            _admin.Update(Time.unscaledDeltaTime);

            if (_screen == UiScreen.Game) { PracticeKeys(); UpdateHud(); }
            UpdateLeaderboard();
        }

        void OnGoldEarned(int amount) => GoldPopup.Show(_canvas.transform, amount);

        /// <summary>In a practice chamber: J resets it, K drops a dummy where you aim, I drops a corpse there, L leaves, H says whether the next round pulls you in (J, L and H work from the loadout screen too).</summary>
        void PracticeKeys()
        {
            var pc = PlayerController.Local;
            if (pc == null || !pc.InPractice || _paused || _settingsOpen || _shop.IsOpen || _admin.IsOpen || LoadoutMenu.IsTyping) return;
            if (InputMap.DownRaw(GameAction.PracticeReset)) pc.RequestPractice(PlayerController.PracticeAction.Reset);
            else if (InputMap.DownRaw(GameAction.PracticeLeave)) { _loadout.Close(); pc.RequestPractice(PlayerController.PracticeAction.Leave); }
            else if (InputMap.DownRaw(GameAction.PracticeRounds)) pc.JoinRounds.Value = !pc.JoinRounds.Value;
            else if (InputMap.Down(GameAction.PracticeDummy) && pc.IsAlive)
            {
                Vector3 p = pc.Caster.AimPoint(false, out bool hit);
                if (hit) pc.RequestPractice(PlayerController.PracticeAction.Summon, p);
                else _noTargetT = 1.2f;
            }
            else if (InputMap.Down(GameAction.PracticeCorpse) && pc.IsAlive)
            {
                // a dead body where you aim (to raise with Undead Servitude)
                Vector3 p = pc.Caster.AimPoint(false, out bool hit);
                if (hit) pc.RequestPractice(PlayerController.PracticeAction.Corpse, p);
                else _noTargetT = 1.2f;
            }
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
            _arcana = MenuArcana.Create(_menu, new Vector2(0.5f, 0.42f), new Vector2(0.5f, 0.72f)); // rune circles first, so they sit behind everything
            var title = UIFactory.Text("Title", _menu, "S.M.E", 150, TextAnchor.MiddleCenter, new Color(0.88f, 0.94f, 1f), FontStyle.Bold);
            UIFactory.Place(title.rectTransform, new Vector2(0.5f, 0.72f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1600, 200));
            Outlined(title, 6f, MenuInk);
            _arcana.Title = title.rectTransform;

            var col = UIFactory.Rect("Buttons", _menu);
            UIFactory.Place(col, new Vector2(0.5f, 0.42f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(360, 392));
            UIFactory.VList(col, 16);
            var play = MenuButton(_arcana, col, "Play", "PLAY", () => SessionManager.Play(), 36, 72);
            ServerListButton((RectTransform)play.transform.parent);
            MenuButton(_arcana, col, "Loadout", "LOADOUT", () => _loadout.Open(), 30, 60);
            MenuButton(_arcana, col, "Shop", "SHOP", () => _shop.Open(), 30, 60);
            MenuButton(_arcana, col, "Settings", "SETTINGS", OpenSettings, 30, 60);
            MenuButton(_arcana, col, "Quit", "QUIT", AskQuit, 30, 60);

            _status = UIFactory.Text("Status", _menu, "", 26, TextAnchor.MiddleCenter, UIFactory.Accent);
            UIFactory.Place(_status.rectTransform, new Vector2(0.5f, 0.15f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1200, 40));

            var nameBox = UIFactory.Rect("Name", _menu);
            UIFactory.Place(nameBox, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-30, -30), new Vector2(360, 90));
            var nl = UIFactory.Text("Label", nameBox, "YOUR NAME", 18, TextAnchor.UpperLeft, UIFactory.Dim, FontStyle.Bold);
            UIFactory.Place(nl.rectTransform, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, new Vector2(340, 24));
            var nf = NameField(nameBox);
            UIFactory.Place((RectTransform)nf.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, -28), new Vector2(360, 54));

            BuildServerList(); // last, so it draws over the menu
        }

        // ------------------------------------------------------------------ server list (the small ☰ next to PLAY)

        RectTransform _servers, _serverRows;
        Text _serverEmpty;
        int _serverQuery;

        /// <summary>A small inked square just right of PLAY that opens the server list.</summary>
        void ServerListButton(RectTransform playHolder)
        {
            var holder = UIFactory.Rect("Servers", playHolder);
            UIFactory.Place(holder, new Vector2(1, 0.5f), new Vector2(0, 0.5f), new Vector2(14, 0), new Vector2(72, 72));
            var ink = UIFactory.Image("Ink", holder, MenuInk, CardView.Rounded);
            ink.type = Image.Type.Sliced;
            ink.raycastTarget = false;
            UIFactory.Stretch(ink.rectTransform);
            var b = UIFactory.Button("Button", holder, "☰", OpenServers, 34);
            var img = (Image)b.targetGraphic;
            img.sprite = CardView.Rounded;
            img.type = Image.Type.Sliced;
            img.color = MenuBlue;
            UIFactory.Stretch(img.rectTransform, 5, 5, 5, 5);
            var cb = b.colors;
            cb.colorMultiplier = 1f;
            cb.normalColor = cb.selectedColor = Color.white;
            cb.highlightedColor = new Color(1.18f, 1.18f, 1.18f, 1f);
            cb.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            b.colors = cb;
            var t = UIFactory.Label(b);
            t.fontStyle = FontStyle.Bold;
            Outlined(t, 2.5f, MenuInk);
        }

        void BuildServerList()
        {
            var dim = UIFactory.Image("ServerList", _menu, new Color(0.02f, 0.03f, 0.08f, 0.8f));
            _servers = UIFactory.Stretch(dim.rectTransform);

            var ink = UIFactory.Image("Ink", _servers, MenuInk, CardView.Rounded);
            ink.type = Image.Type.Sliced;
            UIFactory.Place(ink.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(640, 600));
            var box = UIFactory.Image("Box", ink.rectTransform, new Color(0.11f, 0.12f, 0.17f, 1f), CardView.Rounded);
            box.type = Image.Type.Sliced;
            var boxRt = UIFactory.Stretch(box.rectTransform, 5, 5, 5, 5);

            // header: refresh (left), close (right)
            var refresh = UIFactory.Button("Refresh", boxRt, "↻", RefreshServers, 34);
            ((Image)refresh.targetGraphic).color = MenuBlue;
            UIFactory.Place((RectTransform)refresh.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(18, -18), new Vector2(60, 60));
            var close = UIFactory.Button("Close", boxRt, "✕", CloseServers, 30);
            ((Image)close.targetGraphic).color = QuitRed;
            UIFactory.Place((RectTransform)close.transform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-18, -18), new Vector2(60, 60));

            var listRoot = UIFactory.Rect("List", boxRt);
            UIFactory.Stretch(listRoot, 18, 18, 18, 96);
            _serverRows = UIFactory.ScrollList(listRoot, 10);

            _serverEmpty = UIFactory.Text("Empty", boxRt, "", 40, TextAnchor.MiddleCenter, UIFactory.Dim, FontStyle.Bold);
            _serverEmpty.raycastTarget = false;
            UIFactory.Stretch(_serverEmpty.rectTransform, 18, 18, 18, 96);

            _servers.gameObject.SetActive(false);
        }

        void OpenServers()
        {
            _servers.gameObject.SetActive(true);
            _servers.SetAsLastSibling();
            RefreshServers();
        }

        void CloseServers()
        {
            _serverQuery++; // drop any answer still on its way
            _servers.gameObject.SetActive(false);
        }

        async void RefreshServers()
        {
            int q = ++_serverQuery;
            for (int i = _serverRows.childCount - 1; i >= 0; i--) Destroy(_serverRows.GetChild(i).gameObject);
            _serverEmpty.text = "…";
            var list = await SessionManager.ListServers();
            if (this == null || q != _serverQuery || !_servers.gameObject.activeInHierarchy) return;
            _serverEmpty.text = list == null ? "✕" : list.Count == 0 ? "∅" : "";
            if (list == null) return;
            foreach (var s in list) ServerRow(s);
        }

        /// <summary>One server: a dot (green = room, red = full), players / max, a short id, and a ▶ join button.</summary>
        void ServerRow(Unity.Services.Multiplayer.ISessionInfo s)
        {
            int players = s.MaxPlayers - s.AvailableSlots;
            bool full = s.AvailableSlots <= 0;
            var bg = UIFactory.Image("Server", _serverRows, new Color(1, 1, 1, 0.06f), CardView.Rounded);
            bg.type = Image.Type.Sliced;
            UIFactory.Size(bg, 68);
            var r = bg.rectTransform;

            var dot = UIFactory.Text("Dot", r, "●", 30, TextAnchor.MiddleCenter, full ? DeniedRed : new Color(0.35f, 0.9f, 0.4f));
            UIFactory.Place(dot.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(14, 0), new Vector2(40, 60));
            var count = UIFactory.Text("Players", r, $"{players} / {s.MaxPlayers}", 30, TextAnchor.MiddleLeft, Color.white, FontStyle.Bold);
            UIFactory.Place(count.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(62, 0), new Vector2(160, 60));
            string shortId = s.Id != null && s.Id.Length > 6 ? s.Id.Substring(0, 6) : s.Id;
            var id = UIFactory.Text("Id", r, "#" + shortId, 20, TextAnchor.MiddleLeft, UIFactory.Dim);
            UIFactory.Place(id.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(230, 0), new Vector2(180, 60));

            string sid = s.Id;
            var join = UIFactory.Button("Join", r, "▶", () => { CloseServers(); SessionManager.Join(sid); }, 30);
            ((Image)join.targetGraphic).color = full ? UIFactory.ButtonCol : MenuBlue;
            join.interactable = !full;
            UIFactory.Place((RectTransform)join.transform, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-10, 0), new Vector2(96, 52));
        }

        static readonly Color MenuBlue = new(0.2f, 0.4f, 0.9f, 1f);
        static readonly Color MenuInk = new(0.07f, 0.055f, 0.1f, 1f);
        MenuArcana _arcana, _pauseArcana;

        /// <summary>A main / pause menu button: arcane blue, rounded, inked; pressing it stamps a rune circle (its screen opens a beat later), hovering writes a rune.</summary>
        Button MenuButton(MenuArcana arcana, RectTransform col, string name, string label, Action onClick, int fontSize, float height, bool instant = false)
        {
            var holder = UIFactory.Rect(name, col);
            UIFactory.Size(holder, height);
            var ink = UIFactory.Image("Ink", holder, MenuInk, CardView.Rounded);
            ink.type = Image.Type.Sliced;
            ink.raycastTarget = false;
            UIFactory.Stretch(ink.rectTransform);

            var b = UIFactory.Button("Button", holder, label, () => arcana.Press(holder, onClick, instant), fontSize);
            var img = (Image)b.targetGraphic;
            img.sprite = CardView.Rounded;
            img.type = Image.Type.Sliced;
            img.color = MenuBlue;
            UIFactory.Stretch(img.rectTransform, 5, 5, 5, 5);
            // keep the face its own blue (the default tint washes it out); a little lift on hover
            var cb = b.colors;
            cb.colorMultiplier = 1f;
            cb.normalColor = cb.selectedColor = Color.white;
            cb.highlightedColor = new Color(1.18f, 1.18f, 1.18f, 1f);
            cb.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            b.colors = cb;
            var text = UIFactory.Label(b);
            text.fontStyle = FontStyle.Bold;
            Outlined(text, 2.5f, MenuInk);
            b.gameObject.AddComponent<ShopClick>().onHover = on => { if (on) arcana.Hover(holder); };
            return b;
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
            bool show = _screen == UiScreen.Game && !_paused && !_settingsOpen && !_loadout.IsOpen && !_shop.IsOpen &&
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
            _pause = FullPanel("Pause", new Color(0.02f, 0.03f, 0.08f, 0.6f));
            _pauseArcana = MenuArcana.Create(_pause, new Vector2(0.5f, 0.5f), null, 950f);
            var col = UIFactory.Rect("Buttons", _pause);
            UIFactory.Place(col, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(380, 630));
            UIFactory.VList(col, 14);
            var a = _pauseArcana;
            MenuButton(a, col, "Resume", "RESUME", () => { _paused = false; RefreshVisibility(); }, 32, 64);
            // the practice range: a bullseye and PRACTICE; while you're in there it takes you back out
            var practice = MenuButton(a, col, "Practice", "PRACTICE", TogglePractice, 28, 56);
            _pausePracticeLabel = UIFactory.Label(practice);
            var target = UIFactory.Rect("Target", practice.transform).gameObject.AddComponent<RawImage>();
            target.texture = ShopArt.Target;
            target.raycastTarget = false;
            UIFactory.Place(target.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(10, 0), new Vector2(38, 38));
            MenuButton(a, col, "Loadout", "LOADOUT", () => { _paused = false; _loadout.Open(); }, 28, 56);
            var shop = MenuButton(a, col, "Shop", "SHOP", () => { _paused = false; _shop.Open(); }, 28, 56);
            _pauseShopLabel = UIFactory.Label(shop); // gold while the first-round nudge is up
            MenuButton(a, col, "Settings", "SETTINGS", OpenSettings, 28, 56);
            var nl = UIFactory.Text("NameLabel", col, "YOUR NAME", 18, TextAnchor.LowerLeft, UIFactory.Dim, FontStyle.Bold);
            UIFactory.Size(nl, 26);
            UIFactory.Size(NameField(col), 50);
            MenuButton(a, col, "MainMenu", "MAIN MENU", () => SessionManager.Leave(), 28, 56);
            MenuButton(a, col, "Quit", "QUIT", AskQuit, 28, 56);
        }

        // ------------------------------------------------------------------ quit (asks first)

        RectTransform _confirm;
        static readonly Color QuitRed = new(0.82f, 0.24f, 0.2f, 1f);

        /// <summary>"QUIT GAME?" over everything: NO (blue) goes back, YES (red) closes the game.</summary>
        void BuildConfirmQuit()
        {
            var dim = UIFactory.Image("ConfirmQuit", _canvas.transform, new Color(0.02f, 0.03f, 0.08f, 0.8f));
            _confirm = UIFactory.Stretch(dim.rectTransform);
            var sub = dim.gameObject.AddComponent<Canvas>();
            sub.overrideSorting = true;
            sub.sortingOrder = 80; // over every other screen
            dim.gameObject.AddComponent<GraphicRaycaster>();
            var arcana = MenuArcana.Create(_confirm, new Vector2(0.5f, 0.5f), null, 760f, 320f);

            var frame = UIFactory.Image("Frame", _confirm, MenuInk, CardView.Rounded);
            frame.type = Image.Type.Sliced;
            UIFactory.Place(frame.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(532, 292));
            var winImg = UIFactory.Image("Window", frame.rectTransform, SettingsPanel, CardView.Rounded);
            winImg.type = Image.Type.Sliced;
            var win = UIFactory.Stretch(winImg.rectTransform, 6, 6, 6, 6);
            arcana.StampsIn(win);

            var title = UIFactory.Text("Title", win, "QUIT GAME?", 50, TextAnchor.MiddleCenter, new Color(0.88f, 0.94f, 1f), FontStyle.Bold);
            UIFactory.Place(title.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -34), new Vector2(500, 70));
            Outlined(title, 3.5f, MenuInk);

            var row = UIFactory.Rect("Buttons", win);
            UIFactory.Place(row, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 40), new Vector2(440, 70));
            var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 24; h.childControlWidth = true; h.childForceExpandWidth = true; h.childControlHeight = true;
            MenuButton(arcana, row, "No", "NO", () => _confirm.gameObject.SetActive(false), 32, 70);
            var yes = MenuButton(arcana, row, "Yes", "YES", QuitGame, 32, 70);
            ((Image)yes.targetGraphic).color = QuitRed;
            _confirm.gameObject.SetActive(false);
        }

        void AskQuit() => _confirm.gameObject.SetActive(true);

        static void QuitGame()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        Text _pausePracticeLabel;

        void TogglePractice()
        {
            var pc = PlayerController.Local;
            if (pc == null || !pc.IsSpawned) return;
            pc.RequestPractice(pc.InPractice ? PlayerController.PracticeAction.Leave : PlayerController.PracticeAction.Enter);
            _paused = false;
            RefreshVisibility();
        }

        void BuildSettings()
        {
            _settings = FullPanel("Settings", MenuBackground);
            var arcana = MenuArcana.Create(_settings, new Vector2(0.5f, 0.5f), null, 1300f, 500f);

            // the window: dark navy, rounded, inked
            var frame = UIFactory.Image("Frame", _settings, MenuInk, CardView.Rounded);
            frame.type = Image.Type.Sliced;
            UIFactory.Place(frame.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(912, 772));
            var winImg = UIFactory.Image("Window", frame.rectTransform, SettingsPanel, CardView.Rounded);
            winImg.type = Image.Type.Sliced;
            var win = UIFactory.Stretch(winImg.rectTransform, 6, 6, 6, 6);
            arcana.StampsIn(win);

            var title = UIFactory.Text("Title", win, "SETTINGS", 46, TextAnchor.MiddleLeft, new Color(0.88f, 0.94f, 1f), FontStyle.Bold);
            UIFactory.Place(title.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(30, -18), new Vector2(400, 56));
            Outlined(title, 3.5f, MenuInk);

            var tabs = UIFactory.Rect("Tabs", win);
            UIFactory.Place(tabs, new Vector2(0, 1), new Vector2(0, 1), new Vector2(24, -84), new Vector2(840, 54));
            var h = tabs.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 12; h.childControlWidth = true; h.childForceExpandWidth = true; h.childControlHeight = true;

            var pages = _settingsPages;
            var tabButtons = new List<Button>();
            RectTransform Page(string name)
            {
                var p = UIFactory.Rect(name, win);
                UIFactory.Place(p, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -156), new Vector2(840, 510));
                pages.Add(p);
                return UIFactory.ScrollList(p, 6);
            }
            void Show(int i)
            {
                for (int k = 0; k < pages.Count; k++)
                {
                    pages[k].gameObject.SetActive(k == i);
                    // the open tab is lit arcane blue; the others sit back in dark blue
                    ((Image)tabButtons[k].targetGraphic).color = k == i ? MenuBlue : SettingsControl;
                    UIFactory.Label(tabButtons[k]).color = k == i ? Color.white : UIFactory.Dim;
                }
            }
            _showSettingsTab = Show;
            string[] names = { "VIDEO", "AUDIO", "CONTROLS" };
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                tabButtons.Add(MenuButton(arcana, tabs, names[i], names[i], () => Show(idx), 24, 54, true));
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

            UIFactory.Toggle(video, "Damage Totals", () => GameSettings.DamageTotals, v => { GameSettings.DamageTotals = v; GameSettings.Apply(false); });

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
            foreach (var p in pages) StyleSettingsPage(p);

            var backBox = UIFactory.Rect("BackBox", win);
            UIFactory.Place(backBox, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-24, 22), new Vector2(220, 60));
            UIFactory.VList(backBox, 0);
            MenuButton(arcana, backBox, "Back", "BACK", () => { _settingsOpen = false; RefreshVisibility(); }, 28, 60);
            Show(0);
        }

        static readonly Color SettingsPanel = new(0.07f, 0.085f, 0.15f, 0.98f);
        static readonly Color SettingsControl = new(0.13f, 0.18f, 0.34f, 1f);
        readonly List<RectTransform> _settingsPages = new();
        Action<int> _showSettingsTab;

        /// <summary>
        /// Dresses a settings page (built from the plain <see cref="UIFactory"/> rows) in the menu style: rounded blue
        /// buttons, arcane blue slider fills with a pale handle, and faint stripes behind alternate rows.
        /// </summary>
        static void StyleSettingsPage(RectTransform page)
        {
            int row = 0;
            foreach (Transform child in page.GetComponentsInChildren<Transform>(true))
            {
                if (!child.name.EndsWith("Row") || child.GetComponent<Image>() != null) continue;
                var stripe = child.gameObject.AddComponent<Image>();
                stripe.sprite = CardView.Rounded;
                stripe.type = Image.Type.Sliced;
                stripe.color = new Color(0.5f, 0.65f, 1f, row++ % 2 == 0 ? 0.06f : 0.02f);
                stripe.raycastTarget = false;
            }
            foreach (var b in page.GetComponentsInChildren<Button>(true))
            {
                var img = (Image)b.targetGraphic;
                img.sprite = CardView.Rounded;
                img.type = Image.Type.Sliced;
                var label = UIFactory.Label(b);
                bool arrow = label != null && (label.text == "<" || label.text == ">");
                img.color = arrow ? MenuBlue : SettingsControl;
                var cb = b.colors;
                cb.colorMultiplier = 1f;
                cb.normalColor = cb.selectedColor = Color.white;
                cb.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
                cb.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
                b.colors = cb;
                if (label != null) label.fontStyle = FontStyle.Bold;
            }
            foreach (var s in page.GetComponentsInChildren<Slider>(true))
            {
                if (s.fillRect != null) s.fillRect.GetComponent<Image>().color = new Color(0.35f, 0.56f, 1f, 1f);
                var track = s.transform.Find("Background")?.GetComponent<Image>();
                if (track != null) track.color = SettingsControl;
                var handle = s.handleRect != null ? s.handleRect.GetComponent<Image>() : null;
                if (handle != null)
                {
                    handle.sprite = CardView.RoundedSmall;
                    handle.type = Image.Type.Sliced;
                    handle.color = new Color(0.88f, 0.94f, 1f, 1f);
                    handle.rectTransform.sizeDelta = new Vector2(20, 0);
                }
            }
            foreach (var sb in page.GetComponentsInChildren<Scrollbar>(true))
                if (sb.handleRect != null) sb.handleRect.GetComponent<Image>().color = new Color(0.35f, 0.56f, 1f, 0.6f);
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
            GameAction.PracticeReset => "Practice: Reset",
            GameAction.PracticeDummy => "Practice: Dummy",
            GameAction.PracticeCorpse => "Practice: Corpse",
            GameAction.PracticeLeave => "Practice: Leave",
            GameAction.PracticeRounds => "Practice: Join Rounds",
            GameAction.SkipVote => "Vote: Skip Break",
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
        RectTransform _manaRing;
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
        Text _roundWinner, _spectating, _countdown, _mapName;
        float _mapScale;
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

            // round over
            var round = UIFactory.Rect("RoundOver", _hud);
            UIFactory.Place(round, new Vector2(0.5f, 0.68f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1400, 120));
            _roundGroup = round.gameObject.AddComponent<CanvasGroup>();
            _roundGroup.blocksRaycasts = false;
            _roundWinner = UIFactory.Text("Winner", round, "", 78, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            _roundWinner.supportRichText = true;
            UIFactory.Stretch(_roundWinner.rectTransform);
            Outlined(_roundWinner, 3f);

            // 3-2-1 before a round
            _countdown = UIFactory.Text("Countdown", _hud, "", 220, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            _countdown.raycastTarget = false;
            UIFactory.Place(_countdown.rectTransform, new Vector2(0.5f, 0.62f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(800, 300));
            Outlined(_countdown, 6f, new Color(0.06f, 0.05f, 0.08f, 1f));

            // the map's name while it builds itself (top middle)
            _mapName = UIFactory.Text("MapName", _hud, "", 60, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            _mapName.raycastTarget = false;
            UIFactory.Place(_mapName.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -60), new Vector2(1400, 90));
            Outlined(_mapName, 4f, new Color(0.06f, 0.05f, 0.08f, 1f));
            _mapName.rectTransform.localScale = Vector3.zero;

            // who you're watching
            _spectating = UIFactory.Text("Spectating", _hud, "", 30, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            _spectating.supportRichText = true;
            UIFactory.Place(_spectating.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 60), new Vector2(900, 44));
            Outlined(_spectating, 2f);

            BuildSkipVote();

            BuildGoldCounter();

            // kill feed (top right, under the gold)
            _feed = UIFactory.Rect("KillFeed", _hud);
            UIFactory.Place(_feed, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-30, TopRightY), new Vector2(700, 400));
            var fv = _feed.gameObject.AddComponent<VerticalLayoutGroup>();
            fv.childAlignment = TextAnchor.UpperRight;
            fv.spacing = 6;
            fv.childControlWidth = fv.childControlHeight = false;
            fv.childForceExpandWidth = fv.childForceExpandHeight = false;

            BuildPracticeHints();
            BuildSongToast();
        }

        // ------------------------------------------------------------------ skip the break between rounds (top middle)

        RectTransform _skipBox;
        Image _skipBg;
        Text _skipKey, _skipIcon, _skipPips;
        float _skipScale, _skipPunch;
        bool _skipWasVoted;
        static readonly Color SkipOn = new(0.4f, 0.95f, 0.5f);

        /// <summary>Under the break's timer: a button (click it, or press its key) showing ►► and a pip per vote needed.</summary>
        void BuildSkipVote()
        {
            var b = UIFactory.Button("SkipVote", _hud, "", MatchManager.ToggleSkipVote);
            _skipBox = b.GetComponent<RectTransform>();
            _skipBg = b.GetComponent<Image>();
            _skipBg.sprite = CardView.RoundedSmall;
            _skipBg.type = Image.Type.Sliced;
            UIFactory.Place(_skipBox, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -158), new Vector2(330, 62));
            var o = _skipBox.gameObject.AddComponent<Outline>();
            o.effectColor = UIFactory.Accent;
            o.effectDistance = new Vector2(2, -2);

            var cap = UIFactory.Image("Key", _skipBox, new Color(0.08f, 0.09f, 0.12f, 0.9f), CardView.RoundedSmall);
            cap.type = Image.Type.Sliced;
            cap.raycastTarget = false;
            UIFactory.Place(cap.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(9, 0), new Vector2(46, 46));
            _skipKey = UIFactory.Text("Cap", cap.rectTransform, "", 22, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            _skipKey.raycastTarget = false;
            UIFactory.Stretch(_skipKey.rectTransform);

            _skipIcon = UIFactory.Text("Icon", _skipBox, "►►", 30, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            _skipIcon.raycastTarget = false;
            UIFactory.Place(_skipIcon.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(62, 0), new Vector2(70, 50));
            Outlined(_skipIcon, 2f);

            _skipPips = UIFactory.Text("Pips", _skipBox, "", 26, TextAnchor.MiddleLeft, Color.white, FontStyle.Bold);
            _skipPips.supportRichText = true;
            _skipPips.raycastTarget = false;
            UIFactory.Place(_skipPips.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(140, 0), new Vector2(180, 50));
            Outlined(_skipPips, 2f);

            _skipBox.localScale = Vector3.zero;
        }

        /// <summary>Shown during the break between rounds: grows in, green once you've voted, one pip lit per vote.</summary>
        void UpdateSkipVote(PlayerController me, MatchManager.State s)
        {
            bool show = MatchManager.InIntermission && !(me != null && me.InPractice) && !_loadout.IsOpen && !_shop.IsOpen;
            // the key works whether or not the cursor is free (the button is there to click when it is)
            if (MatchManager.InIntermission && !_paused && !_settingsOpen && !_admin.IsOpen && !LoadoutMenu.IsTyping && InputMap.DownRaw(GameAction.SkipVote))
                MatchManager.ToggleSkipVote();

            bool voted = MatchManager.LocalSkipVote;
            if (voted != _skipWasVoted) { _skipWasVoted = voted; _skipPunch = 1f; }
            _skipPunch = Mathf.MoveTowards(_skipPunch, 0f, Time.deltaTime * 5f);
            _skipScale = Mathf.MoveTowards(_skipScale, show ? 1f : 0f, Time.deltaTime * 6f);
            _skipBox.localScale = Vector3.one * (_skipScale * (1f + 0.15f * _skipPunch));
            _skipBox.gameObject.SetActive(_skipScale > 0.001f);
            if (_skipScale <= 0.001f) return;

            _skipKey.text = GameSettings.Binds[GameAction.SkipVote].ShortName;
            _skipBg.color = voted ? new Color(SkipOn.r * 0.4f, SkipOn.g * 0.4f, SkipOn.b * 0.4f, 0.95f) : new Color(0.1f, 0.11f, 0.15f, 0.85f);
            _skipIcon.color = voted ? SkipOn : Color.white;
            int need = MatchManager.SkipNeeded(s.skipVoters), have = Mathf.Min(s.skipVotes, need);
            var sb = new System.Text.StringBuilder();
            sb.Append("<color=#66F27F>");
            for (int i = 0; i < have; i++) sb.Append('●');
            sb.Append("</color><color=#8A8E99>");
            for (int i = have; i < need; i++) sb.Append('○');
            sb.Append("</color>");
            _skipPips.text = sb.ToString();
        }

        // ------------------------------------------------------------------ gold (top right) + the first-round shop nudge

        /// <summary>Where the top-right stack (practice keys, kill feed) starts: under the gold counter.</summary>
        const float TopRightY = -24f - GoldH - 14f;
        const float GoldH = 52f;
        RectTransform _goldBox, _shopHint;
        Text _pauseShopLabel;
        Text _goldText;
        float _goldPulse, _hintT;

        void BuildGoldCounter()
        {
            _goldBox = UIFactory.Rect("Gold", _hud);
            UIFactory.Place(_goldBox, new Vector2(1, 1), new Vector2(1, 0.5f), new Vector2(-30, -24 - GoldH / 2f), new Vector2(240, GoldH));
            _goldText = ShopMenu.CoinAmount(_goldBox, Wallet.Gold.ToString(), 40, true);
            Wallet.Changed += OnGoldChanged;

            // "go to the shop": a pack, a gold arrow and the Esc key that gets you there, bobbing under the counter
            var hint = UIFactory.Image("ShopHint", _hud, new Color(0.07f, 0.055f, 0.1f, 0.92f), CardView.RoundedSmall);
            hint.type = Image.Type.Sliced;
            hint.raycastTarget = false;
            _shopHint = UIFactory.Place(hint.rectTransform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-30, TopRightY), new Vector2(330, 84));
            var o = hint.gameObject.AddComponent<Outline>();
            o.effectColor = new Color(1f, 0.86f, 0.4f, 1f);
            o.effectDistance = new Vector2(3, -3);
            var pack = UIFactory.Rect("Pack", _shopHint).gameObject.AddComponent<RawImage>();
            pack.texture = ShopArt.Pack;
            pack.raycastTarget = false;
            UIFactory.Place(pack.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(14, 0), new Vector2(46, 64));
            var word = UIFactory.Text("Shop", _shopHint, "SHOP", 30, TextAnchor.MiddleLeft, new Color(1f, 0.86f, 0.4f), FontStyle.Bold);
            UIFactory.Place(word.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(72, 12), new Vector2(150, 36));
            Outlined(word, 2f);
            var sub = UIFactory.Text("Buy", _shopHint, "Buy a card pack!", 19, TextAnchor.MiddleLeft, Color.white);
            UIFactory.Place(sub.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(72, -18), new Vector2(170, 26));
            var cap = UIFactory.Image("Esc", _shopHint, new Color(0.17f, 0.19f, 0.24f, 1f), CardView.RoundedSmall);
            cap.type = Image.Type.Sliced;
            cap.raycastTarget = false;
            UIFactory.Place(cap.rectTransform, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-14, 0), new Vector2(70, 50));
            var co = cap.gameObject.AddComponent<Outline>();
            co.effectColor = new Color(1f, 0.86f, 0.4f, 1f);
            co.effectDistance = new Vector2(2, -2);
            var esc = UIFactory.Text("Key", cap.rectTransform, "ESC", 22, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            UIFactory.Stretch(esc.rectTransform);
            _shopHint.localScale = Vector3.zero;
            Wallet.FirstRoundPaid += () => _hintT = 0f; // pop it in fresh
        }

        void OnGoldChanged()
        {
            if (_goldText == null) return;
            _goldText.text = Wallet.Gold.ToString();
            _goldPulse = 1f;
        }

        void UpdateGold(float dt)
        {
            _goldPulse = Mathf.MoveTowards(_goldPulse, 0f, dt * 4f);
            _goldBox.localScale = Vector3.one * (1f + 0.3f * _goldPulse * _goldPulse);

            // the nudge stays until the shop's been opened (pops in, bobs, shrinks away once it's done its job)
            bool want = Wallet.ShopHint;
            _hintT += dt;
            float s = _shopHint.localScale.x;
            s = want ? (s < 0.01f ? 0.01f : Mathf.Lerp(s, 1f + Mathf.Sin(_hintT * 4f) * 0.04f, 1f - Mathf.Exp(-12f * dt)))
                     : Mathf.MoveTowards(s, 0f, dt * 6f);
            _shopHint.localScale = Vector3.one * s;
            _shopHint.anchoredPosition = new Vector2(-30, TopRightY + Mathf.Sin(_hintT * 2.5f) * 4f);
            // the kill feed and practice keys move down out of its way
            float push = s > 0.01f ? (_shopHint.sizeDelta.y + 14f) * Mathf.Clamp01(s) : 0f;
            _practiceHints.anchoredPosition = new Vector2(-30, TopRightY - push);
            float feedY = _wasPracticing ? TopRightY - push - _practiceHints.sizeDelta.y - 20 : TopRightY - push;
            _feed.anchoredPosition = new Vector2(-30, feedY);
            if (_pauseShopLabel != null) _pauseShopLabel.color = want ? new Color(1f, 0.86f, 0.4f) : UIFactory.TextCol;
        }

        // ------------------------------------------------------------------ practice chamber (top right)

        const float HintRow = 46f, HintGap = 8f;
        RectTransform _practiceHints;
        const int HintCount = 5;
        readonly Text[] _hintKeys = new Text[HintCount];
        readonly Text[] _hintWords = new Text[HintCount];
        Image _roundsCap;
        bool _wasPracticing;

        void BuildPracticeHints()
        {
            _practiceHints = UIFactory.Rect("PracticeKeys", _hud);
            UIFactory.Place(_practiceHints, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-30, TopRightY), new Vector2(260, HintCount * HintRow + (HintCount - 1) * HintGap));
            string[] words = { "RESET", "DUMMY", "CORPSE", "LEAVE", "ROUNDS" };
            for (int i = 0; i < HintCount; i++)
            {
                float y = -i * (HintRow + HintGap);
                var cap = UIFactory.Image("Key", _practiceHints, new Color(0.08f, 0.09f, 0.12f, 0.85f), CardView.RoundedSmall);
                cap.type = Image.Type.Sliced;
                cap.raycastTarget = false;
                UIFactory.Place(cap.rectTransform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(0, y), new Vector2(HintRow, HintRow));
                var o = cap.gameObject.AddComponent<Outline>();
                o.effectColor = UIFactory.Accent;
                o.effectDistance = new Vector2(2, -2);
                _hintKeys[i] = UIFactory.Text("Cap", cap.rectTransform, "", 24, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
                UIFactory.Stretch(_hintKeys[i].rectTransform);
                var word = UIFactory.Text("Word", _practiceHints, words[i], 24, TextAnchor.MiddleRight, Color.white, FontStyle.Bold);
                _hintWords[i] = word;
                if (i == HintCount - 1) _roundsCap = cap;
                word.raycastTarget = false;
                UIFactory.Place(word.rectTransform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-HintRow - 12, y), new Vector2(190, HintRow));
                Outlined(word, 2f);
            }
            _practiceHints.gameObject.SetActive(false);
        }

        void UpdatePracticeHints(PlayerController me)
        {
            bool on = me != null && me.InPractice;
            if (on != _wasPracticing || (on && _hudKeysDirty))
            {
                if (on != _wasPracticing && _loadout.IsOpen) _loadout.Refresh(); // the practice button lights up / goes dark
                _wasPracticing = on;
                _practiceHints.gameObject.SetActive(on);
                _hintKeys[0].text = GameSettings.Binds[GameAction.PracticeReset].ShortName;
                _hintKeys[1].text = GameSettings.Binds[GameAction.PracticeDummy].ShortName;
                _hintKeys[2].text = GameSettings.Binds[GameAction.PracticeCorpse].ShortName;
                _hintKeys[3].text = GameSettings.Binds[GameAction.PracticeLeave].ShortName;
                _hintKeys[4].text = GameSettings.Binds[GameAction.PracticeRounds].ShortName;
                // (the kill feed moves down out of the way: UpdateGold)
            }
            if (!on) return;
            // join rounds: a green tick while the next round will pull you in, a dim cross while you stay here
            bool joins = me.JoinRounds.Value;
            _hintWords[HintCount - 1].text = joins ? "ROUNDS ✓" : "ROUNDS ✕";
            _hintWords[HintCount - 1].color = joins ? RoundsOn : new Color(0.6f, 0.6f, 0.65f);
            _roundsCap.color = joins ? new Color(RoundsOn.r * 0.45f, RoundsOn.g * 0.45f, RoundsOn.b * 0.45f, 0.95f) : new Color(0.08f, 0.09f, 0.12f, 0.85f);
        }

        static readonly Color RoundsOn = new(0.4f, 0.95f, 0.5f);

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
            _manaRing = ring;
            BuildManaSegs(Mathf.RoundToInt(ManaState.Max));
            _manaText = UIFactory.Text("Mana", ring, "0", 58, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            UIFactory.Stretch(_manaText.rectTransform);
            Outlined(_manaText, 2f, new Color(0, 0, 0, 0.6f));
        }

        /// <summary>One segment of the mana ring per point this player can hold.</summary>
        void BuildManaSegs(int n)
        {
            n = Mathf.Max(1, n);
            if (_manaSegs != null) foreach (var s in _manaSegs) if (s != null) Destroy(s.gameObject);
            _manaSegs = new Image[n];
            for (int i = 0; i < n; i++)
            {
                var seg = Radial(UIFactory.Image("Seg" + i, _manaRing, ManaColor, _ring));
                UIFactory.Stretch(seg.rectTransform);
                seg.rectTransform.localRotation = Quaternion.Euler(0, 0, -360f / n * i - 0.6f);
                seg.transform.SetSiblingIndex(1 + i); // under the number
                _manaSegs[i] = seg;
            }
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
            UpdateGold(Time.unscaledDeltaTime);
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

            // mana (as many segments as this player can hold: Temperance adds some, the Undead have fewer)
            int cap = Mathf.RoundToInt(pc.Mana.Value.Limit);
            if (cap != _manaSegs.Length) BuildManaSegs(cap);
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
                        b.costText.rectTransform.offsetMin = new Vector2(0, info.costType == CostType.Health ? 3 : 0);
                    }
                }
                var shownInfo = Cards.Get(card);
                if (shownInfo != null) // (every frame: a Fool card is free, Temperance adds 1 to the others)
                    b.costText.text = shownInfo.costType == CostType.Passive ? "P" : pc.Caster.CostIn(i).ToString();
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
            // round over: the winner
            UpdatePracticeHints(me);
            var s = MatchManager.Current;
            bool over = s.phase == MatchPhase.RoundOver && !string.IsNullOrEmpty(s.winner) && !_loadout.IsOpen && !(me != null && me.InPractice);
            _roundGroup.alpha = Mathf.MoveTowards(_roundGroup.alpha, over ? 1f : 0f, Time.deltaTime * 4f);
            if (over)
            {
                bool mine = me != null && s.winner == me.DisplayName;
                string hex = s.winnerColor >= 0 ? PlayerPalette.HexString(s.winnerColor) : "FFFFFF";
                _roundWinner.text = $"♔  <color=#{hex}>{s.winner.ToUpperInvariant()}</color>";
                _roundWinner.rectTransform.localScale = Vector3.one * (1f + Mathf.Max(0f, 0.25f - MatchManager.PhaseTime) * 1.5f);
                if (mine) _roundWinner.text = $"♔  <color=#{hex}>{s.winner.ToUpperInvariant()}</color>  ♔";
            }

            UpdateCountdown(me, s);
            UpdateMapName(me, s);
            UpdateSkipVote(me, s);

            var spec = CameraRig.Spectating;
            _spectating.text = spec != null ? $"<color=#{spec.ColorHex}>{spec.DisplayName}</color>" : "";
        }

        static readonly Color[] CountColors =
        {
            new(0.45f, 1f, 0.5f),   // FIGHT
            new(1f, 0.92f, 0.3f),   // 1
            new(1f, 0.65f, 0.2f),   // 2
            new(1f, 0.35f, 0.3f),   // 3
        };

        /// <summary>While the arena builds itself before a round: its name, top middle. Punches in big, shrinks away when the build is done.</summary>
        void UpdateMapName(PlayerController me, MatchManager.State s)
        {
            bool show = s.phase == MatchPhase.Building && !(me != null && me.InPractice) && !string.IsNullOrEmpty(MatchManager.ArenaName);
            if (show)
            {
                _mapName.text = MatchManager.ArenaName.ToUpperInvariant();
                float t = MatchManager.PhaseTime;
                _mapScale = t < 0.15f ? Mathf.Lerp(1.6f, 1f, Mathf.Clamp01(t / 0.15f)) : Mathf.MoveTowards(_mapScale, 1f, Time.deltaTime * 4f);
            }
            else if (s.phase == MatchPhase.Intermission && !(me != null && me.InPractice))
            {
                // the break between rounds: an hourglass and the seconds left, ticking
                int left = Mathf.Max(0, Mathf.CeilToInt(MatchManager.IntermissionSeconds - MatchManager.PhaseTime));
                _mapName.text = $"⌛  {left}";
                float tick = 1f - (MatchManager.IntermissionSeconds - MatchManager.PhaseTime - (left - 1));
                _mapScale = Mathf.MoveTowards(_mapScale, 0.8f + 0.08f * Mathf.Clamp01(1f - tick * 4f), Time.deltaTime * 4f);
            }
            else if (MatchManager.WaitingForPlayers && !(me != null && me.InPractice) && !_loadout.IsOpen)
            {
                // not enough players for a round: an hourglass and how many are ready, breathing gently
                _mapName.text = $"⌛  {MatchManager.ReadyForRound(out _)} / {MatchManager.MinPlayers}";
                _mapScale = Mathf.MoveTowards(_mapScale, 0.8f + 0.04f * Mathf.Sin(Time.time * 3f), Time.deltaTime * 4f);
            }
            else _mapScale = Mathf.MoveTowards(_mapScale, 0f, Time.deltaTime * 5f);
            _mapName.rectTransform.localScale = Vector3.one * _mapScale;
        }

        /// <summary>The 3-2-1 before a round (everyone frozen in place), then FIGHT: each number punches in big, holds, and shrinks away.</summary>
        void UpdateCountdown(PlayerController me, MatchManager.State s)
        {
            float t = MatchManager.PhaseTime;
            const float go = 0.9f;
            int n = -1;          // 3, 2, 1, 0 = FIGHT
            float u = 0f, life = 1f;   // time into this number, and how long it lasts
            if (s.phase == MatchPhase.Countdown && t >= 0f)
            {
                if (t < MatchManager.CountdownSeconds)
                {
                    n = Mathf.Clamp(Mathf.CeilToInt(MatchManager.CountdownSeconds - t), 1, 3);
                    u = t - Mathf.Floor(t);
                }
                else { n = 0; u = t - MatchManager.CountdownSeconds; life = go; }
            }
            else if (s.phase == MatchPhase.Playing && t >= 0f && t < go) { n = 0; u = t; life = go; }
            if (me != null && me.InPractice) n = -1; // off practising: not your round

            if (n < 0)
            {
                if (_countdown.text.Length > 0) _countdown.text = "";
                return;
            }
            _countdown.text = n == 0 ? "FIGHT!" : n.ToString();
            _countdown.color = CountColors[n];
            float k = u / life;
            float scale = k < 0.15f ? Mathf.Lerp(1.7f, 1f, k / 0.15f)           // punch in
                        : k > 0.8f ? Mathf.Lerp(1f, 0f, (k - 0.8f) / 0.2f)        // shrink away
                        : 1f;
            _countdown.rectTransform.localScale = Vector3.one * scale;
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
