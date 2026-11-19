using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.TmdbImdbSync.Configuration;
using Emby.TmdbImdbSync.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.TmdbImdbSync.Api
{
    public class SyncApiService : IService
    {
        private readonly ILogManager _logManager;
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ILogger _logger;

        public SyncApiService(ILogManager logManager, IUserManager userManager, ILibraryManager libraryManager, IUserDataManager userDataManager)
        {
            _logManager = logManager;
            _userManager = userManager;
            _libraryManager = libraryManager;
            _userDataManager = userDataManager;
            _logger = logManager.GetLogger("Emby.TmdbImdbSync.Api");
        }

        public object Get(GetConfigurationRequest request)
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            return config;
        }

        public void Post(UpdateConfigurationRequest request)
        {
            if (Plugin.Instance == null) return;

            var config = Plugin.Instance.Configuration;
            config.TmdbAppReadAccessToken = request.TmdbAppReadAccessToken ?? string.Empty;
            config.TmdbApiKey = request.TmdbApiKey ?? string.Empty;
            config.MirrorRemovalsOnFullSync = request.MirrorRemovalsOnFullSync;
            config.DebounceSeconds = request.DebounceSeconds > 0 ? request.DebounceSeconds : 2;
            config.Users = request.Users ?? new System.Collections.Generic.List<UserSyncConfig>();

            Plugin.Instance.SaveConfiguration();
            _logger.Info("[SyncApiService] Configuration successfully saved.");
        }

        public async Task<object> Post(TmdbLoginRequest request)
        {
            var entryPoint = SyncEntryPoint.Instance;
            if (entryPoint?.TmdbClient == null)
            {
                return new TmdbLoginResponse { Success = false, ErrorMessage = "TMDb service not initialized." };
            }

            var config = Plugin.Instance?.Configuration;
            var tokenOrKey = !string.IsNullOrWhiteSpace(request.AppReadTokenOrKey)
                ? request.AppReadTokenOrKey.Trim()
                : (!string.IsNullOrWhiteSpace(config?.TmdbAppReadAccessToken) ? config.TmdbAppReadAccessToken.Trim() : TmdbClient.DefaultApiKey);

            var result = await entryPoint.TmdbClient.LoginWithCredentialsAsync(request.Username, request.Password, tokenOrKey, CancellationToken.None).ConfigureAwait(false);

            if (result.Success && config != null)
            {
                if (!string.IsNullOrWhiteSpace(request.AppReadTokenOrKey))
                {
                    config.TmdbAppReadAccessToken = request.AppReadTokenOrKey.Trim();
                }
                var userConfig = FindUserConfig(config, request.EmbyUserId);
                if (userConfig != null)
                {
                    userConfig.TmdbUsername = result.Username;
                    userConfig.TmdbSessionId = result.SessionId;
                    userConfig.TmdbAccountId = result.AccountId;
                    if (result.WatchedListId.HasValue && (!userConfig.TmdbWatchedListId.HasValue || userConfig.TmdbWatchedListId == 0))
                    {
                        userConfig.TmdbWatchedListId = result.WatchedListId;
                    }
                    if (result.FavoritesListId.HasValue && (!userConfig.TmdbFavoritesListId.HasValue || userConfig.TmdbFavoritesListId == 0))
                    {
                        userConfig.TmdbFavoritesListId = result.FavoritesListId;
                    }
                    Plugin.Instance?.SaveConfiguration();
                }
            }

            return new TmdbLoginResponse
            {
                Success = result.Success,
                SessionId = result.SessionId,
                AccountId = result.AccountId,
                WatchedListId = result.WatchedListId,
                FavoritesListId = result.FavoritesListId,
                ErrorMessage = result.ErrorMessage
            };
        }

        public async Task<object> Post(TmdbTestRequest request)
        {
            var entryPoint = SyncEntryPoint.Instance;
            if (entryPoint?.TmdbClient == null)
            {
                return new TestResultResponse { Success = false, StatusMessage = "TMDb client not ready." };
            }

            var config = Plugin.Instance?.Configuration;
            var user = FindUserConfig(config, request.EmbyUserId);
            if (user == null || string.IsNullOrWhiteSpace(user.TmdbSessionId))
            {
                return new TestResultResponse { Success = false, StatusMessage = "User has no active TMDb session. Please log in first." };
            }

            var tokenOrKey = config?.TmdbAppReadAccessToken ?? string.Empty;
            var ok = await entryPoint.TmdbClient.TestConnectionAsync(user.TmdbSessionId, user.TmdbAccountId, user.TmdbWatchedListId, tokenOrKey, CancellationToken.None).ConfigureAwait(false);

            return new TestResultResponse
            {
                Success = ok,
                StatusMessage = ok ? "TMDb connection verified successfully!" : "Failed to verify TMDb session. Please check credentials.",
                Details = $"Account ID: {user.TmdbAccountId}, Watched List ID: {user.TmdbWatchedListId}"
            };
        }

        public async Task<object> Post(ImdbCreateFavoritesListRequest request)
        {
            var entryPoint = SyncEntryPoint.Instance;
            if (entryPoint?.ImdbClient == null)
            {
                return new ImdbCreateListResponse { Success = false, ErrorMessage = "IMDb client not ready." };
            }

            var config = Plugin.Instance?.Configuration;
            var user = FindUserConfig(config, request.EmbyUserId);
            var cookie = user?.GetEffectiveImdbCookie();
            if (user == null || string.IsNullOrWhiteSpace(cookie))
            {
                return new ImdbCreateListResponse { Success = false, ErrorMessage = "IMDb cookie is missing for this user." };
            }

            var res = await entryPoint.ImdbClient.CreateListAsync(user.EmbyUserId, cookie, request.ListName, CancellationToken.None).ConfigureAwait(false);
            if (res.Success && !string.IsNullOrEmpty(res.Data))
            {
                user.ImdbFavoritesListId = res.Data;
                Plugin.Instance?.SaveConfiguration();
                return new ImdbCreateListResponse { Success = true, ListId = res.Data };
            }

            return new ImdbCreateListResponse
            {
                Success = false,
                ErrorMessage = res.ErrorMessage
            };
        }

        public async Task<object> Post(ImdbTestRequest request)
        {
            var entryPoint = SyncEntryPoint.Instance;
            if (entryPoint?.ImdbClient == null)
            {
                return new TestResultResponse { Success = false, StatusMessage = "IMDb client not ready." };
            }

            var config = Plugin.Instance?.Configuration;
            var user = FindUserConfig(config, request.EmbyUserId);
            var cookie = user?.GetEffectiveImdbCookie();
            if (user == null || string.IsNullOrWhiteSpace(cookie))
            {
                return new TestResultResponse { Success = false, StatusMessage = "No IMDb cookie configured for this user." };
            }

            var res = await entryPoint.ImdbClient.TestAuthAsync(user.EmbyUserId, cookie, CancellationToken.None).ConfigureAwait(false);
            user.ImdbLastCheckedUtc = DateTime.UtcNow;
            if (res.Success)
            {
                user.ImdbLastStatus = "OK (Authenticated)";
            }
            else if (res.AuthExpired)
            {
                user.ImdbLastStatus = "Cookie expired - please refresh";
            }
            else
            {
                user.ImdbLastStatus = $"Error: {res.ErrorMessage}";
            }

            Plugin.Instance?.SaveConfiguration();

            return new TestResultResponse
            {
                Success = res.Success,
                StatusMessage = user.ImdbLastStatus,
                Details = res.ErrorMessage
            };
        }

        public object Post(SyncNowRequest request)
        {
            var entryPoint = SyncEntryPoint.Instance;
            if (entryPoint == null)
            {
                return new SyncNowResponse { Success = false, Message = "Sync service is not available." };
            }

            _ = Task.Run(async () =>
            {
                var task = new Tasks.FullSyncScheduledTask(_libraryManager, _userManager, _logManager, _userDataManager);
                await task.ExecuteUserSyncAsync(request.EmbyUserId, CancellationToken.None, null).ConfigureAwait(false);
            });

            return new SyncNowResponse
            {
                Success = true,
                Message = "Immediate sync initiated in background."
            };
        }

        private UserSyncConfig? FindUserConfig(PluginConfiguration? config, string? userId)
        {
            if (config?.Users == null || string.IsNullOrWhiteSpace(userId)) return null;

            var user = config.Users.FirstOrDefault(u => string.Equals(u.EmbyUserId, userId, StringComparison.OrdinalIgnoreCase));
            if (user != null) return user;

            User? embyUser = null;
            if (Guid.TryParse(userId, out var userGuid))
            {
                embyUser = _userManager.GetUserById(userGuid);
            }
            if (embyUser == null)
            {
                embyUser = _userManager.GetUserById(userId);
            }

            if (embyUser != null)
            {
                user = config.Users.FirstOrDefault(u =>
                    string.Equals(u.EmbyUserName, embyUser.Name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(u.EmbyUserId, embyUser.Id.ToString("N"), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(u.EmbyUserId, embyUser.Id.ToString("D"), StringComparison.OrdinalIgnoreCase));
            }

            return user;
        }
    }
}
