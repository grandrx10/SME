using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

namespace SuddenMageEncounter
{
    public enum MatchPhase : byte { None = 0, Playing = 1, RoundOver = 2 }

    /// <summary>
    /// Free-for-all rounds: everyone starts alive on a random arena with no mana; the last one standing wins.
    /// Eliminated players (and anyone joining mid-round) watch until the next round. Playing alone, a round ends
    /// when you die (so you can practise); when someone joins a solo round, a proper round starts.
    /// The arena itself is loaded through <see cref="WorldNet"/> (so its voxels and edits stay in order).
    /// Round state goes over NGO named messages (no NetworkObject needed).
    /// </summary>
    [AddComponentMenu("Sudden Mage Encounter/Match Manager")]
    [DefaultExecutionOrder(-200)]
    public class MatchManager : MonoBehaviour
    {
        const string MsgState = "sme_match_state";
        const string MsgHello = "sme_match_hello";

        [Tooltip("Arena prefabs (each with an Arena component). Empty = the built-in practice arena.")]
        public List<GameObject> arenas = new();
        [Tooltip("Seconds the winner is shown before the next round starts.")]
        public float roundOverSeconds = 4f;

        public struct State
        {
            public int round;
            public short arena;
            public MatchPhase phase;
            public double phaseStart;
            public string winner;   // "" = nobody
            public sbyte winnerColor;
        }

        public static MatchManager Instance { get; private set; }
        public static State Current { get; private set; }
        public static event Action StateChanged;

        public static bool Running => Instance != null && Current.phase != MatchPhase.None;
        public static bool CastingAllowed => !Running || Current.phase == MatchPhase.Playing;
        public static double ServerNow => WorldNet.Now;
        public static float PhaseTime => (float)(ServerNow - Current.phaseStart);

        GameObject _arenaInstance;
        bool _registered, _helloSent;
        State _server;
        readonly HashSet<ulong> _participants = new();
        int _lastArena = -2;

        void Awake()
        {
            Instance = this;
            Current = default;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------ lifecycle

        void Update()
        {
            var nm = NetworkManager.Singleton;
            bool listening = nm != null && nm.IsListening && nm.CustomMessagingManager != null;
            if (listening && !_registered) Register(nm);
            else if (!listening && _registered) ResetAll();
            if (!_registered) return;

            if (nm.IsServer) ServerTick();
            else if (nm.IsConnectedClient && !_helloSent)
            {
                _helloSent = true;
                using var w = new FastBufferWriter(8, Allocator.Temp);
                w.WriteValueSafe((byte)1);
                nm.CustomMessagingManager.SendNamedMessage(MsgHello, NetworkManager.ServerClientId, w, NetworkDelivery.ReliableSequenced);
            }
        }

        void Register(NetworkManager nm)
        {
            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgState, OnStateMessage);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgHello, OnHelloMessage);
            _registered = true;
            _helloSent = false;
        }

        void ResetAll()
        {
            _registered = false;
            _helloSent = false;
            _server = default;
            _participants.Clear();
            if (_arenaInstance != null) Destroy(_arenaInstance);
            _arenaInstance = null;
            Debris.ClearAll();
            Current = default;
            StateChanged?.Invoke();
        }

        // ------------------------------------------------------------------ arena loading (every peer, via WorldNet)

        /// <summary>Replaces the arena with a fresh copy of arena <paramref name="index"/> (-1 = practice arena).</summary>
        public static void LoadArenaLocal(int index, int round)
        {
            var i = Instance;
            if (i == null) return;
            if (i._arenaInstance != null)
            {
                i._arenaInstance.SetActive(false); // gone this frame for spawn picking / physics
                Destroy(i._arenaInstance);
            }
            GameObject prefab = index >= 0 && index < i.arenas.Count ? i.arenas[index] : null;
            Arena arena;
            if (prefab != null)
            {
                var go = Instantiate(prefab, prefab.transform.position, prefab.transform.rotation);
                go.name = prefab.name;
                arena = go.GetComponent<Arena>();
                if (arena == null) arena = go.AddComponent<Arena>();
            }
            else arena = PracticeArena.Create();
            i._arenaInstance = arena.gameObject;
            arena.Build();
            Physics.SyncTransforms();
        }

        public static string ArenaName => Arena.Current != null ? Arena.Current.DisplayName : "";

        // ------------------------------------------------------------------ messages

