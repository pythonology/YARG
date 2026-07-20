using System;
using System.Net;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Gameplay;
using YARG.Localization;
using YARG.Menu;
using YARG.Menu.DifficultySelect;
using YARG.Menu.Persistent;
using YARG.Menu.ScoreScreen;
using YARG.Online.Game.Contracts.Enums;
using YARG.Online.Game.Contracts.Packets;
using YARG.Player;
using YARG.Song;

namespace YARG.Online
{
    /// <summary>
    /// Drives the post-StartGame client flow for a single game: opens the UDP
    /// connection, pushes the (lobby-mode) DifficultySelect menu, sends the local
    /// player's loadout once they confirm, registers the online session and loads
    /// the gameplay scene when GameStart arrives, and tears the UDP session down
    /// on GameEnded/Disconnected.
    ///
    /// Lifetime is scoped to one game. The owning <see cref="LobbyHubSession"/>
    /// constructs the orchestrator via <see cref="InitializeAsync"/> when the
    /// lobby hub broadcasts game start, and disposes it (via <see cref="DisposeAsync"/>)
    /// in response to <see cref="Ended"/> or when the lobby session itself shuts
    /// down. The orchestrator owns the <see cref="GameClientSession"/> outright.
    /// </summary>
    public sealed class LobbyGameOrchestrator
    {
        private static int _instanceCounter;

        private readonly int _instanceId;
        private readonly LobbyHubSession _lobbySession;
        private readonly GameClientSession _gameSession;
        private readonly OnlineSessionDirector _director;
        private readonly ServerClockSync _clockSync;
        private readonly UniTask<bool> _connectTask;
        private readonly byte[] _chartHash;
        private int _disposed;

        /// <summary>
        /// Fired (Unity main thread) when the per-game flow has finished -- server
        /// GameEnded, UDP disconnected, or an unrecoverable error during the
        /// loadout step. The owner disposes this instance in response.
        /// </summary>
        public event Action Ended;

        /// <summary>
        /// Validate the lobby's pending game state, create the <see cref="GameClientSession"/>,
        /// kick off UDP connect, push DifficultySelect, and return the orchestrator
        /// instance. Throws if the lobby state isn't ready (missing endpoint/token,
        /// empty queue, queued song not in local library, bad endpoint).
        /// </summary>
        public static UniTask<LobbyGameOrchestrator> InitializeAsync(LobbyHubSession lobbySession)
        {
            if (lobbySession == null) throw new ArgumentNullException(nameof(lobbySession));

            var lobby = lobbySession.CurrentLobby
                ?? throw new InvalidOperationException("LobbyGameOrchestrator: CurrentLobby is null");
            if (string.IsNullOrEmpty(lobby.GameToken) || string.IsNullOrEmpty(lobby.GameServerEndpoint))
                throw new InvalidOperationException("LobbyGameOrchestrator: missing endpoint/token");
            if (lobby.SongQueue.Count == 0)
                throw new InvalidOperationException("LobbyGameOrchestrator: empty queue");

            var queued = lobby.SongQueue[0];
            var hash = HashWrapper.FromString(queued.SongHash);
            bool found = SongContainer.SongsByHash.TryGetValue(hash, out var songs) && songs.Count > 0;
            if (!found)
            {
                found = SongContainer.SongsByGameplayHash.TryGetValue(hash, out songs) && songs.Count > 0;
            }
            if (!found)
            {
                DialogManager.Instance.ShowMessage("Could not start game",
                    "The queued song wasn't found in your local library.");
                throw new InvalidOperationException(
                    $"LobbyGameOrchestrator: queued song {queued.SongHash} not in local library");
            }

            var endpoint = ParseEndpoint(lobby.GameServerEndpoint)
                ?? throw new InvalidOperationException(
                    $"LobbyGameOrchestrator: bad endpoint '{lobby.GameServerEndpoint}'");

            // We own the GameClientSession, the OnlineSessionDirector, and the
            // ServerClockSync for this game's lifetime. Construct the session, kick off
            // connect, then build the director and clock-sync (both subscribe to the
            // session's instance events in their ctors). If construction throws between
            // steps, dispose what we already built.
            var gameSession = new GameClientSession();
            OnlineSessionDirector director;
            ServerClockSync clockSync;
            UniTask<bool> connectTask;
            try
            {
                connectTask = gameSession.ConnectAsync(endpoint, lobby.GameToken);
                director = new OnlineSessionDirector(gameSession);
                clockSync = new ServerClockSync(gameSession);
            }
            catch
            {
                // ConnectAsync is async; a synchronous throw here means the session is
                // unusable. Fire-and-forget dispose (caller doesn't await it).
                gameSession.DisposeAsync().Forget();
                throw;
            }

            // Chart hash bytes are sent with the loadout so the server can gate
            // session start on every peer having the same chart. The hash here
            // is the same one the lobby uses to identify the song.
            var chartHashBytes = hash.HashBytes;

            var orch = new LobbyGameOrchestrator(lobbySession, gameSession, director, clockSync, connectTask, chartHashBytes);
            orch.PushDifficultySelect(songs[0], queued.SongSpeed);
            return UniTask.FromResult(orch);
        }

