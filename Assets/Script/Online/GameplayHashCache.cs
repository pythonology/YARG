using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using YARG.Core.Song;
using YARG.Helpers;

namespace YARG.Online
{
/// <summary>
    /// Persists the (expensive) mapping from a song's strict hash to its gameplay hash,
    /// so it's only ever computed once per song, not once per session.
    ///
    /// Keyed by strict hash rather than file path on purpose: if the source file changes,
    /// its strict hash changes too, so a stale entry is simply orphaned (and ignored).
    ///
    /// Cache file name is suffixed with <see cref="GameplayHasher.HASH_VERSION"/>; bumping
    /// that version starts a fresh file, and stale versioned files are deleted automatically.
    /// </summary>
    internal static class GameplayHashCache
    {
        private static readonly string CachePath =
            Path.Combine(PathHelper.PersistentDataPath, $"GameplayHashCache{GameplayHasher.HASH_VERSION}.json");

        private static Dictionary<string, string> _map;
        private static bool _dirty;
       private static bool _staleFilesDeleted;

        private static void DeleteStaleCacheFiles()
        {
            if (_staleFilesDeleted) return;
            _staleFilesDeleted = true;

            try
            {
                var dir = Path.GetDirectoryName(CachePath);
                var currentFile = Path.GetFileName(CachePath);
                foreach (var file in Directory.EnumerateFiles(dir, "GameplayHashCache*.json"))
                {
                    if (Path.GetFileName(file) != currentFile)
                    {
                        File.Delete(file);
                    }
                }

                // Pre-versioning legacy filename -- doesn't match the glob above :(
                var legacyFile = Path.Combine(dir, "gameplay_hash_cache.json");
                if (File.Exists(legacyFile))
                {
                    File.Delete(legacyFile);
                }
            }
            catch
            {
                // Best-effort cleanup. A leftover file just wastes a little disk space :)
            }
        }

        private static Dictionary<string, string> Map
        {
            get
            {
                if (_map != null)
                {
                    return _map;
                }
                DeleteStaleCacheFiles();


                if (File.Exists(CachePath))
                {
                    try
                    {
                        var text = File.ReadAllText(CachePath);
                        _map = JsonConvert.DeserializeObject<Dictionary<string, string>>(text)
                            ?? new Dictionary<string, string>();
                    }
                    catch
                    {
                        // Corrupt/partial file (e.g. crash mid-write) -- start clean rather
                        // than block startup. Everything here is re-derivable from the
                        // player's own song library, so there's nothing to lose.
                        _map = new Dictionary<string, string>();
                    }
                }
                else
                {
                    _map = new Dictionary<string, string>();
                }

                return _map;
            }
        }

        public static bool TryGet(string strictHash, out string gameplayHash) =>
            Map.TryGetValue(strictHash, out gameplayHash);

        public static void Set(string strictHash, string gameplayHash)
        {
            Map[strictHash] = gameplayHash;
            _dirty = true;
        }

        /// <summary>
        /// Writes pending changes to disk. Call after a batch of <see cref="Set"/> calls,
        /// not after every single one -- this is a background-thread-friendly cache, not
        /// a transactional store.
        /// </summary>
        public static void Flush()
        {
            if (!_dirty && File.Exists(CachePath))
            {
                return;
            }
            var text = JsonConvert.SerializeObject(Map);
            File.WriteAllText(CachePath, text);
            _dirty = false;
        }
    }
}
