using System;
using System.Collections.Generic;
using YARG.Core.Song;
using YARG.Online.Lobbies.Contracts.Enums;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Online.Lobbies.Contracts.Rest;
using YARG.Player;
using YARG.Song;
using Instrument = YARG.Core.Instrument;

namespace YARG.Online
{
    /// <summary>Mutable client-side model of the lobby the local player is in.</summary>
    public sealed class LobbyRoomState
    {
        public string LobbyId;
        public string LobbyName;
        public string HostUserId;
        public string HostName;
        public GameMode GameMode;
        public Region Region;
        public LobbyStatus Status;
        public int MaxPlayers;
        public bool IsPublic;

        public List<string> Members = new();
        public List<ChatMessage> ChatHistory = new();
        // Intersection of local library with what every member owns.
        public HashSet<HashWrapper> LobbySongLibrary = new();
        public List<QueuedSongDto> SongQueue = new();

        // Not removed on leave so toasts can still resolve names.
        public Dictionary<string, string> MemberNames = new();
        public Dictionary<string, Instrument> MemberInstruments = new();
        // True = back in lobby; false = in-game or on results screen.
        public Dictionary<string, bool> MemberIsBackInLobby = new();

        // Derived from event ordering -- true while song is playing, false on results/song-select.
        public bool IsSongInProgress;

        /// <summary>3-state stage derived from IsBackInLobby and IsSongInProgress.</summary>
        public LobbyMemberStage ResolveMemberStage(string userId)
        {
            bool back = !MemberIsBackInLobby.TryGetValue(userId, out var ready) || ready;
            if (back) return LobbyMemberStage.InLobby;
            return IsSongInProgress ? LobbyMemberStage.InGame : LobbyMemberStage.OnResults;
        }

        /// <summary>True if every member has reported back to the lobby.</summary>
        public bool AllMembersBackInLobby
        {
            get
            {
                foreach (var userId in Members)
                {
                    if (MemberIsBackInLobby.TryGetValue(userId, out var ready) && !ready) return false;
                }
                return true;
            }
        }

        private static IEnumerable<HashWrapper> AllLocalHashes()
        {
            foreach (var h in SongContainer.SongsByHash.Keys) yield return h;
            foreach (var h in SongContainer.SongsByGameplayHash.Keys) yield return h;
        }

        public string GameServerEndpoint;
        public string GameToken;
        public DateTimeOffset GameTokenExpiresAt;

        public bool IsLocalHost => HostUserId != null && HostUserId == LobbyHubSession.Current?.LocalUserId;

        public string GetDisplayName(string userId) =>
            MemberNames.TryGetValue(userId, out var name) ? name : userId;

        public static LobbyRoomState FromCreate(LobbyDto lobby)
        {
            var state = FromLobbyDto(lobby);
            var session = LobbyHubSession.Current;
            if (session != null && !string.IsNullOrEmpty(session.LocalUserId))
            {
                state.Members.Add(session.LocalUserId);
                state.MemberNames[session.LocalUserId] = session.LocalDisplayName;
                state.MemberIsBackInLobby[session.LocalUserId] = true;
                if (PlayerContainer.Players.Count > 0)
                {
                    state.MemberInstruments[session.LocalUserId] =
                        PlayerContainer.Players[0].Profile.CurrentInstrument;
                }
            }

            var localHashes = AllLocalHashes();
            state.LobbySongLibrary = new HashSet<HashWrapper>();
            foreach (var hash in localHashes)
                state.LobbySongLibrary.Add(hash);

            return state;
        }

        public static LobbyRoomState FromEnter(EnterLobbyResult result)
        {
            var state = FromLobbyDto(result.Lobby);
            if (result.CurrentMembers != null)
            {
                foreach (var member in result.CurrentMembers)
                {
                    state.Members.Add(member.UserId);
                    state.MemberNames[member.UserId] = member.DisplayName;
                    state.MemberInstruments[member.UserId] = (Instrument) member.Instrument;
                    state.MemberIsBackInLobby[member.UserId] = member.IsBackInLobby;
                }
            }
            var session = LobbyHubSession.Current;
            if (session != null && !string.IsNullOrEmpty(session.LocalUserId)
                && PlayerContainer.Players.Count > 0)
            {
                state.MemberInstruments[session.LocalUserId] =
                    PlayerContainer.Players[0].Profile.CurrentInstrument;
            }
            if (result.ChatHistory != null) state.ChatHistory.AddRange(result.ChatHistory);
            if (result.SongQueue   != null) state.SongQueue.AddRange(result.SongQueue);

            HashSet<HashWrapper> removalSet = null;
            if (result.LibraryRemovals is { Length: > 0 } removals)
            {
                removalSet = new HashSet<HashWrapper>(removals.Length);
                foreach (var s in removals)
                    removalSet.Add(HashWrapper.FromString(s.AsSpan()));
            }

            var localHashes = AllLocalHashes();
            state.LobbySongLibrary = new HashSet<HashWrapper>();
            foreach (var hash in localHashes)
            {
                if (removalSet != null && removalSet.Contains(hash)) continue;
                state.LobbySongLibrary.Add(hash);
            }

            return state;
        }

        private static LobbyRoomState FromLobbyDto(LobbyDto lobby) => new()
        {
            LobbyId = lobby.Id,
            LobbyName = lobby.Name,
            HostUserId = lobby.HostUserId,
            HostName = lobby.HostName,
            GameMode = lobby.GameMode,
            Region = lobby.Region,
            Status = lobby.Status,
            MaxPlayers = lobby.MaxPlayers,
            IsPublic = lobby.IsPublic,
            // Default to in-game for mid-session joiners; corrected by first status event.
            IsSongInProgress = lobby.Status == LobbyStatus.GameStarted,
        };
    }

    public enum LobbyMemberStage
    {
        InLobby,
        InGame,
        OnResults,
    }
}
