using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>Shared materials for voxels, debris and spell indicators.</summary>
    public static class VoxelRender
    {
        static Material _voxel, _ghost, _glass;
        static Material[] _voxelPair;

        /// <summary>Glass voxels (palette alpha under 255): submesh 1 of every voxel mesh.</summary>
        public static Material Glass
        {
            get
            {
                if (_glass != null) return _glass;
                var s = Shader.Find("SME/VoxelGlass");
                if (s == null)
                {
                    Debug.LogWarning("[SME] SME/VoxelGlass shader missing; glass voxels use a fallback shader.");
                    s = Shader.Find("Sprites/Default");
                }
                _glass = new Material(s) { name = "Voxel Glass" };
                return _glass;
            }
        }

        /// <summary>Materials for a voxel mesh, in submesh order (solid, glass).</summary>
        public static Material[] Materials => _voxelPair != null && _voxelPair[0] != null && _voxelPair[1] != null
            ? _voxelPair
            : _voxelPair = new[] { Material, Glass };

        /// <summary>Lit voxel material (vertex colours).</summary>
        public static Material Material
        {
            get
            {
                if (_voxel != null) return _voxel;
                var s = Shader.Find("SME/Voxel");
                if (s == null)
                {
                    Debug.LogWarning("[SME] SME/Voxel shader missing; voxels use a fallback shader.");
                    s = Shader.Find("Universal Render Pipeline/Particles/Simple Lit") ?? Shader.Find("Sprites/Default");
                }
                _voxel = new Material(s) { name = "Voxels" };
                _voxel.SetFloat("_VoxelSize", VoxelWorld.Size);
                return _voxel;
            }
        }

        /// <summary>Unlit, see-through, vertex-coloured: indicators and ghosts.</summary>
        public static Material Ghost
        {
            get
            {
                if (_ghost != null) return _ghost;
                _ghost = new Material(Shader.Find("Sprites/Default")) { name = "Ghost" };
                _ghost.renderQueue = 3100;
                return _ghost;
            }
        }
    }
}
