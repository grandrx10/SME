using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Baked voxels of an arena: a palette and the non-empty 16³ chunks, relative to the arena's root.
    /// Stored as a small gzip'd binary (.bytes TextAsset) next to the arena prefab.
    /// </summary>
    public class VoxelArenaData
    {
        const uint Magic = 0x56454D53; // "SMEV"
        const int Version = 1;

        /// <summary>Index 0 = air.</summary>
        public readonly List<VoxelMaterial> palette = new() { default };
        public readonly Dictionary<Vector3Int, ushort[]> chunks = new();

        public int CellCount
        {
            get
            {
                int n = 0;
                foreach (var c in chunks.Values) foreach (var v in c) if (v != 0) n++;
                return n;
            }
        }

        public ushort AddMaterial(Color32 color, bool indestructible)
        {
            for (int i = 1; i < palette.Count; i++)
                if (palette[i].color.Equals(color) && palette[i].indestructible == indestructible) return (ushort)i;
            if (palette.Count >= ushort.MaxValue) return (ushort)(palette.Count - 1);
            palette.Add(new VoxelMaterial { color = color, indestructible = indestructible });
            return (ushort)(palette.Count - 1);
        }

        public void Set(int x, int y, int z, ushort v)
        {
            var key = VoxelWorld.ChunkOf(x, y, z);
            if (!chunks.TryGetValue(key, out var c))
            {
                if (v == 0) return;
                chunks[key] = c = new ushort[VoxelChunk.N * VoxelChunk.N * VoxelChunk.N];
            }
            c[VoxelChunk.Index(x & VoxelWorld.ChunkMask, y & VoxelWorld.ChunkMask, z & VoxelWorld.ChunkMask)] = v;
        }

        public ushort Get(int x, int y, int z)
        {
            if (!chunks.TryGetValue(VoxelWorld.ChunkOf(x, y, z), out var c)) return 0;
            return c[VoxelChunk.Index(x & VoxelWorld.ChunkMask, y & VoxelWorld.ChunkMask, z & VoxelWorld.ChunkMask)];
        }

        // ------------------------------------------------------------------ serialization

        public byte[] ToBytes()
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
            using (var w = new BinaryWriter(gz))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(palette.Count);
                for (int i = 0; i < palette.Count; i++)
                {
                    var p = palette[i];
                    w.Write(p.color.r); w.Write(p.color.g); w.Write(p.color.b); w.Write(p.color.a);
                    w.Write((byte)(p.indestructible ? 1 : 0));
                }
                int count = 0;
                foreach (var c in chunks.Values) if (!IsEmpty(c)) count++;
                w.Write(count);
                foreach (var kv in chunks)
                {
                    if (IsEmpty(kv.Value)) continue;
                    w.Write((short)kv.Key.x); w.Write((short)kv.Key.y); w.Write((short)kv.Key.z);
                    // run-length: (run, value) pairs
                    var cells = kv.Value;
                    int i = 0;
                    while (i < cells.Length)
                    {
                        ushort v = cells[i];
                        int run = 1;
                        while (i + run < cells.Length && cells[i + run] == v && run < ushort.MaxValue) run++;
                        w.Write((ushort)run);
                        w.Write(v);
                        i += run;
                    }
                }
            }
            return ms.ToArray();
        }

        public static VoxelArenaData FromBytes(byte[] bytes)
        {
            var d = new VoxelArenaData();
            d.palette.Clear();
            using var ms = new MemoryStream(bytes);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var r = new BinaryReader(gz);
            if (r.ReadUInt32() != Magic) throw new InvalidDataException("Not a voxel arena file");
            int version = r.ReadInt32();
            if (version != Version) throw new InvalidDataException("Unknown voxel arena version " + version);
            int pc = r.ReadInt32();
            for (int i = 0; i < pc; i++)
            {
                var col = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());
                bool ind = r.ReadByte() != 0;
                d.palette.Add(new VoxelMaterial { color = col, indestructible = ind });
            }
            if (d.palette.Count == 0) d.palette.Add(default);
            int cc = r.ReadInt32();
            int total = VoxelChunk.N * VoxelChunk.N * VoxelChunk.N;
            for (int k = 0; k < cc; k++)
            {
                var key = new Vector3Int(r.ReadInt16(), r.ReadInt16(), r.ReadInt16());
                var cells = new ushort[total];
                int i = 0;
                while (i < total)
                {
                    int run = r.ReadUInt16();
                    ushort v = r.ReadUInt16();
                    for (int j = 0; j < run && i < total; j++) cells[i++] = v;
                }
                d.chunks[key] = cells;
            }
            return d;
        }

        static bool IsEmpty(ushort[] c)
        {
            foreach (var v in c) if (v != 0) return false;
            return true;
        }
    }
}
