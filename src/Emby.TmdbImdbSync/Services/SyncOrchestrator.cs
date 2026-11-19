using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.TmdbImdbSync.Configuration;
using MediaBrowser.Model.Logging;

namespace Emby.TmdbImdbSync.Services
{
    public class SyncWorkItem
    {
        public string EmbyUserId { get; set; } = string.Empty;
        public long MovieInternalId { get; set; }
        public string MovieName { get; set; } = string.Empty;
        public string? TmdbId { get; set; }
        public string? ImdbId { get; set; }
        public bool IsFavorite { get; set; }
        public bool IsPlayed { get; set; }
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    }

    public class SyncOrchestrator
    {
        private readonly TmdbClient _tmdbClient;
        private readonly ImdbClient _imdbClient;
        private readonly SyncStateStore _stateStore;
        private readonly ILogger _logger;
        private readonly Func<PluginConfiguration?> _configProvider;

        // In-memory debounce queue: key is (embyUserId:movieInternalId)
        private readonly ConcurrentDictionary<string, SyncWorkItem> _pendingItems = new ConcurrentDictionary<string, SyncWorkItem>();

        // Provider ID cache: tmdbId -> imdbId and imdbId -> tmdbId
        private readonly ConcurrentDictionary<string, string> _providerIdCache = new ConcurrentDictionary<string, string>();

        public SyncOrchestrator(
            TmdbClient tmdbClient,
            ImdbClient imdbClient,
            SyncStateStore stateStore,
            ILogger logger,
            Func<PluginConfiguration?>? configProvider = null)
        {
            _tmdbClient = tmdbClient;
            _imdbClient = imdbClient;
            _stateStore = stateStore;
            _logger = logger;
            _configProvider = configProvider ?? (() => Plugin.Instance?.Configuration);
        }

        public void Enqueue(SyncWorkItem item)
        {
            var key = $"{item.EmbyUserId}:{item.MovieInternalId}";
            _pendingItems.AddOrUpdate(key, item, (_, __) => item);
        }

