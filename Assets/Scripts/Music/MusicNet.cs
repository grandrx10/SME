using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Shared music for the lobby, over NGO named messages (no NetworkObject / prefab needed).
    ///
    /// Clients: send their song list (MusicLibrary) to the host whenever it changes, and play whatever the host says
    /// is on, in sync with the server clock (MusicStream). Audio never goes over the game connection —
    /// each player streams it from YouTube themselves.
    ///
    /// Host picks what plays:
    ///  - Players take turns in a random order: everyone with songs gets a turn before anyone goes again,
    ///    and nobody goes twice in a row (unless they're the only one with songs).
    ///  - Within a player's list, a song that just played is very unlikely to come up again, recovering to
    ///    normal odds after about half of that player's list has played.
    ///  - Songs not marked "keep" are removed from the owner's list once they start playing.
    /// </summary>
    public class MusicNet : MonoBehaviour
    {
        const string MsgList = "sme_music_list";   // client -> server: my whole song list
        const string MsgState = "sme_music_state"; // server -> client: what's playing
        const string MsgSkip = "sme_music_skip";   // client -> server: skip / vote to skip
        const string MsgFail = "sme_music_fail";   // client -> server: couldn't play it
        /// <summary>Gap before each song starts, so every client has time to start its stream.</summary>
        const double LeadSeconds = 3.0;
        const float ListSendDelay = 0.4f;

        public struct NowPlaying
        {
            public int playId;
            public ulong owner;
            public string ownerName, id, title;
            public float duration;
            public double start;
            public int votes, needed;
            public bool Active => !string.IsNullOrEmpty(id);
        }

        public static MusicNet Instance { get; private set; }
        public static NowPlaying Current { get; private set; }
        public static event Action StateChanged;

        public static bool Online => Instance != null && Instance._registered;
        public static double ServerNow => NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Time : 0;
        /// <summary>Seconds into the current song (negative before it starts).</summary>
        public static double SongTime => Current.Active ? ServerNow - Current.start : 0;
        public static string PlaybackStatus => Instance != null && Instance._stream != null ? Instance._stream.Status : "";
        public static bool IsMine => Current.Active && NetworkManager.Singleton != null && Current.owner == NetworkManager.Singleton.LocalClientId;
        /// <summary>Host and the song's owner skip instantly; everyone else votes.</summary>
        public static bool CanSkipInstantly => IsMine || (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer);
        public static bool HasVoted => Instance != null && Instance._votedPlayId == Current.playId && Current.Active;

        MusicStream _stream;
        bool _registered, _wasConnected, _listDirty;
        float _listSendAt;
        int _lastStreamedPlayId, _lastMarkedPlayId, _votedPlayId;

        // ---- server state ----
        class PlayerQueue
        {
            public List<Song> songs = new();
            /// <summary>Song id -> value of 'played' when it last played.</summary>
            public readonly Dictionary<string, int> lastPlayed = new();
            public int played;
        }

        readonly Dictionary<ulong, PlayerQueue> _queues = new();
        readonly HashSet<ulong> _pickedThisRound = new();
        readonly HashSet<ulong> _votes = new();
        readonly List<ulong> _tmpIds = new();
        ulong? _lastPicked;
        int _nextPlayId = 1;
        NowPlaying _server;

        void Awake()
        {
            Instance = this;
            MusicTools.Init();
            var go = new GameObject("Music", typeof(AudioSource));
            go.transform.SetParent(transform, false);
            _stream = go.AddComponent<MusicStream>();
            _stream.Failed += OnLocalFail;
            MusicLibrary.Changed += MarkListDirty;
        }

        void OnDestroy()
        {
            MusicLibrary.Changed -= MarkListDirty;
            if (Instance == this) Instance = null;
        }

        void MarkListDirty()
        {
            _listDirty = true;
            _listSendAt = Time.unscaledTime + ListSendDelay;
        }

        // ------------------------------------------------------------------ lifecycle

        void Update()
        {
            var nm = NetworkManager.Singleton;
            bool listening = nm != null && nm.IsListening && nm.CustomMessagingManager != null;
            if (listening && !_registered) Register(nm);
            else if (!listening && _registered) Reset();
            if (!_registered) return;

            // client: (re)send my list on connect and after edits
            bool connected = nm.IsConnectedClient;
            if (connected && !_wasConnected) { _listDirty = true; _listSendAt = 0; }
            _wasConnected = connected;
            if (connected && _listDirty && Time.unscaledTime >= _listSendAt)
            {
                _listDirty = false;
                SendList(nm);
            }

            if (nm.IsServer) ServerTick(nm);
        }

        void Register(NetworkManager nm)
        {
            var cmm = nm.CustomMessagingManager;
            cmm.RegisterNamedMessageHandler(MsgList, OnListMessage);
            cmm.RegisterNamedMessageHandler(MsgState, OnStateMessage);
            cmm.RegisterNamedMessageHandler(MsgSkip, OnSkipMessage);
            cmm.RegisterNamedMessageHandler(MsgFail, OnFailMessage);
            _registered = true;
            _wasConnected = false;
        }

        void Reset()
        {
            _registered = false;
            _wasConnected = false;
            _queues.Clear();
            _pickedThisRound.Clear();
            _votes.Clear();
            _lastPicked = null;
            _server = default;
            _stream.Stop();
            _lastStreamedPlayId = 0;
            Current = default;
            StateChanged?.Invoke();
        }

        // ------------------------------------------------------------------ client -> server

        void SendList(NetworkManager nm)
        {
            var songs = MusicLibrary.Songs;
            if (nm.IsServer)
            {
                ServerSetList(nm.LocalClientId, new List<Song>(songs));
                return;
            }
            using var w = new FastBufferWriter(1024, Allocator.Temp, 128 * 1024);
            int n = Mathf.Min(songs.Count, MusicLibrary.MaxSongs);
            w.WriteValueSafe(n);
            for (int i = 0; i < n; i++)
            {
                w.WriteValueSafe(songs[i].id, true);
                w.WriteValueSafe(songs[i].title);
                w.WriteValueSafe(songs[i].duration);
                w.WriteValueSafe(songs[i].keep);
            }
            nm.CustomMessagingManager.SendNamedMessage(MsgList, NetworkManager.ServerClientId, w, NetworkDelivery.ReliableFragmentedSequenced);
        }

        /// <summary>Skip (host / owner) or vote to skip the current song.</summary>
        public static void RequestSkip()
        {
            var i = Instance;
            var nm = NetworkManager.Singleton;
            if (i == null || !i._registered || nm == null || !Current.Active) return;
            i._votedPlayId = Current.playId;
            SendPlayId(nm, MsgSkip, Current.playId, i.ServerSkip);
        }

        void OnLocalFail(int playId)
        {
            var nm = NetworkManager.Singleton;
            if (!_registered || nm == null || playId == 0) return;
            SendPlayId(nm, MsgFail, playId, ServerFail);
        }

        static void SendPlayId(NetworkManager nm, string msg, int playId, Action<ulong, int> local)
        {
            if (nm.IsServer) { local(nm.LocalClientId, playId); return; }
            using var w = new FastBufferWriter(16, Allocator.Temp);
            w.WriteValueSafe(playId);
            nm.CustomMessagingManager.SendNamedMessage(msg, NetworkManager.ServerClientId, w, NetworkDelivery.ReliableSequenced);
        }

        // ------------------------------------------------------------------ message handlers

        void OnListMessage(ulong sender, FastBufferReader r)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            var list = new List<Song>();
            try
            {
                r.ReadValueSafe(out int n);
                n = Mathf.Clamp(n, 0, MusicLibrary.MaxSongs);
                for (int i = 0; i < n; i++)
                {
                    r.ReadValueSafe(out string id, true);
                    r.ReadValueSafe(out string title);
                    r.ReadValueSafe(out float dur);
                    r.ReadValueSafe(out bool keep);
                    list.Add(new Song { id = id, title = title, duration = dur, keep = keep });
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Music] bad song list from client {sender}: {e.Message}");
                return;
            }
            ServerSetList(sender, list);
        }

        void OnStateMessage(ulong sender, FastBufferReader r)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.IsServer || sender != NetworkManager.ServerClientId) return;
            try { ApplyState(ReadState(r)); }
            catch (Exception e) { Debug.LogWarning("[Music] bad state message: " + e.Message); }
        }

        void OnSkipMessage(ulong sender, FastBufferReader r)
        {
            try { r.ReadValueSafe(out int playId); ServerSkip(sender, playId); }
            catch (Exception) { /* ignore junk */ }
        }

        void OnFailMessage(ulong sender, FastBufferReader r)
        {
            try { r.ReadValueSafe(out int playId); ServerFail(sender, playId); }
            catch (Exception) { /* ignore junk */ }
        }

        // ------------------------------------------------------------------ server

        void ServerSetList(ulong client, List<Song> songs)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            if (!_queues.TryGetValue(client, out var q)) _queues[client] = q = new PlayerQueue();

            var seen = new HashSet<string>();
            q.songs.Clear();
            foreach (var s in songs)
            {
                if (s == null || !MusicTools.IsValidId(s.id) || !seen.Add(s.id)) continue;
                if (s.duration <= 0 || s.duration > MusicTools.MaxSongSeconds) continue;
                // the owner hasn't processed "it played" yet: don't let a play-once song sneak back in
                if (!s.keep && _server.Active && _server.owner == client && _server.id == s.id) continue;
                q.songs.Add(new Song { id = s.id, title = MusicTools.CleanTitle(s.title), duration = s.duration, keep = s.keep });
                if (q.songs.Count >= MusicLibrary.MaxSongs) break;
            }

            // first contact doubles as "hello": tell them what's playing
            if (client != nm.LocalClientId) SendState(client);
        }

        void ServerTick(NetworkManager nm)
        {
            // forget players who left (their current song is allowed to finish)
            _tmpIds.Clear();
            foreach (var id in _queues.Keys) if (!IsConnected(nm, id)) _tmpIds.Add(id);
            foreach (var id in _tmpIds)
            {
                _queues.Remove(id);
                _pickedThisRound.Remove(id);
                _votes.Remove(id);
            }

            if (!_server.Active)
            {
                bool any = false;
                foreach (var q in _queues.Values) if (q.songs.Count > 0) { any = true; break; }
                if (any) Advance(nm);
            }
            else if (nm.ServerTime.Time >= _server.start + _server.duration + 0.5) Advance(nm);
        }

        static bool IsConnected(NetworkManager nm, ulong id)
        {
            foreach (var c in nm.ConnectedClientsIds) if (c == id) return true;
            return false;
        }

        void Advance(NetworkManager nm)
        {
            _votes.Clear();
            var pick = PickNext();
            if (pick == null)
            {
                if (!_server.Active) return; // nothing was playing, nothing to play: no change
                _server = default;
            }
            else
            {
                var (owner, song) = pick.Value;
                _server = new NowPlaying
                {
                    playId = _nextPlayId++,
                    owner = owner,
                    ownerName = NameOf(owner),
                    id = song.id,
                    title = song.title,
                    duration = song.duration,
                    start = nm.ServerTime.Time + LeadSeconds,
                };
            }
            Broadcast(nm);
        }

        (ulong owner, Song song)? PickNext()
        {
            var nm = NetworkManager.Singleton;
            var eligible = new List<ulong>();
            foreach (var kv in _queues)
                if (kv.Value.songs.Count > 0 && IsConnected(nm, kv.Key)) eligible.Add(kv.Key);
            if (eligible.Count == 0) return null;

            // everyone with songs gets a turn before anyone goes again
            var pool = eligible.FindAll(c => !_pickedThisRound.Contains(c));
            if (pool.Count == 0)
            {
                _pickedThisRound.Clear();
                pool = eligible.FindAll(c => eligible.Count == 1 || c != _lastPicked);
            }
            ulong who = pool[Random.Range(0, pool.Count)];
            _pickedThisRound.Add(who);
            _lastPicked = who;

            // weighted pick inside their list: a song that just played starts near zero, back to full
            // odds once about half of this player's list has played since
            var q = _queues[who];
            float recover = Mathf.Max(1f, q.songs.Count / 2f);
            var weights = new float[q.songs.Count];
            float total = 0f;
            for (int i = 0; i < q.songs.Count; i++)
            {
                float w = 1f;
                if (q.lastPlayed.TryGetValue(q.songs[i].id, out int at))
                    w = Mathf.Clamp((q.played - at - 1) / recover, 0.02f, 1f);
                weights[i] = w;
                total += w;
            }
            float roll = Random.value * total;
            int chosen = q.songs.Count - 1;
            for (int i = 0; i < weights.Length; i++)
            {
                roll -= weights[i];
                if (roll <= 0f) { chosen = i; break; }
            }

            var song = q.songs[chosen];
            q.lastPlayed[song.id] = q.played;
            q.played++;
            if (!song.keep) q.songs.RemoveAt(chosen);
            return (who, song);
        }

        static string NameOf(ulong client)
        {
            foreach (var p in PlayerController.All)
                if (p != null && p.IsSpawned && p.OwnerClientId == client) return p.DisplayName;
            return "Player " + (client + 1);
        }

        void ServerSkip(ulong sender, int playId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || !_server.Active || playId != _server.playId) return;
            if (sender == nm.LocalClientId || sender == _server.owner) { Advance(nm); return; }
            _votes.Add(sender);
            if (_votes.Count >= VotesNeeded(nm)) Advance(nm);
            else Broadcast(nm);
        }

        void ServerFail(ulong sender, int playId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || !_server.Active || playId != _server.playId) return;
            // the owner's game couldn't play it either -> video is probably gone/blocked; move on
            if (sender == _server.owner) Advance(nm);
        }

        static int VotesNeeded(NetworkManager nm) => nm.ConnectedClientsIds.Count / 2 + 1;

        void Broadcast(NetworkManager nm)
        {
            _server.votes = _votes.Count;
            _server.needed = VotesNeeded(nm);
            foreach (var c in nm.ConnectedClientsIds)
                if (c != nm.LocalClientId) SendState(c);
            if (nm.IsClient) ApplyState(_server); // host plays too
        }

        void SendState(ulong client)
        {
            var nm = NetworkManager.Singleton;
            _server.votes = _votes.Count;
            _server.needed = VotesNeeded(nm);
            using var w = new FastBufferWriter(512, Allocator.Temp, 4096);
            var s = _server;
            w.WriteValueSafe(s.playId);
            w.WriteValueSafe(s.owner);
            w.WriteValueSafe(s.ownerName ?? "");
            w.WriteValueSafe(s.id ?? "", true);
            w.WriteValueSafe(s.title ?? "");
            w.WriteValueSafe(s.duration);
            w.WriteValueSafe(s.start);
            w.WriteValueSafe(s.votes);
            w.WriteValueSafe(s.needed);
            nm.CustomMessagingManager.SendNamedMessage(MsgState, client, w, NetworkDelivery.ReliableSequenced);
        }

        static NowPlaying ReadState(FastBufferReader r)
        {
            var s = new NowPlaying();
            r.ReadValueSafe(out s.playId);
            r.ReadValueSafe(out s.owner);
            r.ReadValueSafe(out s.ownerName);
            r.ReadValueSafe(out s.id, true);
            r.ReadValueSafe(out s.title);
            r.ReadValueSafe(out s.duration);
            r.ReadValueSafe(out s.start);
            r.ReadValueSafe(out s.votes);
            r.ReadValueSafe(out s.needed);
            return s;
        }

        // ------------------------------------------------------------------ client

        void ApplyState(NowPlaying s)
        {
            if (s.Active && (!MusicTools.IsValidId(s.id) || s.duration <= 0)) s = default;
            Current = s;
            var nm = NetworkManager.Singleton;

            if (s.playId != _lastStreamedPlayId)
            {
                _lastStreamedPlayId = s.playId;
                if (s.Active) _stream.Play(s.playId, s.id, s.start, s.duration, () => ServerNow);
                else _stream.Stop();
            }
            // one of my play-once songs started: take it off my list
            if (s.Active && nm != null && s.owner == nm.LocalClientId && s.playId != _lastMarkedPlayId)
            {
                _lastMarkedPlayId = s.playId;
                MusicLibrary.MarkPlayed(s.id);
            }
            StateChanged?.Invoke();
        }
    }
}
