using UnityEngine;
using UnityEngine.UI;

namespace SuddenMageEncounter
{
    /// <summary>The arcane shield's button, bottom right: its key below it, and a ring round it showing its health.</summary>
    public partial class GameUI
    {
        RectTransform _shieldRoot;
        Image _shieldFill, _shieldTrail, _shieldBack, _shieldFrame;
        RawImage _shieldIcon;
        Text _shieldKey;
        float _shieldShown = 1f, _shieldDenied;
        const float ShieldRingSize = 150f, ShieldKeyY = 40f;

        void BuildShield()
        {
            float x = -30f - ShieldRingSize / 2f;
            _shieldRoot = UIFactory.Rect("Shield", _hud);
            UIFactory.Place(_shieldRoot, new Vector2(1, 0), new Vector2(0.5f, 0.5f), new Vector2(x, ShieldKeyY + 32 + ShieldRingSize / 2f), new Vector2(ShieldRingSize, ShieldRingSize));
            UIFactory.Stretch(UIFactory.Image("RingBack", _shieldRoot, new Color(0, 0, 0, 0.45f), _thinRing).rectTransform);
            _shieldTrail = Radial(UIFactory.Image("Trail", _shieldRoot, Color.white, _thinRing));
            UIFactory.Stretch(_shieldTrail.rectTransform);
            _shieldFill = Radial(UIFactory.Image("Fill", _shieldRoot, ManaColor, _thinRing));
            UIFactory.Stretch(_shieldFill.rectTransform);

            var box = UIFactory.Rect("Box", _shieldRoot);
            UIFactory.Place(box, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(86, 86));
            _shieldFrame = UIFactory.Image("Frame", box, Color.black, CardView.RoundedSmall);
            _shieldFrame.type = Image.Type.Sliced;
            UIFactory.Stretch(_shieldFrame.rectTransform, -3, -3, -3, -3);
            _shieldBack = UIFactory.Image("Back", box, Color.white, CardView.RoundedSmall);
            _shieldBack.type = Image.Type.Sliced;
            UIFactory.Stretch(_shieldBack.rectTransform);
            _shieldIcon = UIFactory.Rect("Symbol", box).gameObject.AddComponent<RawImage>();
            _shieldIcon.raycastTarget = false;
            _shieldIcon.texture = Resources.Load<Texture2D>("CardArt/ArcaneShield"); // (the user's sketch)
            UIFactory.Stretch(_shieldIcon.rectTransform, 8, 8, 8, 8);

            _shieldKey = UIFactory.Text("ShieldKey", _hud, "", 20, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            UIFactory.Place(_shieldKey.rectTransform, new Vector2(1, 0), new Vector2(0.5f, 0), new Vector2(x, ShieldKeyY), new Vector2(ShieldRingSize, 26));
            Outlined(_shieldKey, 1.5f, new Color(0, 0, 0, 0.8f));
            AbilityCaster.ShieldDenied += () => _shieldDenied = 0.35f;
        }

        void UpdateShield(PlayerController pc, float dt)
        {
            double now = WorldNet.Now;
            var s = pc.Shield.Value;
            float f = pc.IsAlive ? s.At(now) / s.Max : 0f;
            bool usable = pc.IsAlive && s.Usable(now);
            _shieldShown = f > _shieldShown ? f : Mathf.MoveTowards(_shieldShown, f, dt * 0.8f); // what was just lost lingers
            _shieldFill.fillAmount = f;
            _shieldFill.color = usable ? ManaColor : DeniedRed;
            _shieldTrail.fillAmount = _shieldShown;
            _shieldDenied = Mathf.Max(0f, _shieldDenied - dt);
            Color back = usable ? Color.white : DeniedRed;
            if (_shieldDenied > 0f) back = Color.Lerp(back, DeniedRed, 0.6f);
            _shieldBack.color = back;
            bool up = pc.Caster != null && pc.Caster.ShieldUp;
            _shieldFrame.color = up ? UIFactory.Accent : new Color(0, 0, 0, 0.85f);
            _shieldRoot.localScale = Vector3.one * Mathf.Lerp(_shieldRoot.localScale.x, up ? 1.08f : 1f, 1f - Mathf.Exp(-20f * dt));
            float shake = _shieldDenied > 0f ? Mathf.Sin(_shieldDenied * 80f) * 4f * _shieldDenied / 0.35f : 0f;
            _shieldIcon.rectTransform.anchoredPosition = new Vector2(shake, 0);
            if (_hudKeysDirty) _shieldKey.text = GameSettings.Binds[GameAction.Shield].ShortName;
        }
    }
}
