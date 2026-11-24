using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Admin panel (M, for testing). Locked behind a password the first time it's opened; once unlocked it stays
    /// unlocked until the game closes (never saved). Buttons: gold, an empty collection, every card, a dev test server of your own (max 1).
    /// </summary>
    public class AdminMenu
    {
        const string Password = "SME";
        static bool _unlocked; // this run only

        public bool IsOpen { get; private set; }

        readonly RectTransform _root, _lockPage, _toolsPage, _window;
        readonly InputField _password;
        readonly Image _passwordBack;
        readonly Action _onVisibilityChanged;
        int _focusFrame = -1;
        float _wrongT;
        static readonly Color FieldCol = new(0.12f, 0.13f, 0.16f, 0.95f);
        static readonly Color WrongCol = new(0.6f, 0.12f, 0.1f, 0.95f);

        public AdminMenu(Transform canvas, Action onVisibilityChanged)
        {
            _onVisibilityChanged = onVisibilityChanged;
            var dim = UIFactory.Image("Admin", canvas, new Color(0, 0, 0, 0.6f));
            _root = UIFactory.Stretch(dim.rectTransform);
            var sub = dim.gameObject.AddComponent<Canvas>();
            sub.overrideSorting = true;
            sub.sortingOrder = 70; // over the shop and loadout
            dim.gameObject.AddComponent<GraphicRaycaster>();

            _window = UIFactory.Image("Window", _root, UIFactory.PanelCol).rectTransform;
            UIFactory.Place(_window, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(440, 478));
            var o = _window.gameObject.AddComponent<Outline>();
            o.effectColor = UIFactory.Accent;
            o.effectDistance = new Vector2(3, -3);

            var title = UIFactory.Text("Title", _window, "ADMIN", 34, TextAnchor.MiddleLeft, UIFactory.Accent, FontStyle.Bold);
            UIFactory.Place(title.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(28, -16), new Vector2(300, 44));
            var close = UIFactory.Button("Close", _window, "✕", Close, 26);
            UIFactory.Place((RectTransform)close.transform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-16, -16), new Vector2(44, 44));

            // locked: a password box (Enter to try it)
            _lockPage = UIFactory.Rect("Locked", _window);
            UIFactory.Stretch(_lockPage, 28, 28, 28, 76);
            _password = UIFactory.Input(_lockPage, "Password", "", 32, OnSubmit, 28);
            _password.contentType = InputField.ContentType.Password;
            _passwordBack = (Image)_password.targetGraphic;
            UIFactory.Place((RectTransform)_password.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(360, 60));

            // unlocked: the tools
            _toolsPage = UIFactory.Rect("Tools", _window);
            UIFactory.Stretch(_toolsPage, 28, 28, 28, 76);
            UIFactory.VList(_toolsPage, 14);
            var gold = UIFactory.Button("Gold", _toolsPage, $"+{Rewards.Admin}      ", () => Wallet.Earn(Rewards.Admin), 30);
            UIFactory.Label(gold).color = new Color(1f, 0.86f, 0.4f);
            UIFactory.Size(gold, 60);
            var coin = UIFactory.Rect("Coin", gold.transform).gameObject.AddComponent<RawImage>();
            coin.texture = ShopArt.Coin;
            coin.raycastTarget = false;
            UIFactory.Place(coin.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(34, 0), new Vector2(40, 40));
            UIFactory.Size(UIFactory.Button("Fresh", _toolsPage, "FRESH INVENTORY", () => { Collection.Clear(); Wardrobe.Clear(); Wallet.ResetFirstRound(); }, 26), 60);
            UIFactory.Size(UIFactory.Button("AllCards", _toolsPage, "ALL CARDS", Collection.AddAll, 26), 60);
            UIFactory.Size(UIFactory.Button("AllCosmetics", _toolsPage, "ALL COSMETICS", Wardrobe.AddAll, 26), 60);
            // a new server of your own (max 1 player), for testing alone
            UIFactory.Size(UIFactory.Button("DevServer", _toolsPage, "DEV TEST SERVER", () => { Close(); SessionManager.HostSolo(); }, 26), 60);

            _root.gameObject.SetActive(false);
        }

        public void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }

        public void Open()
        {
            IsOpen = true;
            _password.text = "";
            _wrongT = 0f;
            ShowPage();
            _root.gameObject.SetActive(true);
            _focusFrame = Time.frameCount + 1; // focus next frame, so the M that opened it isn't typed in
            _onVisibilityChanged?.Invoke();
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            _password.DeactivateInputField();
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            _root.gameObject.SetActive(false);
            _onVisibilityChanged?.Invoke();
        }

        public void Update(float dt)
        {
            if (!IsOpen) return;
            if (!_unlocked && Time.frameCount >= _focusFrame && _focusFrame >= 0)
            {
                _focusFrame = -1;
                _password.Select();
                _password.ActivateInputField();
            }
            // wrong password: the box flashes red and shakes
            _wrongT = Mathf.Max(0f, _wrongT - dt);
            _passwordBack.color = Color.Lerp(FieldCol, WrongCol, _wrongT / 0.5f);
            _window.anchoredPosition = new Vector2(_wrongT > 0f ? Mathf.Sin(_wrongT * 90f) * 10f * _wrongT / 0.5f : 0f, 0f);
        }

        void OnSubmit(string text)
        {
            if (!IsOpen || _unlocked || string.IsNullOrEmpty(text)) return;
            if (text == Password)
            {
                _unlocked = true;
                ShowPage();
                return;
            }
            _wrongT = 0.5f;
            _password.text = "";
            _focusFrame = Time.frameCount + 1; // keep typing straight away
        }

        void ShowPage()
        {
            _lockPage.gameObject.SetActive(!_unlocked);
            _toolsPage.gameObject.SetActive(_unlocked);
        }
    }
}
