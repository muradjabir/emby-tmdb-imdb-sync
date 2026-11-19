using System;

namespace Emby.TmdbImdbSync.Configuration
{
    public class UserSyncConfig
    {
        public string EmbyUserId { get; set; } = string.Empty;
        public string EmbyUserName { get; set; } = string.Empty;

        // TMDb Settings
        public bool EnableTmdbSync { get; set; } = false;
        public bool SyncTmdbFavorites { get; set; } = true;
        public bool SyncTmdbWatched { get; set; } = true;
        public string TmdbUsername { get; set; } = string.Empty;
        public string TmdbSessionId { get; set; } = string.Empty;
        public int TmdbAccountId { get; set; } = 0;
        public string TmdbV4AccessToken { get; set; } = string.Empty;
        public string TmdbV4AccountId { get; set; } = string.Empty;
        public int? TmdbWatchedListId { get; set; }
        public string TmdbWatchedListName { get; set; } = "Emby Watched Movies";

        // IMDb Settings
        public bool EnableImdbSync { get; set; } = false;
        public bool SyncImdbWatched { get; set; } = true;
        public bool SyncImdbFavorites { get; set; } = true;
        public string ImdbCookie { get; set; } = string.Empty;
        public string ImdbFavoritesListId { get; set; } = string.Empty;
        public string ImdbLastStatus { get; set; } = "Not Tested";
        public DateTime? ImdbLastCheckedUtc { get; set; }
    }
}
