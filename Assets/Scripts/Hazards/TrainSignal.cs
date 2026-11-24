using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Put on a signal's lamp (an ordinary mesh that gets baked into voxels: just the lamp, not its post). The lamp's
    /// voxels glow red, and green while a train is about to come down <see cref="track"/>. It's as breakable as anything
    /// else: voxels knocked off it go dark, and with half of it gone (or the lot knocked loose) the signal is dead. The
    /// train still comes. Any number of signals can share a track.
    /// </summary>
    [AddComponentMenu("Sudden Mage Encounter/Train Signal")]
    public class TrainSignal : MonoBehaviour
    {
        [Tooltip("The track it watches.")]
        public TrainTrack track;

        static readonly Color Red = new(4.2f, 0.22f, 0.12f), Green = new(0.25f, 4f, 0.5f);
        /// <summary>The glow stands this far off the voxels it's on; its ink, this much more.</summary>
        const float Skin = 0.012f, Ink = 0.035f;

        readonly List<Vector3Int> _cells = new();
        readonly HashSet<Vector3Int> _lit = new();
        VoxelWorld _world;
        Mesh _glow, _ink;
        MeshRenderer _glowRenderer;
        GameObject _view;
        int _count = -1;
        float _next, _look;
        bool _dead, _green, _tinted;
        Arena _arena;

        void Awake() => _arena = GetComponentInParent<Arena>();

        void Update()
        {
            if (_dead || Time.time < _next) return;
            // (the arena being fought in, or a practice chamber, which is stamped into that arena's world)
            var w = _arena == null ? null : _arena.isChamber ? VoxelWorld.Current : _arena.World;
            if (w == null || w != VoxelWorld.Current || w.Building) { _next = Time.time + 0.25f; return; }
            if (_world != w) Find(w);
            if (_dead) return;

            bool green = track != null && track.Warning;
            if (green != _green || !_tinted)
            {
                _green = green;
                _tinted = true;
                Glow.Tint(_glowRenderer, green ? Green : Red);
            }
            if (Time.time < _look) return;
            _look = Time.time + 0.1f;
            Refresh(w);
        }

        /// <summary>The lamp's voxels: the ones its mesh was baked into.</summary>
        void Find(VoxelWorld w)
        {
            _world = w;
            _cells.Clear();
            bool any = false;
            Bounds b = default;
            foreach (var r in GetComponentsInChildren<MeshRenderer>(true))
            {
                if (any) b.Encapsulate(r.bounds); else b = r.bounds;
                any = true;
            }
            if (!any) b = new Bounds(transform.position, Vector3.one * 0.5f);
            Gather(w, b);
            if (_cells.Count == 0) { b.Expand(VoxelWorld.Size); Gather(w, b); } // (thinner than a voxel)
            if (_cells.Count == 0)
            {
                Debug.LogWarning($"[SME] Train signal '{name}': no voxels where its lamp is. Bake the arena again.");
                _dead = true;
                return;
            }
            _view = new GameObject("SignalGlow") { layer = Layers.Fx };
            _view.transform.SetParent(w.transform, true);
            _view.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            _ink = VoxelOverlay.Layer(_view.transform, "Outline", VoxelOverlay.InkMaterial, Glow.Ink);
            _glow = VoxelOverlay.Layer(_view.transform, "Glow", Glow.Shared, Red);
            _glowRenderer = _view.transform.GetChild(1).GetComponent<MeshRenderer>();
            _count = -1;
        }

        void Gather(VoxelWorld w, Bounds b)
        {
            Vector3Int lo = w.CellOf(b.min), hi = w.CellOf(b.max);
            for (int x = lo.x; x <= hi.x; x++)
            for (int y = lo.y; y <= hi.y; y++)
            for (int z = lo.z; z <= hi.z; z++)
            {
                var c = new Vector3Int(x, y, z);
                if (w.IsSolid(c) && b.Contains(w.CellCenter(c))) _cells.Add(c);
            }
        }

        void Refresh(VoxelWorld w)
        {
            _lit.Clear();
            foreach (var c in _cells) if (w.IsSolid(c)) _lit.Add(c);
            if (_lit.Count == _count) return;
            _count = _lit.Count;
            if (_count * 2 < _cells.Count || _count == 0)
            {
                _dead = true;
                EnergyBurst.Create(w.CellCenter(_cells[_cells.Count / 2]), new EnergyBurst.Settings
                {
                    flashSize = 0.7f, flashTime = 0.12f, sparkCount = 8, sparkSize = 0.18f, sparkSpeed = new Vector2(3f, 6f), sparkLife = 0.3f, colour = _green ? Green : Red,
                });
                Destroy(_view);
                return;
            }
            var vg = new List<Vector3>();
            var vi = new List<Vector3>();
            var tg = new List<int>();
            var ti = new List<int>();
            float half = VoxelWorld.Size * 0.5f;
            foreach (var c in _lit)
            {
                int mask = 0;
                if (!_lit.Contains(c + Vector3Int.right)) mask |= 1;
                if (!_lit.Contains(c + Vector3Int.left)) mask |= 2;
                if (!_lit.Contains(c + Vector3Int.up)) mask |= 4;
                if (!_lit.Contains(c + Vector3Int.down)) mask |= 8;
                if (!_lit.Contains(c + Vector3Int.forward)) mask |= 16;
                if (!_lit.Contains(c + Vector3Int.back)) mask |= 32;
                if (mask == 0) continue;
                VoxelOverlay.AddCube(vg, tg, w.CellCenter(c), half + Skin, mask);
                VoxelOverlay.AddCube(vi, ti, w.CellCenter(c), half + Skin + Ink, mask);
            }
            VoxelOverlay.Apply(_glow, vg, tg);
            VoxelOverlay.Apply(_ink, vi, ti);
        }

        void OnDestroy()
        {
            if (_view != null) Destroy(_view);
            if (_glow != null) Destroy(_glow);
            if (_ink != null) Destroy(_ink);
        }

        void OnDrawGizmosSelected()
        {
            if (track == null || track.from == null || track.to == null) return;
            Gizmos.color = Color.green;
            Vector3 a = track.from.position, d = track.to.position - a;
            float u = d.sqrMagnitude > 0.01f ? Mathf.Clamp01(Vector3.Dot(transform.position - a, d) / d.sqrMagnitude) : 0f;
            Gizmos.DrawLine(transform.position, a + d * u);
        }
    }
}
