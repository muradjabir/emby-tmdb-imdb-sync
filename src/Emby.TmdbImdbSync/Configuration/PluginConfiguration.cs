using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Emby.TmdbImdbSync.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Global TMDb API Read Access Token (v4 Bearer token) or API key.
        /// </summary>
        public string TmdbAppReadAccessToken { get; set; } = string.Empty;

        /// <summary>
        /// Optional TMDb v3 API Key fallback.
        /// </summary>
        public string TmdbApiKey { get; set; } = string.Empty;

        /// <summary>
        /// When true, items missing locally in Emby will be removed remotely during full sync.
        /// Defaults to false (additive-only) to protect remote history.
        /// </summary>
        public bool MirrorRemovalsOnFullSync { get; set; } = false;

        /// <summary>
        /// Debounce window in seconds for rapid playback/toggle events.
        /// </summary>
        public int DebounceSeconds { get; set; } = 2;

        /// <summary>
        /// List of user sync configurations.
        /// </summary>
        public List<UserSyncConfig> Users { get; set; } = new List<UserSyncConfig>();
    }
}
