using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.TmdbImdbSync.Services;
using MediaBrowser.Model.Logging;
using Moq;
using Xunit;

namespace Emby.TmdbImdbSync.Tests
{
    public class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Handler(request));
        }
    }

    public class TmdbClientTests
    {
        private readonly Mock<ILogger> _loggerMock = new Mock<ILogger>();

        [Fact]
        public async Task LoginWithCredentialsAsync_CompletesSuccessfully()
        {
            var mockHandler = new MockHttpMessageHandler
            {
                Handler = req =>
                {
                    var uri = req.RequestUri?.ToString() ?? "";
                    if (uri.Contains("/authentication/token/new"))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new { success = true, request_token = "token123" }))
                        };
                    }
                    if (uri.Contains("/authentication/token/validate_with_login"))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new { success = true, request_token = "token123" }))
                        };
                    }
                    if (uri.Contains("/authentication/session/new"))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new { success = true, session_id = "session456" }))
                        };
                    }
                    if (uri.Contains("/3/account") && !uri.Contains("/lists"))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new { id = 9999, username = "testuser" }))
                        };
                    }
                    if (uri.Contains("/lists"))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new { results = new object[0] }))
                        };
                    }
                    if (uri.Contains("/3/list"))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new { success = true, list_id = 7777 }))
                        };
                    }
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
            };

            var httpClient = new HttpClient(mockHandler);
            var client = new TmdbClient(httpClient, _loggerMock.Object);

            var result = await client.LoginWithCredentialsAsync("testuser", "testpassword", "appToken", CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal("session456", result.SessionId);
            Assert.Equal(9999, result.AccountId);
            Assert.Equal(7777, result.WatchedListId);
        }

        [Fact]
        public async Task SetFavoriteAsync_SendsCorrectPayloadAndHeader()
        {
            HttpRequestMessage? capturedRequest = null;
            var mockHandler = new MockHttpMessageHandler
            {
                Handler = req =>
                {
                    capturedRequest = req;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new { status_code = 1, status_message = "Success." }))
                    };
                }
            };

            var httpClient = new HttpClient(mockHandler);
            var client = new TmdbClient(httpClient, _loggerMock.Object);

            var success = await client.SetFavoriteAsync(1234, "session_abc", 550, true, "my_long_jwt_bearer_token_1234567890_abcdefghij", CancellationToken.None);

            Assert.True(success);
            Assert.NotNull(capturedRequest);
            Assert.Contains("/3/account/1234/favorite", capturedRequest.RequestUri?.ToString());
            Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
            Assert.Equal("my_long_jwt_bearer_token_1234567890_abcdefghij", capturedRequest.Headers.Authorization?.Parameter);

            var body = await capturedRequest.Content!.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("movie", doc.RootElement.GetProperty("media_type").GetString());
            Assert.Equal(550, doc.RootElement.GetProperty("media_id").GetInt32());
            Assert.True(doc.RootElement.GetProperty("favorite").GetBoolean());
        }

        [Fact]
        public async Task SendWithRetryAsync_Honors429RetryAfter()
        {
            var attempts = 0;
            var mockHandler = new MockHttpMessageHandler
            {
                Handler = req =>
                {
                    attempts++;
                    if (attempts == 1)
                    {
                        var res = new HttpResponseMessage((HttpStatusCode)429);
                        res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(50));
                        return res;
                    }
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new { status_code = 1, status_message = "Success." }))
                    };
                }
            };

            var httpClient = new HttpClient(mockHandler);
            var client = new TmdbClient(httpClient, _loggerMock.Object);

            var success = await client.SetFavoriteAsync(1234, "sess", 100, true, "appKey", CancellationToken.None);

            Assert.True(success);
            Assert.Equal(2, attempts);
        }

        [Fact]
        public async Task SetFavoriteAsync_FallsBackToDefaultApiKey_WhenEmpty()
        {
            HttpRequestMessage? capturedRequest = null;
            var mockHandler = new MockHttpMessageHandler
            {
                Handler = req =>
                {
                    capturedRequest = req;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new { status_code = 1, status_message = "Success." }))
                    };
                }
            };

            var httpClient = new HttpClient(mockHandler);
            var client = new TmdbClient(httpClient, _loggerMock.Object);

            var success = await client.SetFavoriteAsync(1234, "sess", 100, true, "", CancellationToken.None);

            Assert.True(success);
            Assert.NotNull(capturedRequest);
            Assert.Contains($"api_key={TmdbClient.DefaultApiKey}", capturedRequest.RequestUri?.ToString());
        }

        [Fact]
        public async Task LoginWithCredentialsAsync_ReusesExistingWatchedList_IfFound()
        {
            var mockHandler = new MockHttpMessageHandler
            {
                Handler = req =>
                {
                    var uri = req.RequestUri?.ToString() ?? "";
                    if (uri.Contains("/authentication/token/new"))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new { success = true, request_token = "tok1" }))
                        };
                    }
                    if (uri.Contains("/authentication/token/validate_with_login"))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new { success = true, request_token = "tok1" }))
                        };
                    }
                    if (uri.Contains("/authentication/session/new"))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new { success = true, session_id = "sess1" }))
                        };
                    }
                    if (uri.Contains("/3/account") && !uri.Contains("/lists"))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new { id = 5555, username = "testuser" }))
                        };
                    }
                    if (uri.Contains("/lists"))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new
                            {
                                results = new[]
                                {
                                    new { id = 8888, name = "Emby Watched Movies" }
                                }
                            }))
                        };
                    }
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
            };

            var httpClient = new HttpClient(mockHandler);
            var client = new TmdbClient(httpClient, _loggerMock.Object);

            var result = await client.LoginWithCredentialsAsync("testuser", "testpassword", "", CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal("sess1", result.SessionId);
            Assert.Equal(5555, result.AccountId);
            Assert.Equal(8888, result.WatchedListId);
        }
    }
}
