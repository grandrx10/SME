using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

namespace SuddenMageEncounter
{
    public enum MatchPhase : byte { None = 0, Playing = 1, RoundOver = 2, Countdown = 3, Building = 4, Intermission = 5 }

    /// <summary>
    /// Free-for-all rounds. A round loads a random arena, which builds itself over <see cref="BuildSeconds"/> while
    /// everyone watches it from a camera circling it (so loading never freezes the game). Then everyone spawns into it
    /// for a 3-2-1 countdown (you can look around, but can't move or cast, and your mana stays at 0), then FIGHT:
    /// mana starts flowing and the last one standing wins. The winner is shown for <see cref="WinnerSeconds"/>, then everyone goes back to watching and the next
    /// arena loads after a <see cref="IntermissionSeconds"/> break (skipped early once <see cref="SkipFraction"/> of the players vote to). Eliminated players (and anyone joining mid-round) watch until the next round. A round needs two
    /// or more players: until then everyone waits (<see cref="MatchPhase.None"/>), watching an arena from a slow
    /// circle around it, and can go off to the practice range meanwhile. People in the practice range are pulled into the next round unless they've turned that off
    /// (<see cref="PlayerController.JoinRounds"/>); those who stay keep practising in a fresh chamber.
    /// The arena itself is loaded through <see cref="WorldNet"/> (so its voxels and edits stay in order).
    /// Round state goes over NGO named messages (no NetworkObject needed).
    /// </summary>
    [AddComponentMenu("Sudden Mage Encounter/Match Manager")]
    [DefaultExecutionOrder(-200)]
    public class MatchManager : MonoBehaviour
    {
        const string MsgState = "sme_match_state";
        const string MsgHello = "sme_match_hello";
        const string MsgSkip = "sme_match_skip";

        [Tooltip("Arena prefabs (each with an Arena component). Empty = the built-in practice arena.")]
        public List<GameObject> arenas = new();
        /// <summary>The new arena builds itself while everyone watches it, before anyone spawns into it.</summary>
        public const float BuildSeconds = 5f;
        /// <summary>The 3-2-1 before a round: everyone can look around, nobody can move, cast or gain mana.</summary>
        public const float CountdownSeconds = 3f;
        /// <summary>How long the winner is announced before everyone goes back to watching and the next arena loads.</summary>
        public const float WinnerSeconds = 5f;
        /// <summary>The break between rounds (after the winner is shown), before the next arena loads.</summary>
        public const float IntermissionSeconds = 30f;
        /// <summary>The break ends early once at least this share of the players vote to skip it.</summary>
        public const float SkipFraction = 0.6f;
        /// <summary>Players a round needs before it starts.</summary>
        public const int MinPlayers = 2;

        [Tooltip("Arena prefab copied into each player's practice chamber (add Dummy Spots to it to place its dummies). " +
                 "Empty = the first arena in the list above.")]
        public GameObject practiceArena;

        /// <summary>What practice chambers are built from (null = the generated practice grounds).</summary>
        public static GameObject PracticePrefab
        {
            get
            {
                var i = Instance;
                if (i == null) return null;
                if (i.practiceArena != null) return i.practiceArena;
                foreach (var a in i.arenas) if (a != null) return a;
                return null;
            }
        }

        public struct State
        {
            public int round;
            public short arena;
            public MatchPhase phase;
            public double phaseStart;
            public string winner;   // "" = nobody
            public sbyte winnerColor;
            public byte skipVotes, skipVoters; // the break between rounds: votes to skip it, and how many people can vote
        }

        public static MatchManager Instance { get; private set; }
        public static State Current { get; private set; }
        public static event Action StateChanged;

