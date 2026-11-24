using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// In the scene, editor only: a copy of the player wearing one cosmetic. Move, turn or scale the cosmetic, save the
    /// scene, and that's its default spot (written to its prefab). Hidden while playing.
    /// </summary>
    public class CosmeticStand : MonoBehaviour
    {
        public Transform model;
        public Transform worn;
        public GameObject prefab;

        void Awake()
        {
            if (Application.isPlaying) gameObject.SetActive(false);
        }
    }
}
