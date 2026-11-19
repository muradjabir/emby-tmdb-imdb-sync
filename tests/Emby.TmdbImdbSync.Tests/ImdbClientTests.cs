using System;
using System.Linq;
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
    public class ImdbClientTests
    {
        private readonly Mock<ILogger> _loggerMock = new Mock<ILogger>();
        private readonly ImdbQueryTemplates _templates = new ImdbQueryTemplates();

        [Fact]
        public async Task ExecuteGraphQlAsync_IncludesRequiredHeadersAndCookie()
        {
            HttpRequestMessage? capturedRequest = null;
            var mockHandler = new MockHttpMessageHandler
            {
                Handler = req =>
                {
                    capturedRequest = req;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new
                        {
                            data = new { user = new { id = "ur123456" } }
                        }))
                    };
                }
            };

            var httpClient = new HttpClient(mockHandler);
            var client = new ImdbClient(httpClient, _loggerMock.Object, () => _templates);

            var result = await client.TestAuthAsync("userA", "at-main=secret_token_123; ubid-main=ubid_456;", CancellationToken.None);

            Assert.True(result.Success);
            Assert.NotNull(capturedRequest);
            Assert.Contains("at-main=secret_token_123", capturedRequest.Headers.GetValues("Cookie").First());
            Assert.Equal("imdb-web-next-localized", capturedRequest.Headers.GetValues("x-imdb-client-name").First());
            Assert.Equal("https://www.imdb.com", capturedRequest.Headers.GetValues("Origin").First());
        }

        [Fact]
        public async Task TestAuthAsync_DetectsNullUserAsAuthExpired()
        {
            var mockHandler = new MockHttpMessageHandler
            {
                Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        data = new { user = (object?)null },
                        extensions = new { authToken = "token.invalid" }
                    }))
                }
            };

            var httpClient = new HttpClient(mockHandler);
            var client = new ImdbClient(httpClient, _loggerMock.Object, () => _templates);

            var result = await client.TestAuthAsync("userA", "at-main=bad;", CancellationToken.None);

            Assert.False(result.Success);
            Assert.True(result.AuthExpired);
        }

        [Fact]
        public async Task ExecuteGraphQlAsync_DetectsHttp401AsAuthExpired()
        {
            var mockHandler = new MockHttpMessageHandler
            {
                Handler = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("Unauthorized")
                }
            };

            var httpClient = new HttpClient(mockHandler);
            var client = new ImdbClient(httpClient, _loggerMock.Object, () => _templates);

            var result = await client.TestAuthAsync("userA", "at-main=expired;", CancellationToken.None);

            Assert.False(result.Success);
            Assert.True(result.AuthExpired);
        }

        [Fact]
        public async Task ExecuteGraphQlAsync_DetectsGraphQlUnauthenticatedError()
        {
            var mockHandler = new MockHttpMessageHandler
            {
                Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        errors = new[]
                        {
                            new { message = "UNAUTHENTICATED request.", extensions = new { code = "UNAUTHENTICATED" } }
                        }
                    }))
                }
            };

            var httpClient = new HttpClient(mockHandler);
            var client = new ImdbClient(httpClient, _loggerMock.Object, () => _templates);

            var result = await client.SetTitleWatchedAsync("userA", "cookie", "tt0111161", true, CancellationToken.None);

            Assert.False(result.Success);
            Assert.True(result.AuthExpired);
        }

        [Fact]
        public async Task CreateListAsync_ExtractsListIdSuccessfully()
        {
            var mockHandler = new MockHttpMessageHandler
            {
                Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        data = new
                        {
                            createList = new
                            {
                                listId = "ls998877665",
                                __typename = "CreateListPayload"
                            }
                        }
                    }))
                }
            };

            var httpClient = new HttpClient(mockHandler);
            var client = new ImdbClient(httpClient, _loggerMock.Object, () => _templates);

            var result = await client.CreateListAsync("userA", "cookie", "Emby Favorites", CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal("ls998877665", result.Data);
        }
    }
}
