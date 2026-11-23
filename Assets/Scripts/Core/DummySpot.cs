using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Where a test dummy stands in a practice chamber. Put these inside the practice arena prefab
    /// (Sudden Mage Encounter > Add Dummy Spot); the dummy faces the spot's blue arrow, is dropped onto the ground
    /// under it, and stands back up there 3 s after it dies or when the chamber is reset (J).
    /// Only used in practice chambers: the main arena ignores them.
    /// </summary>
    [AddComponentMenu("Sudden Mage Encounter/Dummy Spot")]
    public class DummySpot : MonoBehaviour
    {
        void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.55f, 0.1f, 0.9f);
            Gizmos.DrawWireSphere(transform.position, 0.4f);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward);
            Gizmos.color = new Color(1f, 0.55f, 0.1f, 0.3f);
            Gizmos.DrawWireCube(transform.position, new Vector3(0.7f, 1.95f, 0.7f));
        }
    }
}
