using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.TmdbImdbSync.Services
{
    public class TmdbAuthResult
    {
        public bool Success { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public int AccountId { get; set; }
        public string Username { get; set; } = string.Empty;
        public int? WatchedListId { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    public class TmdbClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private const string BaseUrl = "https://api.themoviedb.org";

        public const string DefaultApiKey = "f6bd687ffa63cd282b6ff2c6877f2669";

        public TmdbClient(HttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string path, string tokenOrKey)
        {
            var uri = new Uri($"{BaseUrl}{path}");
            var request = new HttpRequestMessage(method, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var effectiveTokenOrKey = string.IsNullOrWhiteSpace(tokenOrKey) ? DefaultApiKey : tokenOrKey.Trim();

            // If it looks like a JWT / v4 token (long), use Bearer header
            if (effectiveTokenOrKey.Length > 40)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", effectiveTokenOrKey);
            }
            else
            {
                // Otherwise v3 api_key query param (avoid duplicating if already present in URI)
                if (!uri.Query.Contains("api_key="))
                {
                    var separator = uri.Query.Length > 0 ? "&" : "?";
                    request.RequestUri = new Uri($"{BaseUrl}{path}{separator}api_key={effectiveTokenOrKey}");
                }
            }

            return request;
        }

        private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const int maxRetries = 3;
            var retryDelay = TimeSpan.FromSeconds(1);

            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    // Clone request for potential retry
                    var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                    if (response.StatusCode == (HttpStatusCode)429)
                    {
                        var retryAfter = response.Headers.RetryAfter?.Delta ?? retryDelay;
                        _logger.Warn("[TmdbClient] 429 Rate limit hit, backing off for {0}s (attempt {1}/{2})", retryAfter.TotalSeconds, attempt, maxRetries);
                        await Task.Delay(retryAfter, cancellationToken).ConfigureAwait(false);
                        retryDelay = TimeSpan.FromSeconds(retryDelay.TotalSeconds * 2);

                        // Recreate request because HttpRequestMessage cannot be resent
                        request = CloneRequest(request);
                        continue;
                    }

                    return response;
                }
                catch (HttpRequestException ex) when (attempt < maxRetries)
                {
                    _logger.Warn("[TmdbClient] Network error: {0}, retrying in {1}s...", ex.Message, retryDelay.TotalSeconds);
                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                    retryDelay = TimeSpan.FromSeconds(retryDelay.TotalSeconds * 2);
                    request = CloneRequest(request);
                }
            }

            return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage original)
        {
            var clone = new HttpRequestMessage(original.Method, original.RequestUri);
            foreach (var header in original.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            if (original.Content != null)
            {
                var contentBytes = original.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                var byteContent = new ByteArrayContent(contentBytes);
                foreach (var contentHeader in original.Content.Headers)
                {
                    byteContent.Headers.TryAddWithoutValidation(contentHeader.Key, contentHeader.Value);
                }
                clone.Content = byteContent;
            }
            return clone;
        }

        public async Task<TmdbAuthResult> LoginWithCredentialsAsync(string username, string password, string tokenOrKey, CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    return new TmdbAuthResult { Success = false, ErrorMessage = "Username and password cannot be empty." };
                }

                // Step 1: Create request token
                var step1Req = CreateRequest(HttpMethod.Get, "/3/authentication/token/new", tokenOrKey);
                using var step1Res = await SendWithRetryAsync(step1Req, cancellationToken).ConfigureAwait(false);
                var step1Json = await step1Res.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!step1Res.IsSuccessStatusCode)
                {
                    return new TmdbAuthResult { Success = false, ErrorMessage = $"Failed to get TMDb request token: {step1Json}" };
                }

                using var doc1 = JsonDocument.Parse(step1Json);
                var requestToken = doc1.RootElement.GetProperty("request_token").GetString();
                if (string.IsNullOrEmpty(requestToken))
                {
                    return new TmdbAuthResult { Success = false, ErrorMessage = "TMDb did not return a valid request token." };
                }

                // Step 2: Validate token with username and password
                var step2Req = CreateRequest(HttpMethod.Post, "/3/authentication/token/validate_with_login", tokenOrKey);
                var validatePayload = JsonSerializer.Serialize(new
                {
                    username,
                    password,
                    request_token = requestToken
                });
                step2Req.Content = new StringContent(validatePayload, Encoding.UTF8, "application/json");

                using var step2Res = await SendWithRetryAsync(step2Req, cancellationToken).ConfigureAwait(false);
                var step2Json = await step2Res.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!step2Res.IsSuccessStatusCode)
                {
                    return new TmdbAuthResult { Success = false, ErrorMessage = $"TMDb login failed: Invalid username or password." };
                }

                // Step 3: Create session
                var step3Req = CreateRequest(HttpMethod.Post, "/3/authentication/session/new", tokenOrKey);
                var sessionPayload = JsonSerializer.Serialize(new { request_token = requestToken });
                step3Req.Content = new StringContent(sessionPayload, Encoding.UTF8, "application/json");

                using var step3Res = await SendWithRetryAsync(step3Req, cancellationToken).ConfigureAwait(false);
                var step3Json = await step3Res.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!step3Res.IsSuccessStatusCode)
                {
                    return new TmdbAuthResult { Success = false, ErrorMessage = $"Failed to create TMDb session: {step3Json}" };
                }

                using var doc3 = JsonDocument.Parse(step3Json);
                var sessionId = doc3.RootElement.GetProperty("session_id").GetString();
                if (string.IsNullOrEmpty(sessionId))
                {
                    return new TmdbAuthResult { Success = false, ErrorMessage = "TMDb did not return a session ID." };
                }

                // Step 4: Get Account details
                var step4Req = CreateRequest(HttpMethod.Get, $"/3/account?session_id={sessionId}", tokenOrKey);
                using var step4Res = await SendWithRetryAsync(step4Req, cancellationToken).ConfigureAwait(false);
                var step4Json = await step4Res.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!step4Res.IsSuccessStatusCode)
                {
                    return new TmdbAuthResult { Success = false, ErrorMessage = $"Failed to fetch TMDb account: {step4Json}" };
                }

                using var doc4 = JsonDocument.Parse(step4Json);
                var accountId = doc4.RootElement.GetProperty("id").GetInt32();
                var accountUsername = doc4.RootElement.TryGetProperty("username", out var u) ? u.GetString() ?? username : username;

                // Step 5: Check/Create "Emby Watched Movies" List
                int? watchedListId = await EnsureWatchedListExistsAsync(accountId, sessionId, tokenOrKey, cancellationToken).ConfigureAwait(false);

                return new TmdbAuthResult
                {
                    Success = true,
                    SessionId = sessionId,
                    AccountId = accountId,
                    Username = accountUsername,
                    WatchedListId = watchedListId
                };
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[TmdbClient] Unexpected error during login", ex);
                return new TmdbAuthResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        public async Task<int?> EnsureWatchedListExistsAsync(int accountId, string sessionId, string tokenOrKey, CancellationToken cancellationToken)
        {
            try
            {
                // Step A: Check if "Emby Watched Movies" list already exists for this account
                if (accountId > 0)
                {
                    try
                    {
                        var checkReq = CreateRequest(HttpMethod.Get, $"/3/account/{accountId}/lists?session_id={sessionId}", tokenOrKey);
                        using var checkRes = await SendWithRetryAsync(checkReq, cancellationToken).ConfigureAwait(false);
                        if (checkRes.IsSuccessStatusCode)
                        {
                            var checkJson = await checkRes.Content.ReadAsStringAsync().ConfigureAwait(false);
                            using var checkDoc = JsonDocument.Parse(checkJson);
                            if (checkDoc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var list in results.EnumerateArray())
                                {
                                    if (list.TryGetProperty("name", out var nameProp) &&
                                        string.Equals(nameProp.GetString(), "Emby Watched Movies", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (list.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var existingId))
                                        {
                                            _logger.Info("[TmdbClient] Found existing TMDb watched list '{0}' with ID {1}", nameProp.GetString(), existingId);
                                            return existingId;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("[TmdbClient] Error querying existing TMDb lists: {0}", ex.Message);
                    }
                }

                // Step B: Create a v3 list
                var req = CreateRequest(HttpMethod.Post, $"/3/list?session_id={sessionId}", tokenOrKey);
                var payload = JsonSerializer.Serialize(new
                {
                    name = "Emby Watched Movies",
                    description = "Movies marked as watched in Emby",
                    language = "en"
                });
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using var res = await SendWithRetryAsync(req, cancellationToken).ConfigureAwait(false);
                var resJson = await res.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (res.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(resJson);
                    if (doc.RootElement.TryGetProperty("list_id", out var listIdProp))
                    {
                        if (listIdProp.ValueKind == JsonValueKind.Number)
                        {
                            return listIdProp.GetInt32();
                        }
                        if (listIdProp.ValueKind == JsonValueKind.String && int.TryParse(listIdProp.GetString(), out var parsedId))
                        {
                            return parsedId;
                        }
                    }
                }
                else
                {
                    _logger.Warn("[TmdbClient] Watched list creation response: {0}", resJson);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("[TmdbClient] Could not automatically create watched list: {0}", ex.Message);
            }

            return null;
        }

        public Task<int?> EnsureWatchedListExistsAsync(string sessionId, string tokenOrKey, CancellationToken cancellationToken)
            => EnsureWatchedListExistsAsync(0, sessionId, tokenOrKey, cancellationToken);

        public async Task<bool> SetFavoriteAsync(int accountId, string sessionId, int tmdbMovieId, bool isFavorite, string tokenOrKey, CancellationToken cancellationToken)
        {
            try
            {
                var req = CreateRequest(HttpMethod.Post, $"/3/account/{accountId}/favorite?session_id={sessionId}", tokenOrKey);
                var payload = JsonSerializer.Serialize(new
                {
                    media_type = "movie",
                    media_id = tmdbMovieId,
                    favorite = isFavorite
                });
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using var res = await SendWithRetryAsync(req, cancellationToken).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    var resJson = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    _logger.Error("[TmdbClient] SetFavorite failed for movie {0}: {1}", tmdbMovieId, resJson);
                    return false;
                }

                _logger.Info("[TmdbClient] Successfully set TMDb favorite={0} for movie {1}", isFavorite, tmdbMovieId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[TmdbClient] Error setting TMDb favorite for movie {0}", ex, tmdbMovieId);
                return false;
            }
        }

        public async Task<bool> AddToWatchedListAsync(int listId, string sessionId, int tmdbMovieId, string? v4AccessToken, string tokenOrKey, CancellationToken cancellationToken)
        {
            try
            {
                // Prefer v4 if user token provided
                if (!string.IsNullOrWhiteSpace(v4AccessToken))
                {
                    var req = CreateRequest(HttpMethod.Post, $"/4/list/{listId}/items", v4AccessToken);
                    var payload = JsonSerializer.Serialize(new
                    {
                        items = new[] { new { media_type = "movie", media_id = tmdbMovieId } }
                    });
                    req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                    using var res = await SendWithRetryAsync(req, cancellationToken).ConfigureAwait(false);
                    return res.IsSuccessStatusCode;
                }
                else
                {
                    // v3 fallback
                    var req = CreateRequest(HttpMethod.Post, $"/3/list/{listId}/add_item?session_id={sessionId}", tokenOrKey);
                    var payload = JsonSerializer.Serialize(new { media_id = tmdbMovieId });
                    req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                    using var res = await SendWithRetryAsync(req, cancellationToken).ConfigureAwait(false);
                    return res.IsSuccessStatusCode;
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[TmdbClient] Error adding movie {0} to TMDb list {1}", ex, tmdbMovieId, listId);
                return false;
            }
        }

        public async Task<bool> RemoveFromWatchedListAsync(int listId, string sessionId, int tmdbMovieId, string? v4AccessToken, string tokenOrKey, CancellationToken cancellationToken)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(v4AccessToken))
                {
                    var req = CreateRequest(HttpMethod.Delete, $"/4/list/{listId}/items", v4AccessToken);
                    var payload = JsonSerializer.Serialize(new
                    {
                        items = new[] { new { media_type = "movie", media_id = tmdbMovieId } }
                    });
                    req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                    using var res = await SendWithRetryAsync(req, cancellationToken).ConfigureAwait(false);
                    return res.IsSuccessStatusCode;
                }
                else
                {
                    var req = CreateRequest(HttpMethod.Post, $"/3/list/{listId}/remove_item?session_id={sessionId}", tokenOrKey);
                    var payload = JsonSerializer.Serialize(new { media_id = tmdbMovieId });
                    req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                    using var res = await SendWithRetryAsync(req, cancellationToken).ConfigureAwait(false);
                    return res.IsSuccessStatusCode;
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[TmdbClient] Error removing movie {0} from TMDb list {1}", ex, tmdbMovieId, listId);
                return false;
            }
        }

        public async Task<HashSet<int>> GetFavoriteMovieIdsAsync(int accountId, string sessionId, string tokenOrKey, CancellationToken cancellationToken)
        {
            var results = new HashSet<int>();
            var page = 1;
            var totalPages = 1;

            do
            {
                try
                {
                    var req = CreateRequest(HttpMethod.Get, $"/3/account/{accountId}/favorite/movies?session_id={sessionId}&page={page}", tokenOrKey);
                    using var res = await SendWithRetryAsync(req, cancellationToken).ConfigureAwait(false);
                    if (!res.IsSuccessStatusCode) break;

                    var json = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("total_pages", out var tp)) totalPages = tp.GetInt32();

                    if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in arr.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var idProp))
                            {
                                results.Add(idProp.GetInt32());
                            }
                        }
                    }

                    page++;
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("[TmdbClient] Error fetching favorites page {0}", ex, page);
                    break;
                }
            } while (page <= totalPages && page <= 50);

            return results;
        }

        public async Task<HashSet<int>> GetWatchedListMovieIdsAsync(int listId, string? v4AccessToken, string tokenOrKey, CancellationToken cancellationToken)
        {
            var results = new HashSet<int>();

            try
            {
                if (!string.IsNullOrWhiteSpace(v4AccessToken))
                {
                    var page = 1;
                    var totalPages = 1;
                    do
                    {
                        var req = CreateRequest(HttpMethod.Get, $"/4/list/{listId}?page={page}", v4AccessToken);
                        using var res = await SendWithRetryAsync(req, cancellationToken).ConfigureAwait(false);
                        if (!res.IsSuccessStatusCode) break;

                        var json = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("total_pages", out var tp)) totalPages = tp.GetInt32();

                        if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in arr.EnumerateArray())
                            {
                                if (item.TryGetProperty("id", out var idProp)) results.Add(idProp.GetInt32());
                            }
                        }
                        page++;
                    } while (page <= totalPages && page <= 50);
                }
                else
                {
                    // v3 list (single page)
                    var req = CreateRequest(HttpMethod.Get, $"/3/list/{listId}", tokenOrKey);
                    using var res = await SendWithRetryAsync(req, cancellationToken).ConfigureAwait(false);
                    if (res.IsSuccessStatusCode)
                    {
                        var json = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in arr.EnumerateArray())
                            {
                                if (item.TryGetProperty("id", out var idProp)) results.Add(idProp.GetInt32());
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[TmdbClient] Error fetching items for watched list {0}", ex, listId);
            }

            return results;
        }

        public async Task<string?> GetImdbIdFromTmdbIdAsync(int tmdbId, string tokenOrKey, CancellationToken cancellationToken)
        {
            try
            {
                var req = CreateRequest(HttpMethod.Get, $"/3/movie/{tmdbId}/external_ids", tokenOrKey);
                using var res = await SendWithRetryAsync(req, cancellationToken).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    var json = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("imdb_id", out var imdbProp) && imdbProp.ValueKind == JsonValueKind.String)
                    {
                        return imdbProp.GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("[TmdbClient] Failed to resolve IMDb ID for TMDb {0}: {1}", tmdbId, ex.Message);
            }
            return null;
        }

        public async Task<int?> GetTmdbIdFromImdbIdAsync(string imdbId, string tokenOrKey, CancellationToken cancellationToken)
        {
            try
            {
                var req = CreateRequest(HttpMethod.Get, $"/3/find/{imdbId}?external_source=imdb_id", tokenOrKey);
                using var res = await SendWithRetryAsync(req, cancellationToken).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    var json = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("movie_results", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                    {
                        var first = arr[0];
                        if (first.TryGetProperty("id", out var idProp))
                        {
                            return idProp.GetInt32();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("[TmdbClient] Failed to resolve TMDb ID for IMDb {0}: {1}", imdbId, ex.Message);
            }
            return null;
        }

        public async Task<bool> TestConnectionAsync(string sessionId, int accountId, int? watchedListId, string tokenOrKey, CancellationToken cancellationToken)
        {
            try
            {
                var req = CreateRequest(HttpMethod.Get, $"/3/account?session_id={sessionId}", tokenOrKey);
                using var res = await SendWithRetryAsync(req, cancellationToken).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) return false;

                if (watchedListId.HasValue && watchedListId.Value > 0)
                {
                    var listReq = CreateRequest(HttpMethod.Get, $"/3/list/{watchedListId.Value}", tokenOrKey);
                    using var listRes = await SendWithRetryAsync(listReq, cancellationToken).ConfigureAwait(false);
                    if (!listRes.IsSuccessStatusCode)
                    {
                        _logger.Warn("[TmdbClient] Account valid, but watched list {0} not found/accessible.", watchedListId.Value);
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
