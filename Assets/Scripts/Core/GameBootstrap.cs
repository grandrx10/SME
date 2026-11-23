using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

namespace SuddenMageEncounter
{
    /// <summary>Scene entry point: settings, physics layers, the world/match systems, UI and camera.</summary>
    [DefaultExecutionOrder(-300)]
    public class GameBootstrap : MonoBehaviour
    {
        [Tooltip("Optional custom ragdoll prefab (same bone names as the player model). Leave empty to auto-build one.")]
        public GameObject ragdollPrefab;

        public const int MaxPayload = 256 * 1024;
        public const int MaxPacketQueue = 512;

        void Awake()
        {
            // world edits (a summoned wall is ~8 KB) and late-join replays are bigger than the transport's default limit
            var utp = FindAnyObjectByType<Unity.Netcode.Transports.UTP.UnityTransport>();
            if (utp != null && utp.MaxPayloadSize < MaxPayload) utp.MaxPayloadSize = MaxPayload;
            // packets the transport can take in one frame: a late joiner's replay or a big fight's rubble overflowed 128
            // (overflowing makes the reliable channel resend and stall)
            if (utp != null && utp.MaxPacketQueueSize < MaxPacketQueue) utp.MaxPacketQueueSize = MaxPacketQueue;

            GameSettings.Load();
            GameSettings.Apply(applyResolution: true);
            Layers.ConfigureCollisionMatrix();
            RagdollFactory.CustomPrefab = ragdollPrefab;

            if (FindAnyObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
                es.transform.SetParent(transform, false);
            }

            var cam = Camera.main;
            if (cam != null && cam.GetComponent<CameraRig>() == null) cam.gameObject.AddComponent<CameraRig>();

            if (GetComponent<MusicNet>() == null) gameObject.AddComponent<MusicNet>();
            if (GetComponent<WorldNet>() == null) gameObject.AddComponent<WorldNet>();
            if (GetComponent<MatchManager>() == null) gameObject.AddComponent<MatchManager>();
            if (GetComponent<GameUI>() == null) gameObject.AddComponent<GameUI>();
        }
    }
}
