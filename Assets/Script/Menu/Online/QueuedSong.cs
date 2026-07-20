using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Song;
using YARG.Helpers.Extensions;
using YARG.Menu.MusicLibrary;
using YARG.Player;
using YARG.Song;

namespace YARG.Menu.Online
{
    public class QueuedSong : MonoBehaviour
    {
        // FormerlySerializedAs preserves existing wiring from the old _albumArt name.
        [SerializeField]
        [FormerlySerializedAs("_albumArt")]
        private Image _songBackground;
        [SerializeField]
        private Sprite _placeholder;
        [SerializeField]
        private TextMeshProUGUI _songName;
        [SerializeField]
        private TextMeshProUGUI _artistText;
        [SerializeField]
        private TextMeshProUGUI _charterText;
        [SerializeField]
        private DifficultyRing _difficultyRing;
        [SerializeField]
        private Button _removeButton;
        [SerializeField]
        private TextMeshProUGUI _songSpeedText;
        [SerializeField]
        private GameObject _songSpeedSeparator;

        private CancellationTokenSource _albumCts;
        private Texture2D _ownedTexture;
        private Action _onRemove;

        public void Initialize(HashWrapper hash, bool isLocalHost, float songSpeed, Action onRemove)
        {
            CancelAlbumLoad();
            ClearOwnedTexture();

            _onRemove = onRemove;
            // Remove button is optional in the prefab -- just toggle visibility.
            if (_removeButton != null)
            {
                _removeButton.gameObject.SetActive(isLocalHost);
            }

            SetSongSpeed(songSpeed);

            if (!SongContainer.SongsByHash.TryGetValue(hash, out var songs)
                && !SongContainer.SongsByGameplayHash.TryGetValue(hash, out songs))
            {
                _songName.text = hash.ToString();
                _songBackground.sprite = _placeholder;
                if (_artistText  != null) _artistText.text  = string.Empty;
                if (_charterText != null) _charterText.text = string.Empty;
                return;
            }

            var song = songs[0];
            _songName.text = song.Name;
            if (_artistText  != null) _artistText.text  = song.Artist.ToString();
            if (_charterText != null) _charterText.text = song.Charter.ToString();

            // Shows difficulty for the local player's selected instrument.
            if (_difficultyRing != null && PlayerContainer.Players.Count > 0)
            {
                var instrument = PlayerContainer.Players[0].Profile.CurrentInstrument;
                _difficultyRing.SetInfo(instrument.ToResourceName(), instrument, song[instrument]);
            }

            _songBackground.sprite = _placeholder;

            _albumCts = new CancellationTokenSource();
            LoadAlbumArtAsync(song, _albumCts.Token).Forget();
        }

        private async UniTaskVoid LoadAlbumArtAsync(SongEntry song, CancellationToken cancellationToken)
        {
            Texture2D texture = null;

            // Don't pass token to RunOnThreadPool -- we need to resume here to dispose the YARGImage.
            // ReSharper disable once MethodSupportsCancellation
            using var image = await UniTask.RunOnThreadPool(song.LoadAlbumData);
            if (image != null)
            {
                texture = image.LoadTexture(false);
            }

            if (cancellationToken.IsCancellationRequested || this == null)
            {
                if (texture != null)
                {
                    Destroy(texture);
                }
                return;
            }

            if (texture == null)
            {
                _songBackground.sprite = _placeholder;
                return;
            }

            _ownedTexture = texture;
            _songBackground.sprite = Sprite.Create(
                texture,
                new Rect(0, 0, texture.width, texture.height),
                new Vector2(0.5f, 0.5f));
        }

        public void SetRemoveButtonVisible(bool visible)
        {
            if (_removeButton != null) _removeButton.gameObject.SetActive(visible);
        }

        public void SetSongSpeed(float songSpeed)
        {
            bool isNormalSpeed = Mathf.Approximately(songSpeed, 1f);

            if (_songSpeedText != null)
            {
                _songSpeedText.gameObject.SetActive(!isNormalSpeed);
                if (!isNormalSpeed)
                {
                    _songSpeedText.text = YARG.Localization.Localize.Percent(songSpeed);
                }
            }

            if (_songSpeedSeparator != null)
            {
                _songSpeedSeparator.SetActive(!isNormalSpeed);
            }
        }

        public void InvokeRemove()
        {
            _onRemove?.Invoke();
        }

        private void OnDisable()
        {
            CancelAlbumLoad();
            ClearOwnedTexture();
        }

        private void OnDestroy()
        {
            CancelAlbumLoad();
            ClearOwnedTexture();
        }

        private void CancelAlbumLoad()
        {
            if (_albumCts == null)
            {
                return;
            }

            _albumCts.Cancel();
            _albumCts.Dispose();
            _albumCts = null;
        }

        private void ClearOwnedTexture()
        {
            if (_ownedTexture == null)
            {
                return;
            }

            _songBackground.sprite = _placeholder;
            Destroy(_ownedTexture);
            _ownedTexture = null;
        }
    }
}
