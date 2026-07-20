using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Online;
using YARG.Online.Lobbies.Contracts.Enums;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Player;
using YARG.Song;

namespace YARG.Menu.Online
{
    /// <summary>
    /// In-lobby view. Source of truth is <see cref="LobbyHubSession.CurrentLobby"/>.
    /// </summary>
    public class LobbyViewMenu : MonoBehaviour
    {
        // Client-side cap on the queue length.
        private const int MaxQueueSize = 6;


        // Header elements (wired via Inspector).
        [SerializeField]
        private TextMeshProUGUI _headerMainText;
        [SerializeField]
        private TextMeshProUGUI _headerSubText;
        // "Public Lobby" / "Private Lobby" label. May be unwired on older prefabs.
        [SerializeField]
        private TextMeshProUGUI _lobbyTypeText;
        [SerializeField]
        private Button _backButton;

        // Section headers -- updated every Refresh.
        [SerializeField]
        private TextMeshProUGUI _playersHeaderText;
        [SerializeField]
        private TextMeshProUGUI _songsHeaderText;

        [SerializeField]
        private Transform _playersContent;
        [SerializeField]
        private LobbyPlayer _playerPrefab;
        // Shared popup for host actions (Make Host / Kick). May be unwired on older prefabs.
        [SerializeField]
        private LobbyPlayerActionsPopup _playerActionsPopup;

        [SerializeField]
        private Transform _songsContent;
        [SerializeField]
        private QueuedSong _songPrefab;

        [SerializeField]
        private Transform _chatContent;
        [SerializeField]
        private ChatMessageCard _chatMessagePrefab;
        [SerializeField]
        private TMP_InputField _chatInputField;
        [SerializeField]
        private ScrollRect _chatScrollRect;

        // Diff-based card tracking to avoid re-creating unchanged cards.
        private readonly Dictionary<string, LobbyPlayer> _playerCards = new();
        private readonly Dictionary<long, QueuedSong>    _songCards   = new();

        // Chat is append-only with a high-water mark on Sequence.
        private readonly List<ChatMessageCard> _chatCards = new();
        private long _lastChatSequenceRendered = -1;

        // Locks MusicPlayer to the top-of-queue song. Released in OnDisable.
        private string _previewSongHash;
        private float _previewSongSpeed = 1f;

        // Tracks whether the host or non-host nav scheme is currently pushed.
        private bool _schemePushedAsHost;

        // Cached session reference for safe unsubscribe even if Current changes.
        private LobbyHubSession _boundSession;

        private void OnEnable()
        {
            // Start with non-host scheme; Refresh() promotes to host scheme if needed.
            PushNavSchemeForRole(isHost: false);
            _schemePushedAsHost = false;

            ConfigureChatScroll();

            // Rebuild from scratch since child cards release textures on disable.
            ClearAllCards();

            if (_chatInputField != null)
            {
                // Matches server validator limit.
                _chatInputField.characterLimit = 256;
                _chatInputField.onSubmit.RemoveListener(OnChatSubmit);
                _chatInputField.onSubmit.AddListener(OnChatSubmit);
            }

            if (_backButton != null)
            {
                _backButton.onClick.RemoveListener(OnLeaveClicked);
                _backButton.onClick.AddListener(OnLeaveClicked);
            }

            _boundSession = LobbyHubSession.Current;
            if (_boundSession == null)
            {
                YargLogger.LogError("LobbyView: opened without an active session");
                MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.Online);
                return;
            }

            _boundSession.CurrentLobbyChanged += Refresh;
            _boundSession.KickedFromLobby     += OnKickedFromLobby;
            Refresh();
        }

