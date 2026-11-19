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
        public string ImdbAtMain { get; set; } = string.Empty;
        public string ImdbUbidMain { get; set; } = string.Empty;
        public string ImdbCookie { get; set; } = string.Empty;
        public string ImdbFavoritesListId { get; set; } = string.Empty;
        public string ImdbLastStatus { get; set; } = "Not Tested";
        public DateTime? ImdbLastCheckedUtc { get; set; }

        public string GetEffectiveImdbCookie()
        {
            if (!string.IsNullOrWhiteSpace(ImdbAtMain) && !string.IsNullOrWhiteSpace(ImdbUbidMain))
            {
                var at = ImdbAtMain.Trim();
                if (at.StartsWith("at-main=", StringComparison.OrdinalIgnoreCase)) at = at.Substring("at-main=".Length);
                at = at.Trim().TrimEnd(';');

                var ubid = ImdbUbidMain.Trim();
                if (ubid.StartsWith("ubid-main=", StringComparison.OrdinalIgnoreCase)) ubid = ubid.Substring("ubid-main=".Length);
                ubid = ubid.Trim().TrimEnd(';');

                return $"at-main={at}; ubid-main={ubid};";
            }
            return ImdbCookie;
        }
    }
}
