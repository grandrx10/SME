using UnityEditor;
using UnityEngine;

namespace SuddenMageEncounter.EditorTools
{
    /// <summary>
    /// Sky Wrap: everything is measured from the middle of the map. Drag the top and bottom up and down, and the
    /// area's sides in and out (it stays centred on the map), in the Scene view.
    /// </summary>
    [CustomEditor(typeof(SkyWrap))]
    public class SkyWrapEditor : Editor
    {
        void OnEnable()
        {
            var w = (SkyWrap)target;
            if (w == null) return;
            if (w.Unset) Fit(w); // a new one: round the map
            else
            {
                Undo.RecordObject(w, "Recentre Sky Wrap");
                if (w.Recentre()) EditorUtility.SetDirty(w); // the map moved (or this one was set up before it measured from the middle)
            }
        }

        static void Fit(SkyWrap w)
        {
            Undo.RecordObject(w, "Fit Sky Wrap");
            w.FitToMap();
            EditorUtility.SetDirty(w);
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            if (GUILayout.Button("Fit to map")) Fit((SkyWrap)target);
            EditorGUILayout.HelpBox("Measured from the middle of the map. Scene view: blue arrow = top, red arrow = bottom, white dots = sides of the area.", MessageType.None);
        }

        void OnSceneGUI()
        {
            var w = (SkyWrap)target;
            if (w.Unset) return;
            Vector3 c = w.Origin;
            float hx = w.size.x * 0.5f, hz = w.size.y * 0.5f, mid = (w.TopY + w.BottomY) * 0.5f;

            EditorGUI.BeginChangeCheck();
            Handles.color = new Color(0.45f, 0.8f, 1f);
            Vector3 t = Handles.Slider(new Vector3(c.x, w.TopY, c.z), Vector3.up, HandleUtility.GetHandleSize(c) * 1.2f, Handles.ArrowHandleCap, 0.25f);
            Handles.color = new Color(1f, 0.25f, 0.15f);
            Vector3 b = Handles.Slider(new Vector3(c.x, w.BottomY, c.z), Vector3.down, HandleUtility.GetHandleSize(c) * 1.2f, Handles.ArrowHandleCap, 0.25f);
            Handles.color = Color.white;
            float xp = Side(new Vector3(c.x + hx, mid, c.z), Vector3.right).x - c.x;
            float xn = c.x - Side(new Vector3(c.x - hx, mid, c.z), Vector3.left).x;
            float zp = Side(new Vector3(c.x, mid, c.z + hz), Vector3.forward).z - c.z;
            float zn = c.z - Side(new Vector3(c.x, mid, c.z - hz), Vector3.back).z;
            if (!EditorGUI.EndChangeCheck()) return;

            Undo.RecordObject(w, "Move Sky Wrap");
            w.top = t.y - c.y;
            w.bottom = Mathf.Min(b.y - c.y, w.top - 1f);
            // whichever side moved, both move: it stays centred on the map
            float nx = Mathf.Abs(xp - hx) > Mathf.Abs(xn - hx) ? xp : xn;
            float nz = Mathf.Abs(zp - hz) > Mathf.Abs(zn - hz) ? zp : zn;
            w.size = new Vector2(Mathf.Max(1f, nx * 2f), Mathf.Max(1f, nz * 2f));
            EditorUtility.SetDirty(w);
        }

        static Vector3 Side(Vector3 p, Vector3 dir) =>
            Handles.Slider(p, dir, HandleUtility.GetHandleSize(p) * 0.15f, Handles.DotHandleCap, 0.25f);
    }
}
