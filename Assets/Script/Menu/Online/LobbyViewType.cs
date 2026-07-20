using UnityEngine;
using YARG.Core.Song;
using YARG.Menu.ListMenu;
using YARG.Online.Lobbies.Contracts.Enums;
using YARG.Song;

namespace YARG.Menu.Online
{
    /// <summary>
    /// Row data for one lobby in the browser list. Mirrors the role of
    /// <c>SongViewType</c> in the music library, but with lobby fields and
    /// no scoring/star concepts.
    /// </summary>
    public sealed class LobbyViewType : BaseViewType
    {
        private readonly LobbyData   _lobby;
        private readonly OnlineMenu  _menu;

        public LobbyData Lobby => _lobby;

        public LobbyViewType(OnlineMenu menu, LobbyData lobby)
        {
            _menu  = menu;
            _lobby = lobby;
        }

        public override BackgroundType Background => BackgroundType.Normal;

        public override string GetPrimaryText(bool selected)
        {
            return _lobby.LobbyName;
        }

        public override string GetSecondaryText(bool selected)
        {
            return $"{_lobby.PlayerCount}/{_lobby.PlayerMax} Players";
        }

        public string GetHostNameText()
        {
            return _lobby.HostName;
        }

        /// <summary>
        /// Returns the source icon (RB, GH, Custom, etc.) for the lobby's current
        /// top-queue song, looked up by hash from the local song library. Same
        /// pattern as <c>SongViewType.GetIcon()</c>. Returns null (icon hidden)
        /// when the lobby has no queued song or the local player doesn't own it.
        /// </summary>
        public override Sprite GetIcon()
        {
            if (string.IsNullOrEmpty(_lobby.SongName)) return null;

            var hash = HashWrapper.FromString(_lobby.SongName);
            if (SongContainer.SongsByHash.TryGetValue(hash, out var entries) && entries.Count > 0)
            {
                return SongSources.SourceToIcon(entries[0].Source);
            }
            if (SongContainer.SongsByGameplayHash.TryGetValue(hash, out var looseEntries) && looseEntries.Count > 0)
            {
                return SongSources.SourceToIcon(looseEntries[0].Source);
            }
            return null;
        }

        public string GetLobbyTypeText()
        {
            return _lobby.GameMode switch
            {
                GameMode.Band      => "Band",
                // Contract enum value is "Quickplay" (kept for wire compat);
                // user-visible name is "Versus".
                GameMode.Quickplay => "Versus",
                _                  => _lobby.GameMode.ToString(),
            };
        }

        public string GetLobbyStateText()
        {
            return _lobby.Status switch
            {
                LobbyStatus.GameStarted => "Playing",
                LobbyStatus.SongSelect  => "Song Select",
                _                       => _lobby.Status.ToString(),
            };
        }

        public void PrimaryButtonClick()
        {
            _menu.JoinLobby(_lobby);
        }
    }
}