        private void OnDisable()
        {
            if (_boundSession != null)
            {
                _boundSession.CurrentLobbyChanged -= Refresh;
                _boundSession.KickedFromLobby     -= OnKickedFromLobby;
                _boundSession = null;
            }
            if (_chatInputField != null) _chatInputField.onSubmit.RemoveListener(OnChatSubmit);
            if (_backButton != null) _backButton.onClick.RemoveListener(OnLeaveClicked);
            // Release MusicPlayer lock so it returns to random rotation.
            ReleasePreviewLock();
            if (Navigator.Instance != null) Navigator.Instance.PopScheme();
        }

        /// <summary>
        /// Diffs the current lobby state into the scroll containers.
        /// </summary>
        private void Refresh()
        {
            var lobby = _boundSession?.CurrentLobby;
            if (lobby == null)
            {
                YargLogger.LogInfo("LobbyView: refresh -- no current lobby");
                ClearAllCards();
                ReleasePreviewLock();
                return;
            }

            YargLogger.LogInfo(
                $"LobbyView: refresh -- id={lobby.LobbyId}, name='{lobby.LobbyName}', "
                + $"host={lobby.HostName}, status={lobby.Status}, "
                + $"members={lobby.Members.Count}/{lobby.MaxPlayers}, "
                + $"queue={lobby.SongQueue.Count}, chat={lobby.ChatHistory.Count}, "
                + $"lobbyLibrary={lobby.LobbySongLibrary.Count}, isHost={lobby.IsLocalHost}");

            RefreshNavSchemeForRole(lobby.IsLocalHost);
            RefreshHeader(lobby);
            RefreshPlayers(lobby);
            RefreshSongs(lobby);
            RefreshChat(lobby);
            RefreshTopSongPreview(lobby);
        }

        /// <summary>
        /// Locks MusicPlayer to the top-of-queue song at the queued speed, or
        /// releases if queue is empty.
        /// </summary>
        private void RefreshTopSongPreview(LobbyRoomState lobby)
        {
            string topHash = lobby.SongQueue.Count > 0 ? lobby.SongQueue[0].SongHash : null;
            float topSpeed = lobby.SongQueue.Count > 0 ? lobby.SongQueue[0].SongSpeed : 1f;
            if (topHash == _previewSongHash && Mathf.Approximately(topSpeed, _previewSongSpeed)) return;
            _previewSongHash = topHash;
            _previewSongSpeed = topSpeed;

            SongEntry songToLock = null;
            if (!string.IsNullOrEmpty(topHash))
            {
                var topHashWrapper = HashWrapper.FromString(topHash);
                if (SongContainer.SongsByHash.TryGetValue(topHashWrapper, out var entries) && entries.Count > 0)
                {
                    songToLock = entries[0];
                }
                else if (SongContainer.SongsByGameplayHash.TryGetValue(topHashWrapper, out var looseEntries) && looseEntries.Count > 0)
                {
                    songToLock = looseEntries[0];
                }
            }

            MusicPlayer.SetLockedSong(songToLock, topSpeed);
        }

        private void ReleasePreviewLock()
        {
            _previewSongHash = null;
            _previewSongSpeed = 1f;
            MusicPlayer.SetLockedSong(null);
        }

        /// <summary>
        /// Pushes the nav scheme for the given role. Host gets StartGame; both get AddSong.
        /// </summary>
        private const float CopyCodeHoldSeconds = 0.5f;

        private void PushNavSchemeForRole(bool isHost)
        {
            var entries = new List<NavigationScheme.Entry>
            {
                new NavigationScheme.Entry(MenuAction.Yellow, "Menu.Online.AddSong", OnQueueSongClicked),
                // Tap copies code to clipboard; hold shows it in a dialog.
                new NavigationScheme.Entry(
                    MenuAction.Blue,
                    "Menu.Online.CopyCode",
                    handler:       OnCopyCodeClicked,
                    onHoldHandler: OnShowCodeHold,
                    holdSeconds:   CopyCodeHoldSeconds),
            };
            if (isHost)
            {
                entries.Add(new NavigationScheme.Entry(MenuAction.Start, "Menu.Online.StartGame", OnStartGameClicked));
            }
            Navigator.Instance.PushScheme(new NavigationScheme(entries, true));
        }