        void OnHelloMessage(ulong sender, FastBufferReader r)
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsServer) Send(nm, sender);
        }

        void OnStateMessage(ulong sender, FastBufferReader r)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.IsServer || sender != NetworkManager.ServerClientId) return;
            try
            {
                var s = new State();
                r.ReadValueSafe(out s.round);
                r.ReadValueSafe(out s.arena);
                r.ReadValueSafe(out byte phase); s.phase = (MatchPhase)phase;
                r.ReadValueSafe(out s.phaseStart);
                r.ReadValueSafe(out s.winner);
                r.ReadValueSafe(out s.winnerColor);
                Apply(s);
            }
            catch (Exception e) { Debug.LogWarning("[SME] bad match state: " + e.Message); }
        }

        void Broadcast()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            foreach (var c in nm.ConnectedClientsIds)
                if (c != nm.LocalClientId) Send(nm, c);
            Apply(_server);
        }

        void Send(NetworkManager nm, ulong client)
        {
            if (client == nm.LocalClientId) return;
            using var w = new FastBufferWriter(128, Allocator.Temp, 1024);
            var s = _server;
            w.WriteValueSafe(s.round);
            w.WriteValueSafe(s.arena);
            w.WriteValueSafe((byte)s.phase);
            w.WriteValueSafe(s.phaseStart);
            w.WriteValueSafe(s.winner ?? "");
            w.WriteValueSafe(s.winnerColor);
            nm.CustomMessagingManager.SendNamedMessage(MsgState, client, w, NetworkDelivery.ReliableSequenced);
        }

        void Apply(State s)
        {
            Current = s;
            StateChanged?.Invoke();
        }

        // ------------------------------------------------------------------ server

        void ServerTick()
        {
            if (_server.phase == MatchPhase.None)
            {
                if (PlayerController.All.Count > 0) BeginRound();
                return;
            }
            double now = ServerNow;
            if (_server.phase == MatchPhase.RoundOver)
            {
                if (now - _server.phaseStart >= roundOverSeconds) BeginRound();
                return;
            }

            // playing: who's left?
            var nm = NetworkManager.Singleton;
            _participants.RemoveWhere(id => !nm.ConnectedClients.ContainsKey(id));
            int alive = 0;
            PlayerController last = null;
            foreach (var p in PlayerController.All)
            {
                if (p == null || !p.IsSpawned || !_participants.Contains(p.OwnerClientId) || !p.IsAlive) continue;
                alive++;
                last = p;
            }
            int players = 0;
            foreach (var p in PlayerController.All) if (p != null && p.IsSpawned) players++;

            if (_participants.Count >= 2 && alive <= 1) EndRound(alive == 1 ? last : null);
            else if (_participants.Count == 1 && alive == 0) EndRound(null);
            else if (_participants.Count <= 1 && players >= 2) EndRound(null, 1.5f); // someone joined a solo round
            else if (_participants.Count == 0 && players > 0) BeginRound();
        }

        void BeginRound()
        {
            int count = arenas.Count;
            int arena = -1;
            if (count == 1) arena = 0;
            else if (count > 1)
            {
                arena = Random.Range(0, count - 1);
                if (arena >= _lastArena && _lastArena >= 0) arena++; // never the same arena twice in a row
            }
            _lastArena = arena;
            _server.round++;
            _server.arena = (short)arena;
            _server.phase = MatchPhase.Playing;
            _server.phaseStart = ServerNow;
            _server.winner = "";
            _server.winnerColor = -1;

            WorldNet.ServerLoadArena(arena, _server.round); // loads it here right away too

            _participants.Clear();
            var players = new List<PlayerController>();
            foreach (var p in PlayerController.All) if (p != null && p.IsSpawned) players.Add(p);
            var spots = SpawnPoint.Assign(players.Count);
            for (int i = 0; i < players.Count; i++)
            {
                _participants.Add(players[i].OwnerClientId);
                players[i].ServerBeginRound(spots[i].position, spots[i].yaw);
            }
            foreach (var c in Combatant.Registry.ToArray())
                if (c is TestDummy d) d.ServerReset();
            Broadcast();
        }

        void EndRound(PlayerController winner, float hold = -1f)
        {
            _server.phase = MatchPhase.RoundOver;
            _server.phaseStart = ServerNow - (hold > 0f ? roundOverSeconds - hold : 0f);
            _server.winner = winner != null ? winner.DisplayName : "";
            _server.winnerColor = (sbyte)(winner != null && winner.ColorIndex.Value != 255 ? winner.ColorIndex.Value : -1);
            if (winner != null) winner.Wins.Value++;
            Broadcast();
        }

        /// <summary>Server: a player object just spawned (joined). Mid-round they spectate until the next one.</summary>
        public static void ServerOnPlayerJoined(PlayerController pc)
        {
            var i = Instance;
            if (i == null || pc == null) return;
            if (i._server.phase == MatchPhase.None) return; // the first round starts on the next tick
            if (i._server.phase == MatchPhase.Playing && i._participants.Count == 0)
            {
                i.BeginRound();
                return;
            }
            pc.ServerMakeSpectator();
        }
    }
}
