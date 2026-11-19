using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.TmdbImdbSync.Configuration;
using Emby.TmdbImdbSync.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.TmdbImdbSync.Tasks
{
    public class FullSyncScheduledTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly ILogger _logger;
        private readonly IUserDataManager _userDataManager;

        public string Name => "Sync Favorites & Watched Movies (TMDb / IMDb)";
        public string Key => "TmdbImdbSyncFullSync";
        public string Description => "Performs full reconciliation of watched and favorited movies from Emby to TMDb and IMDb per user.";
        public string Category => "Library";

        public FullSyncScheduledTask(
            ILibraryManager libraryManager,
            IUserManager userManager,
            ILogManager logManager,
            IUserDataManager userDataManager)
        {
            _libraryManager = libraryManager;
            _userManager = userManager;
            _logger = logManager.GetLogger("Emby.TmdbImdbSync.FullSync");
            _userDataManager = userDataManager;
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerDaily,
                    TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
                }
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || config.Users == null || config.Users.Count == 0)
            {
                _logger.Info("[FullSync] No users configured for sync.");
                progress?.Report(100.0);
                return;
            }

            var enabledUsers = config.Users.Where(u => u.EnableTmdbSync || u.EnableImdbSync).ToList();
            if (enabledUsers.Count == 0)
            {
                _logger.Info("[FullSync] No users have TMDb or IMDb sync enabled.");
                progress?.Report(100.0);
                return;
            }

            var totalUsers = enabledUsers.Count;
            for (var i = 0; i < totalUsers; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var userConfig = enabledUsers[i];

                _logger.Info("[FullSync] Starting sync for user '{0}' ({1}/{2})...", userConfig.EmbyUserName, i + 1, totalUsers);

                var startPct = (double)i / totalUsers * 100.0;
                var endPct = (double)(i + 1) / totalUsers * 100.0;

                var userProgress = new Progress<double>(p =>
                {
                    var overall = startPct + (p / 100.0 * (endPct - startPct));
                    progress?.Report(overall);
                });

                await ExecuteUserSyncAsync(userConfig.EmbyUserId, cancellationToken, userProgress).ConfigureAwait(false);
            }

            progress?.Report(100.0);
            _logger.Info("[FullSync] Scheduled sync completed successfully for all users.");
        }

        public async Task ExecuteUserSyncAsync(string embyUserId, CancellationToken cancellationToken, IProgress<double>? progress)
        {
            var config = Plugin.Instance?.Configuration;
            var entryPoint = SyncEntryPoint.Instance;
            if (config == null || entryPoint == null || entryPoint.TmdbClient == null || entryPoint.ImdbClient == null || entryPoint.StateStore == null)
            {
                _logger.Warn("[FullSync] Services not ready for user sync.");
                return;
            }

            var userConfig = config.Users.FirstOrDefault(u => string.Equals(u.EmbyUserId, embyUserId, StringComparison.OrdinalIgnoreCase));
            if (userConfig == null) return;

            User? embyUser = null;
            if (Guid.TryParse(embyUserId, out var userGuid))
            {
                embyUser = _userManager.GetUserById(userGuid);
            }
            if (embyUser == null)
            {
                embyUser = _userManager.GetUserById(embyUserId);
            }

            if (embyUser == null)
            {
                _logger.Warn("[FullSync] Could not find Emby user with ID {0}", embyUserId);
                return;
            }

            // Fetch all movies in library
            var query = new InternalItemsQuery(embyUser)
            {
                IncludeItemTypes = new[] { nameof(Movie) },
                Recursive = true,
                IsVirtualItem = false
            };

            var items = _libraryManager.GetItemList(query);
            var movies = items.OfType<Movie>().Where(m => m.ExtraType == null).ToList();
            _logger.Info("[FullSync] Found {0} movies for user '{1}'", movies.Count, userConfig.EmbyUserName);

            var userState = entryPoint.StateStore.LoadState(embyUserId);
            var tokenOrKey = config.TmdbAppReadAccessToken;

            // TMDb remote sets
            HashSet<int> remoteTmdbFavorites = new HashSet<int>();
            HashSet<int> remoteTmdbWatched = new HashSet<int>();

            if (userConfig.EnableTmdbSync && !string.IsNullOrWhiteSpace(userConfig.TmdbSessionId))
            {
                if (userConfig.SyncTmdbFavorites)
                {
                    remoteTmdbFavorites = await entryPoint.TmdbClient.GetFavoriteMovieIdsAsync(userConfig.TmdbAccountId, userConfig.TmdbSessionId, tokenOrKey, cancellationToken).ConfigureAwait(false);
                }

                if (userConfig.SyncTmdbWatched && userConfig.TmdbWatchedListId.HasValue && userConfig.TmdbWatchedListId.Value > 0)
                {
                    remoteTmdbWatched = await entryPoint.TmdbClient.GetWatchedListMovieIdsAsync(userConfig.TmdbWatchedListId.Value, userConfig.TmdbV4AccessToken, tokenOrKey, cancellationToken).ConfigureAwait(false);
                }
            }

            var localTmdbFavorites = new HashSet<int>();
            var localTmdbWatched = new HashSet<int>();

            var total = movies.Count;
            for (var i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var movie = movies[i];
                var userData = _userDataManager.GetUserData(embyUser, movie);
                var isFavorite = userData.IsFavorite;
                var isPlayed = userData.Played;

                var tmdbIdStr = movie.GetProviderId(MetadataProviders.Tmdb);
                var imdbId = movie.GetProviderId(MetadataProviders.Imdb);

                int tmdbId = 0;
                if (!string.IsNullOrWhiteSpace(tmdbIdStr) && int.TryParse(tmdbIdStr, out var parsedTmdbId))
                {
                    tmdbId = parsedTmdbId;
                }

                if (tmdbId > 0)
                {
                    if (isFavorite) localTmdbFavorites.Add(tmdbId);
                    if (isPlayed) localTmdbWatched.Add(tmdbId);
                }

                // 1. Reconcile TMDb
                if (userConfig.EnableTmdbSync && !string.IsNullOrWhiteSpace(userConfig.TmdbSessionId) && tmdbId > 0)
                {
                    if (userConfig.SyncTmdbFavorites)
                    {
                        if (isFavorite && !remoteTmdbFavorites.Contains(tmdbId))
                        {
                            await entryPoint.TmdbClient.SetFavoriteAsync(userConfig.TmdbAccountId, userConfig.TmdbSessionId, tmdbId, true, tokenOrKey, cancellationToken).ConfigureAwait(false);
                            remoteTmdbFavorites.Add(tmdbId);

                            if (userConfig.TmdbFavoritesListId.HasValue && userConfig.TmdbFavoritesListId.Value > 0)
                            {
                                await entryPoint.TmdbClient.AddToWatchedListAsync(userConfig.TmdbFavoritesListId.Value, userConfig.TmdbSessionId, tmdbId, userConfig.TmdbV4AccessToken, tokenOrKey, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }

                    if (userConfig.SyncTmdbWatched && userConfig.TmdbWatchedListId.HasValue && userConfig.TmdbWatchedListId.Value > 0)
                    {
                        if (isPlayed && !remoteTmdbWatched.Contains(tmdbId))
                        {
                            await entryPoint.TmdbClient.AddToWatchedListAsync(userConfig.TmdbWatchedListId.Value, userConfig.TmdbSessionId, tmdbId, userConfig.TmdbV4AccessToken, tokenOrKey, cancellationToken).ConfigureAwait(false);
                            remoteTmdbWatched.Add(tmdbId);
                        }
                    }
                }

                // 2. Reconcile IMDb
                var effectiveImdbCookie = userConfig.GetEffectiveImdbCookie();
                var stateKey = movie.InternalId.ToString();
                userState.Movies.TryGetValue(stateKey, out var lastSynced);

                var imdbFavSynced = lastSynced?.ImdbFavoriteSynced ?? false;
                var imdbWatchSynced = lastSynced?.ImdbWatchedSynced ?? false;

                if (userConfig.EnableImdbSync && !string.IsNullOrWhiteSpace(effectiveImdbCookie) && !string.IsNullOrWhiteSpace(imdbId))
                {
                    if (userConfig.SyncImdbFavorites && !string.IsNullOrWhiteSpace(userConfig.ImdbFavoritesListId))
                    {
                        var favChanged = lastSynced == null || !imdbFavSynced || lastSynced.IsFavorite != isFavorite;
                        if (favChanged && isFavorite)
                        {
                            var res = await entryPoint.ImdbClient.AddItemToListAsync(embyUserId, effectiveImdbCookie, userConfig.ImdbFavoritesListId, imdbId, cancellationToken).ConfigureAwait(false);
                            if (res.AuthExpired)
                            {
                                userConfig.ImdbLastStatus = "Cookie expired - please refresh";
                                break; // Don't hammer IMDb if expired
                            }
                            if (res.Success)
                            {
                                imdbFavSynced = true;
                            }
                        }
                        else if (!isFavorite)
                        {
                            imdbFavSynced = false;
                        }
                    }

                    if (userConfig.SyncImdbWatched)
                    {
                        var playedChanged = lastSynced == null || !imdbWatchSynced || lastSynced.IsPlayed != isPlayed;
                        if (playedChanged && isPlayed)
                        {
                            var res = await entryPoint.ImdbClient.SetTitleWatchedAsync(embyUserId, effectiveImdbCookie, imdbId, true, cancellationToken).ConfigureAwait(false);
                            if (res.AuthExpired)
                            {
                                userConfig.ImdbLastStatus = "Cookie expired - please refresh";
                                break;
                            }
                            if (res.Success)
                            {
                                imdbWatchSynced = true;
                            }
                        }
                        else if (!isPlayed)
                        {
                            imdbWatchSynced = false;
                        }
                    }
                }

                // Update state record
                var mKey = movie.InternalId.ToString();
                userState.Movies[mKey] = new MovieSyncState
                {
                    InternalId = movie.InternalId,
                    Name = movie.Name,
                    TmdbId = tmdbId > 0 ? tmdbId.ToString() : tmdbIdStr,
                    ImdbId = imdbId,
                    IsFavorite = isFavorite,
                    IsPlayed = isPlayed,
                    ImdbFavoriteSynced = imdbFavSynced,
                    ImdbWatchedSynced = imdbWatchSynced,
                    LastSyncedUtc = DateTime.UtcNow
                };

                if (i % 10 == 0 || i == total - 1)
                {
                    progress?.Report((double)i / total * 100.0);
                }
            }

            // Optional mirror removals for TMDb if enabled
            if (config.MirrorRemovalsOnFullSync && userConfig.EnableTmdbSync && !string.IsNullOrWhiteSpace(userConfig.TmdbSessionId))
            {
                if (userConfig.SyncTmdbFavorites)
                {
                    var removals = remoteTmdbFavorites.Except(localTmdbFavorites).ToList();
                    foreach (var removeId in removals)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await entryPoint.TmdbClient.SetFavoriteAsync(userConfig.TmdbAccountId, userConfig.TmdbSessionId, removeId, false, tokenOrKey, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (userConfig.SyncTmdbWatched && userConfig.TmdbWatchedListId.HasValue && userConfig.TmdbWatchedListId.Value > 0)
                {
                    var removals = remoteTmdbWatched.Except(localTmdbWatched).ToList();
                    foreach (var removeId in removals)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await entryPoint.TmdbClient.RemoveFromWatchedListAsync(userConfig.TmdbWatchedListId.Value, userConfig.TmdbSessionId, removeId, userConfig.TmdbV4AccessToken, tokenOrKey, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            entryPoint.StateStore.SaveState(embyUserId, userState);
            progress?.Report(100.0);
            _logger.Info("[FullSync] Finished user sync for '{0}'", userConfig.EmbyUserName);
        }
    }
}