        /// <summary>
        /// Swaps the nav scheme on host transitions. No-op when the role is unchanged.
        /// </summary>
        private void RefreshNavSchemeForRole(bool isHost)
        {
            if (isHost == _schemePushedAsHost) return;
            if (Navigator.Instance) Navigator.Instance.PopScheme();
            PushNavSchemeForRole(isHost);
            _schemePushedAsHost = isHost;
        }

        private void RefreshHeader(LobbyRoomState lobby)
        {
            if (_headerMainText)
            {
                _headerMainText.text = lobby.LobbyName;
            }
            if (_headerSubText)
            {
                _headerSubText.text = Localize.Key("Menu.Online.LobbyHeaderSubText");
            }
            if (_lobbyTypeText)
            {
                _lobbyTypeText.text = Localize.Key(lobby.IsPublic
                    ? "Menu.Online.LobbyType.Public"
                    : "Menu.Online.LobbyType.Private");
            }
        }

        private void RefreshPlayers(LobbyRoomState lobby)
        {
            bool   isLocalHost = lobby.IsLocalHost;
            string localUserId = LobbyHubSession.Current?.LocalUserId;

            if (_playersHeaderText)
            {
                _playersHeaderText.text = Localize.KeyFormat(
                    "Menu.Online.PlayersHeader", lobby.Members.Count, lobby.MaxPlayers);
            }

            // Local instrument from PlayerContainer; remote from LobbyRoomState.MemberInstruments.
            Instrument? localInstrument = PlayerContainer.Players.Count > 0
                ? PlayerContainer.Players[0].Profile.CurrentInstrument
                : null;

            // Offset past any static (non-card) children in the prefab.
            int staticChildCount = _playersContent.childCount - _playerCards.Count;
            int memberCount      = lobby.Members.Count;

            // Host pinned to sibling 0; others in join order.
            int nonHostRank = 0;

            var seen = new HashSet<string>();
            for (int i = 0; i < memberCount; i++)
            {
                string userId = lobby.Members[i];
                seen.Add(userId);

                if (!_playerCards.TryGetValue(userId, out var card))
                {
                    card = Instantiate(_playerPrefab, _playersContent);
                    _playerCards[userId] = card;
                }

                bool isSelf = userId == localUserId;
                Instrument? memberInstrument;
                if (isSelf)
                {
                    memberInstrument = localInstrument;
                }
                else if (lobby.MemberInstruments.TryGetValue(userId, out var ri))
                {
                    memberInstrument = ri;
                }
                else
                {
                    memberInstrument = null;
                }
                string instrumentSprite = memberInstrument?.ToResourceName();

                // Stage derivation lives on LobbyRoomState.
                var stage = lobby.ResolveMemberStage(userId);

                card.Initialize(
                    userId,
                    lobby.GetDisplayName(userId),
                    instrumentSprite,
                    isLocalHost,
                    isSelf:       isSelf,
                    stage:        stage,
                    onKick:       () => OnKickPlayerClicked(userId),
                    onMakeHost:   () => OnMakeHostClicked(userId),
                    actionsPopup: _playerActionsPopup);
                int displayIndex = userId == lobby.HostUserId ? 0 : 1 + nonHostRank++;
                card.transform.SetSiblingIndex(staticChildCount + displayIndex);
            }

            RemoveStale(_playerCards, seen);
        }

