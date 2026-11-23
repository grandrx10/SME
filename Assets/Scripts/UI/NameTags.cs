using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Names floating above other players' heads (never your own). Drawn on a screen overlay so they stay
    /// crisp and readable at any distance; hidden when the player is dead, invisible, behind you or behind a wall.
    /// Creates itself — nothing to add to the scene.
    /// </summary>
    public class NameTags : MonoBehaviour
    {
        const float HeightAboveHead = 0.35f;
        const float MaxDistance = 150f;     // names fade out towards this range
        const float FadeStart = 100f;

        class Tag
        {
            public Text text, shadow;
            public RectTransform rect;
            public Transform head;
            public string shownName;
            public int shownColor = -2;
        }

        readonly Dictionary<PlayerController, Tag> _tags = new();
        readonly List<PlayerController> _gone = new();
        RectTransform _root;
        Canvas _canvas;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            var go = new GameObject("NameTags");
            DontDestroyOnLoad(go);
            go.AddComponent<NameTags>();
        }

        void Awake()
        {
            var go = new GameObject("NameTags Canvas", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 5; // under the HUD and menus (GameUI is 10)
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            _root = (RectTransform)go.transform;
        }

        void LateUpdate()
        {
            var cam = Camera.main;

            // drop tags of players that left
            _gone.Clear();
            foreach (var kv in _tags) if (kv.Key == null || !PlayerController.All.Contains(kv.Key)) _gone.Add(kv.Key);
            foreach (var p in _gone)
            {
                if (_tags[p].rect != null) Destroy(_tags[p].rect.gameObject);
                _tags.Remove(p);
            }

            foreach (var p in PlayerController.All)
            {
                if (p == null || p.IsOwner) continue; // you can't see your own name
                if (!_tags.TryGetValue(p, out var tag)) _tags[p] = tag = MakeTag(p);
                Place(p, tag, cam);
            }
        }

        Tag MakeTag(PlayerController p)
        {
            var tag = new Tag();
            tag.rect = UIFactory.Rect("Tag", _root);
            tag.rect.anchorMin = tag.rect.anchorMax = Vector2.zero;
            tag.rect.pivot = new Vector2(0.5f, 0f);
            tag.rect.sizeDelta = new Vector2(400, 40);
            tag.shadow = UIFactory.Text("Shadow", tag.rect, "", 24, TextAnchor.LowerCenter, new Color(0, 0, 0, 0.8f), FontStyle.Bold);
            UIFactory.Stretch(tag.shadow.rectTransform, 2, -2, -2, 2);
            tag.text = UIFactory.Text("Name", tag.rect, "", 24, TextAnchor.LowerCenter, Color.white, FontStyle.Bold);
            UIFactory.Stretch(tag.text.rectTransform);
            tag.shadow.raycastTarget = tag.text.raycastTarget = false;
            return tag;
        }

        void Place(PlayerController p, Tag tag, Camera cam)
        {
            // only people in the same place as you (the match, or your own practice chamber)
            var me = PlayerController.Local;
            int mySpace = me != null && me.IsSpawned ? me.PracticeSlot.Value : 0;
            bool show = cam != null && p.IsSpawned && p.IsAlive && p.Visible && p.PracticeSlot.Value == mySpace && !p.Status.Value.Taken(WorldNet.Now);
            Vector3 world = default, screen = default;
            float alpha = 1f;
            if (show)
            {
                world = HeadTop(p, tag) + Vector3.up * HeightAboveHead;
                Vector3 camPos = cam.transform.position;
                float dist = Vector3.Distance(camPos, world);
                screen = cam.WorldToScreenPoint(world);
                show = screen.z > 0.05f && dist < MaxDistance;
                // hidden behind walls
                if (show && Physics.Linecast(camPos, world, out _, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) show = false;
                alpha = 1f - Mathf.InverseLerp(FadeStart, MaxDistance, dist);
            }

            tag.rect.gameObject.SetActive(show);
            if (!show) return;

            string name = p.TagName; // (a skinwalker wears the name of the body it took)
            int colorKey = p.ColorIndex.Value;
            if (name != tag.shownName || colorKey != tag.shownColor)
            {
                tag.shownName = name;
                tag.shownColor = colorKey;
                tag.text.text = tag.shadow.text = name;
            }
            var c = Color.Lerp(p.PlayerColor, Color.white, 0.35f);
            c.a = alpha;
            tag.text.color = c;
            tag.shadow.color = new Color(0, 0, 0, 0.8f * alpha);

            // screen pixels -> canvas units
            float scale = _root.lossyScale.x > 0f ? _root.lossyScale.x : 1f;
            tag.rect.anchoredPosition = new Vector2(screen.x, screen.y) / scale;
        }

        static Vector3 HeadTop(PlayerController p, Tag tag)
        {
            if (tag.head == null && p.Bones != null &&
                !p.Bones.TryGetValue("Bone.001_end", out tag.head)) p.Bones.TryGetValue("Bone.001", out tag.head);
            if (tag.head != null) return tag.head.position;
            var cc = p.CC;
            if (cc != null)
                return p.transform.TransformPoint(cc.center) + p.transform.up * (cc.height * Mathf.Abs(p.transform.lossyScale.y) * 0.5f);
            return p.transform.position + Vector3.up * 1.8f;
        }
    }
}