        public static bool Running => Instance != null && Current.phase != MatchPhase.None;
        /// <summary>The countdown is over and the round is on (clients count the countdown down themselves, so it ends on time).</summary>
        public static bool Fighting => Current.phase == MatchPhase.Playing ||
                                       (Current.phase == MatchPhase.Countdown && PhaseTime >= CountdownSeconds);
        public static bool CastingAllowed => !Running || Fighting;
        /// <summary>The arena is still building itself (everyone's watching it go up).</summary>
        public static bool BuildingArena => Current.phase == MatchPhase.Building;
        /// <summary>Connected, but not enough players for a round: everyone waits (or practises).</summary>
        public static bool WaitingForPlayers => Instance != null && Instance._registered && Current.phase == MatchPhase.None;
        /// <summary>The 3-2-1 is on: those in the round stand still (they can still look around).</summary>
        public static bool CountingDown => Current.phase == MatchPhase.Countdown && PhaseTime < CountdownSeconds;
        public static bool MovementLockedFor(PlayerController p) => CountingDown && p != null && !p.InPractice;
        /// <summary>Practice chambers don't care about the round: you can always cast there.</summary>
        public static bool CastingAllowedFor(PlayerController p) => CastingAllowed || (p != null && p.InPractice);
        public static double ServerNow => WorldNet.Now;
        public static float PhaseTime => (float)(ServerNow - Current.phaseStart);

        GameObject _arenaInstance;
        bool _registered, _helloSent;
        State _server;
        readonly HashSet<ulong> _participants = new();
        readonly HashSet<ulong> _skipVotes = new(), _voters = new();
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

