using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Menu.MusicLibrary;
using YARG.Localization;
using YARG.Menu.Persistent;
using YARG.Online.Lobbies.Contracts.Enums;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Online.Lobbies.Contracts.Rest;
using YARG.Song;

namespace YARG.Online
{
    /// <summary>
    /// SignalR session for the lobby hub, scoped to the online flow. Maintains the
    /// lobby browser cache and tracks the lobby the local player is currently in.
    /// </summary>
    public sealed class LobbyHubSession
    {
        public enum ConnectionState
        {
            Disconnected,
            Connecting,
            Connected,
            Reconnecting,
        }

        // ---------- Static surface ----------

        private static int _instanceCounter;

        /// <summary>The active session, or null if the player isn't in the online flow.</summary>
        public static LobbyHubSession Current { get; private set; }

        /// <summary>Convenience for <c>Current != null</c>.</summary>
        public static bool IsActive => Current != null;

        /// <summary>
        /// Create or reuse the active session and start connecting. Idempotent.
        /// </summary>
        public static UniTask InitializeAsync(OnlineAccessTokenProvider provider, CancellationToken ct = default)
        {
            var existing = Current;
            if (existing != null) return existing.ConnectAsync(ct);

            var session = new LobbyHubSession(provider);
            Current = session;
            return session.ConnectAsync(ct);
        }

        /// <summary>Dispose the active session if any. Idempotent.</summary>
        public static async UniTask ShutdownAsync()
        {
            var session = Current;
            if (session == null) return;
            Current = null;
            await session.DisposeAsync();
        }

        // ---------- Instance state ----------

        private readonly int _instanceId;
        private readonly object _lock = new();
        private readonly Dictionary<string, LobbyDto> _byId = new();
        private long _lastSequence = -1;

        private readonly OnlineAccessTokenProvider _tokenProvider;

        private HubConnection _connection;
        private UniTask? _inflightConnect;
        private ConnectionState _state = ConnectionState.Disconnected;

        private LobbyRoomState _currentLobby;
        private LobbyGameOrchestrator _orchestrator;
        // Awaited by StartOrchestratorAsync so a new NetManager doesn't overlap the prior teardown.
        private UniTask? _pendingOrchestratorDispose;

        private readonly CancellationTokenSource _lifetimeCts = new();
        private int _disposing; // 0 = alive, 1 = disposing/disposed
        private int _inflightHandlers; // Track() bodies in flight; DisposeAsync drains to zero

        private LobbyHubSession(OnlineAccessTokenProvider tokenProvider)
        {
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            _instanceId = Interlocked.Increment(ref _instanceCounter);
            Application.quitting += OnApplicationQuitting;
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: created");
        }

        private static void OnApplicationQuitting()
        {
            ShutdownAsync().Forget();
        }

        /// <summary>The local player's server-assigned user id (from dev auth).</summary>
        public string LocalUserId => _tokenProvider.UserId;

        /// <summary>The local player's server-assigned display name (from dev auth).</summary>
        public string LocalDisplayName => _tokenProvider.DisplayName;

        public ConnectionState State
        {
            get { lock (_lock) return _state; }
        }

        /// <summary>Snapshot of the current browse cache (fresh copy).</summary>
        public IReadOnlyList<LobbyDto> Lobbies
        {
            get
            {
                lock (_lock)
                {
                    var copy = new LobbyDto[_byId.Count];
                    int i = 0;
                    foreach (var dto in _byId.Values) copy[i++] = dto;
                    return copy;
                }
            }
        }

        /// <summary>The lobby the local player is currently in, or null.</summary>
        public LobbyRoomState CurrentLobby => _currentLobby;

        /// <summary>Fired on the main thread after a snapshot or batch is applied.</summary>
        public event Action LobbiesChanged;

        /// <summary>Fired on the main thread when connection state changes.</summary>
        public event Action<ConnectionState> StateChanged;

        /// <summary>Fired on the main thread when CurrentLobby changes.</summary>
        public event Action CurrentLobbyChanged;

        /// <summary>Fired on the main thread when kicked from the current lobby.</summary>
        public event Action<string> KickedFromLobby;

        /// <summary>Connect to the lobby hub. Idempotent.</summary>
        public UniTask ConnectAsync(CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_state == ConnectionState.Connected) return UniTask.CompletedTask;
                if (_inflightConnect.HasValue) return _inflightConnect.Value;

