using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.TmdbImdbSync.Services
{
    public class MovieSyncState
    {
        public long InternalId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? TmdbId { get; set; }
        public string? ImdbId { get; set; }
        public bool IsFavorite { get; set; }
        public bool IsPlayed { get; set; }
        public bool ImdbWatchedSynced { get; set; }
        public bool ImdbFavoriteSynced { get; set; }
        public DateTime LastSyncedUtc { get; set; }
    }

    public class UserSyncState
    {
        public string EmbyUserId { get; set; } = string.Empty;
        public Dictionary<string, MovieSyncState> Movies { get; set; } = new Dictionary<string, MovieSyncState>();
    }

    public class SyncStateStore
    {
        private readonly string _stateDirectory;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, object> _fileLocks = new ConcurrentDictionary<string, object>();

        public SyncStateStore(string dataFolderPath, IJsonSerializer jsonSerializer, ILogger logger)
        {
            _stateDirectory = Path.Combine(dataFolderPath, "state");
            _jsonSerializer = jsonSerializer;
            _logger = logger;

            try
            {
                if (!Directory.Exists(_stateDirectory))
                {
                    Directory.CreateDirectory(_stateDirectory);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Failed to create sync state directory {0}", ex, _stateDirectory);
            }
        }

        private string GetStateFilePath(string embyUserId)
        {
            var sanitized = string.Concat(embyUserId.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(_stateDirectory, $"{sanitized}.json");
        }

        public UserSyncState LoadState(string embyUserId)
        {
            var filePath = GetStateFilePath(embyUserId);
            var fileLock = _fileLocks.GetOrAdd(embyUserId, _ => new object());

            lock (fileLock)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        var json = File.ReadAllText(filePath);
                        var state = _jsonSerializer.DeserializeFromString<UserSyncState>(json);
                        if (state != null)
                        {
                            return state;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error loading sync state for user {0}", ex, embyUserId);
                }

                return new UserSyncState { EmbyUserId = embyUserId };
            }
        }

        public void SaveState(string embyUserId, UserSyncState state)
        {
            var filePath = GetStateFilePath(embyUserId);
            var tmpPath = filePath + ".tmp";
            var fileLock = _fileLocks.GetOrAdd(embyUserId, _ => new object());

            lock (fileLock)
            {
                try
                {
                    var json = _jsonSerializer.SerializeToString(state);
                    File.WriteAllText(tmpPath, json);

                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                    File.Move(tmpPath, filePath);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error saving sync state for user {0}", ex, embyUserId);
                }
            }
        }
    }
}
