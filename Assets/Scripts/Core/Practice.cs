using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Practice chambers: everyone can step out of the match into a private copy of the practice arena (the loadout
    /// screen's target button) to try spells on dummies. J resets it, K drops a one-off dummy where you aim, L leaves.
    ///
    /// Each chamber is stamped into the same voxel world as the main arena, <see cref="SlotCells"/> cells further along
    /// +x per slot. All spells, voxel ops and late-join replays keep working unchanged, because chambers are just
    /// distant parts of one world. Loading and clearing a chamber are ordered <see cref="WorldNet"/> ops, so every
    /// peer's world stays identical. A new round reloads the whole world: practisers who join rounds (H, on by
    /// default) are pulled into it, and the others get a fresh chamber back straight after the reload.
    /// </summary>
    public static class Practice
    {
        /// <summary>Cells between chamber origins along x (384 m: far beyond any spell's reach).</summary>
        public const int SlotCells = 1536;
        /// <summary>Slots 1..MaxSlots (cells are sent as shorts, so 20 × 1536 stays inside ±32767).</summary>
        public const int MaxSlots = 20;
        public const int MaxSummoned = 12;
        /// <summary>Camera far plane: well past any arena, well short of the next chamber over.</summary>
        public const float ViewDistance = 250f;
        const float RespawnDelay = 2f;
        const float SummonReach = 60f;

        public static float SlotMeters => SlotCells * VoxelWorld.Size;
        static Vector3 WorldOrigin => VoxelWorld.Current != null ? VoxelWorld.Current.Origin : Vector3.zero;

        /// <summary>The chamber slot a point is in (0 = the main arena).</summary>
        public static int SlotAt(Vector3 p)
        {
            int s = Mathf.RoundToInt((p.x - WorldOrigin.x) / SlotMeters);
            return s >= 1 && s <= MaxSlots ? s : 0;
        }

        public static Vector3 SlotOrigin(int slot) => WorldOrigin + Vector3.right * (slot * SlotMeters);

        /// <summary>The height below which someone at <paramref name="p"/> falls out (the chamber's or the arena's).</summary>
        public static float KillYAt(Vector3 p)
        {
            int s = SlotAt(p);
            if (s > 0 && Chambers.TryGetValue(s, out var ch)) return ch.killY;
            return Arena.Current != null ? Arena.Current.KillY : float.NegativeInfinity;
        }

        // ------------------------------------------------------------------ chambers (every peer, via WorldNet ops)

        class Chamber
        {
            public GameObject root;
            public float killY;
            public readonly List<SpawnPoint> spawns = new();
            public readonly List<DummySpot> dummySpots = new();
        }

        static readonly Dictionary<int, Chamber> Chambers = new();
        static readonly Dictionary<TextAsset, VoxelArenaData> Baked = new();
        static VoxelArenaData _generated;

        /// <summary>(Re)builds the chamber in <paramref name="slot"/> fresh: voxels, spawn points, dummy spots, kill volumes.</summary>
        public static void LoadLocal(int slot)
        {
            var world = VoxelWorld.Current;
            if (world == null || slot < 1 || slot > MaxSlots) return;
            CloseLocal(slot);

            Vector3 at = SlotOrigin(slot);
            var prefab = MatchManager.PracticePrefab;
            Arena arena;
            VoxelArenaData data;
            if (prefab != null)
            {
                var go = Object.Instantiate(prefab, at, Quaternion.identity);
                arena = go.GetComponent<Arena>();
                if (arena == null) arena = go.AddComponent<Arena>();
                data = arena.voxelData != null && Baked.TryGetValue(arena.voxelData, out var cached) ? cached : arena.LoadData();
                if (data != null && arena.voxelData != null) Baked[arena.voxelData] = data;
            }
            else
            {
                arena = PracticeArena.Create(_generated);
                arena.transform.position = at;
                data = _generated = arena.RuntimeData;
            }
            arena.name = "Practice Chamber " + slot;
            arena.HideSources();
            if (data != null) world.Stamp(data, new Vector3Int(slot * SlotCells, 0, 0));
            Physics.SyncTransforms();

            var ch = new Chamber { root = arena.gameObject, killY = arena.KillY };
            arena.GetComponentsInChildren(true, ch.spawns);
            arena.GetComponentsInChildren(true, ch.dummySpots);
            Chambers[slot] = ch;
        }

        /// <summary>Removes the chamber in <paramref name="slot"/> (its voxels, its objects, and any rocks/walls still moving in it).</summary>
        public static void CloseLocal(int slot)
        {
            if (Chambers.TryGetValue(slot, out var ch))
            {
                if (ch.root != null)
                {
                    ch.root.SetActive(false); // spawn points / kill volumes unregister now
                    Object.Destroy(ch.root);
                }
                Chambers.Remove(slot);
            }
            int min = slot * SlotCells - SlotCells / 2, max = slot * SlotCells + SlotCells / 2;
            VoxelWorld.Current?.ClearRegion(min, max);
            if (WorldNet.Instance != null)
            {
                Vector3 o = WorldOrigin;
                WorldNet.Instance.ClearEffectsIn(o.x + min * VoxelWorld.Size, o.x + max * VoxelWorld.Size);
            }
        }

        /// <summary>The main arena is being replaced: every chamber goes with it.</summary>
        public static void ClearAllLocal()
        {
            foreach (var ch in Chambers.Values)
                if (ch.root != null) { ch.root.SetActive(false); Object.Destroy(ch.root); }
            Chambers.Clear();
        }

        // ------------------------------------------------------------------ server

        static readonly Dictionary<ulong, int> SlotOfClient = new();
        static readonly Dictionary<int, List<TestDummy>> DummiesIn = new();
        static readonly Dictionary<ulong, double> DiedAt = new();

        static bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

        /// <summary>Server: into a fresh chamber of their own, alive with full mana.</summary>
        public static void ServerEnter(PlayerController pc)
        {
            if (!IsServer || pc == null || pc.InPractice || VoxelWorld.Current == null) return;
            int slot = FreeSlot();
            if (slot == 0) return;
            SlotOfClient[pc.OwnerClientId] = slot;
            WorldNet.ServerPracticeLoad(slot);
            pc.PracticeSlot.Value = (byte)slot;
            SpawnDummies(slot);
            SpawnPlayer(pc, slot);
        }

        /// <summary>Server: the chamber as new — ground, dummies (one-off ones gone), and you back at the start.</summary>
        public static void ServerReset(PlayerController pc)
        {
            if (!IsServer || pc == null || !SlotOfClient.TryGetValue(pc.OwnerClientId, out int slot)) return;
            RemoveDummies(slot);
            // everything else in it goes too: summoned zombies, and spells still running there (a tower, rocks circling...)
            Vector3 o = SlotOrigin(slot);
            SpellServer.ServerClearPractice(pc, slot, o.x - SlotMeters * 0.5f, o.x + SlotMeters * 0.5f);
            WorldNet.ServerPracticeLoad(slot);
            SpawnDummies(slot);
            SpawnPlayer(pc, slot);
        }

        /// <summary>Server: a dummy that doesn't come back, standing where the player aimed (<paramref name="corpse"/>: lying there dead, to raise).</summary>
        public static void ServerSummon(PlayerController pc, Vector3 point, bool corpse = false)
        {
            if (!IsServer || pc == null || !pc.IsAlive || !SlotOfClient.TryGetValue(pc.OwnerClientId, out int slot)) return;
            if (SlotAt(point) != slot || (point - pc.transform.position).magnitude > SummonReach) return;
            var list = Dummies(slot);
            int temps = 0;
            foreach (var d in list) if (d != null && d.Temporary) temps++;
            if (temps >= MaxSummoned)
            {
                // too many: the oldest one-off goes
                var oldest = list.Find(d => d != null && d.Temporary);
                if (oldest != null) { list.Remove(oldest); Despawn(oldest); }
            }
            Vector3 face = pc.transform.position - point;
            face.y = 0f;
            var rot = face.sqrMagnitude > 0.01f ? Quaternion.LookRotation(face) : Quaternion.identity;
            var made = Spawn(slot, point + Vector3.up * 0.95f, rot, true);
            if (corpse && made != null) made.ServerMakeCorpse();
        }

        /// <summary>Server: out of the chamber, watching the match.</summary>
        public static void ServerLeave(PlayerController pc)
        {
            if (!IsServer || pc == null || !SlotOfClient.TryGetValue(pc.OwnerClientId, out int slot)) return;
            Close(pc.OwnerClientId, slot);
            pc.PracticeSlot.Value = 0;
            var spot = SpawnPoint.Assign(1);
            pc.ServerLeavePractice(spot.Count > 0 ? spot[0].position : Vector3.up * 3f);
        }

        /// <summary>
        /// Server, at round start (just before the world reloads): the players joining the round leave their
        /// chambers. Everyone else's chamber is rebuilt by <see cref="ServerRebuildChambers"/> once the new arena is in.
        /// </summary>
        public static void ServerRoundStarting(List<PlayerController> joining)
        {
            foreach (var p in joining)
            {
                if (p == null || !p.IsSpawned || !SlotOfClient.TryGetValue(p.OwnerClientId, out int slot)) continue;
                RemoveDummies(slot);
                DummiesIn.Remove(slot);
                SlotOfClient.Remove(p.OwnerClientId);
                DiedAt.Remove(p.OwnerClientId);
                p.PracticeSlot.Value = 0;
            }
            // anyone marked as practising without a chamber (shouldn't happen) is back in the match too
            foreach (var p in PlayerController.All)
                if (p != null && p.IsSpawned && p.PracticeSlot.Value != 0 && !SlotOfClient.ContainsKey(p.OwnerClientId))
                    p.PracticeSlot.Value = 0;
        }

        /// <summary>
        /// Server, right after the world reloaded for a new round (which wiped every chamber): the players who stayed
        /// in the practice range get their chamber back, fresh, with its dummies, and start over in it.
        /// </summary>
        public static void ServerRebuildChambers()
        {
            foreach (var kv in new List<KeyValuePair<ulong, int>>(SlotOfClient))
            {
                var pc = PlayerController.ServerFind(kv.Key);
                if (pc == null) continue; // ServerTick closes it
                ServerReset(pc);
            }
        }

        /// <summary>Server, every frame: practising players come back after dying; chambers of people who left close.</summary>
        public static void ServerTick()
        {
            if (SlotOfClient.Count == 0) return;
            var nm = NetworkManager.Singleton;
            List<ulong> gone = null;
            foreach (var kv in SlotOfClient)
            {
                var pc = PlayerController.ServerFind(kv.Key);
                if (pc == null || !nm.ConnectedClients.ContainsKey(kv.Key)) { (gone ??= new()).Add(kv.Key); continue; }
                if (!pc.Dead.Value) { DiedAt.Remove(kv.Key); continue; }
                if (!DiedAt.TryGetValue(kv.Key, out double t)) DiedAt[kv.Key] = WorldNet.Now;
                else if (WorldNet.Now - t >= RespawnDelay) { DiedAt.Remove(kv.Key); SpawnPlayer(pc, kv.Value); }
            }
            if (gone != null)
                foreach (var c in gone) Close(c, SlotOfClient[c]);
        }

        /// <summary>Session over: forget everything (the objects go with the world).</summary>
        public static void ResetAll()
        {
            SlotOfClient.Clear();
            DummiesIn.Clear();
            DiedAt.Clear();
            ClearAllLocal();
        }

        static void Close(ulong client, int slot)
        {
            RemoveDummies(slot);
            DummiesIn.Remove(slot);
            SlotOfClient.Remove(client);
            DiedAt.Remove(client);
            WorldNet.ServerPracticeClose(slot);
        }

        static int FreeSlot()
        {
            for (int s = 1; s <= MaxSlots; s++)
                if (!SlotOfClient.ContainsValue(s)) return s;
            return 0;
        }

        static void SpawnPlayer(PlayerController pc, int slot)
        {
            Vector3 pos;
            float yaw = 0f;
            var spawns = Chambers.TryGetValue(slot, out var ch) ? ch.spawns.FindAll(s => s != null) : new List<SpawnPoint>();
            if (spawns.Count > 0)
            {
                var s = spawns[Random.Range(0, spawns.Count)];
                pos = s.transform.position;
                yaw = s.transform.eulerAngles.y;
            }
            else
            {
                pos = SlotOrigin(slot);
                if (Physics.Raycast(pos + Vector3.up * 80f, Vector3.down, out var hit, 200f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                    pos = hit.point + Vector3.up;
            }
            pc.ServerPracticeSpawn(pos, yaw);
        }

        static List<TestDummy> Dummies(int slot)
        {
            if (!DummiesIn.TryGetValue(slot, out var list)) DummiesIn[slot] = list = new List<TestDummy>();
            list.RemoveAll(d => d == null || !d.IsSpawned);
            return list;
        }

        /// <summary>The chamber's own dummies: one per Dummy Spot, or one in front of the first spawn if it has none.</summary>
        static void SpawnDummies(int slot)
        {
            if (!Chambers.TryGetValue(slot, out var ch)) return;
            int placed = 0;
            foreach (var spot in ch.dummySpots)
            {
                if (spot == null) continue;
                Spawn(slot, spot.transform.position, Quaternion.Euler(0f, spot.transform.eulerAngles.y, 0f), false);
                placed++;
            }
            if (placed > 0) return;
            var first = ch.spawns.Find(s => s != null);
            Vector3 from = first != null ? first.transform.position : SlotOrigin(slot) + Vector3.up * 3f;
            Vector3 fwd = first != null ? first.transform.forward : Vector3.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 0.01f ? fwd.normalized : Vector3.forward;
            Spawn(slot, from + fwd * 8f, Quaternion.LookRotation(-fwd), false);
        }

        static TestDummy Spawn(int slot, Vector3 pos, Quaternion rot, bool temporary)
        {
            var prefab = DummyPrefab();
            if (prefab == null) { Debug.LogWarning("[SME] No TestDummy in the network prefab list: practice has no dummies."); return null; }
            var go = Object.Instantiate(prefab, pos, rot);
            var d = go.GetComponent<TestDummy>();
            d.PracticeSlot = slot;
            d.Temporary = temporary;
            go.GetComponent<NetworkObject>().Spawn(true);
            d.ServerReset(); // dropped onto the ground under the spot
            Dummies(slot).Add(d);
            return d;
        }

        static void RemoveDummies(int slot)
        {
            if (DummiesIn.TryGetValue(slot, out var list))
            {
                foreach (var d in list) Despawn(d);
                list.Clear();
            }
            // and any others that belong to it (Summon Horde zombies, raised dummies)
            foreach (var c in Combatant.Registry.ToArray())
                if (c is TestDummy d && d.PracticeSlot == slot) Despawn(d);
        }

        static void Despawn(TestDummy d)
        {
            if (d != null && d.IsSpawned) d.NetworkObject.Despawn(true);
        }

        static GameObject _dummyPrefab;

        public static GameObject DummyPrefab()
        {
            if (_dummyPrefab != null) return _dummyPrefab;
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.NetworkConfig?.Prefabs == null) return null;
            foreach (var p in nm.NetworkConfig.Prefabs.Prefabs)
                if (p?.Prefab != null && p.Prefab.GetComponent<TestDummy>() != null) return _dummyPrefab = p.Prefab;
            return null;
        }
    }
}
