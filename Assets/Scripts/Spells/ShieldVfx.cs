using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The arcane shield at the tip of the right hand, facing where its owner aims: a circle with symbols floating round
    /// its edge (no circles of their own: the user's wish). They go one after another as its health does. Struck, it
    /// flashes and swells; broken, all of it bursts.
    /// </summary>
    [DefaultExecutionOrder(150)] // after the ProceduralAnimator has posed the hand this frame
    public class ShieldView : MonoBehaviour
    {
        static readonly Color Flash = new(2.2f, 3.2f, 6f, 1f);
        static readonly Quaternion Flat = Quaternion.Euler(-90f, 0f, 0f); // a drawing's XZ plane → the shield's XY

        Combatant _pc;
        Transform _root, _ring;
        RuneDrawing _core;
        readonly RuneDrawing[] _outer = new RuneDrawing[SpellPlans.ShieldRunes];
        readonly float[] _left = new float[SpellPlans.ShieldRunes];
        float _open, _hit, _turn;

        void Awake() => _pc = GetComponent<Combatant>();

        void Build()
        {
            _root = new GameObject("ArcaneShield") { layer = Layers.Fx }.transform;
            _ring = new GameObject("Ring") { layer = Layers.Fx }.transform;
            _ring.SetParent(_root, false);
            _core = Drawing(_root, CoreStrokes(), SpellPlans.ShieldCore, 0.034f);
            for (int i = 0; i < _outer.Length; i++)
                _outer[i] = Drawing(_ring, RuneGlyphs.Rune(i), SpellPlans.ShieldRune, 0.034f);
        }

        static RuneDrawing Drawing(Transform parent, List<List<Vector2>> strokes, float radius, float width)
        {
            var go = new GameObject("Drawing") { layer = Layers.Fx };
            go.transform.SetParent(parent, false);
            go.transform.localRotation = Flat;
            var d = new RuneDrawing(go);
            d.AddRune(strokes, radius * 2f, width);
            d.Progress = 1f;
            d.SetColour(Glow.Arcane);
            return d;
        }

        static List<List<Vector2>> CoreStrokes()
        {
            var s = new List<List<Vector2>> { TarotVfx.Ring(1f, 0f, 56), TarotVfx.Ring(0.8f, 180f, 48) };
            for (int k = 0; k < 2; k++)
            {
                var tri = new List<Vector2>();
                for (int i = 0; i <= 3; i++)
                {
                    float a = (90f + 180f * k + 120f * i) * Mathf.Deg2Rad;
                    tri.Add(new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 0.78f);
                }
                s.Add(tri);
            }
            return s;
        }

        bool Up
        {
            get
            {
                if (_pc == null || !_pc.IsSpawned || !_pc.IsAlive || !_pc.Visible || _pc.ViewPose != SpellPose.ArcaneShield || (_pc.ViewFlags & NetState.Ragdoll) != 0) return false;
                var s = _pc.Shield.Value;
                return s.Raised || !s.Broken; // (just broken: gone at once, whatever the hand is still doing)
            }
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            bool up = Up;
            _open = Mathf.MoveTowards(_open, up ? 1f : 0f, dt / (up ? 0.16f : 0.12f));
            _hit = Mathf.MoveTowards(_hit, 0f, dt * 4f);
            if (_open <= 0f || !_pc.IsSpawned)
            {
                if (_root != null && _root.gameObject.activeSelf) _root.gameObject.SetActive(false);
                return;
            }
            float hp = _pc.Shield.Value.At(WorldNet.Now) / _pc.Shield.Value.Max * _outer.Length;
            bool opening = _root == null || !_root.gameObject.activeSelf;
            if (_root == null) Build();
            if (opening)
            {
                _root.gameObject.SetActive(true);
                for (int i = 0; i < _left.Length; i++) _left[i] = Mathf.Clamp01(hp - i);
            }

            Quaternion look = Quaternion.Euler(_pc.ViewPitch, _pc.ViewYaw, 0f);
            _root.SetPositionAndRotation(SpellVisuals.RightHand(_pc) + look * Vector3.forward * SpellPlans.ShieldOut, look);
            float pop = up ? 1f + 0.35f * Mathf.Sin(_open * Mathf.PI) : 1f; // opens with a bounce
            _root.localScale = Vector3.one * (_open * pop);
            _turn += dt * (40f + 400f * _hit);
            _ring.localRotation = Quaternion.Euler(0f, 0f, -_turn);
            _ring.localScale = Vector3.one * (1f + 0.2f * _hit);
            Color colour = Color.Lerp(Glow.Arcane, Flash, _hit);
            _core.Host.localRotation = Quaternion.Euler(0f, 0f, _turn * 0.6f) * Flat;
            _core.Host.localScale = Vector3.one * (1f + 0.15f * _hit);
            _core.SetColour(colour);
            for (int i = 0; i < _outer.Length; i++)
            {
                _left[i] = Mathf.MoveTowards(_left[i], Mathf.Clamp01(hp - i), dt * 6f);
                // floating: drifting in and out a little, each in its own time
                float a = i / (float)_outer.Length * Mathf.PI * 2f;
                _outer[i].Host.localPosition = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * (SpellPlans.ShieldRing + 0.035f * Mathf.Sin(Time.time * 2.6f + i * 1.7f));
                _outer[i].Host.localScale = Vector3.one * Mathf.Sqrt(_left[i]);
                _outer[i].Host.localRotation = Quaternion.Euler(0f, 0f, _turn) * Flat; // (the runes stay upright)
                _outer[i].SetColour(colour);
            }
        }

        /// <summary>It stopped something (<paramref name="strength"/>: how much of a full shield that took), or broke.</summary>
        public void Struck(bool broke, float strength)
        {
            _hit = 1f;
            if (_root == null || !_root.gameObject.activeSelf) return;
            Vector3 p = _root.position;
            if (!broke)
            {
                EnergyBurst.Create(p, strength > 0.35f ? EnergyBurst.Impact : EnergyBurst.Small);
                CameraRig.ShakeFrom(p, 0.06f + 0.14f * strength, 0.18f);
                return;
            }
            EnergyBurst.Create(p, EnergyBurst.Release);
            foreach (var o in _outer) EnergyBurst.Create(o.Host.position, EnergyBurst.Small);
            CameraRig.ShakeFrom(p, 0.3f, 0.3f);
            _open = 0f;
        }

        void OnDestroy()
        {
            _core?.Release();
            foreach (var o in _outer) o?.Release();
            if (_root != null) Destroy(_root.gameObject);
        }
    }
}
