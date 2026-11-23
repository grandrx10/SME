using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Combo break (see <see cref="SpellPlans.ComboBreakTime"/>): a body pops up out of a juggle in a punchy arcane
    /// burst, then for as long as they can't be knocked down, three arcane shards circle them and runes keep coming off
    /// them. Everything shrinks away at the end (never fades).
    /// </summary>
    public class ComboBreakView : MonoBehaviour
    {
        const int Shards = 3;
        const float Orbit = 0.75f, Spin = 300f, ShardSize = 0.32f, Grow = 0.15f, Shrink = 0.3f;

        Combatant _who;
        float _time, _age;
        readonly Transform[] _shards = new Transform[Shards];
        RuneSpew _runes;

        static Vector3 Body(Combatant c) => c.RagdollFocus != null ? c.RagdollFocus.position : c.transform.position + Vector3.up * 0.1f;

        public static void Create(Combatant who, float seconds)
        {
            if (who == null || Debris.Suppressed) return;
            // one at a time per body
            foreach (var old in FindObjectsByType<ComboBreakView>(FindObjectsSortMode.None))
                if (old._who == who) Destroy(old.gameObject);

            Vector3 at = Body(who);
            var burst = EnergyBurst.Release;
            burst.flashSize = 2.4f;
            burst.flashTime = 0.22f;
            burst.sparkCount = 22;
            burst.sparkSize = 0.38f;
            burst.sparkSpeed = new Vector2(7f, 14f);
            burst.sparkLife = 0.45f;
            EnergyBurst.Create(at, burst);
            CameraRig.ShakeFrom(at, 0.2f, 0.25f);

            var go = new GameObject("ComboBreak") { layer = Layers.Fx };
            go.transform.position = at;
            var v = go.AddComponent<ComboBreakView>();
            v._who = who;
            v._time = Mathf.Max(0.1f, seconds);
            for (int i = 0; i < Shards; i++)
            {
                v._shards[i] = Glow.Piece(go.transform, Glow.PrismMesh, Glow.Arcane, "Shard");
                v._shards[i].localScale = Vector3.zero;
            }
            v._runes = RuneSpew.Create(() => v != null && v._who != null ? Body(v._who) : at, () => v != null && v._age < v._time,
                v._time, Glow.Arcane, 9f, new Vector3(0.35f, 0.6f, 0.35f));
            v._runes?.Burst(10, 2.2f);
        }

        void LateUpdate()
        {
            _age += Time.deltaTime;
            if (_who == null || !_who.IsAlive || _age > _time + Shrink) { Destroy(gameObject); return; }
            transform.position = Body(_who);
            float size = ShardSize * Mathf.Clamp01(_age / Grow) * (1f - Mathf.Clamp01((_age - _time) / Shrink));
            float spin = _age * Spin;
            for (int i = 0; i < Shards; i++)
            {
                float a = (spin + i * 360f / Shards) * Mathf.Deg2Rad;
                float bob = 0.25f * Mathf.Sin(_age * 5f + i * 2.1f);
                Vector3 off = new(Mathf.Cos(a) * Orbit, bob, Mathf.Sin(a) * Orbit);
                Vector3 tangent = new(-Mathf.Sin(a), 0f, Mathf.Cos(a));
                _shards[i].localPosition = off;
                _shards[i].rotation = Quaternion.LookRotation(tangent);
                _shards[i].localScale = new Vector3(size, size, size * 1.8f);
            }
        }
    }
}
