using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Outlines everyone else: enemies red, Team Deathmatch teammates blue. Never through walls.
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
                if (me != null && c != me && c.IsAlive) m = _enemy; // free-for-all: everyone else is an enemy
                c.SetOutline(m);
            }
        }
    }
}
