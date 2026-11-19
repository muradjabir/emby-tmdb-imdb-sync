using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.TmdbImdbSync.Services
{
    public class ImdbClientResult<T>
    {
        public bool Success { get; set; }
        public T? Data { get; set; }
        public bool AuthExpired { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    public class ImdbClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly Func<ImdbQueryTemplates> _templatesProvider;
        private const string GraphQlEndpoint = "https://api.graphql.imdb.com/";

        // Per-user polite pacing: minimum 750ms between calls
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _userSemaphores = new ConcurrentDictionary<string, SemaphoreSlim>();
        private readonly ConcurrentDictionary<string, DateTime> _lastUserCallUtc = new ConcurrentDictionary<string, DateTime>();

        public ImdbClient(HttpClient httpClient, ILogger logger, Func<ImdbQueryTemplates> templatesProvider)
        {
            _httpClient = httpClient;
            _logger = logger;
            _templatesProvider = templatesProvider;
        }

        public static string CleanCookie(string cookie)
        {
            if (string.IsNullOrWhiteSpace(cookie)) return string.Empty;
            var cleaned = cookie.Replace('\r', ' ').Replace('\n', ' ').Replace('\u2028', ' ').Replace('\u2029', ' ');
            return System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();
        }

        private HttpRequestMessage CreateRequest(string cookie, object payload)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, GraphQlEndpoint);

            // Per-request headers - isolated so cookies never bleed across users
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                request.Headers.TryAddWithoutValidation("Cookie", CleanCookie(cookie));
            }

            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("x-imdb-client-name", "imdb-web-next-localized");
            request.Headers.TryAddWithoutValidation("Origin", "https://www.imdb.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.imdb.com/");
            request.Headers.TryAddWithoutValidation("Accept", "application/graphql+json, application/json");

            var json = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            return request;
        }

        private async Task EnforcePacingAsync(string userKey, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(userKey)) return;

            if (_lastUserCallUtc.TryGetValue(userKey, out var lastCall))
            {
                var elapsed = DateTime.UtcNow - lastCall;
                var minSpacing = TimeSpan.FromMilliseconds(750);
                if (elapsed < minSpacing)
                {
                    await Task.Delay(minSpacing - elapsed, cancellationToken).ConfigureAwait(false);
                }
            }

            _lastUserCallUtc[userKey] = DateTime.UtcNow;
        }

        public async Task<ImdbClientResult<JsonDocument>> ExecuteGraphQlAsync(string userKey, string cookie, string query, string operationName, object? variables, CancellationToken cancellationToken)
        {
            var userLock = _userSemaphores.GetOrAdd(userKey, _ => new SemaphoreSlim(1, 1));
            await userLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await EnforcePacingAsync(userKey, cancellationToken).ConfigureAwait(false);

                var payload = new
                {
                    query,
                    operationName,
                    variables = variables ?? new { }
                };

                const int maxRetries = 3;
                var retryDelay = TimeSpan.FromSeconds(1);

                for (var attempt = 1; attempt <= maxRetries; attempt++)
                {
                    using var request = CreateRequest(cookie, payload);
                    using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        _logger.Warn("[ImdbClient] IMDb cookie expired or unauthorized (HTTP {0}) for user key {1}", (int)response.StatusCode, userKey);
                        return new ImdbClientResult<JsonDocument>
                        {
                            Success = false,
                            AuthExpired = true,
                            ErrorMessage = $"IMDb authentication expired (HTTP {(int)response.StatusCode})."
                        };
                    }

                    if (response.StatusCode == (HttpStatusCode)429)
                    {
                        var retryAfter = response.Headers.RetryAfter?.Delta ?? retryDelay;
                        _logger.Warn("[ImdbClient] IMDb 429 Rate Limit, backing off for {0}s (attempt {1}/{2})", retryAfter.TotalSeconds, attempt, maxRetries);
                        await Task.Delay(retryAfter, cancellationToken).ConfigureAwait(false);
                        retryDelay = TimeSpan.FromSeconds(retryDelay.TotalSeconds * 2);
                        continue;
                    }

                    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.Error("[ImdbClient] GraphQL HTTP Error {0}: {1}", (int)response.StatusCode, responseBody);
                        return new ImdbClientResult<JsonDocument>
                        {
                            Success = false,
                            ErrorMessage = $"IMDb HTTP Error {(int)response.StatusCode}: {responseBody}"
                        };
                    }

                    var doc = JsonDocument.Parse(responseBody);
                    if (doc.RootElement.TryGetProperty("errors", out var errorsProp) && errorsProp.ValueKind == JsonValueKind.Array && errorsProp.GetArrayLength() > 0)
                    {
                        var errStr = errorsProp.ToString();
                        var isAuth = errStr.IndexOf("UNAUTHENTICATED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     errStr.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     errStr.IndexOf("NOT_AUTHENTICATED", StringComparison.OrdinalIgnoreCase) >= 0;

                        if (isAuth)
                        {
                            _logger.Warn("[ImdbClient] GraphQL reported authentication error: {0}", errStr);
                            return new ImdbClientResult<JsonDocument>
                            {
                                Success = false,
                                AuthExpired = true,
                                ErrorMessage = "IMDb cookie expired / invalid.",
                                Data = doc
                            };
                        }

                        _logger.Warn("[ImdbClient] GraphQL errors: {0}", errStr);
                        return new ImdbClientResult<JsonDocument>
                        {
                            Success = false,
                            ErrorMessage = errStr,
                            Data = doc
                        };
                    }

                    return new ImdbClientResult<JsonDocument>
                    {
                        Success = true,
                        Data = doc
                    };
                }

                return new ImdbClientResult<JsonDocument>
                {
                    Success = false,
                    ErrorMessage = "Exceeded maximum retry attempts for IMDb GraphQL."
                };
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[ImdbClient] Exception in GraphQL call {0}", ex, operationName);
                return new ImdbClientResult<JsonDocument>
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
            finally
            {
                userLock.Release();
            }
        }

        public async Task<ImdbClientResult<string>> CreateListAsync(string userKey, string cookie, string listName, CancellationToken cancellationToken)
        {
            var templates = _templatesProvider();
            var variables = new
            {
                input = new
                {
                    name = string.IsNullOrWhiteSpace(listName) ? "Emby Favorites" : listName,
                    listDescription = "Synced from Emby",
                    allowDuplicates = false,
                    listType = "TITLES",
                    visibility = "PRIVATE"
                }
            };

            var res = await ExecuteGraphQlAsync(userKey, cookie, templates.CreateListMutation, "CreateList", variables, cancellationToken).ConfigureAwait(false);
            if (!res.Success)
            {
                return new ImdbClientResult<string>
                {
                    Success = false,
                    AuthExpired = res.AuthExpired,
                    ErrorMessage = res.ErrorMessage
                };
            }

            try
            {
                if (res.Data != null &&
                    res.Data.RootElement.TryGetProperty("data", out var dataProp) &&
                    dataProp.TryGetProperty("createList", out var createListProp) &&
                    createListProp.TryGetProperty("listId", out var listIdProp))
                {
                    var listId = listIdProp.GetString();
                    return new ImdbClientResult<string> { Success = true, Data = listId };
                }
            }
            catch (Exception ex)
            {
                return new ImdbClientResult<string> { Success = false, ErrorMessage = ex.Message };
            }

            return new ImdbClientResult<string> { Success = false, ErrorMessage = "Could not parse listId from response." };
        }

        public async Task<ImdbClientResult<bool>> AddItemToListAsync(string userKey, string cookie, string listId, string imdbId, CancellationToken cancellationToken)
        {
            var templates = _templatesProvider();
            var variables = new
            {
                input = new
                {
                    listId,
                    item = new
                    {
                        itemElementId = imdbId
                    }
                }
            };

            var res = await ExecuteGraphQlAsync(userKey, cookie, templates.AddItemToListMutation, "AddItemToList", variables, cancellationToken).ConfigureAwait(false);
            return new ImdbClientResult<bool>
            {
                Success = res.Success,
                Data = res.Success,
                AuthExpired = res.AuthExpired,
                ErrorMessage = res.ErrorMessage
            };
        }

        public async Task<ImdbClientResult<bool>> RemoveItemFromListAsync(string userKey, string cookie, string listId, string imdbId, CancellationToken cancellationToken)
        {
            var templates = _templatesProvider();
            var variables = new
            {
                input = new
                {
                    listId,
                    itemElementId = imdbId
                }
            };

            var res = await ExecuteGraphQlAsync(userKey, cookie, templates.DeleteListItemMutation, "RemoveElementFromList", variables, cancellationToken).ConfigureAwait(false);
            return new ImdbClientResult<bool>
            {
                Success = res.Success,
                Data = res.Success,
                AuthExpired = res.AuthExpired,
                ErrorMessage = res.ErrorMessage
            };
        }

        public async Task<ImdbClientResult<bool>> SetTitleWatchedAsync(string userKey, string cookie, string imdbId, bool isWatched, CancellationToken cancellationToken)
        {
            var templates = _templatesProvider();
            var query = isWatched ? templates.AddWatchedTitleMutation : templates.RemoveWatchedTitleMutation;
            var operationName = isWatched ? "AddWatched" : "RemoveWatched";
            var variables = new
            {
                titleId = imdbId
            };

            var res = await ExecuteGraphQlAsync(userKey, cookie, query, operationName, variables, cancellationToken).ConfigureAwait(false);
            return new ImdbClientResult<bool>
            {
                Success = res.Success,
                Data = res.Success,
                AuthExpired = res.AuthExpired,
                ErrorMessage = res.ErrorMessage
            };
        }

        public async Task<ImdbClientResult<bool>> TestAuthAsync(string userKey, string cookie, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(cookie))
            {
                return new ImdbClientResult<bool>
                {
                    Success = false,
                    ErrorMessage = "Cookie string is empty."
                };
            }

            var templates = _templatesProvider();
            var res = await ExecuteGraphQlAsync(userKey, cookie, templates.CheckAuthQuery, "CheckAuth", new { }, cancellationToken).ConfigureAwait(false);

            if (res.AuthExpired)
            {
                return new ImdbClientResult<bool>
                {
                    Success = false,
                    AuthExpired = true,
                    ErrorMessage = "IMDb cookie is expired or unauthorized."
                };
            }

            if (!res.Success)
            {
                return new ImdbClientResult<bool>
                {
                    Success = false,
                    ErrorMessage = res.ErrorMessage
                };
            }

            if (res.Data != null &&
                res.Data.RootElement.TryGetProperty("data", out var dataProp) &&
                dataProp.TryGetProperty("user", out var userProp) &&
                userProp.ValueKind == JsonValueKind.Null)
            {
                return new ImdbClientResult<bool>
                {
                    Success = false,
                    AuthExpired = true,
                    ErrorMessage = "IMDb cookie is invalid or expired (session not recognized)."
                };
            }

            return new ImdbClientResult<bool> { Success = true, Data = true };
        }
    }
}
