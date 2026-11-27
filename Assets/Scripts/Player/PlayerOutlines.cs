using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Outlines everyone else: enemies red, Team Deathmatch teammates blue. Never through walls, except whoever a star
    /// from The Star hit. The Sun's inverted colours are put on the body here too.
    /// Creates itself — nothing to add to the scene.
    /// </summary>
    public class PlayerOutlines : MonoBehaviour
    {
        public static readonly Color EnemyColor = new(1f, 0.16f, 0.1f);
        public static readonly Color TeamColor = new(0.2f, 0.55f, 1f);
        const float Width = 2.5f;

        Material _enemy, _team;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            var go = new GameObject("PlayerOutlines");
            DontDestroyOnLoad(go);
            go.AddComponent<PlayerOutlines>();
        }

        void Awake()
        {
            var shader = Shader.Find("SME/HullOutline");
            if (shader == null)
            {
                Debug.LogWarning("[SME] SME/HullOutline shader missing; player outlines disabled.");
                enabled = false;
                return;
            }
            _enemy = Make(shader, "EnemyOutline", EnemyColor);
            _team = Make(shader, "TeamOutline", TeamColor);
            // Wheel of Fortune: golden purple (untouchable), red (about to blow up)
            _gold = Make(shader, "GoldGlow", ArcanaVfx.GoldPurple);
            _doom = Make(shader, "DoomGlow", ArcanaVfx.Doom);
            _sun = Make(shader, "SunGlow", SpellPlans.SunRim);
            foreach (var m in new[] { _gold, _doom, _sun }) m.SetFloat("_Width", Width * 2.2f);
            // The Star: an outline seen through anything. The body first (marked, not drawn), then its hull round it
            var mask = Shader.Find("SME/LitMask");
            var lit = Shader.Find("SME/LitOutline");
            if (mask != null && lit != null)
            {
                _litMask = new Material(mask) { name = "LitMask" };
                _lit = Make(lit, "LitOutline", LitColor);
                _lit.SetFloat("_Width", Width * 1.8f);
                _justice = Make(lit, "JusticeOutline", SpellPlans.JusticeGlow);
                _justice.SetFloat("_Width", Width * 1.8f);
            }
            var invert = Shader.Find("SME/Invert");
            if (invert != null) _invert = new Material(invert) { name = "Inverted" };
        }

        static readonly Color LitColor = new(3.2f, 1.5f, 0.6f, 1f); // glowing mahogany gold
        Material _sun, _litMask, _lit, _justice, _invert;

        Material _gold, _doom;

        /// <summary>A glow the Wheel of Fortune put on this body right now, if any.</summary>
        Material StatusGlow(Combatant c)
        {
            if (!c.IsSpawned || !c.IsAlive) return null;
            var st = c.Status.Value;
            double now = WorldNet.Now;
            if (st.Doomed) return _doom;
            if (st.Gold(now)) return _gold;
            if (st.Sun(now)) return _sun;
            return null;
        }

        static Material Make(Shader s, string name, Color c)
        {
            var m = new Material(s) { name = name };
            m.SetColor("_OutlineColor", c);
            m.SetFloat("_Width", Width);
            return m;
        }

        void LateUpdate()
        {
            var me = PlayerController.Local;
            foreach (var c in Combatant.Registry)
            {
                if (c == null) continue;
                Material m = null;
                // everyone else is an enemy, except teammates, and a necromancer and the Undead on their side
                if (me != null && c != me && c.IsAlive) m = Combatant.Allies(me, c) || Combatant.Teammates(me, c) ? _team : _enemy;
                var glow = StatusGlow(c);
                if (glow != null || c.IsSpawned && c.Status.Value.Doomed) m = glow ?? m;
                Material over = null, round = null;
                if (c.IsSpawned && c.IsAlive)
                {
                    var st = c.Status.Value;
                    double now = WorldNet.Now;
                    if (st.Sun(now)) over = _invert;
                    // Justice: the killer its caster is after, on their screen only
                    else if (me != null && c != me && me.IsSpawned && me.Status.Value.JusticeOn == c.NetworkObjectId) { over = _litMask; round = _justice; }
                    else if (st.Lit(now)) { over = _litMask; round = _lit; }
                }
                c.SetOutline(m, over, round);
            }
        }
    }
}
