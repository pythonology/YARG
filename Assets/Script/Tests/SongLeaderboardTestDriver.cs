using System;
using System.Collections.Generic;
using UnityEngine;
using YARG.Core;
using YARG.Core.Game;
using YARG.Scores;

#if UNITY_EDITOR
namespace YARG.Menu.MusicLibrary
{
    public class SongLeaderboardTestDriver : MonoBehaviour
    {
        [SerializeField]
        private SongLeaderboard _leaderboard;
        [SerializeField]
        private int _recordCount = 40;

        private static readonly string[] FakeNames =
        {
            "Riff", "Wraith", "Echo", "Pyro", "Glitch", "Nova", "Vex", "Quill",
            "Sable", "Mox", "Cipher", "Halo", "Onyx", "Rune", "Tempest", "Drift",
        };

        private static readonly string[] FakeEngines =
        {
            "Default", "Casual", "Precision", "Custom",
        };

        private static readonly float[] Speeds = { 0.75f, 1.00f, 1.00f, 1.00f, 1.25f, 1.50f };

        private static readonly Modifier[] ModifierPool =
        {
            Modifier.None,
            Modifier.None,
            Modifier.AllHopos,
            Modifier.AllStrums,
            Modifier.NoKicks,
            Modifier.NoteShuffle,
            Modifier.AllHopos | Modifier.NoKicks,
            Modifier.TapsToHopos | Modifier.NoDynamics,
        };

        private void Start()
        {
            var rng = new System.Random(42);
            var nameById = new Dictionary<Guid, string>();
            var engineNameById = new Dictionary<Guid, string>();
            var entries = new List<SongLeaderboardEntry>(_recordCount);

            for (int i = 0; i < _recordCount; i++)
            {
                var playerId = Guid.NewGuid();
                nameById[playerId] = FakeNames[i % FakeNames.Length];

                var engineId = Guid.NewGuid();
                engineNameById[engineId] = FakeEngines[i % FakeEngines.Length];

                var mods = ModifierPool[rng.Next(ModifierPool.Length)];
                int notesTotal = rng.Next(400, 1200);
                int notesHit   = (int) (notesTotal * (0.70 + rng.NextDouble() * 0.30));

                entries.Add(new SongLeaderboardEntry
                {
                    PlayerId       = playerId,
                    EnginePresetId = engineId,
                    Score          = rng.Next(100_000, 1_000_000),
                    Percent        = (float) notesHit / notesTotal,
                    NotesHit       = notesHit,
                    NotesMissed    = notesTotal - notesHit,
                    Modifiers      = mods == Modifier.None ? null : (Modifier?) mods,
                    Instrument     = Instrument.FiveFretGuitar,
                    Difficulty     = Difficulty.Expert,
                    IsFc           = false,
                    SongSpeed      = Speeds[rng.Next(Speeds.Length)],
                });
            }

            _leaderboard.Show(entries,
                id => nameById.GetValueOrDefault(id, "Unknown"),
                id => engineNameById.GetValueOrDefault(id, "Default"));
        }
    }
}
#endif
