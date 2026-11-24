using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>On a cosmetic's prefab: where it goes on the body until the player moves it, in the bone's own space.</summary>
    public class CosmeticBody : MonoBehaviour
    {
        public string bone = "Bone.001";
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public float scale = 1f;
    }
}
