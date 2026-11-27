using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>Death's looks: a doomed body bursting (the voxels and the bile on the ground are separate ops).</summary>
    public static class DeathVfx
    {
        public static void Burst(Vector3 p)
        {
            EnergyBurst.Create(p, new EnergyBurst.Settings
            {
                flashSize = SpellPlans.BileBlast, flashTime = 0.22f, sparkCount = 34, sparkSize = 0.45f,
                sparkSpeed = new Vector2(6f, 14f), sparkLife = 0.45f, colour = SpellPlans.BileBlack,
            });
            var runes = RuneSpew.Create(() => p, () => false, 0.1f, Color.black, 1f, new Vector3(0.6f, 1f, 0.6f));
            if (runes != null) runes.Burst(30);
            CameraRig.ShakeFrom(p, 0.6f, 0.35f);
        }
    }

    /// <summary>
    /// Death on a body, from its replicated <see cref="BodyStatus"/>: marked, black symbols keep forming round it; doomed,
    /// the whole body turns black (the bile itself is a breath: see BreathView).
    /// </summary>
    public class DeathBodyFx : MonoBehaviour
    {
        Combatant _c;
        RuneSpew _marks;
        bool _marked, _black;
        MaterialPropertyBlock _mpb;

        void Awake() => _c = GetComponent<Combatant>();

        void LateUpdate()
        {
            if (_c == null || !_c.IsSpawned) return;
            var st = _c.Status.Value;
            bool alive = _c.IsAlive && (_c.Visible || _c.HasLiveRagdoll);
            // marked: dark symbols forming round them, for good
            _marked = alive && st.DeathMarked;
            if (_marked && _marks == null && !Debris.Suppressed)
                _marks = RuneSpew.Create(() => _c.RagdollFocus != null ? _c.RagdollFocus.position : _c.transform.position,
                    () => this != null && _marked, 3600f, Color.black, 9f, new Vector3(0.45f, 0.85f, 0.45f));
            if (!_marked) _marks = null;
            // doomed: black all over until they burst
            bool black = _c.IsAlive && st.Biling(WorldNet.Now);
            if (black) Paint(SpellPlans.BileBody);
            else if (_black) Restore();
            _black = black;
        }

        void Paint(Color c)
        {
            _mpb ??= new MaterialPropertyBlock();
            foreach (var r in _c.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r.name == FaceDecal.ObjectName || CosmeticLook.Is(r)) continue; // the face and cosmetics keep their own colours
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor("_BaseColor", c);
                _mpb.SetColor("_Color", c);
                r.SetPropertyBlock(_mpb);
            }
        }

        /// <summary>Back to their own colours (a new round).</summary>
        void Restore()
        {
            if (_c is PlayerController p) p.RefreshColor();
            else if (_c is TestDummy d) d.RefreshLook();
        }
    }
}
