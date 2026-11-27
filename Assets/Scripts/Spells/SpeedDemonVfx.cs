using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Speed Demon: at 30% health or less, inked wind strokes stream off behind the body while it moves (more of them,
    /// and longer, the lower the health). Each stroke is left behind in the world, writes on and wipes away.
    /// </summary>
    public class SpeedDemonView : MonoBehaviour
    {
        class Stroke { public ToonLine line; public Vector3 a, b; public float born, life; }

        PlayerController _pc;
        Transform _root;
        readonly List<Stroke> _strokes = new();
        readonly List<Vector3> _pts = new(), _part = new();
        float _owed;

        void Awake() => _pc = GetComponent<PlayerController>();

        void LateUpdate()
        {
            if (_pc == null || !_pc.IsSpawned) return;
            float dt = Time.deltaTime;
            float share = SpellPlans.HealthShare(_pc);
            Vector3 vel = _pc.ViewVelocity;
            vel.y = 0f;
            float speed = vel.magnitude;
            bool on = !Debris.Suppressed && SpellPlans.HasSpeedDemon(_pc) && share <= SpellPlans.DemonTrailBelow && _pc.Visible && !_pc.HasLiveRagdoll && speed > 1.5f;
            if (on)
            {
                // lower health: thicker with strokes, and they reach further back
                float k = 1f - share / SpellPlans.DemonTrailBelow;
                _owed += Mathf.Lerp(10f, 26f, k) * dt;
                Vector3 dir = vel / speed, side = Vector3.Cross(Vector3.up, dir);
                Vector3 hips = _pc.transform.position;
                while (_owed >= 1f)
                {
                    _owed -= 1f;
                    if (_root == null) _root = new GameObject("SpeedDemonWind") { layer = Layers.Fx }.transform;
                    Vector3 from = hips + side * Random.Range(-0.4f, 0.4f) + Vector3.up * Random.Range(-0.75f, 0.6f);
                    float len = Mathf.Lerp(0.8f, 1.8f, k) * Random.Range(0.8f, 1.2f);
                    _strokes.Add(new Stroke
                    {
                        line = new ToonLine(_root, Random.Range(0.035f, 0.06f)),
                        a = from - dir * len, b = from, born = Time.time, life = Random.Range(0.25f, 0.4f),
                    });
                }
            }
            else _owed = 0f;

            for (int i = _strokes.Count - 1; i >= 0; i--)
            {
                var s = _strokes[i];
                float u = (Time.time - s.born) / s.life;
                if (u >= 1f) { s.line.Destroy(); _strokes.RemoveAt(i); continue; }
                _pts.Clear();
                _pts.Add(s.a);
                _pts.Add(s.b);
                Swoosh.Window(u, out float tail, out float head);
                Swoosh.Part(_pts, tail, head, _part);
                s.line.Set(_part);
            }
        }

        void OnDestroy()
        {
            if (_root != null) Destroy(_root.gameObject);
        }
    }
}