            // the round's on and you're in it: the arena must be all there under your feet
            var me = PlayerController.Local;
            var world = VoxelWorld.Current;
            if (world != null && world.Building && (Current.phase is MatchPhase.Countdown or MatchPhase.Playing) &&
                me != null && me.IsSpawned && me.IsAlive && !me.InPractice)
                world.FinishBuild();

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
            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgSkip, OnSkipMessage);
            _registered = true;
            _helloSent = false;
        }

        void ResetAll()
        {
            _registered = false;
            _helloSent = false;
            _server = default;
            _participants.Clear();
            _skipVotes.Clear();
            LocalSkipVote = false;
            Practice.ResetAll();
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
            Practice.ClearAllLocal(); // chambers live in the arena's voxel world
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
                r.ReadValueSafe(out s.skipVotes);
                r.ReadValueSafe(out s.skipVoters);
                Apply(s);
            }
            catch (Exception e) { Debug.LogWarning("[SME] bad match state: " + e.Message); }
        }

        // ------------------------------------------------------------------ skipping the break between rounds

        /// <summary>This player has voted to skip the current break (until it ends).</summary>
        public static bool LocalSkipVote { get; private set; }
        /// <summary>The break between rounds is on (a vote to skip it can be cast).</summary>
        public static bool InIntermission => Running && Current.phase == MatchPhase.Intermission;
        /// <summary>Votes needed to skip the break: <see cref="SkipFraction"/> of those who can vote, rounded up.</summary>
        public static int SkipNeeded(int voters) => Mathf.Max(1, Mathf.CeilToInt(voters * SkipFraction - 1e-4f));

        /// <summary>Local player: vote (or take back the vote) to skip the break between rounds.</summary>
        public static void ToggleSkipVote()
        {
            var i = Instance;
            var nm = NetworkManager.Singleton;
            if (i == null || nm == null || !i._registered || !InIntermission) return;
            LocalSkipVote = !LocalSkipVote;
            if (nm.IsServer) i.ServerSetSkip(nm.LocalClientId, LocalSkipVote);
            else
            {
                using var w = new FastBufferWriter(8, Allocator.Temp);
                w.WriteValueSafe((byte)(LocalSkipVote ? 1 : 0));
                nm.CustomMessagingManager.SendNamedMessage(MsgSkip, NetworkManager.ServerClientId, w, NetworkDelivery.ReliableSequenced);
            }
            StateChanged?.Invoke();
        }

        void OnSkipMessage(ulong sender, FastBufferReader r)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            try
            {
                r.ReadValueSafe(out byte on);
                ServerSetSkip(sender, on != 0);
            }
            catch (Exception e) { Debug.LogWarning("[SME] bad skip vote: " + e.Message); }
        }

        void ServerSetSkip(ulong client, bool on)
        {
            if (_server.phase != MatchPhase.Intermission) return;
            bool changed = on ? _skipVotes.Add(client) : _skipVotes.Remove(client);
            if (changed) CountSkipVotes(true);
        }

        /// <summary>Server: everyone connected with a player can vote (including those practising).</summary>
        void CountSkipVotes(bool broadcast)
        {
            var nm = NetworkManager.Singleton;
            var voters = _voters;
            voters.Clear();
            foreach (var p in PlayerController.All) if (p != null && p.IsSpawned) voters.Add(p.OwnerClientId);
            _skipVotes.RemoveWhere(id => !voters.Contains(id) || (nm != null && !nm.ConnectedClients.ContainsKey(id)));
            byte votes = (byte)Mathf.Min(255, _skipVotes.Count), n = (byte)Mathf.Min(255, voters.Count);
            if (votes == _server.skipVotes && n == _server.skipVoters) return;
            _server.skipVotes = votes;
            _server.skipVoters = n;
            if (broadcast) Broadcast();
        }

        /// <summary>The break between rounds is over (ran out, or voted away): on to the next round, or waiting for players.</summary>
        void EndIntermission()
        {
            _skipVotes.Clear();
            _server.skipVotes = _server.skipVoters = 0;
            if (ReadyForRound(out _) >= MinPlayers) BeginRound();
            else ServerWait();
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
            w.WriteValueSafe(s.skipVotes);
            w.WriteValueSafe(s.skipVoters);
            nm.CustomMessagingManager.SendNamedMessage(MsgState, client, w, NetworkDelivery.ReliableSequenced);
        }

        void Apply(State s)
        {
            if (s.phase != MatchPhase.Intermission || s.round != Current.round) LocalSkipVote = false; // a new break: not voted yet
            Current = s;
            StateChanged?.Invoke();
        }

        // ------------------------------------------------------------------ server

        /// <summary>In the match (not off in a practice chamber).</summary>
        static bool InMatch(PlayerController p) => p != null && p.IsSpawned && !p.InPractice;

        /// <summary>In the practice range, but wants to be pulled into the next round.</summary>
        static bool JoinsFromPractice(PlayerController p) => p != null && p.IsSpawned && p.InPractice && p.JoinRounds.Value;

        static int PlayersInMatch()
        {
            int n = 0;
            foreach (var p in PlayerController.All) if (InMatch(p)) n++;
            return n;
        }

        static int PractisersJoining()
        {
            int n = 0;
            foreach (var p in PlayerController.All) if (JoinsFromPractice(p)) n++;
            return n;
        }

        /// <summary>
        /// Who would play a round starting now. Anyone in the match, plus practisers who want in -- but only when
        /// that makes two or more (alone, a practiser isn't dragged out into a solo round).
        /// </summary>
        public static int ReadyForRound(out bool pullPractisers)
        {
            int match = PlayersInMatch(), joining = PractisersJoining();
            pullPractisers = match + joining >= MinPlayers;
            return pullPractisers ? match + joining : match;
        }

        /// <summary>Not enough players for a round: everyone in the match watches an arena (loading one if there's none yet).</summary>
        void ServerWait()
        {
            foreach (var p in PlayerController.All)
                if (InMatch(p) && p.IsAlive) p.ServerMakeSpectator();
            bool changed = _server.phase != MatchPhase.None;
            _server.phase = MatchPhase.None;
            if (_arenaInstance == null)
            {
                // something to look at, and a world for the practice chambers to live in
                int arena = PickArena();
                _server.arena = (short)arena;
                WorldNet.ServerLoadArena(arena, _server.round);
                Practice.ServerRebuildChambers();
                changed = true;
            }
            if (!changed) return;
            _server.phaseStart = ServerNow;
            _server.winner = "";
            _server.winnerColor = -1;
            Broadcast();
        }

        void ServerTick()
        {
            Practice.ServerTick();
            double now = ServerNow;
            switch (_server.phase)
            {
                case MatchPhase.None:
                    if (ReadyForRound(out _) >= MinPlayers) BeginRound();
                    else ServerWait();
                    return;
                case MatchPhase.RoundOver:
                    if (now - _server.phaseStart < WinnerSeconds) return;
                    // the winner's had their moment: everyone back to watching for the break before the next round
                    foreach (var p in PlayerController.All)
                        if (InMatch(p) && p.IsAlive) p.ServerMakeSpectator();
                    if (ReadyForRound(out _) < MinPlayers) { ServerWait(); return; }
                    _server.phase = MatchPhase.Intermission;
                    _server.phaseStart = now;
                    _skipVotes.Clear();
                    _server.skipVotes = 0;
                    _server.skipVoters = 0;
                    CountSkipVotes(false);
                    Broadcast();
                    return;
                case MatchPhase.Intermission:
                {
                    CountSkipVotes(true); // (people joining and leaving change how many votes it takes)
                    bool voted = _server.skipVoters > 0 && _server.skipVotes >= SkipNeeded(_server.skipVoters);
                    if (voted || now - _server.phaseStart >= IntermissionSeconds) EndIntermission();
                    else if (ReadyForRound(out _) < MinPlayers) { _skipVotes.Clear(); ServerWait(); }
                    return;
                }
                case MatchPhase.Building:
                    // the arena is still going up: nobody's in it yet
                    if (now - _server.phaseStart >= BuildSeconds) StartCountdown();
                    return;
                case MatchPhase.Countdown:
                    if (now - _server.phaseStart >= CountdownSeconds)
                    {
                        _server.phase = MatchPhase.Playing;
                        _server.phaseStart += CountdownSeconds; // the moment mana started flowing
                        Broadcast();
                    }
                    break;
            }

            // countdown / playing: who's left? (someone who went off to practise has given up their place)
            var nm = NetworkManager.Singleton;
            _participants.RemoveWhere(id => !nm.ConnectedClients.ContainsKey(id));
            // sides still standing: a necromancer and the Undead they raised are one side (it's theirs if it wins)
            var sides = new HashSet<ulong>();
            PlayerController last = null;
            foreach (var p in PlayerController.All)
            {
                // (dead with Undying still to come: they're getting up in a moment, so they still count)
                if (!InMatch(p) || !_participants.Contains(p.OwnerClientId) || !p.IsAlive && !SpellServer.UndyingPending(p)) continue;
                sides.Add(p.Team);
                last = p;
            }
            if (sides.Count <= 1)
            {
                PlayerController winner = null;
                if (last != null) winner = last.IsUndead ? WorldNet.FindCaster(last.Team) ?? last : last;
                EndRound(winner);
            }
        }

        /// <summary>A random arena, never the same one twice in a row (-1 = the built-in practice arena).</summary>
        int PickArena()
        {
            int count = arenas.Count;
            int arena = -1;
            if (count == 1) arena = 0;
            else if (count > 1)
            {
                arena = Random.Range(0, count - 1);
                if (arena >= _lastArena && _lastArena >= 0) arena++;
            }
            _lastArena = arena;
            return arena;
        }

        /// <summary>
        /// A new round: the next arena loads and starts building itself (over <see cref="BuildSeconds"/>) while
        /// everyone watches. Practisers who want in leave their chambers now; nobody spawns until it's built.
        /// </summary>
        void BeginRound()
        {
            int arena = PickArena();
            _server.round++;
            _server.arena = (short)arena;
            _server.phase = MatchPhase.Building;
            _server.phaseStart = ServerNow;
            _server.winner = "";
            _server.winnerColor = -1;

            // who plays: everyone in the match, and practisers who want in
            ReadyForRound(out bool pullPractisers);
            var players = new List<PlayerController>();
            foreach (var p in PlayerController.All)
                if (InMatch(p) || (pullPractisers && JoinsFromPractice(p))) players.Add(p);

            Practice.ServerRoundStarting(players);              // those players leave their chambers
            foreach (var p in players)
                if (p.IsAlive) p.ServerMakeSpectator();         // and everyone watches the arena go up
            WorldNet.ServerLoadArena(arena, _server.round);     // loads it here right away too (and wipes every chamber)
            Practice.ServerRebuildChambers();                   // whoever stayed practising gets a fresh chamber back

            _participants.Clear();
            Broadcast();
        }

        /// <summary>The arena's up: everyone in the match spawns into it for the 3-2-1 (no mana until it's over).</summary>
        void StartCountdown()
        {
            if (VoxelWorld.Current != null) VoxelWorld.Current.FinishBuild(); // spawn spots and dummies need the ground
            Physics.SyncTransforms();
            _server.phase = MatchPhase.Countdown;
            _server.phaseStart = ServerNow;

            var players = new List<PlayerController>();
            foreach (var p in PlayerController.All)
                if (InMatch(p)) players.Add(p);
            _participants.Clear();
            if (players.Count < MinPlayers)
            {
                // someone left (or went off to practise) while it was building: not enough for a round after all
                ServerWait();
                return;
            }
            var spots = SpawnPoint.Assign(players.Count);
            double manaFrom = _server.phaseStart + CountdownSeconds;
            for (int i = 0; i < players.Count; i++)
            {
                _participants.Add(players[i].OwnerClientId);
                players[i].ServerBeginRound(spots[i].position, spots[i].yaw, manaFrom);
            }
            foreach (var c in Combatant.Registry.ToArray())
                if (c is TestDummy d && !d.InPracticeChamber)
                {
                    if (d.Temporary) d.NetworkObject.Despawn(true); // a Summon Horde zombie from last round
                    else d.ServerReset();
                }
            Broadcast();
        }

        void EndRound(PlayerController winner)
        {
            _server.phase = MatchPhase.RoundOver;
            _server.phaseStart = ServerNow;
            _server.winner = winner != null ? winner.DisplayName : "";
            _server.winnerColor = (sbyte)(winner != null && winner.ColorIndex.Value != 255 ? winner.ColorIndex.Value : -1);
            if (winner != null)
            {
                winner.Wins.Value++;
                winner.ServerGrantGold(Rewards.RoundWin);
            }
            foreach (var id in _participants)
            {
                var p = PlayerController.ServerFind(id);
                if (p != null) p.ServerRoundPlayed();
            }
            Broadcast();
        }

        /// <summary>
        /// Server: a player object just spawned (joined). While the arena builds they watch, and spawn with everyone
        /// else when it's up; early in the countdown they're dropped straight into the round; once it's under way
        /// they spectate until the next one.
        /// </summary>
        public static void ServerOnPlayerJoined(PlayerController pc)
        {
            var i = Instance;
            if (i == null || pc == null) return;
            if (i._server.phase is MatchPhase.None or MatchPhase.Building)
            {
                pc.ServerMakeSpectator(); // waiting (a round starts on the next tick if there are enough now), or watching it build
                return;
            }
            if (i._server.phase == MatchPhase.Countdown && ServerNow - i._server.phaseStart < CountdownSeconds - 0.5)
            {
                if (VoxelWorld.Current != null) VoxelWorld.Current.FinishBuild();
                var spot = SpawnPoint.Assign(1);
                Vector3 pos = spot.Count > 0 ? spot[0].position : Vector3.up * 3f;
                float yaw = spot.Count > 0 ? spot[0].yaw : 0f;
                i._participants.Add(pc.OwnerClientId);
                pc.ServerBeginRound(pos, yaw, i._server.phaseStart + CountdownSeconds);
                return;
            }
            pc.ServerMakeSpectator();
        }
    }
}