                _inflightConnect = ConnectInternalAsync(ct);
                return _inflightConnect.Value;
            }
        }

        private async UniTask ConnectInternalAsync(CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
            try
            {
                SetState(ConnectionState.Connecting);

                // Dispose stale connection before building a new one.
                HubConnection stale;
                lock (_lock) { stale = _connection; _connection = null; }
                if (stale != null)
                {
                    try { await stale.DisposeAsync(); }
                    catch (Exception ex) { YargLogger.LogWarning($"LobbyHubSession[#{_instanceId}]: stale dispose -- {ex.Message}"); }
                }

                var url = OnlineAccessTokenProvider.BaseUrl.TrimEnd('/') + HubRoutes.Lobby;
                _connection = new HubConnectionBuilder()
                    .WithUrl(url, options =>
                    {
                        options.AccessTokenProvider = _tokenProvider.GetAccessTokenAsync;
                        // Skip negotiate round-trip -- WS-only avoids sticky-session requirements.
                        options.SkipNegotiation = true;
                        options.Transports = HttpTransportType.WebSockets;
                    })
                    .AddJsonProtocol(o =>
                    {
                        o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                        o.PayloadSerializerOptions.Converters.Add(
                            new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
                    })
                    .WithAutomaticReconnect()
                    .Build();

                _connection.On<LobbyDto[]>(nameof(ILobbyHubClient.OnLobbySnapshot), ApplySnapshotAsync);
                _connection.On<LobbyBatchUpdate>(nameof(ILobbyHubClient.OnLobbyBatch), ApplyBatchAsync);

                _connection.On<PlayerJoinedEvent>(nameof(ILobbyHubClient.OnPlayerJoined), OnPlayerJoinedAsync);
                _connection.On<PlayerLeftEvent>(nameof(ILobbyHubClient.OnPlayerLeft), OnPlayerLeftAsync);
                _connection.On<LobbyClosedEvent>(nameof(ILobbyHubClient.OnLobbyClosed), OnLobbyClosedAsync);
                _connection.On<HostChangedEvent>(nameof(ILobbyHubClient.OnHostChanged), OnHostChangedAsync);
                _connection.On<PlayerKickedEvent>(nameof(ILobbyHubClient.OnPlayerKicked), OnPlayerKickedAsync);
                _connection.On<LobbyStatusChangedEvent>(nameof(ILobbyHubClient.OnLobbyStatusChanged), OnLobbyStatusChangedAsync);
                _connection.On<LobbySongLibraryUpdatedEvent>(nameof(ILobbyHubClient.OnLobbySongLibraryUpdated), OnLobbySongLibraryUpdatedAsync);
                _connection.On<SongQueuedEvent>(nameof(ILobbyHubClient.OnSongQueued), OnSongQueuedAsync);
                _connection.On<SongRemovedFromQueueEvent>(nameof(ILobbyHubClient.OnSongRemovedFromQueue), OnSongRemovedFromQueueAsync);
                _connection.On<QueuedSongAvailabilityChangedEvent>(nameof(ILobbyHubClient.OnQueuedSongAvailabilityChanged), OnQueuedSongAvailabilityChangedAsync);
                _connection.On<ChatMessageEvent>(nameof(ILobbyHubClient.OnChatMessage), OnChatMessageAsync);
                _connection.On<GameStartedEvent>(nameof(ILobbyHubClient.OnGameStarted), OnGameStartedAsync);
                _connection.On<PlayerLobbyReadyChangedEvent>(
                    nameof(ILobbyHubClient.OnPlayerLobbyReadyChanged),
                    OnPlayerLobbyReadyChangedAsync);

                _connection.Reconnecting += _ =>
                {
                    SetState(ConnectionState.Reconnecting);
                    return Task.CompletedTask;
                };
                _connection.Reconnected += async _ =>
                {
                    // In-lobby state is not resent on reconnect -- clear CurrentLobby.
                    try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
                    catch (OperationCanceledException) { return; }
                    if (_currentLobby != null)
                    {
                        YargLogger.LogWarning(
                            $"LobbyHubSession[#{_instanceId}]: reconnected while in a lobby -- clearing CurrentLobby "
                            + "(server-side rejoin not implemented).");
                        _currentLobby = null;
                        CurrentLobbyChanged?.Invoke();
                    }
                    SetState(ConnectionState.Connected);
                };
                _connection.Closed += async _ =>
                {
                    try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
                    catch (OperationCanceledException) { return; }
                    if (_currentLobby != null)
                    {
                        _currentLobby = null;
                        CurrentLobbyChanged?.Invoke();
                    }
                    SetState(ConnectionState.Disconnected);
                };

                YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: connecting to {url}");
                await _connection.StartAsync(linked.Token);
                SetState(ConnectionState.Connected);
                YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: connected");
            }
            catch (Exception ex)
            {
                SetState(ConnectionState.Disconnected);
                YargLogger.LogError($"LobbyHubSession[#{_instanceId}]: connect failed -- {ex.Message}");
                throw;
            }
            finally
            {
                lock (_lock) _inflightConnect = null;
            }
        }

        public async UniTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposing, 1) != 0) return;

            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: disposing");

            Application.quitting -= OnApplicationQuitting;

            // Best-effort leave RPCs before cancel so server state cleans up promptly.
            // Short timeout because this may run from Application.quitting.
            try
            {
                using var leaveCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                leaveCts.CancelAfter(TimeSpan.FromMilliseconds(750));
                if (_orchestrator != null)
                {
                    await LeaveResultsAsync(leaveCts.Token);
                }
                await LeaveLobbyAsync(leaveCts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                YargLogger.LogWarning($"LobbyHubSession[#{_instanceId}]: leave-on-dispose threw -- {ex.Message}");
            }

            try { _lifetimeCts.Cancel(); } catch { }

            var orch = _orchestrator;
            _orchestrator = null;
            if (orch != null)
            {
                orch.Ended -= OnOrchestratorEnded;
                try { await orch.DisposeAsync(); }
                catch (Exception ex) { YargLogger.LogException(ex); }
            }

            UniTask? inflight;
            lock (_lock) inflight = _inflightConnect;
            if (inflight.HasValue)
            {
                try { await inflight.Value.SuppressCancellationThrow(); }
                catch (Exception ex) { YargLogger.LogException(ex); }
            }

            HubConnection conn;
            lock (_lock) { conn = _connection; _connection = null; }
            if (conn != null)
            {
                try { await conn.StopAsync(); }
                catch (Exception ex) { YargLogger.LogWarning($"LobbyHubSession[#{_instanceId}]: stop -- {ex.Message}"); }
                try { await conn.DisposeAsync(); }
                catch (Exception ex) { YargLogger.LogWarning($"LobbyHubSession[#{_instanceId}]: dispose -- {ex.Message}"); }
            }

            await UniTask.WaitUntil(() => Volatile.Read(ref _inflightHandlers) == 0);

            lock (_lock) { _byId.Clear(); _lastSequence = -1; }
            _currentLobby = null;
            _state = ConnectionState.Disconnected;
            _lifetimeCts.Dispose();

            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: disposed");
        }

        // ---------- Main-thread dispatch helpers ----------

        private async UniTaskVoid Track(Func<UniTask> body)
        {
            Interlocked.Increment(ref _inflightHandlers);
            try
            {
                await body();
            }
            catch (OperationCanceledException)
            {
                // Expected when _lifetimeCts is cancelled mid-dispatch.
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
            }
            finally
            {
                Interlocked.Decrement(ref _inflightHandlers);
            }
        }

        // ---------- Lobby RPCs ----------

        /// <summary>Create a lobby and populate <see cref="CurrentLobby"/>. The local song
        /// library is streamed to the server as chunked hashes.</summary>
        public async UniTask<CreateLobbyResult> CreateLobbyAsync(
            CreateLobbyArgs args, string[] libraryHashes, CancellationToken ct = default)
        {
            var conn = RequireConnection();
            YargLogger.LogInfo(
                $"LobbyHubSession[#{_instanceId}]: CreateLobby name='{args.Name}', mode={args.GameMode}, "
                + $"libraryHashes={libraryHashes?.Length ?? 0}");
            var result = await conn.InvokeAsync<CreateLobbyResult>(
                nameof(ILobbyHub.CreateLobby), args, StreamHashes(libraryHashes, ct), ct);
            await UniTask.SwitchToMainThread();
            if (Volatile.Read(ref _disposing) != 0) return result;
            _currentLobby = LobbyRoomState.FromCreate(result.Lobby);
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: CreateLobby ok -- id={result.Lobby.Id}");
            CurrentLobbyChanged?.Invoke();
            return result;
        }

        /// <summary>Enter a lobby and populate <see cref="CurrentLobby"/>. The local song
        /// library is streamed to the server as chunked hashes.</summary>
        public async UniTask<EnterLobbyResult> EnterLobbyAsync(
            EnterLobbyArgs args, string[] libraryHashes, CancellationToken ct = default)
        {
            var conn = RequireConnection();
            YargLogger.LogInfo(
                $"LobbyHubSession[#{_instanceId}]: EnterLobby id={args.LobbyId}, "
                + $"libraryHashes={libraryHashes?.Length ?? 0}");
            var result = await conn.InvokeAsync<EnterLobbyResult>(
                nameof(ILobbyHub.EnterLobby), args, StreamHashes(libraryHashes, ct), ct);
            await UniTask.SwitchToMainThread();
            if (Volatile.Read(ref _disposing) != 0) return result;
            _currentLobby = LobbyRoomState.FromEnter(result);
            YargLogger.LogInfo(
                $"LobbyHubSession[#{_instanceId}]: EnterLobby ok -- id={result.Lobby.Id}, "
                + $"members={_currentLobby.Members.Count}, "
                + $"lobbyLibrary={_currentLobby.LobbySongLibrary.Count}");
            CurrentLobbyChanged?.Invoke();
            return result;
        }

        /// <summary>Leave the current lobby and clear <see cref="CurrentLobby"/>.</summary>
        public async UniTask LeaveLobbyAsync(CancellationToken ct = default)
        {
            var conn = _connection;
            try
            {
                if (conn != null && _currentLobby != null)
                {
                    YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: LeaveLobby id={_currentLobby.LobbyId}");
                    await conn.InvokeAsync(nameof(ILobbyHub.LeaveLobby), ct);
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogWarning($"LobbyHubSession[#{_instanceId}]: LeaveLobby threw -- {ex.Message}");
            }
            finally
            {
                await UniTask.SwitchToMainThread();
                if (Volatile.Read(ref _disposing) == 0 && _currentLobby != null)
                {
                    _currentLobby = null;
                    CurrentLobbyChanged?.Invoke();
                }
            }
        }

        private async UniTaskVoid PushGameplayHashUpdateAsync()
        {
            await UniTask.SwitchToMainThread();
            if (Volatile.Read(ref _disposing) != 0 || _currentLobby == null)
            {
                return;
            }

            try
            {
                await UpdateLibraryAsync(LocalSongLibrary.SnapshotLocalHashes());
            }
            catch (Exception ex)
            {
                YargLogger.LogWarning(
                    $"LobbyHubSession[#{_instanceId}]: gameplay-hash backfill push failed -- {ex.Message}");
            }
        }

        /// <summary>Queue a song. State is mutated by the server's broadcast callback, not here.</summary>
        public async UniTask<QueuedSongDto> QueueSongAsync(
            HashWrapper hash, float songSpeed, CancellationToken ct = default)
        {
            var conn = RequireConnection();
            var args = new QueueSongArgs(hash.ToString()) { SongSpeed = songSpeed };
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: QueueSong hash={args.SongHash} speed={songSpeed}");
            var result = await conn.InvokeAsync<QueuedSongDto>(
                nameof(ILobbyHub.QueueSong), args, ct);
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: QueueSong ok -- sequence={result.Sequence}");
            return result;
        }

        /// <summary>Remove a song from the queue. State is mutated by the server's broadcast callback.</summary>
        public async UniTask RemoveQueuedSongAsync(long sequence, CancellationToken ct = default)
        {
            var conn = RequireConnection();
            var args = new RemoveQueuedSongArgs(sequence);
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: RemoveQueuedSong sequence={sequence}");
            await conn.InvokeAsync(nameof(ILobbyHub.RemoveQueuedSong), args, ct);
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: RemoveQueuedSong ok -- sequence={sequence}");
        }

        /// <summary>Send a chat message. State is mutated by the server's broadcast callback.</summary>
        public async UniTask SendChatMessageAsync(string text, CancellationToken ct = default)
        {
            var conn = RequireConnection();
            var args = new SendChatMessageArgs(text);
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: SendChatMessage length={text?.Length ?? 0}");
            await conn.InvokeAsync(nameof(ILobbyHub.SendChatMessage), args, ct);
        }

        /// <summary>Start the game. Host-only; state is mutated by the server's broadcast callbacks.</summary>
        public async UniTask StartGameAsync(CancellationToken ct = default)
        {
            var conn = RequireConnection();
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: StartGame");
            await conn.InvokeAsync(nameof(ILobbyHub.StartGame), ct);
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: StartGame ok -- awaiting OnGameStarted");
        }

        /// <summary>Transfer host to another player. Host-only.</summary>
        public async UniTask TransferHostAsync(string targetUserId, CancellationToken ct = default)
        {
            var conn = RequireConnection();
            var args = new TransferHostArgs(targetUserId);
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: TransferHost target={targetUserId}");
            await conn.InvokeAsync(nameof(ILobbyHub.TransferHost), args, ct);
        }

        /// <summary>Kick a player from the lobby. Host-only.</summary>
        public async UniTask KickPlayerAsync(string targetUserId, CancellationToken ct = default)
        {
            var conn = RequireConnection();
            var args = new KickPlayerArgs(targetUserId);
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: KickPlayer target={targetUserId}");
            await conn.InvokeAsync(nameof(ILobbyHub.KickPlayer), args, ct);
        }

        /// <summary>Signal that this player left the results screen. Safe to call when no lobby is active.</summary>
        public async UniTask LeaveResultsAsync(CancellationToken ct = default)
        {
            var conn = _connection;
            if (conn == null || _state != ConnectionState.Connected) return;
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: LeaveResults");
            await conn.InvokeAsync(nameof(ILobbyHub.LeaveResults), ct);
        }

        /// <summary>Push the local song library to the lobby for shared-library recomputation.
        /// Streamed to the server as chunked hashes.</summary>
        public async UniTask UpdateLibraryAsync(string[] libraryHashes, CancellationToken ct = default)
        {
            var conn = _connection;
            if (conn == null || _state != ConnectionState.Connected || _currentLobby == null)
            {
                return;
            }
            YargLogger.LogInfo(
                $"LobbyHubSession[#{_instanceId}]: UpdateLibrary -- hashes={libraryHashes?.Length ?? 0}");
            await conn.InvokeAsync(nameof(ILobbyHub.UpdateLibrary), StreamHashes(libraryHashes, ct), ct);
        }

        /// <summary>
        /// Chunk a flat hash snapshot into an upload stream. SignalR enumerates this off the
        /// main thread, so the array must already be materialized (see
        /// <see cref="LocalSongLibrary.SnapshotLocalHashes"/>) -- this only slices it.
        /// </summary>
        private static async IAsyncEnumerable<string[]> StreamHashes(
            string[] hashes, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (hashes == null) yield break;
            for (int i = 0; i < hashes.Length; i += SongLibraryStreaming.ChunkSize)
            {
                ct.ThrowIfCancellationRequested();
                int len = Math.Min(SongLibraryStreaming.ChunkSize, hashes.Length - i);
                var chunk = new string[len];
                Array.Copy(hashes, i, chunk, 0, len);
                yield return chunk;
            }
        }

        /// <summary>Tear down the current game session and signal back-in-lobby. Safe when no game is active.</summary>
        public void LeaveCurrentGame()
        {
            LeaveResultsAsync().Forget();

            var orch = _orchestrator;
            if (orch == null) return;
            _orchestrator = null;
            orch.Ended -= OnOrchestratorEnded;
            orch.DisposeAsync().Forget();
        }

        private HubConnection RequireConnection()
        {
            var conn = _connection;
            if (conn == null || _state != ConnectionState.Connected)
            {
                throw new InvalidOperationException(
                    "LobbyHubSession is not connected -- call ConnectAsync first.");
            }
            return conn;
        }

        // ---------- In-lobby callbacks ----------
        // Each handler hops to the main thread before touching _currentLobby.

        private async Task OnPlayerJoinedAsync(PlayerJoinedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            if (!_currentLobby.Members.Contains(e.UserId)) _currentLobby.Members.Add(e.UserId);
            _currentLobby.MemberNames[e.UserId] = e.DisplayName;
            _currentLobby.MemberInstruments[e.UserId] = (YARG.Core.Instrument) e.Instrument;
            _currentLobby.MemberIsBackInLobby[e.UserId] = true;
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: OnPlayerJoined {e.UserId} ({e.DisplayName}) instrument={(YARG.Core.Instrument) e.Instrument}");
            CurrentLobbyChanged?.Invoke();

            if (e.UserId != _tokenProvider.UserId)
            {
                LobbyChatterToast(Localize.KeyFormat("Menu.Online.Toast.PlayerJoined", e.DisplayName));
            }
        }

        private async Task OnPlayerLeftAsync(PlayerLeftEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            string displayName = _currentLobby.GetDisplayName(e.UserId);
            _currentLobby.Members.Remove(e.UserId);
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: OnPlayerLeft {e.UserId}");
            CurrentLobbyChanged?.Invoke();

            if (e.UserId != _tokenProvider.UserId)
            {
                LobbyChatterToast(Localize.KeyFormat("Menu.Online.Toast.PlayerLeft", displayName));
            }
        }

        private async Task OnLobbyClosedAsync(LobbyClosedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: OnLobbyClosed reason='{e.Reason}'");
            _currentLobby = null;
            CurrentLobbyChanged?.Invoke();
        }

        private async Task OnHostChangedAsync(HostChangedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            _currentLobby.HostUserId = e.NewHostUserId;
            _currentLobby.HostName = e.NewHostName;
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: OnHostChanged -> {e.NewHostUserId} ({e.NewHostName})");
            CurrentLobbyChanged?.Invoke();
        }

        private async Task OnPlayerKickedAsync(PlayerKickedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            bool kickedSelf = e.UserId == _tokenProvider.UserId;
            string displayName = _currentLobby.GetDisplayName(e.UserId);
            _currentLobby.Members.Remove(e.UserId);
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: OnPlayerKicked {e.UserId} reason='{e.Reason}'");
            if (kickedSelf)
            {
                _currentLobby = null;
                CurrentLobbyChanged?.Invoke();
                KickedFromLobby?.Invoke(e.Reason);
            }
            else
            {
                CurrentLobbyChanged?.Invoke();
                LobbyChatterToast(Localize.KeyFormat("Menu.Online.Toast.PlayerKicked", displayName));
            }
        }

        private async Task OnLobbyStatusChangedAsync(LobbyStatusChangedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            var previous = _currentLobby.Status;
            _currentLobby.Status = e.Status;
            // Mirror the server's silent bulk IsBackInLobby=false on GameStarted
            // (server doesn't emit per-member events for this). SongSelect does NOT
            // bulk-set true -- members flip individually via LeaveResults.
            if (e.Status == LobbyStatus.GameStarted)
            {
                _currentLobby.IsSongInProgress = true;
                foreach (var uid in _currentLobby.Members)
                {
                    _currentLobby.MemberIsBackInLobby[uid] = false;
                }
            }
            else if (e.Status == LobbyStatus.SongSelect)
            {
                _currentLobby.IsSongInProgress = false;
            }
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: OnLobbyStatusChanged -> {e.Status}");
            CurrentLobbyChanged?.Invoke();

            bool isHost = _currentLobby.IsLocalHost;
            if (!isHost)
            {
                if (e.Status == LobbyStatus.Starting && previous != LobbyStatus.Starting)
                {
                    LobbyChatterToast(Localize.Key("Menu.Online.Toast.HostStartingGame"));
                }
                else if (e.Status == LobbyStatus.SongSelect && previous == LobbyStatus.Starting)
                {
                    // Starting -> SongSelect means the allocator failed.
                    ToastManager.ToastWarning("Game start failed. No servers available.");
                }
            }
        }

        private async Task OnLobbySongLibraryUpdatedAsync(LobbySongLibraryUpdatedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;

            var library = _currentLobby.LobbySongLibrary;
            int removedCount = 0;
            int addedCount = 0;
            if (e.Removed != null)
            {
                foreach (var h in e.Removed)
                {
                    if (library.Remove(ToHashWrapper(h)))
                        removedCount++;
                }
            }
            if (e.Added != null)
            {
                foreach (var h in e.Added)
                {
                    var hw = ToHashWrapper(h);
                    bool recognized = SongContainer.SongsByHash.ContainsKey(hw)
                        || SongContainer.SongsByGameplayHash.ContainsKey(hw);
                    if (recognized && library.Add(hw))
                        addedCount++;
                }
            }

            CurrentLobbyChanged?.Invoke();

            // Re-derive the picker filter so live library changes are visible immediately.
            if (MusicLibraryMenu.AllowedSongHashes != null)
            {
                var rebuilt = YARG.Menu.Online.LobbyViewMenu.BuildPlayableSongSet(_currentLobby);
                if (rebuilt != null)
                {
                    MusicLibraryMenu.AllowedSongHashes = rebuilt;
                }
            }
            MusicLibraryMenu.NotifyAllowedSongsChanged();

            if (addedCount > 0 && removedCount > 0)
            {
                int total = addedCount + removedCount;
                LobbyChatterToast(Localize.KeyFormat(
                    total == 1
                        ? "Menu.Online.Toast.LibrarySongsBoth.Singular"
                        : "Menu.Online.Toast.LibrarySongsBoth.Plural",
                    addedCount, removedCount));
            }
            else if (addedCount > 0)
            {
                LobbyChatterToast(addedCount == 1
                    ? Localize.Key("Menu.Online.Toast.LibrarySongsAdded.Singular")
                    : Localize.KeyFormat("Menu.Online.Toast.LibrarySongsAdded.Plural", addedCount));
            }
            else if (removedCount > 0)
            {
                LobbyChatterToast(removedCount == 1
                    ? Localize.Key("Menu.Online.Toast.LibrarySongsRemoved.Singular")
                    : Localize.KeyFormat("Menu.Online.Toast.LibrarySongsRemoved.Plural", removedCount));
            }
        }

        private async Task OnSongQueuedAsync(SongQueuedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            _currentLobby.SongQueue.Add(e.Song);
            CurrentLobbyChanged?.Invoke();

            if (e.Song.RequesterId != _tokenProvider.UserId)
            {
                var hash = HashWrapper.FromString(e.Song.SongHash);
                string songLabel = SongContainer.SongsByHash.TryGetValue(hash, out var songs)
                    ? songs[0].Name
                    : e.Song.SongHash;
                string requesterName = _currentLobby.GetDisplayName(e.Song.RequesterId);
                LobbyChatterToast(Localize.KeyFormat("Menu.Online.Toast.SongQueued", requesterName, songLabel));
            }
        }

        private async Task OnSongRemovedFromQueueAsync(SongRemovedFromQueueEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            int idx = _currentLobby.SongQueue.FindIndex(q => q.Sequence == e.Sequence);
            string songLabel = null;
            if (idx >= 0)
            {
                var hash = HashWrapper.FromString(_currentLobby.SongQueue[idx].SongHash);
                songLabel = SongContainer.SongsByHash.TryGetValue(hash, out var songs)
                    ? songs[0].Name
                    : _currentLobby.SongQueue[idx].SongHash;
            }
            _currentLobby.SongQueue.RemoveAll(q => q.Sequence == e.Sequence);
            CurrentLobbyChanged?.Invoke();

            // Played removal = song finished; clear in-progress before early-out.
            if (e.Reason == SongRemovalReason.Played)
            {
                _currentLobby.IsSongInProgress = false;
            }

            if (songLabel == null) return;
            switch (e.Reason)
            {
                case SongRemovalReason.Played:
                    // Score screen already communicates this -- no toast.
                    break;
                case SongRemovalReason.RequesterLeft:
                    LobbyChatterToast(Localize.KeyFormat("Menu.Online.Toast.SongRemovedRequesterLeft", songLabel));
                    break;
                case SongRemovalReason.Removed:
                default:
                    string removerName = !string.IsNullOrEmpty(e.RemovedByUserId)
                        ? _currentLobby.GetDisplayName(e.RemovedByUserId)
                        : _currentLobby.HostName;
                    LobbyChatterToast(Localize.KeyFormat("Menu.Online.Toast.SongRemovedByPlayer", removerName, songLabel));
                    break;
            }
        }

        private async Task OnQueuedSongAvailabilityChangedAsync(QueuedSongAvailabilityChangedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            int idx = _currentLobby.SongQueue.FindIndex(q => q.Sequence == e.Sequence);
            if (idx < 0) return;
            var existing = _currentLobby.SongQueue[idx];
            var missing = new HashSet<string>(existing.MissingFor ?? Array.Empty<string>());
            if (e.RemovedMissing != null) foreach (var u in e.RemovedMissing) missing.Remove(u);
            if (e.AddedMissing   != null) foreach (var u in e.AddedMissing)   missing.Add(u);
            var newMissing = new string[missing.Count];
            missing.CopyTo(newMissing);
            _currentLobby.SongQueue[idx] = existing with { MissingFor = newMissing };
            CurrentLobbyChanged?.Invoke();
        }

        private async Task OnChatMessageAsync(ChatMessageEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            _currentLobby.ChatHistory.Add(e.Message);
            CurrentLobbyChanged?.Invoke();
        }

        private async Task OnGameStartedAsync(GameStartedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            _currentLobby.Status = LobbyStatus.GameStarted;
            _currentLobby.GameServerEndpoint = e.GameServerEndpoint;
            _currentLobby.GameToken = e.GameToken;
            _currentLobby.GameTokenExpiresAt = e.ExpiresAt;
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: OnGameStarted endpoint={e.GameServerEndpoint}");
            CurrentLobbyChanged?.Invoke();

            StartOrchestratorAsync().Forget();
        }

        private async Task OnPlayerLobbyReadyChangedAsync(PlayerLobbyReadyChangedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            _currentLobby.MemberIsBackInLobby[e.UserId] = e.IsBackInLobby;
            YargLogger.LogFormatInfo(
                "LobbyHubSession[#{0}]: OnPlayerLobbyReadyChanged userId={1} isBackInLobby={2}",
                _instanceId, e.UserId, e.IsBackInLobby);
            CurrentLobbyChanged?.Invoke();
        }

        // ---------- helpers ----------

        // Suppress lobby chatter toasts during gameplay (joins, leaves, queue changes).
        private static void LobbyChatterToast(string message)
        {
            if (GlobalVariables.Instance != null
                && GlobalVariables.Instance.CurrentScene == SceneIndex.Gameplay)
            {
                return;
            }
            ToastManager.ToastInformation(message);
        }

        private static HashWrapper ToHashWrapper(string s) => HashWrapper.FromString(s.AsSpan());

        private async UniTaskVoid StartOrchestratorAsync()
        {
            if (_orchestrator != null)
            {
                YargLogger.LogWarning(
                    $"LobbyHubSession[#{_instanceId}]: game flow already in progress; ignoring OnGameStarted");
                return;
            }

            // Wait for previous orchestrator teardown to avoid overlapping NetManagers.
            UniTask? pendingDispose;
            lock (_lock) pendingDispose = _pendingOrchestratorDispose;
            if (pendingDispose.HasValue)
            {
                try { await pendingDispose.Value.SuppressCancellationThrow(); }
                catch (Exception ex) { YargLogger.LogException(ex); }
            }

            // GC between songs while no keep-alive contract exists yet.
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            catch (Exception ex) { YargLogger.LogException(ex); }

            LobbyGameOrchestrator orch;
            try
            {
                orch = await LobbyGameOrchestrator.InitializeAsync(this);
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                return;
            }

            if (Volatile.Read(ref _disposing) != 0)
            {
                // We were torn down while constructing -- dispose what we just built.
                try { await orch.DisposeAsync(); }
                catch (Exception disposeEx) { YargLogger.LogException(disposeEx); }
                return;
            }

            _orchestrator = orch;
            _orchestrator.Ended += OnOrchestratorEnded;
        }

        private void OnOrchestratorEnded()
        {
            // Preserve() for multi-await by both .Forget() and StartOrchestratorAsync.
            var task = EndOrchestratorAsync().Preserve();
            lock (_lock) _pendingOrchestratorDispose = task;
            task.Forget();
        }

        private async UniTask EndOrchestratorAsync()
        {
            var orch = _orchestrator;
            if (orch == null) return;
            _orchestrator = null;
            orch.Ended -= OnOrchestratorEnded;
            try { await orch.DisposeAsync(); }
            catch (Exception ex) { YargLogger.LogException(ex); }
        }

        private bool IsForCurrentLobby(string lobbyId)
        {
            return _currentLobby != null && _currentLobby.LobbyId == lobbyId;
        }

        // ---------- Browse-scope handlers ----------

        private async Task ApplySnapshotAsync(LobbyDto[] lobbies)
        {
            lock (_lock)
            {
                _byId.Clear();
                _lastSequence = -1;
                if (lobbies != null)
                {
                    foreach (var dto in lobbies)
                    {
                        if (dto?.Id != null) _byId[dto.Id] = dto;
                    }
                }
            }
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: snapshot -- {lobbies?.Length ?? 0} lobbies");
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            LobbiesChanged?.Invoke();
        }

        private async Task ApplyBatchAsync(LobbyBatchUpdate batch)
        {
            if (batch == null) return;

            lock (_lock)
            {
                if (batch.Sequence <= _lastSequence) return;

                if (batch.Added != null)
                {
                    foreach (var dto in batch.Added)
                        if (dto?.Id != null) _byId[dto.Id] = dto;
                }
                if (batch.Updated != null)
                {
                    foreach (var dto in batch.Updated)
                        if (dto?.Id != null) _byId[dto.Id] = dto;
                }
                if (batch.Removed != null)
                {
                    foreach (var id in batch.Removed)
                        if (id != null) _byId.Remove(id);
                }

                _lastSequence = batch.Sequence;
            }
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            LobbiesChanged?.Invoke();
        }

        private void SetState(ConnectionState newState)
        {
            bool changed;
            lock (_lock)
            {
                changed = _state != newState;
                _state = newState;
            }
            if (changed)
            {
                YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: state -> {newState}");
                Track(async () =>
                {
                    await UniTask.SwitchToMainThread(_lifetimeCts.Token);
                    StateChanged?.Invoke(newState);
                }).Forget();
            }
        }
    }
}
