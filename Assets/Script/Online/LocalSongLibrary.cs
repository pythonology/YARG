using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Core.Song;
using YARG.Song;

namespace YARG.Online
{
    /// <summary>
    /// Snapshots the local player's installed song hashes. The snapshot is streamed to the
    /// lobby hub (see <see cref="LobbyHubSession"/>) so the server can compute the lobby-wide
    /// shared library (intersection of every member's library).
    ///
    /// Each song contributes BOTH its strict hash and its gameplay hash (when known) to the
    /// same set. The server's intersection logic is untouched and untyped -- it just treats
    /// these as opaque strings -- so two players end up "sharing" a song if either value
    /// matches. Strict takes precedence automatically wherever it's checked downstream
    /// (queueing prefers it when both are present in the synced shared library).
    ///
    /// Gameplay hashes require a full LoadChart() per song, so they can't be computed eagerly
    /// for the whole library without hurting scan performance. Instead, this class backfills
    /// them in the background, starting the moment a scan finishes (not when a lobby is
    /// joined) so someone who scans in new songs and immediately hosts isn't starting from
    /// zero, and prioritizing whatever was just added, since that's what they're most likely
    /// about to try to play with someone else.
    /// </summary>
    internal static class LocalSongLibrary
    {
        private const int BACKFILL_BATCH_SIZE = 25;

        // Gap between each song's LoadChart(), not just between batches -- this is real,
        // back-to-back CPU + allocation work, and without a gap it would pin a thread-pool
        // worker (and generate steady GC pressure) continuously for however long a large
        // library takes to backfill.
        private static readonly TimeSpan SongDelay = TimeSpan.FromMilliseconds(50);

        // How often to re-check whether gameplay has ended while paused.
        private static readonly TimeSpan GameplayPollDelay = TimeSpan.FromSeconds(2);

        private static CancellationTokenSource _backfillCts;

        /// <summary>
        /// Fired on the calling (background) thread once a background gameplay-hash backfill
        /// completes and discovers at least one new gameplay hash. <see cref="LobbyHubSession"/>
        /// subscribes to this only while a lobby session is active, re-streaming the updated
        /// snapshot in response. Since this fires off the main thread, subscribers that need
        /// the main thread must marshal back themselves.
        /// </summary>
        public static event Action BackfillBatchCompleted;

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {
            SongContainer.OnSongsRefreshed += RestartBackfill;
        }

        private static void RestartBackfill()
        {
            // A fresh scan (manual rescan, or one that was already running) always
            // supersedes whatever the previous backfill pass was doing -- it may have
            // found brand-new songs, so start over rather than let a stale pass keep
            // running against a library that no longer matches SongContainer's state.
            _backfillCts?.Cancel();
            _backfillCts = new CancellationTokenSource();
            BackfillAsync(_backfillCts.Token).Forget();
        }

        /// <summary>
        /// Materialize every installed song hash on the calling thread. Must be called from
        /// the main thread -- the resulting array is what gets chunked and streamed, so the
        /// SignalR send thread never touches <see cref="SongContainer.SongsByHash"/>.
        /// </summary>
        public static string[] SnapshotLocalHashes()
        {
            var byHash = SongContainer.SongsByHash;
            var hashes = new List<string>(byHash.Count * 2);

            foreach (var kv in byHash)
            {
                string strict = kv.Key.ToString();
                hashes.Add(strict);

                if (GameplayHashCache.TryGet(strict, out var gameplay))
                {
                    hashes.Add(gameplay);
                }
            }

            return hashes.ToArray();
        }

        private static async UniTask BackfillAsync(CancellationToken ct)
        {
            // Run off the main thread, same as CacheHandler.RunScan -- LoadChart() does
            // real file I/O and parsing work per song, not something the UI thread should
            // be blocked on for however long a library takes to backfill.
            await UniTask.SwitchToThreadPool();

            // Snapshotted once, up front, rather than enumerated live: SongsByHash gets
            // fully cleared and rebuilt by the next scan (possibly from the main thread
            // while this is still running), which would otherwise risk a "collection was
            // modified" exception or working against half-rebuilt state.
            var librarySnapshot = SongContainer.SongsByHash.ToArray();

            // Newest file first: someone who just scanned in new songs and jumps straight
            // into a lobby cares about THOSE getting a gameplay hash before older songs
            // that have likely already been backfilled in a previous session anyway.
            Array.Sort(librarySnapshot, (a, b) =>
                b.Value[0].GetLastWriteTime().CompareTo(a.Value[0].GetLastWriteTime()));

            int sinceLastBatch = 0;
            bool anyNewHashes = false;

            foreach (var kv in librarySnapshot)
            {
                ct.ThrowIfCancellationRequested();

                string strict = kv.Key.ToString();
                if (GameplayHashCache.TryGet(strict, out _))
                {
                    continue; // already known, possibly from a previous session -- no
                              // LoadChart() needed, so nothing to throttle or pause for
                }

                // Fully step aside while the player is actually playing. This is exactly
                // the moment a GC pause or thread-pool contention would be most noticeable
                // -- and nobody's about to join a lobby mid-song anyway, so there's no
                // downside to letting this wait.
                while (GlobalVariables.Instance.CurrentScene is SceneIndex.Gameplay)
                {
                    await UniTask.Delay(GameplayPollDelay, cancellationToken: ct);
                }

                // Every SongEntry filed under the same strict hash is byte-identical by
                // definition, so a single representative is enough to hash for all of them.
                var chart = kv.Value[0].LoadChart();
                if (chart == null)
                {
                    continue;
                }

                var gameplayHash = GameplayHasher.Hash(chart);
                GameplayHashCache.Set(strict, gameplayHash.ToString());
                SongContainer.RegisterGameplayHash(kv.Key, gameplayHash);
                sinceLastBatch++;
                anyNewHashes = true;

                await UniTask.Delay(SongDelay, cancellationToken: ct);

                if (sinceLastBatch >= BACKFILL_BATCH_SIZE)
                {
                    GameplayHashCache.Flush();
                    sinceLastBatch = 0;
                }
            }

            if (sinceLastBatch > 0)
            {
                GameplayHashCache.Flush();
            }

            if (anyNewHashes)
            {
                BackfillBatchCompleted?.Invoke();
            }
        }
    }
}
