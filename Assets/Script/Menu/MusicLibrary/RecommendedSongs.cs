using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YARG.Core.Song;
using YARG.Helpers.Extensions;
using YARG.Scores;
using YARG.Song;

using Random = UnityEngine.Random;

namespace YARG.Menu.MusicLibrary
{
    public static class RecommendedSongs
    {
        public const int RECOMMEND_SONGS_COUNT = 10;

#nullable enable
        public static SongEntry[] GetRecommendedSongs(HashSet<HashWrapper>? allowedHashes = null)
        {
            // When the caller restricts the pool (e.g. an online lobby's shared songs),
            // build the random-pick pool from the intersection of SongContainer.Songs and the
            // allow-list. Cap the target at the pool size so the random-fill loop terminates.
            List<SongEntry>? allowedPool = null;
            int targetCount = RECOMMEND_SONGS_COUNT;
            if (allowedHashes != null)
            {
                allowedPool = new List<SongEntry>(allowedHashes.Count);
                foreach (var s in SongContainer.Songs)
                {
                    if (MusicLibraryMenu.IsAllowed(s))
                        allowedPool.Add(s);
                }
                targetCount = Math.Min(RECOMMEND_SONGS_COUNT, allowedPool.Count);
                if (targetCount == 0) return Array.Empty<SongEntry>();
            }

            var songs = new SongEntry[targetCount];
            int index = 0;
            AddMostPlayedSongs(songs, ref index, allowedHashes);
            AddRandomSongs(songs, ref index, allowedHashes, allowedPool, targetCount);
            return songs[..index];
        }

        private static void AddMostPlayedSongs(SongEntry[] songs, ref int index,
            HashSet<HashWrapper>? allowedHashes)
        {
            const float RNG_PER_SONG = .05f;

            // Get the top ten most played songs
            var mostPlayed = ScoreContainer.GetMostPlayedSongs(10);
            if (allowedHashes != null)
            {
                mostPlayed.RemoveAll(s => !MusicLibraryMenu.IsAllowed(s));
            }
            if (mostPlayed.Count > 0)
            {
                float rng = mostPlayed.Count * RNG_PER_SONG;
                if (Random.value < rng)
                {
                    AddSongFromMostPlayed(songs, ref index, ref mostPlayed);
                }
                AddSongsFromTopPlayedArtists(songs, ref index, ref mostPlayed, allowedHashes);
            }
        }

        private static readonly SortString _YARGSOURCE = new SortString("yarg");
        private static void AddRandomSongs(SongEntry[] songs, ref int index,
            HashSet<HashWrapper>? allowedHashes, List<SongEntry>? allowedPool, int targetCount)
        {
            const float STARTING_RNG = .75f;
            const float RNG_DECREMENT = .25f;

            SongContainer.Sources.TryGetValue(_YARGSOURCE, out var yargSongs);

            // Pre-filter yarg-source pool to allowed-only so picks always land in the allow-list.
            IList<SongEntry>? yargPool = yargSongs;
            if (yargPool != null && allowedHashes != null)
            {
                var filtered = new List<SongEntry>(yargPool.Count);
                foreach (var s in yargPool)
                    if (MusicLibraryMenu.IsAllowed(s)) filtered.Add(s);
                yargPool = filtered.Count > 0 ? filtered : null;
            }

            // Cheap exit if both pools are empty -- protects against infinite loops with a
            // pathologically small allow-list whose songs are all already in `songs`.
            bool hasYargPool = yargPool != null && yargPool.Count > 0;
            bool hasGeneralPool = allowedHashes == null || (allowedPool != null && allowedPool.Count > 0);
            if (!hasYargPool && !hasGeneralPool) return;

            float yargSongRNG = hasYargPool ? STARTING_RNG : 0;
            while (index < targetCount)
            {
                SongEntry song;
                if (hasYargPool && Random.value <= yargSongRNG)
                {
                    yargSongRNG -= RNG_DECREMENT;
                    song = yargPool!.Pick();
                }
                else if (allowedPool != null)
                {
                    song = allowedPool.Pick();
                }
                else
                {
                    song = SongContainer.GetRandomSong();
                }

                if (!songs.Contains(song))
                {
                    songs[index++] = song;
                }
            }
        }
#nullable disable

        private static void AddSongFromMostPlayed(SongEntry[] songs, ref int index, ref List<SongEntry> mostPlayed)
        {
            int songIndex = Random.Range(0, mostPlayed.Count);
            var song = mostPlayed[songIndex];
            mostPlayed.RemoveAt(songIndex);
            songs[index++] = song;
        }

#nullable enable
        private static void AddSongsFromTopPlayedArtists(SongEntry[] songs, ref int index,
            ref List<SongEntry> mostPlayed, HashSet<HashWrapper>? allowedHashes)
        {
            var artists = SongContainer.Artists;
            while (mostPlayed.Count > 0)
            {
                int songIndex = Random.Range(0, mostPlayed.Count);
                var artistSongs = artists[mostPlayed[songIndex].Artist];

                IList<SongEntry> artistPool = artistSongs;
                if (allowedHashes != null)
                {
                    var filtered = new List<SongEntry>(artistSongs.Count);
                    foreach (var s in artistSongs)
                        if (MusicLibraryMenu.IsAllowed(s)) filtered.Add(s);
                    artistPool = filtered;
                }

                if (artistPool.Count > 0)
                {
                    var song = artistPool.Pick();
                    if (!mostPlayed.Contains(song) && !songs.Contains(song))
                    {
                        songs[index++] = song;
                        break;
                    }
                }
                mostPlayed.RemoveAt(songIndex);
            }
        }
#nullable disable
    }
}