using System.Collections.Generic;
using Emby.TmdbImdbSync.Configuration;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.TmdbImdbSync.Api
{
    [Route("/TmdbImdbSync/Configuration", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetConfigurationRequest : IReturn<PluginConfiguration>
    {
    }

    [Route("/TmdbImdbSync/Configuration", "POST")]
    [Authenticated(Roles = "Admin")]
    public class UpdateConfigurationRequest : PluginConfiguration, IReturnVoid
    {
    }

    [Route("/TmdbImdbSync/Tmdb/Login", "POST")]
    [Authenticated(Roles = "Admin")]
    public class TmdbLoginRequest : IReturn<TmdbLoginResponse>
    {
        public string EmbyUserId { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class TmdbLoginResponse
    {
        public bool Success { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public int AccountId { get; set; }
        public int? WatchedListId { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    [Route("/TmdbImdbSync/Tmdb/Test", "POST")]
    [Authenticated(Roles = "Admin")]
    public class TmdbTestRequest : IReturn<TestResultResponse>
    {
        public string EmbyUserId { get; set; } = string.Empty;
    }

    [Route("/TmdbImdbSync/Imdb/CreateFavoritesList", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ImdbCreateFavoritesListRequest : IReturn<ImdbCreateListResponse>
    {
        public string EmbyUserId { get; set; } = string.Empty;
        public string ListName { get; set; } = "Emby Favorites";
    }

    public class ImdbCreateListResponse
    {
        public bool Success { get; set; }
        public string ListId { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
    }

    [Route("/TmdbImdbSync/Imdb/Test", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ImdbTestRequest : IReturn<TestResultResponse>
    {
        public string EmbyUserId { get; set; } = string.Empty;
    }

    [Route("/TmdbImdbSync/SyncNow", "POST")]
    [Authenticated(Roles = "Admin")]
    public class SyncNowRequest : IReturn<SyncNowResponse>
    {
        public string EmbyUserId { get; set; } = string.Empty;
    }

    public class TestResultResponse
    {
        public bool Success { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
    }

    public class SyncNowResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