        private void RefreshSongs(LobbyRoomState lobby)
        {
            bool   isLocalHost  = lobby.IsLocalHost;
            string localUserId  = LobbyHubSession.Current?.LocalUserId;
            int    count        = lobby.SongQueue.Count;

            if (_songsHeaderText)
            {
                // Use Singular for exactly 1 (the placeholder is implicit "1"), Plural otherwise.
                _songsHeaderText.text = count == 1
                    ? Localize.Key("Menu.Online.SongsQueued.Singular")
                    : Localize.KeyFormat("Menu.Online.SongsQueued.Plural", count);
            }

            // Offset past any static (non-card) children in the prefab.
            int staticChildCount = _songsContent.childCount - _songCards.Count;

            var seen = new HashSet<long>();
            for (int i = 0; i < lobby.SongQueue.Count; i++)
            {
                var dto = lobby.SongQueue[i];
                seen.Add(dto.Sequence);

                // Remove visible for host or the song's requester. Server re-validates.
                bool canRemove = isLocalHost
                    || (!string.IsNullOrEmpty(localUserId) && dto.RequesterId == localUserId);

                if (_songCards.TryGetValue(dto.Sequence, out var card))
                {
                    card.SetRemoveButtonVisible(canRemove);
                }
                else
                {
                    card = Instantiate(_songPrefab, _songsContent);
                    _songCards[dto.Sequence] = card;
                    long sequence = dto.Sequence;
                    card.Initialize(
                        HashWrapper.FromString(dto.SongHash),
                        canRemove,
                        dto.SongSpeed,
                        () => OnRemoveQueuedSongClicked(sequence));
                }
                // Queue order preserved (oldest first), offset past any static children.
                card.transform.SetSiblingIndex(staticChildCount + i);
            }

            RemoveStale(_songCards, seen);
        }

        private void ConfigureChatScroll()
        {
        }

        // ---------- Chat scroll (standard ScrollRect) ----------

        private void RefreshChat(LobbyRoomState lobby)
        {
            // verticalNormalizedPosition: 0 = bottom, 1 = top.
            bool wasAtBottom = _chatScrollRect == null
                || _lastChatSequenceRendered < 0
                || _chatScrollRect.verticalNormalizedPosition <= 0.01f;

            bool anyAppended = false;
            foreach (var msg in lobby.ChatHistory)
            {
                if (msg.Sequence <= _lastChatSequenceRendered) continue;

                var card = Instantiate(_chatMessagePrefab, _chatContent);
                card.Initialize(msg);
                _chatCards.Add(card);
                _lastChatSequenceRendered = msg.Sequence;
                anyAppended = true;
            }

            if (anyAppended && wasAtBottom)
            {
                ScrollChatToBottomDeferred().Forget();
            }
        }

        private async UniTaskVoid ScrollChatToBottomDeferred()
        {
            // Wait one frame so ContentSizeFitter / TMP finalize row heights.
            await UniTask.NextFrame();
            if (!this || _chatScrollRect == null) return;

            Canvas.ForceUpdateCanvases();
            _chatScrollRect.verticalNormalizedPosition = 0f;
        }

        private static void RemoveStale<TKey, TCard>(Dictionary<TKey, TCard> cards, HashSet<TKey> seen)
            where TCard : Component
        {
            List<TKey> toRemove = null;
            foreach (var key in cards.Keys)
            {
                if (seen.Contains(key))
                {
                    continue;
                }

                toRemove ??= new List<TKey>();
                toRemove.Add(key);
            }
            if (toRemove == null)
            {
                return;
            }

            foreach (var key in toRemove)
            {
                Destroy(cards[key].gameObject);
                cards.Remove(key);
            }
        }

        private void ClearAllCards()
        {
            foreach (var card in _playerCards.Values)
            {
                Destroy(card.gameObject);
            }

            _playerCards.Clear();

            foreach (var card in _songCards.Values)
            {
                Destroy(card.gameObject);
            }

            _songCards.Clear();

            foreach (var card in _chatCards)
            {
                Destroy(card.gameObject);
            }

            _chatCards.Clear();
            _lastChatSequenceRendered = -1;
        }