        public async Task ProcessDebouncedItemAsync(SyncWorkItem item, CancellationToken cancellationToken)
        {
            try
            {
                var config = _configProvider();
                if (config == null) return;

                var userConfig = config.Users.FirstOrDefault(u => string.Equals(u.EmbyUserId, item.EmbyUserId, StringComparison.OrdinalIgnoreCase));
                if (userConfig == null) return;

                if (!userConfig.EnableTmdbSync && !userConfig.EnableImdbSync) return;

                // Load existing state for diffing
                var userState = _stateStore.LoadState(item.EmbyUserId);
                var stateKey = item.MovieInternalId.ToString();
                userState.Movies.TryGetValue(stateKey, out var lastSynced);

                var favoriteChanged = lastSynced == null || lastSynced.IsFavorite != item.IsFavorite;
                var playedChanged = lastSynced == null || lastSynced.IsPlayed != item.IsPlayed;

                if (!favoriteChanged && !playedChanged)
                {
                    _logger.Debug("[SyncOrchestrator] Movie '{0}' state unchanged for user {1}, skipping.", item.MovieName, item.EmbyUserId);
                    return;
                }

                _logger.Info("[SyncOrchestrator] Processing sync for movie '{0}' (user: {1}): Favorite={2} (changed={3}), Played={4} (changed={5})",
                    item.MovieName, userConfig.EmbyUserName, item.IsFavorite, favoriteChanged, item.IsPlayed, playedChanged);

                // Resolve provider IDs if needed
                var (resolvedTmdbId, resolvedImdbId) = await ResolveMissingProviderIdsAsync(item.TmdbId, item.ImdbId, config.TmdbAppReadAccessToken, cancellationToken).ConfigureAwait(false);

                var tmdbSuccess = true;
                var imdbSuccess = true;

                // 1. TMDb Sync
                if (userConfig.EnableTmdbSync && !string.IsNullOrWhiteSpace(userConfig.TmdbSessionId) && !string.IsNullOrWhiteSpace(resolvedTmdbId) && int.TryParse(resolvedTmdbId, out var tmdbMovieId))
                {
                    try
                    {
                        if (userConfig.SyncTmdbFavorites && favoriteChanged)
                        {
                            var favRes = await _tmdbClient.SetFavoriteAsync(
                                userConfig.TmdbAccountId,
                                userConfig.TmdbSessionId,
                                tmdbMovieId,
                                item.IsFavorite,
                                config.TmdbAppReadAccessToken,
                                cancellationToken).ConfigureAwait(false);

                            if (!favRes) tmdbSuccess = false;

                            if (userConfig.TmdbFavoritesListId.HasValue && userConfig.TmdbFavoritesListId.Value > 0)
                            {
                                if (item.IsFavorite)
                                {
                                    await _tmdbClient.AddToWatchedListAsync(
                                        userConfig.TmdbFavoritesListId.Value,
                                        userConfig.TmdbSessionId,
                                        tmdbMovieId,
                                        userConfig.TmdbV4AccessToken,
                                        config.TmdbAppReadAccessToken,
                                        cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    await _tmdbClient.RemoveFromWatchedListAsync(
                                        userConfig.TmdbFavoritesListId.Value,
                                        userConfig.TmdbSessionId,
                                        tmdbMovieId,
                                        userConfig.TmdbV4AccessToken,
                                        config.TmdbAppReadAccessToken,
                                        cancellationToken).ConfigureAwait(false);
                                }
                            }
                        }

                        if (userConfig.SyncTmdbWatched && playedChanged && userConfig.TmdbWatchedListId.HasValue && userConfig.TmdbWatchedListId.Value > 0)
                        {
                            bool watchRes;
                            if (item.IsPlayed)
                            {
                                watchRes = await _tmdbClient.AddToWatchedListAsync(
                                    userConfig.TmdbWatchedListId.Value,
                                    userConfig.TmdbSessionId,
                                    tmdbMovieId,
                                    userConfig.TmdbV4AccessToken,
                                    config.TmdbAppReadAccessToken,
                                    cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                watchRes = await _tmdbClient.RemoveFromWatchedListAsync(
                                    userConfig.TmdbWatchedListId.Value,
                                    userConfig.TmdbSessionId,
                                    tmdbMovieId,
                                    userConfig.TmdbV4AccessToken,
                                    config.TmdbAppReadAccessToken,
                                    cancellationToken).ConfigureAwait(false);
                            }

                            if (!watchRes) tmdbSuccess = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException("[SyncOrchestrator] Error syncing TMDb for movie {0}", ex, item.MovieName);
                        tmdbSuccess = false;
                    }
                }

                // 2. IMDb Sync (Isolated from TMDb)
                var effectiveImdbCookie = userConfig.GetEffectiveImdbCookie();
                if (userConfig.EnableImdbSync && !string.IsNullOrWhiteSpace(effectiveImdbCookie) && !string.IsNullOrWhiteSpace(resolvedImdbId))
                {
                    try
                    {
                        if (userConfig.SyncImdbFavorites && favoriteChanged && !string.IsNullOrWhiteSpace(userConfig.ImdbFavoritesListId))
                        {
                            ImdbClientResult<bool> favRes;
                            if (item.IsFavorite)
                            {
                                favRes = await _imdbClient.AddItemToListAsync(userConfig.EmbyUserId, effectiveImdbCookie, userConfig.ImdbFavoritesListId, resolvedImdbId, cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                favRes = await _imdbClient.RemoveItemFromListAsync(userConfig.EmbyUserId, effectiveImdbCookie, userConfig.ImdbFavoritesListId, resolvedImdbId, cancellationToken).ConfigureAwait(false);
                            }

                            if (favRes.AuthExpired)
                            {
                                HandleImdbAuthExpired(userConfig);
                                imdbSuccess = false;
                            }
                            else if (!favRes.Success)
                            {
                                imdbSuccess = false;
                            }
                        }

                        if (userConfig.SyncImdbWatched && playedChanged)
                        {
                            var watchRes = await _imdbClient.SetTitleWatchedAsync(userConfig.EmbyUserId, effectiveImdbCookie, resolvedImdbId, item.IsPlayed, cancellationToken).ConfigureAwait(false);
                            if (watchRes.AuthExpired)
                            {
                                HandleImdbAuthExpired(userConfig);
                                imdbSuccess = false;
                            }
                            else if (!watchRes.Success)
                            {
                                imdbSuccess = false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException("[SyncOrchestrator] Error syncing IMDb for movie {0}", ex, item.MovieName);
                        imdbSuccess = false;
                    }
                }

                // Update SyncStateStore
                if (tmdbSuccess && imdbSuccess)
                {
                    userState.Movies[stateKey] = new MovieSyncState
                    {
                        InternalId = item.MovieInternalId,
                        Name = item.MovieName,
                        TmdbId = resolvedTmdbId,
                        ImdbId = resolvedImdbId,
                        IsFavorite = item.IsFavorite,
                        IsPlayed = item.IsPlayed,
                        ImdbFavoriteSynced = item.IsFavorite && imdbSuccess,
                        ImdbWatchedSynced = item.IsPlayed && imdbSuccess,
                        LastSyncedUtc = DateTime.UtcNow
                    };
                    _stateStore.SaveState(item.EmbyUserId, userState);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[SyncOrchestrator] Unexpected error processing work item for movie '{0}'", ex, item.MovieName);
            }
        }

        private void HandleImdbAuthExpired(UserSyncConfig userConfig)
        {
            _logger.Warn("[TmdbImdbSync] IMDb cookie expired for Emby user '{0}'. Please refresh cookie in plugin settings.", userConfig.EmbyUserName);
            userConfig.ImdbLastStatus = "Cookie expired - please refresh";
            userConfig.ImdbLastCheckedUtc = DateTime.UtcNow;
            try
            {
                Plugin.Instance?.SaveConfiguration();
            }
            catch
            {
                // ignore
            }
        }

        private async Task<(string? tmdbId, string? imdbId)> ResolveMissingProviderIdsAsync(string? tmdbId, string? imdbId, string tokenOrKey, CancellationToken cancellationToken)
        {
            var resTmdb = tmdbId;
            var resImdb = imdbId;

            // If TMDb ID is missing, but IMDb ID is present
            if (string.IsNullOrWhiteSpace(resTmdb) && !string.IsNullOrWhiteSpace(resImdb))
            {
                if (_providerIdCache.TryGetValue($"imdb:{resImdb}", out var cachedTmdbId))
                {
                    resTmdb = cachedTmdbId;
                }
                else
                {
                    var resolved = await _tmdbClient.GetTmdbIdFromImdbIdAsync(resImdb, tokenOrKey, cancellationToken).ConfigureAwait(false);
                    if (resolved.HasValue)
                    {
                        resTmdb = resolved.Value.ToString();
                        _providerIdCache[$"imdb:{resImdb}"] = resTmdb;
                        _providerIdCache[$"tmdb:{resTmdb}"] = resImdb;
                    }
                }
            }

            // If IMDb ID is missing, but TMDb ID is present
            if (string.IsNullOrWhiteSpace(resImdb) && !string.IsNullOrWhiteSpace(resTmdb) && int.TryParse(resTmdb, out var parsedTmdbId))
            {
                if (_providerIdCache.TryGetValue($"tmdb:{resTmdb}", out var cachedImdbId))
                {
                    resImdb = cachedImdbId;
                }
                else
                {
                    var resolved = await _tmdbClient.GetImdbIdFromTmdbIdAsync(parsedTmdbId, tokenOrKey, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        resImdb = resolved;
                        _providerIdCache[$"tmdb:{resTmdb}"] = resImdb;
                        _providerIdCache[$"imdb:{resImdb}"] = resTmdb;
                    }
                }
            }

            return (resTmdb, resImdb);
        }
    }
}
