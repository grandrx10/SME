using UnityEngine;
using UnityEngine.UI;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The patch notes: a window over everything (from the main menu, or the loadout screen's scroll button), newest
    /// update first. Each line is a card, in its element's colour, and what changed. Click outside it or ✕ to close.
    /// </summary>
    public static class PatchNotes
    {
        /// <summary>One line: a card (or a label in a colour) and what changed.</summary>
        struct Note
        {
            public CardId card; public string label; public Color colour; public string text;
            public Note(CardId c, string t) { card = c; label = null; colour = default; text = t; }
            public Note(string l, Color col, string t) { card = CardId.None; label = l; colour = col; text = t; }
        }
        struct Update { public string title; public Note[] notes; }

        static readonly Update[] Updates =
        {
            new()
            {
                title = "Weapons",
                notes = new Note[]
                {
                    new("Blade", Cards.ElementColor(Element.Blade), "Severance, Flowing Cut and the new Heavenly Slash and Parry."),
                    new("Gun", Cards.ElementColor(Element.Gun), "Rachmaninoff, Sebastian, Grapple Hook and the new Wolfgang."),
                    new("Hammer", Cards.ElementColor(Element.Hammer), "Rising Hammer, Descending Hammer and the new Hammer Spin."),
                    new(CardId.HammerSpin, "hold to wind up (at least 0.5 s): 10 m, +10 m a second, up to 40 m. Spin through everything in the way: 25 damage. Nothing knocks you down meanwhile."),
                    new(CardId.Wolfgang, "two pistols, 20 shots each: 2 damage a bullet, each one knocks back. Your body stays facing where you cast it."),
                    new(CardId.HeavenlySlash, "leap up, pose for 1 s, then a 30 m cut along your aim at a random angle: 4 strikes of 10."),
                    new(CardId.Parry, "on guard for 1.5 s: the first blow (not fire) is turned aside, and you dash back and stab whoever dealt it for 15."),
                    new(CardId.SpeedDemon, "passive: faster the less health you have (up to 1.8x near death), a wind trail under 30%. Your shield has 10 less health."),
                    new(CardId.FlyTrap, "is now Rare."),
                    new(CardId.SmallifyHex, "being knocked down while small deals 5 damage."),
                },
            },
            new()
            {
                title = "New cards",
                notes = new Note[]
                {
                    new("Botany", Cards.ElementColor(Element.Botany), "a new element: potted plants held in both hands."),
                    new(CardId.Sunflower, "glows for 3 s, then fires a 25 m beam at whatever you aim at for 3 s."),
                    new(CardId.Cactus, "pricks enemies who touch it or you. You run 1.5x faster while holding it."),
                    new(CardId.FlyTrap, "eats the first enemy it touches. Cast again to throw it, with them inside."),
                    new(CardId.SmallifyHex, "stay near your target to shrink them for 5 s: half size, half speed, and anyone walking into them knocks them over."),
                    new(CardId.CompressedShot, "hold to charge (up to 1.5 s) a bolt of air: 15 to 25 damage, 1.5x on the head, breaks through up to 5 layers of ground."),
                    new(CardId.Ephemerate, "vanish for 0.75 s and fly where you aim, through walls. Come back inside the ground and it hurts."),
                    new(CardId.CloudStep, "a jump three times as high, twice in the air before you land."),
                    new(CardId.Submerge, "hold to dive head first into a pool (up to 2 s): untouchable, 1.5x as fast, healing 5 a second. Let go to burst out for 20 damage."),
                    new(CardId.BubbleBomb, "a bubble mine that drifts 3 m. Live after 1 s: enemies within 2 m set it off for 15 damage. It also puts out fire. Pops harmlessly after 5 s."),
                },
            },
            new()
            {
                title = "Balance update",
                notes = new Note[]
                {
                    new(CardId.Death, "was reworked."),
                    new(CardId.TheHangedMan, "was reworked."),
                    new(CardId.ExplosiveEscape, "was reworked."),
                    new(CardId.TheWorld, "now shatters on its own after 12 s, with a countdown at the top of the screen. It no longer deals damage over time."),
                    new(CardId.TheStar, "starts with 4 stars (was 5). Each throw that hits someone adds a star to your next throw (up to 12; back to 4 when you die)."),
                    new(CardId.TheMoon, "costs 4 (was 5), lasts 15 s (was 10) and fills after 30 damage (was 40)."),
                    new(CardId.TheSun, "deals 12 damage a second (was 15)."),
                    new(CardId.Judgement, "circle 1.25x larger. Deals 30 damage (was 25)."),
                    new(CardId.WheelOfFortune, "costs 6 (was 8)."),
                    new(CardId.TheHierophant, "costs 4 (was 5) and forms in 0.3 s (was 0.5 s)."),
                    new(CardId.TheFool, "costs 4 (was 5)."),
                    new(CardId.TheHermit, "costs 5 (was 6)."),
                    new(CardId.TheEmpress, "enemies inside her circle can't use their shield."),
                    new(CardId.ManaSiphon, "dash distance halved (22 m to 11 m)."),
                    new(CardId.ArcaneSwiftness, "can't be cast again until 1 s after it ends."),
                    new(CardId.ManaMissiles, "costs 3 (was 4)."),
                    new(CardId.MagicalBarrier, "costs 3 (was 4)."),
                    new(CardId.WarpSelf, "range 16.5 m (was 15 m)."),
                    new(CardId.Severance, "dash distance is a quarter of what it was."),
                    new(CardId.Sebastian, "range 75 m (was 25 m). Costs 7 (was 8)."),
                    new(CardId.Rachmaninoff, "pellets are twice as big."),
                    new(CardId.DescendingHammer, "costs 6 (was 7)."),
                    new(CardId.ShatterHex, "costs 3 (was 4) and goes off after 30 damage (was 40)."),
                    new("All Hexes", Cards.ElementColor(Element.Hex), "reach 7.5 m (was 6 m)."),
                    new(CardId.NecromaticBlast, "heals 5 (was 3). Its projectile is 1.25x bigger."),
                    new(CardId.UndeadServitude, "costs 8 (was 10). Works on bodies killed by the map."),
                    new(CardId.Skinwalker, "works on bodies killed by the map."),
                    new(CardId.UndyingGrasp, "lets go once its target takes 30 damage or is ragdolled."),
                    new(CardId.FlameThrower, "adds a burn stack every 1 s (was 0.7 s)."),
                    new(CardId.HellfireWall, "puts 1 burn stack on (was 2)."),
                    new(CardId.Attraction, "moves 1.25x faster. Combined with Repulsion, it reaches 75 m (was 40 m)."),
                    new(CardId.RockSwarm, "lasts 12 s (was 6 s)."),
                    new(CardId.Tornado, "a shield now soaks its damage; you're still lifted and thrown (it no longer breaks the shield)."),
                    new("Volcano Domain", new Color(0.95f, 0.45f, 0.2f), "the lava throws you 25% higher."),
                },
            },
        };

        static GameObject _open;

        /// <summary>Shows the notes over <paramref name="canvas"/> (once: a second call while open does nothing).</summary>
        public static void Open(Transform canvas)
        {
            if (_open != null || canvas == null) return;
            var dim = UIFactory.Image("PatchNotes", canvas, new Color(0.02f, 0.03f, 0.05f, 0.86f));
            _open = dim.gameObject;
            UIFactory.Stretch(dim.rectTransform);
            var sub = dim.gameObject.AddComponent<Canvas>();
            sub.overrideSorting = true;
            sub.sortingOrder = 30; // over the loadout screen (20) and the shop
            dim.gameObject.AddComponent<GraphicRaycaster>();
            var outside = dim.gameObject.AddComponent<Button>();
            outside.transition = Selectable.Transition.None;
            outside.onClick.AddListener(Close);

            var win = UIFactory.Image("Window", dim.transform, new Color(0.075f, 0.085f, 0.11f, 0.98f), CardView.Rounded);
            win.type = Image.Type.Sliced;
            UIFactory.Place(win.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1000, 840));

            var icon = UIFactory.Rect("Icon", win.transform);
            UIFactory.Place(icon, new Vector2(0, 1), new Vector2(0, 1), new Vector2(28, -24), new Vector2(64, 64));
            Icon(icon);
            var close = UIFactory.Button("Close", win.transform, "✕", Close, 28);
            var ci = (Image)close.targetGraphic;
            ci.sprite = CardView.Rounded;
            ci.type = Image.Type.Sliced;
            ci.color = new Color(0.17f, 0.19f, 0.24f, 1f);
            UIFactory.Place((RectTransform)close.transform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-24, -24), new Vector2(64, 64));

            var area = UIFactory.Rect("Notes", win.transform);
            UIFactory.Stretch(area, 32, 28, 24, 108);
            var list = UIFactory.ScrollList(area, 10);
            foreach (var u in Updates)
            {
                var head = UIFactory.Text("Update", list, u.title.ToUpperInvariant(), 30, TextAnchor.MiddleLeft, UIFactory.Accent, FontStyle.Bold);
                UIFactory.Size(head, 44);
                foreach (var n in u.notes) Line(list, (n.label != null ? Coloured(n.label, n.colour) : Name(n.card)) + "  " + n.text);
            }
        }

        public static void Close()
        {
            if (_open != null) Object.Destroy(_open);
            _open = null;
        }

        /// <summary>A card's name in its element's colour (rich text).</summary>
        static string Name(CardId id)
        {
            var info = Cards.Get(id);
            return info == null ? id.ToString() : Coloured(info.name, Cards.ElementColor(info.element));
        }

        static string Coloured(string s, Color c) => "<b><color=#" + ColorUtility.ToHtmlStringRGB(c) + ">" + s + "</color></b>";

        /// <summary>One wrapped line of the list.</summary>
        static void Line(RectTransform list, string text)
        {
            var t = UIFactory.Text("Line", list, text, 24, TextAnchor.UpperLeft, UIFactory.TextCol);
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.supportRichText = true;
        }

        /// <summary>The button's picture: a little sheet of paper with lines of writing on it.</summary>
        public static void Icon(Transform parent)
        {
            Color paper = new(0.93f, 0.85f, 0.66f, 1f), ink = new(0.08f, 0.06f, 0.05f, 1f);
            var sheet = UIFactory.Image("Sheet", parent, paper);
            sheet.raycastTarget = false;
            UIFactory.Place(sheet.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(30, 34));
            sheet.gameObject.AddComponent<Outline>().effectColor = ink;

            for (int i = 0; i < 3; i++)
            {
                var l = UIFactory.Image("Line", sheet.transform, ink);
                l.raycastTarget = false;
                UIFactory.Place(l.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(i == 2 ? -4 : 0, 8 - i * 8), new Vector2(i == 2 ? 14 : 22, 3));
            }
        }
    }
}