        private void OnKickedFromLobby(string reason)
        {
            YargLogger.LogInfo($"LobbyView: kicked from lobby -- '{reason}'");
            // Map stable server codes to localized strings; fall back to raw reason.
            var body = reason switch
            {
                "kicked_by_host" => Localize.Key("Menu.Online.KickDialog.Reason.KickedByHost"),
                _                => Localize.KeyFormat("Menu.Online.KickDialog.Reason.Unknown", reason ?? string.Empty),
            };
            DialogManager.Instance.ShowMessage(
                Localize.Key("Menu.Online.KickDialog.Title"),
                body);
            MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.Online);
        }

        public void OnLeaveClicked() => LeaveAsync().Forget();

        /// <summary>
        /// Copies the lobby code to the clipboard and shows a confirmation toast.
        /// </summary>
        private void OnCopyCodeClicked()
        {
            var lobbyId = _boundSession?.CurrentLobby?.LobbyId;
            if (string.IsNullOrEmpty(lobbyId))
            {
                ToastManager.ToastError(Localize.Key("Menu.Online.Toast.CopyCode.NoLobby"));
                return;
            }

            GUIUtility.systemCopyBuffer = lobbyId;
            // Don't show the code in the toast -- keeps it off-screen for streamers.
            ToastManager.ToastSuccess(Localize.Key("Menu.Online.Toast.CopyCode.Success"));
        }

        /// <summary>
        /// Hold-to-show: displays the lobby code in a dialog without copying it.
        /// </summary>
        private void OnShowCodeHold()
        {
            var lobbyId = _boundSession?.CurrentLobby?.LobbyId;
            if (string.IsNullOrEmpty(lobbyId))
            {
                ToastManager.ToastError(Localize.Key("Menu.Online.Toast.CopyCode.NoLobby"));
                return;
            }

            DialogManager.Instance.ShowMessage(
                Localize.Key("Menu.Online.ShowCode.Title"),
                Localize.KeyFormat("Menu.Online.ShowCode.Body", lobbyId));
        }

        private async UniTaskVoid LeaveAsync()
        {
            try
            {
                var session = _boundSession ?? LobbyHubSession.Current;
                if (session != null)
                {
                    await session.LeaveLobbyAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
            }
            finally
            {
                MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.Online);
            }
        }

        public void OnReadyToggleClicked()
        {
            // TODO: ready-state isn't part of the lobby contract yet.
            YargLogger.LogWarning("LobbyView: OnReadyToggleClicked -- not yet wired");
        }

        public void OnSendChatClicked() => SendChatAsync().Forget();

        // Route Enter-key submit through the same path as the Send button.
        private void OnChatSubmit(string _) => OnSendChatClicked();

        private async UniTaskVoid SendChatAsync()
        {
            if (_chatInputField == null) return;
            string text = _chatInputField.text;
            if (string.IsNullOrWhiteSpace(text)) return;

            _chatInputField.text = string.Empty;
            _chatInputField.ActivateInputField(); // keep focus for the next message

            try
            {
                var session = _boundSession ?? LobbyHubSession.Current;
                if (session == null)
                {
                    DialogManager.Instance.ShowMessage("Could not send chat",
                        "The lobby session is no longer active.");
                    _chatInputField.text = text;
                    return;
                }
                await session.SendChatMessageAsync(text, CancellationToken.None);
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                DialogManager.Instance.ShowMessage("Could not send chat", ex.Message);
                // Restore the text so the user doesn't lose what they typed.
                _chatInputField.text = text;
            }
        }

        public void OnQueueSongClicked()
        {
            // Re-seed the lobby allow-list every time the picker opens.
            var lobby = (_boundSession ?? LobbyHubSession.Current)?.CurrentLobby;

            // Client-side cap -- friendly gate before opening the picker.
            int currentCount = lobby?.SongQueue.Count ?? 0;
            if (currentCount >= MaxQueueSize)
            {
                DialogManager.Instance.ShowMessage(
                    Localize.Key("Menu.Online.QueueFull.Title"),
                    Localize.KeyFormat("Menu.Online.QueueFull.Body", MaxQueueSize));
                return;
            }

            MusicLibraryMenu.AllowedSongHashes = BuildPlayableSongSet(lobby);
            MusicLibraryMenu.NotifyAllowedSongsChanged();

            MusicLibraryMenu.SongPickedCallback = OnSongPicked;
            MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.MusicLibrary);
        }

        /// <summary>
        /// Builds the set of songs playable by all lobby members (shared library intersected
        /// with per-member instrument availability). Snapshot semantics -- valid for one picker open.
        /// </summary>
        internal static HashSet<HashWrapper> BuildPlayableSongSet(LobbyRoomState lobby)
        {
            if (lobby == null)
            {
                return null;
            }

            // Skip members whose instrument hasn't been received yet.
            var requiredInstruments = new List<Instrument>(lobby.Members.Count);
            foreach (var uid in lobby.Members)
            {
                if (lobby.MemberInstruments.TryGetValue(uid, out var inst))
                {
                    requiredInstruments.Add(inst);
                }
            }

            // Pre-resolve GameModes. Fully qualified to avoid conflict with the contract enum.
            var memberGameModes = new YARG.Core.GameMode[requiredInstruments.Count];
            for (int i = 0; i < requiredInstruments.Count; i++)
            {
                memberGameModes[i] = requiredInstruments[i].ToNativeGameMode();
            }

            // Resolve the shared-library hash set down to one entry per underlying song,
            // not one per matching hash -- a song shared via BOTH its strict and gameplay
            // hash would otherwise appear twice in the picker. Strict always wins when both
            // are present: it's checked first, and once a song is claimed by its strict
            // hash, a later gameplay-hash entry for the same song is skipped rather than
            // overwriting it.
            var resolvedByEntry = new Dictionary<SongEntry, HashWrapper>();
            foreach (var hash in lobby.LobbySongLibrary)
            {
                if (SongContainer.SongsByHash.TryGetValue(hash, out var strictEntries) && strictEntries.Count > 0)
                {
                    resolvedByEntry[strictEntries[0]] = hash;
                }
                else if (SongContainer.SongsByGameplayHash.TryGetValue(hash, out var looseEntries)
                    && looseEntries.Count > 0)
                {
                    if (!resolvedByEntry.ContainsKey(looseEntries[0]))
                    {
                        resolvedByEntry.Add(looseEntries[0], hash);
                    }
                }
            }

            if (requiredInstruments.Count == 0)
            {
                // No instruments known yet -- fall back to whichever hash resolved each song.
                return new HashSet<HashWrapper>(resolvedByEntry.Values);
            }

            var playable = new HashSet<HashWrapper>();
            foreach (var (entry, hash) in resolvedByEntry)
            {
                bool playableForAll = true;
                for (int i = 0; i < memberGameModes.Length; i++)
                {
                    // Accept the song if the member's GameMode has at least one playable instrument.
                    var candidates = memberGameModes[i].PossibleInstrumentsForSong(entry);
                    bool anyAvailable = false;
                    foreach (var candidate in candidates)
                    {
                        if (entry.HasInstrument(candidate))
                        {
                            anyAvailable = true;
                            break;
                        }
                    }
                    if (!anyAvailable)
                    {
                        playableForAll = false;
                        break;
                    }
                }
                if (playableForAll)
                {
                    playable.Add(hash);
                }
            }
            YargLogger.LogInfo($"Library: {resolvedByEntry.Count}, Playable with instrument: {playable.Count}");
            return playable;
        }

        private void OnSongPicked(HashWrapper hash)
        {
            YargLogger.LogInfo($"LobbyView: song picked for queue -- hash={hash}");
            QueueSongAsync(hash).Forget();
        }

        private async UniTaskVoid QueueSongAsync(HashWrapper hash)
        {
            try
            {
                var session = _boundSession ?? LobbyHubSession.Current;
                if (session == null)
                {
                    DialogManager.Instance.ShowMessage("Could not queue song",
                        "The lobby session is no longer active.");
                    return;
                }
                // Send the requester's last-set song speed (persisted across
                // PersistentState resets) so it travels with the queue entry and
                // applies for everyone at game start.
                float songSpeed = SongSpeedMenu.SongSpeedMultiplier;

                var hashToSend = hash;
                if (GameplayHashCache.TryGet(hash.ToString(), out var gameplayHash))
                {
                    var gameplayHw = HashWrapper.FromString(gameplayHash);

                    // Only use the gameplay hash if it already survived the lobby's
                    // library intersection -- i.e. every member (including ones on
                    // the unmodified client, which never pushes a gameplay hash)
                    // has reported this exact value. Otherwise fall back to the
                    // strict hash so their client can still resolve the song.
                    if (session.CurrentLobby?.LobbySongLibrary.Contains(gameplayHw) == true)
                    {
                        hashToSend = gameplayHw;
                    }
                }

                await session.QueueSongAsync(
                    hashToSend,
                    songSpeed,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                DialogManager.Instance.ShowMessage("Could not queue song", ex.Message);
            }
        }

        public void OnRemoveQueuedSongClicked(long sequence) => RemoveQueuedSongAsync(sequence).Forget();

        private async UniTaskVoid RemoveQueuedSongAsync(long sequence)
        {
            YargLogger.LogInfo($"LobbyView: remove queued song -- sequence={sequence}");
            try
            {
                var session = _boundSession ?? LobbyHubSession.Current;
                if (session == null)
                {
                    DialogManager.Instance.ShowMessage("Could not remove song",
                        "The lobby session is no longer active.");
                    return;
                }
                await session.RemoveQueuedSongAsync(sequence, CancellationToken.None);
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                DialogManager.Instance.ShowMessage("Could not remove song", ex.Message);
            }
        }

        public void OnKickPlayerClicked(string userId) => KickPlayerAsync(userId).Forget();
        public void OnMakeHostClicked(string userId)   => TransferHostAsync(userId).Forget();

        private async UniTaskVoid KickPlayerAsync(string userId)
        {
            var session = _boundSession ?? LobbyHubSession.Current;
            if (session == null) return;
            try
            {
                await session.KickPlayerAsync(userId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                DialogManager.Instance.ShowMessage(
                    Localize.Key("Menu.Online.HostActionError.KickTitle"),
                    TranslateHostActionError(ex.Message, isKick: true));
            }
        }

        private async UniTaskVoid TransferHostAsync(string userId)
        {
            var session = _boundSession ?? LobbyHubSession.Current;
            if (session == null) return;
            try
            {
                await session.TransferHostAsync(userId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                DialogManager.Instance.ShowMessage(
                    Localize.Key("Menu.Online.HostActionError.TransferHostTitle"),
                    TranslateHostActionError(ex.Message, isKick: false));
            }
        }

        // Translate hub error tags to localized strings. Unknown tags fall through raw.
        private static string TranslateHostActionError(string msg, bool isKick)
        {
            if (string.IsNullOrEmpty(msg)) return msg;
            string suffix = isKick ? "Kick" : "MakeHost";
            if (msg.Contains("not_host"))
                return Localize.Key($"Menu.Online.HostActionError.NotHost.{suffix}");
            if (msg.Contains("target_is_host"))
                return Localize.Key($"Menu.Online.HostActionError.TargetIsHost.{suffix}");
            if (msg.Contains("target_not_member"))
                return Localize.Key("Menu.Online.HostActionError.TargetNotMember");
            if (msg.Contains("not_in_lobby"))
                return Localize.Key("Menu.Online.HostActionError.NotInLobby");
            if (msg.Contains("validation_failed"))
                return Localize.Key($"Menu.Online.HostActionError.ValidationFailed.{suffix}");
            return msg;
        }

        public void OnStartGameClicked() => StartGameAsync().Forget();

        private async UniTaskVoid StartGameAsync()
        {
            try
            {
                var session = _boundSession ?? LobbyHubSession.Current;
                if (session == null)
                {
                    DialogManager.Instance.ShowMessage("Could not start game",
                        "The lobby session is no longer active.");
                    return;
                }

                // Client-side preflight. Server still re-validates.
                var lobby = session.CurrentLobby;
                if (lobby != null && lobby.Status == LobbyStatus.Starting)
                {
                    // Already transitioning -- ignore duplicate press.
                    return;
                }
                if (lobby != null && lobby.Status == LobbyStatus.GameStarted)
                {
                    DialogManager.Instance.ShowMessage("Could not start game",
                        "The game has already started.");
                    return;
                }
                if (lobby != null && !lobby.AllMembersBackInLobby)
                {
                    // Bucket not-ready members into InGame vs OnResults for the dialog.
                    var inGame    = new List<string>();
                    var onResults = new List<string>();
                    foreach (var userId in lobby.Members)
                    {
                        switch (lobby.ResolveMemberStage(userId))
                        {
                            case LobbyMemberStage.InGame:    inGame.Add(lobby.GetDisplayName(userId));    break;
                            case LobbyMemberStage.OnResults: onResults.Add(lobby.GetDisplayName(userId)); break;
                        }
                    }
                    string body;
                    if (inGame.Count > 0 && onResults.Count > 0)
                    {
                        body = Localize.KeyFormat("Menu.Online.WaitingForPlayers.Both",
                            string.Join(", ", inGame),
                            string.Join(", ", onResults));
                    }
                    else if (inGame.Count > 0)
                    {
                        body = Localize.KeyFormat("Menu.Online.WaitingForPlayers.InGame",
                            string.Join(", ", inGame));
                    }
                    else if (onResults.Count > 0)
                    {
                        body = Localize.KeyFormat("Menu.Online.WaitingForPlayers.OnResults",
                            string.Join(", ", onResults));
                    }
                    else
                    {
                        // Race condition fallback.
                        body = Localize.Key("Menu.Online.WaitingForPlayers.Generic");
                    }
                    DialogManager.Instance.ShowMessage(
                        Localize.Key("Menu.Online.WaitingForPlayers.Title"), body);
                    return;
                }

                // Allocation can take seconds -- show a loading overlay.
                using (var loading = new LoadingContext())
                {
                    MenuManager.Instance.DisableCurrentMenu();
                    loading.SetLoadingText(Localize.Key("Menu.Online.PreparingGame"));
                    await session.StartGameAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                // Re-show the lobby view after a failed start.
                MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.LobbyView);
                // Translate server error tags to localized strings.
                string body = ex.Message;
                if (body != null)
                {
                    if      (body.Contains("players_still_in_results")) body = Localize.Key("Menu.Online.StartGameError.PlayersStillInResults");
                    else if (body.Contains("already_starting"))         body = Localize.Key("Menu.Online.StartGameError.AlreadyStarting");
                    else if (body.Contains("already_started"))          body = Localize.Key("Menu.Online.StartGameError.AlreadyStarted");
                    else if (body.Contains("allocation_failed"))        body = Localize.Key("Menu.Online.StartGameError.AllocationFailed");
                    else if (body.Contains("start_aborted"))            body = Localize.Key("Menu.Online.StartGameError.StartAborted");
                    else if (body.Contains("not_enough_players"))       body = Localize.Key("Menu.Online.StartGameError.NotEnoughPlayers");
                    else if (body.Contains("queue_empty"))              body = Localize.Key("Menu.Online.StartGameError.QueueEmpty");
                }
                DialogManager.Instance.ShowMessage(
                    Localize.Key("Menu.Online.StartGameError.Title"), body);
            }
        }
    }
}
