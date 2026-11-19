using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.TmdbImdbSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.TmdbImdbSync.Services
{
    public class SyncEntryPoint : IServerEntryPoint
    {
        private readonly IUserDataManager _userDataManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly ILogManager _logManager;
        private readonly IApplicationPaths _applicationPaths;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogger _logger;

        private HttpClient? _httpClient;
        private CancellationTokenSource? _cts;
        private Task? _workerTask;

        // Debounce storage: key = "userId:movieInternalId", value = SyncWorkItem
        private readonly ConcurrentDictionary<string, SyncWorkItem> _queue = new ConcurrentDictionary<string, SyncWorkItem>();

        public static SyncEntryPoint? Instance { get; private set; }

        public TmdbClient? TmdbClient { get; private set; }
        public ImdbClient? ImdbClient { get; private set; }
        public SyncStateStore? StateStore { get; private set; }
        public SyncOrchestrator? Orchestrator { get; private set; }
        public ImdbQueryTemplates QueryTemplates { get; private set; } = new ImdbQueryTemplates();

        public SyncEntryPoint(
            IUserDataManager userDataManager,
            ILibraryManager libraryManager,
            IUserManager userManager,
            ILogManager logManager,
            IApplicationPaths applicationPaths,
            IJsonSerializer jsonSerializer)
        {
            _userDataManager = userDataManager;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _logManager = logManager;
            _applicationPaths = applicationPaths;
            _jsonSerializer = jsonSerializer;
            _logger = logManager.GetLogger("Emby.TmdbImdbSync");

            Instance = this;
        }

        public void Run()
        {
            _logger.Info("[TmdbImdbSync] Starting TMDb & IMDb Sync EntryPoint...");

            // Critical security: UseCookies = false ensures per-user IMDb cookies never bleed across requests/users
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            var dataFolderPath = Plugin.Instance?.DataFolderPath ?? System.IO.Path.Combine(_applicationPaths.DataPath, "TmdbImdbSync");
            QueryTemplates = ImdbQueryTemplates.Load(dataFolderPath, _logger);

            StateStore = new SyncStateStore(dataFolderPath, _jsonSerializer, _logger);
            TmdbClient = new TmdbClient(_httpClient, _logger);
            ImdbClient = new ImdbClient(_httpClient, _logger, () => QueryTemplates);
            Orchestrator = new SyncOrchestrator(TmdbClient, ImdbClient, StateStore, _logger);

            _cts = new CancellationTokenSource();
            _workerTask = Task.Run(() => WorkerLoopAsync(_cts.Token));

            _userDataManager.UserDataSaved += OnUserDataSaved;
            _logger.Info("[TmdbImdbSync] Successfully subscribed to UserDataSaved events.");
        }

        private void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            // Fast zero-I/O guards
            if (e == null || e.Item == null || e.UserData == null || e.User == null)
            {
                return;
            }

            // Only movie items, skip extras/trailers
            if (!(e.Item is Movie movie) || movie.ExtraType != null)
            {
                return;
            }

            // Ignore playback progress updates
            if (e.SaveReason == UserDataSaveReason.PlaybackProgress)
            {
                return;
            }

            // React only to meaningful status change events
            if (e.SaveReason != UserDataSaveReason.PlaybackFinished &&
                e.SaveReason != UserDataSaveReason.TogglePlayed &&
                e.SaveReason != UserDataSaveReason.UpdateUserRating &&
                e.SaveReason != UserDataSaveReason.Import)
            {
                return;
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null || config.Users == null)
            {
                return;
            }

            var userGuidStr = e.User.Id.ToString("N");
            var userGuidHyphen = e.User.Id.ToString("D");
            var userConfig = config.Users.FirstOrDefault(u =>
                string.Equals(u.EmbyUserId, userGuidStr, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(u.EmbyUserId, userGuidHyphen, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(u.EmbyUserName) && string.Equals(u.EmbyUserName, e.User.Name, StringComparison.OrdinalIgnoreCase)));

            if (userConfig == null || (!userConfig.EnableTmdbSync && !userConfig.EnableImdbSync))
            {
                return;
            }

            var tmdbId = movie.GetProviderId(MetadataProviders.Tmdb);
            var imdbId = movie.GetProviderId(MetadataProviders.Imdb);

            var workItem = new SyncWorkItem
            {
                EmbyUserId = userConfig.EmbyUserId,
                MovieInternalId = movie.InternalId,
                MovieName = movie.Name,
                TmdbId = tmdbId,
                ImdbId = imdbId,
                IsFavorite = e.UserData.IsFavorite,
                IsPlayed = e.UserData.Played,
                TimestampUtc = DateTime.UtcNow
            };

            var key = $"{workItem.EmbyUserId}:{workItem.MovieInternalId}";
            _queue.AddOrUpdate(key, workItem, (_, __) => workItem);
        }

        private async Task WorkerLoopAsync(CancellationToken cancellationToken)
        {
            _logger.Info("[TmdbImdbSync] Background debounce queue worker started.");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);

                    var config = Plugin.Instance?.Configuration;
                    var debounceWindow = TimeSpan.FromSeconds(config?.DebounceSeconds ?? 2);
                    var now = DateTime.UtcNow;

                    var keys = _queue.Keys.ToList();
                    foreach (var key in keys)
                    {
                        if (_queue.TryGetValue(key, out var item))
                        {
                            if (now - item.TimestampUtc >= debounceWindow)
                            {
                                // Remove and process
                                if (_queue.TryRemove(key, out var readyItem) && Orchestrator != null)
                                {
                                    await Orchestrator.ProcessDebouncedItemAsync(readyItem, cancellationToken).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("[TmdbImdbSync] Error in queue worker loop", ex);
                }
            }

            _logger.Info("[TmdbImdbSync] Background debounce queue worker exited.");
        }

        public void Dispose()
        {
            try
            {
                _userDataManager.UserDataSaved -= OnUserDataSaved;
            }
            catch { }

            try
            {
                _cts?.Cancel();
                _workerTask?.Wait(TimeSpan.FromSeconds(3));
                _cts?.Dispose();
            }
            catch { }

            try
            {
                _httpClient?.Dispose();
            }
            catch { }

            Instance = null;
        }
    }
}
