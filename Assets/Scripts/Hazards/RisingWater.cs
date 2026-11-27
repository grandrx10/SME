using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Map hazard: water that starts rising <see cref="startAfter"/> seconds into the round and keeps going, at a steady
    /// pace, until there's a winner. You float in it; every second in it (added up over all your dips) hurts twice as
    /// much as the last (1, 2, 4...: SpellServer.Hazards). Puts out fire like any other water, and washes away ground that
    /// spells have put there.
    /// The Box Collider's top is where the water starts (see <see cref="Liquid"/>).
    /// </summary>
    [AddComponentMenu("Sudden Mage Encounter/Rising Water")]
    public class RisingWater : Liquid
    {
        [Tooltip("Seconds after FIGHT that it starts rising.")]
        public float startAfter = 0f;
        [Tooltip("The height it reaches after Rise Seconds (it keeps rising at that pace). Empty = the top of the arena.")]
        public Transform riseTo;
        [Tooltip("Seconds from its starting level to Rise To.")]
        public float riseSeconds = 180f;

        float TopY
        {
            get
            {
                if (riseTo != null) return riseTo.position.y;
                var w = Home != null ? Home.World : null;
                return w != null ? w.ArenaBounds.max.y : BaseY + 30f;
            }
        }

        protected override float Wait => startAfter;
        protected override float LevelAt(float t) => t <= 0f ? BaseY : BaseY + Mathf.Max(0f, TopY - BaseY) / Mathf.Max(1f, riseSeconds) * t;

        // ------------------------------------------------------------------ floating

        /// <summary>The hips float this far under the surface.</summary>
        const float FloatDepth = 0.25f;
        /// <summary>Swimming speed, as a share of running.</summary>
        public const float SwimSpeed = 0.6f;
        const float BuoyUp = 3.5f, BuoyDown = 3f, BuoyAccel = 60f;

        /// <summary>Deep enough in water to float: the height the hips (<paramref name="hips"/>) want to be at.</summary>
        public static bool FloatLine(Vector3 hips, out float line)
        {
            foreach (var l in All)
            {
                if (l is not RisingWater) continue;
                line = l.Level - FloatDepth + 0.05f * Mathf.Sin(Time.time * 1.8f + hips.x * 0.5f);
                if (hips.y < line + 0.05f && l.Contains(hips, 0.5f)) return true;
            }
            line = 0f;
            return false;
        }

        /// <summary>The vertical speed of a floating body a moment later: up to the line, and held there.</summary>
        public static float Buoy(float vy, float y, float line, float dt)
        {
            if (vy > BuoyUp) return vy - 20f * dt; // jumping (or being thrown) out of it
            return Mathf.MoveTowards(vy, Mathf.Clamp((line - y) * 4f, -BuoyDown, BuoyUp), BuoyAccel * dt);
        }

        public static bool InAny(Vector3 p, float pad)
        {
            foreach (var l in All) if (l is RisingWater && l.Contains(p, pad)) return true;
            return false;
        }

        /// <summary>Server: washes away any ground spells have put in water.</summary>
        public static void ServerWash()
        {
            var w = VoxelWorld.Current;
            if (w == null || w.SummonedCount == 0 || !Any) return;
            var cells = w.SummonedWhere(p => InAny(p, 0f));
            cells.RemoveAll(c => w.IsIndestructible(w.Get(c)));
            if (cells.Count > 0) WorldNet.ServerWash(cells, Vector3.up * 0.2f);
        }

        public static bool Any
        {
            get
            {
                foreach (var l in All) if (l is RisingWater) return true;
                return false;
            }
        }

        // ------------------------------------------------------------------ looks

        static Material _mat;

        protected override Material Look
        {
            get
            {
                if (_mat != null) return _mat;
                var s = Shader.Find("SME/Water");
                if (s == null)
                {
                    Debug.LogWarning("[SME] SME/Water shader missing; water uses a fallback shader.");
                    s = Shader.Find("Universal Render Pipeline/Unlit");
                }
                return _mat = new Material(s) { name = "Water" };
            }
        }

        protected override void Entered(Vector3 at, float speed) => WaterVfx.Splash(at, Mathf.Clamp(0.4f + speed * 0.06f, 0.4f, 1.2f));
        protected override Color GizmoColour => new(0.2f, 0.7f, 1f);

        void OnDrawGizmosSelected()
        {
            if (riseTo != null) DrawLevel(riseTo.position.y);
        }
    }
}