        private LobbyGameOrchestrator(
            LobbyHubSession lobbySession,
            GameClientSession gameSession,
            OnlineSessionDirector director,
            ServerClockSync clockSync,
            UniTask<bool> connectTask,
            byte[] chartHash)
        {
            _lobbySession = lobbySession;
            _gameSession  = gameSession;
            _director     = director;
            _clockSync    = clockSync;
            _connectTask  = connectTask.Preserve();
            _chartHash    = chartHash;
            _instanceId   = Interlocked.Increment(ref _instanceCounter);
            YargLogger.LogInfo($"LobbyGameOrchestrator[#{_instanceId}]: created");

            _gameSession.GameStarted  += OnGameStartedOnUdp;
            _gameSession.GameEnded    += OnGameEnded;
            _gameSession.Disconnected += OnUdpDisconnected;
            _gameSession.LoadoutReadyCountReceived += OnLoadoutReadyCountReceived;

            // Watch lobby member list during pre-game; OnGameStartedOnUdp unsubscribes.
            _lobbySession.CurrentLobbyChanged += OnLobbyChangedDuringStart;
        }

        private void OnLobbyChangedDuringStart()
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            // Lobby session cleared CurrentLobby (kick, server tore us out): the
            // session's own teardown path shows the user-facing dialog.
            var lobby = _lobbySession.CurrentLobby;
            if (lobby == null)
            {
                Ended?.Invoke();
                return;
            }

            DifficultySelectMenu.OnLobbyMembersChanged?.Invoke();

            if (lobby.Members.Count < 2)
            {
                YargLogger.LogInfo(
                    $"LobbyGameOrchestrator[#{_instanceId}]: only 1 member left during pre-game; cancelling");

                // Server's lobby is in GameStarted with our IsBackInLobby flipped
                // false by ConfirmStartGameAsync. LeaveResults flips it true; when
                // it's the only remaining member, the server transitions the lobby
                // back to SongSelect.
                _lobbySession.LeaveResultsAsync().Forget();

                ClearDifficultySelectHooks();

                MenuManager.Instance?.SetActiveMenuExclusive(MenuManager.Menu.LobbyView);
                DialogManager.Instance.ShowMessage(
                    Localize.Key("Menu.Online.GameCancelled.Title"),
                    Localize.Key("Menu.Online.GameCancelled.NotEnoughPlayers"));
                Ended?.Invoke();
            }
        }

        private static void ClearDifficultySelectHooks()
        {
            DifficultySelectMenu.LobbyMode = false;
            DifficultySelectMenu.OnLobbyLoadoutsConfirmed = null;
            DifficultySelectMenu.OnLobbyUnreadied = null;
        }

