using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>Physics layers used by the game. Created by Sudden Mage Encounter > Setup Scene.</summary>
    public static class Layers
    {
        public const string PlayerName = "Player";
        public const string HitboxName = "Hitbox";
        public const string RagdollName = "Ragdoll";
        public const string FxName = "FX";

        public static int Player => Get(PlayerName, 8);
        public static int Hitbox => Get(HitboxName, 9);
        public static int Ragdoll => Get(RagdollName, 10);
        public static int Fx => Get(FxName, 11);

        static int Get(string name, int fallback)
        {
            int l = LayerMask.NameToLayer(name);
            return l < 0 ? fallback : l;
        }

        /// <summary>World geometry only (for movement queries, wall checks, ground checks).</summary>
        public static int EnvironmentMask =>
            Physics.DefaultRaycastLayers & ~((1 << Player) | (1 << Hitbox) | (1 << Ragdoll) | (1 << Fx) | (1 << 5));

        /// <summary>What bullets can hit: world + hitboxes.</summary>
        public static int BulletMask => EnvironmentMask | (1 << Hitbox);

        public static void ConfigureCollisionMatrix()
        {
            int p = Player, h = Hitbox, r = Ragdoll, f = Fx;
            for (int i = 0; i < 32; i++) Physics.IgnoreLayerCollision(h, i, true); // hitboxes are query-only
            Physics.IgnoreLayerCollision(r, p, true);
            Physics.IgnoreLayerCollision(f, p, true);
            Physics.IgnoreLayerCollision(f, r, true);
        }

        public static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform c in go.transform) SetLayerRecursive(c.gameObject, layer);
        }
    }
}
