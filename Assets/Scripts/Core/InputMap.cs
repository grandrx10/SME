using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace SuddenMageEncounter
{
    /// <summary>
    /// A key or mouse button a GameAction is bound to. Stored as one int so old saves (plain Key values) still load:
    /// codes below <see cref="MouseBase"/> are <see cref="Key"/> values, codes from MouseBase up are mouse buttons.
    /// </summary>
    public readonly struct KeyBind : IEquatable<KeyBind>
    {
        public const int MouseBase = 10000;
        public enum MouseButton { Left, Right, Middle, Back, Forward }

        public readonly int Code;
        public KeyBind(int code) => Code = code;

        public static implicit operator KeyBind(Key k) => new((int)k);
        public static KeyBind Mouse(MouseButton b) => new(MouseBase + (int)b);

        public bool IsNone => Code == (int)Key.None;
        public bool IsMouse => Code >= MouseBase;

        public static bool IsValidCode(int code) =>
            code >= MouseBase ? code - MouseBase <= (int)MouseButton.Forward : Enum.IsDefined(typeof(Key), code);

        public ButtonControl Control
        {
            get
            {
                if (IsNone) return null;
                if (IsMouse)
                {
                    var m = UnityEngine.InputSystem.Mouse.current;
                    if (m == null) return null;
                    return (MouseButton)(Code - MouseBase) switch
                    {
                        MouseButton.Left => m.leftButton,
                        MouseButton.Right => m.rightButton,
                        MouseButton.Middle => m.middleButton,
                        MouseButton.Back => m.backButton,
                        MouseButton.Forward => m.forwardButton,
                        _ => null,
                    };
                }
                var kb = Keyboard.current;
                return kb != null ? kb[(Key)Code] : null;
            }
        }

        public bool IsPressed { get { var c = Control; return c != null && c.isPressed; } }
        public bool WasPressed { get { var c = Control; return c != null && c.wasPressedThisFrame; } }
        public bool WasReleased { get { var c = Control; return c != null && c.wasReleasedThisFrame; } }

        /// <summary>Short label for HUD key caps: "LMB", "E", "SHIFT".</summary>
        public string ShortName
        {
            get
            {
                if (IsNone) return "-";
                if (IsMouse) return (MouseButton)(Code - MouseBase) switch
                {
                    MouseButton.Left => "LMB",
                    MouseButton.Right => "RMB",
                    MouseButton.Middle => "MMB",
                    MouseButton.Back => "M4",
                    _ => "M5",
                };
                switch ((Key)Code)
                {
                    case Key.LeftShift: case Key.RightShift: return "SHIFT";
                    case Key.LeftCtrl: case Key.RightCtrl: return "CTRL";
                    case Key.LeftAlt: case Key.RightAlt: return "ALT";
                    case Key.Space: return "SPACE";
                    case Key.Tab: return "TAB";
                }
                return DisplayName;
            }
        }

        /// <summary>Upper-case name for prompts, e.g. "SPACE", "B", "RIGHT MOUSE".</summary>
        public string DisplayName
        {
            get
            {
                if (IsNone) return "UNBOUND";
                if (IsMouse) return (MouseButton)(Code - MouseBase) switch
                {
                    MouseButton.Left => "LEFT MOUSE",
                    MouseButton.Right => "RIGHT MOUSE",
                    MouseButton.Middle => "MIDDLE MOUSE",
                    MouseButton.Back => "MOUSE 4",
                    MouseButton.Forward => "MOUSE 5",
                    _ => "MOUSE",
                };
                string n = null;
                try { n = Keyboard.current?[(Key)Code]?.displayName; } catch { /* key missing on this layout */ }
                if (string.IsNullOrWhiteSpace(n)) n = ((Key)Code).ToString();
                return n.ToUpperInvariant();
            }
        }

        public bool Equals(KeyBind o) => Code == o.Code;
        public override bool Equals(object obj) => obj is KeyBind o && Equals(o);
        public override int GetHashCode() => Code;
        public override string ToString() => DisplayName;
    }

    /// <summary>Thin wrapper over the Input System that respects user key bindings.</summary>
    public static class InputMap
    {
        /// <summary>When true, gameplay input is ignored (menus open, typing a rebind, etc).</summary>
        public static bool Blocked;

        public static bool Held(GameAction a) => !Blocked && GameSettings.Binds[a].IsPressed;
        public static bool Down(GameAction a) => !Blocked && GameSettings.Binds[a].WasPressed;
        /// <summary>Ignores <see cref="Blocked"/> — for keys that toggle menus closed again.</summary>
        public static bool DownRaw(GameAction a) => GameSettings.Binds[a].WasPressed;

        /// <summary>"[SPACE]"-style prompt text for an action's current binding.</summary>
        public static string Prompt(GameAction a) => "[" + GameSettings.Binds[a].DisplayName + "]";

        public static bool Up(GameAction a) => !Blocked && GameSettings.Binds[a].WasReleased;

        /// <summary>-1 / 0 / +1 mouse wheel notches this frame.</summary>
        public static int Scroll
        {
            get
            {
                if (Blocked || Mouse.current == null) return 0;
                float y = Mouse.current.scroll.ReadValue().y;
                return y > 0.01f ? 1 : y < -0.01f ? -1 : 0;
            }
        }

        public static bool EscapeDown => Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;

        /// <summary>Mouse look delta in degrees (Source-style: counts * sens * 0.022).</summary>
        public static Vector2 LookDelta
        {
            get
            {
                if (Blocked || Mouse.current == null) return Vector2.zero;
                Vector2 d = Mouse.current.delta.ReadValue() * (GameSettings.Sensitivity * 0.022f);
                if (GameSettings.InvertY) d.y = -d.y;
                return d;
            }
        }

        /// <summary>Raw movement input: x = right, y = forward.</summary>
        public static Vector2 Move
        {
            get
            {
                Vector2 v = Vector2.zero;
                if (Held(GameAction.Forward)) v.y += 1;
                if (Held(GameAction.Back)) v.y -= 1;
                if (Held(GameAction.Right)) v.x += 1;
                if (Held(GameAction.Left)) v.x -= 1;
                return v;
            }
        }
    }
}
