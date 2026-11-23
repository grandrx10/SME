using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// What the camera circles while this arena builds itself. Put one inside an arena prefab
    /// (Sudden Mage Encounter > Add Build Camera Point). The camera looks at this point from <see cref="distance"/>
    /// away, <see cref="pitch"/> degrees above it, starting behind the blue arrow and turning at <see cref="spinSpeed"/>.
    /// No point = the camera frames the whole arena by itself.
    /// </summary>
    [AddComponentMenu("Sudden Mage Encounter/Build Camera Point")]
    public class BuildCameraPoint : MonoBehaviour
    {
        [Tooltip("How far the camera stays from this point (metres).")]
        public float distance = 30f;
        [Tooltip("How steeply the camera looks down on the point (degrees; 0 = level, 90 = straight down).")]
        [Range(-10f, 89f)] public float pitch = 30f;
        [Tooltip("Degrees per second around the point (negative = the other way round).")]
        public float spinSpeed = 14f;

        /// <summary>Where the camera is <paramref name="seconds"/> into the build, and which way it faces.</summary>
        public void Pose(float seconds, out Vector3 position, out Quaternion rotation)
        {
            rotation = Quaternion.Euler(pitch, transform.eulerAngles.y + seconds * spinSpeed, 0f);
            position = transform.position + rotation * new Vector3(0f, 0f, -distance);
        }

        void OnDrawGizmosSelected()
        {
            Vector3 c = transform.position;
            float r = distance * Mathf.Cos(pitch * Mathf.Deg2Rad);
            float h = distance * Mathf.Sin(pitch * Mathf.Deg2Rad);
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.9f);
            Gizmos.DrawWireSphere(c, 0.5f);
            // the camera's path
            Vector3 prev = c + new Vector3(r, h, 0f);
            for (int i = 1; i <= 64; i++)
            {
                float a = i / 64f * Mathf.PI * 2f;
                Vector3 p = c + new Vector3(Mathf.Cos(a) * r, h, Mathf.Sin(a) * r);
                Gizmos.DrawLine(prev, p);
                prev = p;
            }
            // where it starts, and what it sees from there
            Pose(0f, out var pos, out var rot);
            Gizmos.DrawLine(pos, c);
            var m = Gizmos.matrix;
            Gizmos.matrix = Matrix4x4.TRS(pos, rot, Vector3.one);
            Gizmos.DrawFrustum(Vector3.zero, 60f, 3f, 0.3f, 16f / 9f);
            Gizmos.matrix = m;
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.9f);
            Gizmos.DrawWireSphere(transform.position, 0.5f);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * 2f);
        }
    }
}
