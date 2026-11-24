using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Where a meteor is about to land: a glowing red circle as wide as its blast, round the sign for fire, with a ring
    /// closing in on the middle as it comes. Gone the moment it lands (or it's turned away).
    /// </summary>
    public class MeteorMark : MonoBehaviour
    {
        const int Around = 40;

        WorldNet.FireballFx _fx;
        Vector3 _velocity, _x, _z;
        float _out = -1f, _born;
        (LineRenderer core, LineRenderer edge) _rim, _sign, _ring;
        readonly List<Vector3> _pts = new();

        public static void Create(WorldNet.FireballFx fx, Vector3 at, Vector3 normal)
        {
            var go = new GameObject("MeteorMark") { layer = Layers.Fx };
            go.transform.position = at + normal * 0.12f;
            var m = go.AddComponent<MeteorMark>();
            m._fx = fx;
            m._velocity = fx.velocity;
            m._born = Time.time;
            Quaternion q = Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.Euler(0f, Random.value * 360f, 0f);
            m._x = q * Vector3.right;
            m._z = q * Vector3.forward;
            m._rim = ArcanaVfx.Line(go.transform, FireVfx.Red, 0.14f);
            m._sign = ArcanaVfx.Line(go.transform, FireVfx.Red, 0.12f);
            m._ring = ArcanaVfx.Line(go.transform, FireVfx.Orange, 0.09f);
            m.LateUpdate();
        }

        void LateUpdate()
        {
            var net = WorldNet.Instance;
            bool coming = net != null && !_fx.done && net.Fireballs.ContainsKey(_fx.id) && _fx.velocity == _velocity;
            if (!coming && _out < 0f) _out = Time.time;
            float size = Mathf.Clamp01((Time.time - _born) / 0.2f + 0.05f);
            size = 1f - (1f - size) * (1f - size);
            if (_out >= 0f) size *= 1f - (Time.time - _out) / 0.12f; // shrinks away as it lands
            if (size <= 0f) { Destroy(gameObject); return; }

            // (the line's ink is the widest thing drawn: keep it inside the blast)
            float r = (SpellPlans.MeteorBlast - 0.1f) * size;
            Circle(_rim, r);
            _pts.Clear();
            for (int k = 0; k <= 3; k++) _pts.Add(At(90f + k * 120f, r * 0.62f)); // the sign for fire
            _pts.Add(Vector3.Lerp(_pts[0], _pts[1], 0.08f)); // (closes the corner)
            ArcanaVfx.SetLine(_sign, _pts);
            float left = Mathf.Clamp01(1f - (float)(WorldNet.Now - _fx.start) / SpellPlans.MeteorWarn);
            Circle(_ring, Mathf.Max(0.05f, r * left));
        }

        Vector3 At(float degrees, float r) => transform.position + (_x * Mathf.Cos(degrees * Mathf.Deg2Rad) + _z * Mathf.Sin(degrees * Mathf.Deg2Rad)) * r;

        void Circle((LineRenderer core, LineRenderer edge) l, float r)
        {
            _pts.Clear();
            for (int k = 0; k <= Around; k++) _pts.Add(At(k * 360f / Around, r));
            ArcanaVfx.SetLine(l, _pts);
        }
    }
}
