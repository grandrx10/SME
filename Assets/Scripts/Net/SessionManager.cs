using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// One "DEV test" lobby over Unity Relay.
    /// Play: find a live DEV test lobby and join it; if none (or it's dead), host a new one.
    /// If every live one is full -> "DEV test is full".
    /// A leaving host deletes the lobby so nobody joins a dead relay.
    /// </summary>
    public static class SessionManager
    {
        // Different from Gunbound's lobby name: this project was copied from it and may share its Unity Gaming
        // Services project, so the two games must never find each other's lobbies.
        public const string LobbyName = "SME DEV test";
        public const int MaxPlayers = 20;

        public static ISession Session { get; private set; }
        public static bool Busy { get; private set; }
        public static event Action<string> Status;
        public static event Action Left;

        static bool _servicesReady;
        static bool _hooked;

        static async Task EnsureServices()
        {
            if (_servicesReady) return;
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                var options = new InitializationOptions();
                // unique profile per launch so several editor/build instances sign in as different players
                options.SetProfile("p" + Guid.NewGuid().ToString("N").Substring(0, 12));
                await UnityServices.InitializeAsync(options);
            }
            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            _servicesReady = true;
        }

        public static async void Play()
        {
            if (Busy || Session != null) return;
            Busy = true;
            try
            {
                Status?.Invoke("Connecting...");
                await EnsureServices();
                ResetNetworkManager();

                Status?.Invoke("Looking for the DEV test...");
                var lobbies = await FindDevTestLobbies();
                bool sawFull = false;

                foreach (var info in lobbies)
                {
                    if (info.AvailableSlots <= 0) { sawFull = true; continue; }
                    try
                    {
                        Status?.Invoke("Joining DEV test...");
                        Session = await MultiplayerService.Instance.JoinSessionByIdAsync(info.Id, new JoinSessionOptions());
                        break;
                    }
                    catch (Exception e)
                    {
                        Session = null;
                        if (IsFull(e)) { sawFull = true; continue; }
                        // dead host / relay: skip it and try the next (or host our own)
                        ResetNetworkManager();
                    }
                }

                if (Session == null)
                {
                    if (sawFull && lobbies.Exists(l => l.AvailableSlots > 0) == false)
                    {
                        Status?.Invoke("DEV test is full");
                        return;
                    }
                    Status?.Invoke("Hosting DEV test...");
                    var options = new SessionOptions { MaxPlayers = MaxPlayers, IsPrivate = false, Name = LobbyName }.WithRelayNetwork();
                    Session = await MultiplayerService.Instance.CreateSessionAsync(options);
                }

                Session.RemovedFromSession += OnRemoved;
                Session.Deleted += OnRemoved;
                Status?.Invoke(Session.IsHost ? "Hosting DEV test" : "Joined DEV test");
                HookNetwork();
            }
            catch (Exception e)
            {
                Session = null;
                ResetNetworkManager();
                string msg = e.ToString().ToLowerInvariant();
                if (IsFull(e)) Status?.Invoke("DEV test is full");
                else if (msg.Contains("project") && msg.Contains("link")) Status?.Invoke("Unity Gaming Services not linked (Project Settings > Services)");
                else Status?.Invoke("Couldn't connect: " + Short(e));
            }
            finally
            {
                Busy = false;
            }
        }

        /// <summary>
        /// Dev testing (Admin menu): leave whatever server you're on and host a new private one just for you (max 1
        /// player, never listed, so Play never finds it).
        /// </summary>
        public static async void HostSolo()
        {
            if (Busy) return;
            Busy = true;
            try
            {
                if (Session != null)
                {
                    Status?.Invoke("Leaving...");
                    await LeaveInternal("");
                }
                Status?.Invoke("Connecting...");
                await EnsureServices();
                ResetNetworkManager();
                Status?.Invoke("Hosting dev server...");
                var options = new SessionOptions { MaxPlayers = 1, IsPrivate = true, Name = LobbyName + " (solo)" }.WithRelayNetwork();
                Session = await MultiplayerService.Instance.CreateSessionAsync(options);
                Session.RemovedFromSession += OnRemoved;
                Session.Deleted += OnRemoved;
                Status?.Invoke("Hosting dev server");
                HookNetwork();
            }
            catch (Exception e)
            {
                Session = null;
                ResetNetworkManager();
                string msg = e.ToString().ToLowerInvariant();
                if (msg.Contains("project") && msg.Contains("link")) Status?.Invoke("Unity Gaming Services not linked (Project Settings > Services)");
                else Status?.Invoke("Couldn't host: " + Short(e));
            }
            finally
            {
                Busy = false;
            }
        }

        /// <summary>Live DEV test lobbies (including full ones), newest heartbeat first.</summary>
        static async Task<List<ISessionInfo>> FindDevTestLobbies()
        {
            var result = new List<ISessionInfo>();
            var query = new QuerySessionsOptions
            {
                Count = 25,
                FilterOptions = new List<FilterOption>
                {
                    new(FilterField.Name, LobbyName, FilterOperation.Equal),
                    new(FilterField.AvailableSlots, "0", FilterOperation.GreaterOrEqual), // include full lobbies
                },
            };
            var res = await MultiplayerService.Instance.QuerySessionsAsync(query);
            // No "stale" check against this PC's clock: a clock that's off (common in VMs, e.g. Windows on a Mac) hid
            // every live lobby, so that player hosted their own instead. LastUpdated isn't bumped by heartbeats either.
            // The Lobby service already drops lobbies whose host stopped heartbeating; a dead one just fails to join.
            foreach (var s in res.Sessions)
                if (s.Name == LobbyName) result.Add(s);
            // prefer the lobby with the most players (keeps everyone together), then the freshest
            result.Sort((a, b) =>
            {
                int pa = a.MaxPlayers - a.AvailableSlots, pb = b.MaxPlayers - b.AvailableSlots;
                return pa != pb ? pb.CompareTo(pa) : b.LastUpdated.CompareTo(a.LastUpdated);
            });
            return result;
        }

        /// <summary>The server list: every live DEV test lobby (null if the query failed; Status says why).</summary>
        public static async Task<List<ISessionInfo>> ListServers()
        {
            try
            {
                await EnsureServices();
                return await FindDevTestLobbies();
            }
            catch (Exception e)
            {
                Status?.Invoke("Couldn't list servers: " + Short(e));
                return null;
            }
        }

        /// <summary>Join one server from the list (never hosts).</summary>
        public static async void Join(string id)
        {
            if (Busy || Session != null) return;
            Busy = true;
            try
            {
                Status?.Invoke("Joining DEV test...");
                await EnsureServices();
                ResetNetworkManager();
                Session = await MultiplayerService.Instance.JoinSessionByIdAsync(id, new JoinSessionOptions());
                Session.RemovedFromSession += OnRemoved;
                Session.Deleted += OnRemoved;
                Status?.Invoke("Joined DEV test");
                HookNetwork();
            }
            catch (Exception e)
            {
                Session = null;
                ResetNetworkManager();
                Status?.Invoke(IsFull(e) ? "DEV test is full" : "Couldn't join: " + Short(e));
            }
            finally
            {
                Busy = false;
            }
        }

        static bool IsFull(Exception e)
        {
            string m = e.ToString().ToLowerInvariant();
            return m.Contains("lobby is full") || m.Contains("lobbyfull") || m.Contains("16004");
        }

        static void ResetNetworkManager()
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && (nm.IsListening || nm.ShutdownInProgress)) nm.Shutdown(true);
        }

        static void HookNetwork()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;
            nm.OnClientStopped -= OnClientStopped;
            nm.OnClientStopped += OnClientStopped;
            if (!_hooked)
            {
                _hooked = true;
                Application.quitting += OnQuit;
            }
        }

        static void OnClientStopped(bool wasHost)
        {
            if (Session != null) _ = LeaveInternal(wasHost ? "" : "The host left the DEV test");
        }

        static void OnRemoved()
        {
            if (Session != null) _ = LeaveInternal("The host left the DEV test");
        }

        static void OnQuit()
        {
            // best effort: a quitting host removes the lobby so nobody joins a dead relay
            var s = Session;
            if (s != null && s.IsHost)
            {
                try { _ = s.AsHost().DeleteAsync(); } catch { /* quitting anyway */ }
            }
        }

        public static async void Leave() => await LeaveInternal("");

        static async Task LeaveInternal(string reason)
        {
            var s = Session;
            Session = null;
            if (s != null)
            {
                s.RemovedFromSession -= OnRemoved;
                s.Deleted -= OnRemoved;
                try
                {
                    if (s.IsHost) await s.AsHost().DeleteAsync(); // ends it for everyone
                    else await s.LeaveAsync();
                }
                catch (Exception)
                {
                    // already gone / network shut down — fine
                }
            }
            ResetNetworkManager();
            Status?.Invoke(reason);
            Left?.Invoke();
        }

        static string Short(Exception e)
        {
            var m = e.InnerException?.Message ?? e.Message;
            return m.Length > 90 ? m.Substring(0, 90) + "..." : m;
        }
    }
}
