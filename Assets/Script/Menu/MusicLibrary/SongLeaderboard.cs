using System;
using System.Collections.Generic;
using UnityEngine;
using YARG.Core.Game;
using YARG.Player;
using YARG.Scores;
using YARG.Settings.Customization;

namespace YARG.Menu.MusicLibrary
{
    public class SongLeaderboard : MonoBehaviour
    {
        [SerializeField]
        private RectTransform _content;
        [SerializeField]
        private SongLeaderboardRow _rowPrefab;

        private readonly List<SongLeaderboardRow> _rows = new();

        // This code currently looks a bit weird. Its setup to support the ui's test mock environment, and
        // I think the structure will become useful when having to resolve non-local names in the future.
        public void Show(IReadOnlyList<SongLeaderboardEntry> entries,
            Func<Guid, string> nameResolver = null,
            Func<Guid, string> engineNameResolver = null)
        {
            nameResolver ??= id => PlayerContainer.GetProfileById(id)?.Name ?? "Unknown";
            engineNameResolver ??= id =>
                (CustomContentManager.EnginePresets.GetPresetById(id) ?? EnginePreset.Default).Name;

            Clear();
            foreach (var entry in entries)
            {
                var row = Instantiate(_rowPrefab, _content);
                row.Bind(entry, nameResolver(entry.PlayerId), engineNameResolver(entry.EnginePresetId));
                _rows.Add(row);
            }
        }

        public void Clear()
        {
            foreach (var row in _rows)
            {
                Destroy(row.gameObject);
            }
            _rows.Clear();
        }
    }
}