        private void PushDifficultySelect(SongEntry song, float songSpeed)
        {
            GlobalVariables.State.CurrentSong = song;
            GlobalVariables.State.IsPractice = false;
            GlobalVariables.State.SongSpeed = songSpeed;

            DifficultySelectMenu.LobbyMode = true;
            DifficultySelectMenu.OnLobbyLoadoutsConfirmed = OnLoadoutsConfirmed;
            DifficultySelectMenu.OnLobbyUnreadied = OnLoadoutsUnreadied;
            MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.DifficultySelect);
        }

        private void OnLoadoutsConfirmed()
        {
            SendLocalLoadoutAsync().Forget();
        }

        private void OnLoadoutsUnreadied()
        {
            // Best-effort retraction. SendUnready is a no-op when disconnected and the
            // server's ClearLoadout handler ignores the request after the game has started,
            // so the worst case is "the user clicked Unready a hair too late" -- the next
            // GameStarted callback will still fire and transition them into gameplay.
            try
            {
                _gameSession.SendUnready();
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, "LobbyGameOrchestrator: SendUnready failed");
            }
        }

        private async UniTaskVoid SendLocalLoadoutAsync()
        {
            try
            {
                bool connected = await _connectTask;
                if (Volatile.Read(ref _disposed) != 0) return;
                if (!connected)
                {
                    DialogManager.Instance.ShowMessage("Could not start game",
                        "Failed to connect to the game server.");
                    Ended?.Invoke();
                    return;
                }

                if (PlayerContainer.Players.Count == 0)
                {
                    YargLogger.LogError($"LobbyGameOrchestrator[#{_instanceId}]: no local players");
                    Ended?.Invoke();
                    return;
                }

                // v1: a single local player. Multi-profile lobby support comes later.
                var profile = PlayerContainer.Players[0].Profile;
                _gameSession.SendLoadout(
                    (InstrumentId)profile.CurrentInstrument,
                    (DifficultyId)profile.CurrentDifficulty,
                    profile.EnginePreset,
                    profile.NoteSpeed,
                    (ulong)profile.CurrentModifiers,
                    _chartHash);
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                Ended?.Invoke();
            }
        }

        private void OnGameStartedOnUdp(PeerLoadout[] loadouts)
        {
            foreach (var l in loadouts)
            {
                YargLogger.LogInfo(
                    $"LobbyGameOrchestrator[#{_instanceId}]: peer {l.DisplayName} ({l.UserId}) peerId={l.PeerId} -- "
                    + $"{l.Instrument}/{l.Difficulty} preset={l.EnginePreset}");
            }

            // Lobby-side bail only applies pre-game; in-game peer drops route through RemotePeerLeft.
            _lobbySession.CurrentLobbyChanged -= OnLobbyChangedDuringStart;

            // Build the per-peer YargPlayer list (local first, then remotes), arm the
            // start-cue await, and prep GameManager for the online code paths it needs.
            _director.RegisterSession(loadouts);
            GameManager.IsOnline = true;

            // Clear lobby-mode hooks so a future solo DifficultySelect doesn't inherit them.
            ClearDifficultySelectHooks();

            // Deactivate MusicPlayer FIRST, before HideAllMenus. LobbyView's
            // OnDisable calls MusicPlayer.SetLockedSong(null) which can kick
            // off an async NextSong() if the player is still active -- and
            // the activeSelf check inside that async load can read true
            // before the orchestrator's SetActive(false) lands, racing the
            // new mixer into Play() right as gameplay starts.
            if (HelpBar.Instance != null && HelpBar.Instance.MusicPlayer != null)
            {
                HelpBar.Instance.MusicPlayer.gameObject.SetActive(false);
            }

            // Hard invariant: from this point on, no MenuScene menu is loaded.
            try
            {
                MenuManager.Instance?.HideAllMenus();
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
            }

            // Hand off to the gameplay scene. GameManager.Awake reads
            // OnlineSessionDirector.Current.Players and GameManager.IsOnline;
            // GameManager.Start sends PeerReady once the chart is parsed and waits
            // for the server's cue.
            GlobalVariables.Instance.LoadScene(SceneIndex.Gameplay);
        }

        private void OnGameEnded()       => Ended?.Invoke();
        private void OnUdpDisconnected() => Ended?.Invoke();

        private void OnLoadoutReadyCountReceived(int readyCount, int totalExpected)
        {
            DifficultySelectMenu.LobbyReadyCount = readyCount;
            DifficultySelectMenu.LobbyReadyTotal = totalExpected;
            DifficultySelectMenu.OnLobbyMembersChanged?.Invoke();
        }

        public async UniTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            YargLogger.LogInfo($"LobbyGameOrchestrator[#{_instanceId}]: disposing");

            _gameSession.GameStarted  -= OnGameStartedOnUdp;
            _gameSession.GameEnded    -= OnGameEnded;
            _gameSession.Disconnected -= OnUdpDisconnected;
            _gameSession.LoadoutReadyCountReceived -= OnLoadoutReadyCountReceived;
            _lobbySession.CurrentLobbyChanged -= OnLobbyChangedDuringStart;

            // Stale tally would leak into a future lobby's DifficultySelect.
            DifficultySelectMenu.LobbyReadyCount = -1;
            DifficultySelectMenu.LobbyReadyTotal = -1;

            ClearDifficultySelectHooks();

            // Failure-recovery: if we're still in MenuScene with DifficultySelect
            // visible, route the user back to LobbyView. If we're in another
            // scene (Gameplay/Score) MenuManager.Instance is null and the next
            // MenuScene load picks up an override from ScoreScreenMenu (or the
            // GameManager bail-out paths).
            try
            {
                if (MenuManager.Instance != null
                    && MenuManager.Instance.CurrentMenu == MenuManager.Menu.DifficultySelect)
                {
                    MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.LobbyView);
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
            }

            // Dispose the director and clock-sync first so they unsubscribe from the
            // session's events while the session is still alive, then dispose the
            // session itself.
            try { _director.Dispose(); }
            catch (Exception ex) { YargLogger.LogException(ex); }

            try { _clockSync.Dispose(); }
            catch (Exception ex) { YargLogger.LogException(ex); }

            try { await _gameSession.DisposeAsync(); }
            catch (Exception ex) { YargLogger.LogException(ex); }

            YargLogger.LogInfo($"LobbyGameOrchestrator[#{_instanceId}]: disposed");
        }

        private static IPEndPoint ParseEndpoint(string hostPort)
        {
            // hostPort is "host:port" (LobbyHub.GameServerOptions.Endpoint format).
            var idx = hostPort.LastIndexOf(':');
            if (idx <= 0)
            {
                YargLogger.LogError($"LobbyGameOrchestrator: bad endpoint '{hostPort}'");
                return null;
            }
            string host = hostPort.Substring(0, idx);
            if (!int.TryParse(hostPort.AsSpan(idx + 1), out int port))
            {
                YargLogger.LogError($"LobbyGameOrchestrator: bad port in '{hostPort}'");
                return null;
            }
            // For dev: hostnames like "localhost" and "127.0.0.1" both work via
            // Dns.GetHostAddresses; LiteNetLib needs an IPEndPoint though.
            try
            {
                var addrs = System.Net.Dns.GetHostAddresses(host);
                if (addrs.Length == 0)
                {
                    YargLogger.LogError($"LobbyGameOrchestrator: no IP for '{host}'");
                    return null;
                }
                return new IPEndPoint(addrs[0], port);
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                return null;
            }
        }
    }
}
