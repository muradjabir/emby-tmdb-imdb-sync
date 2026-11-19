using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.TmdbImdbSync.Configuration;
using Emby.TmdbImdbSync.Services;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using Moq;
using Xunit;

namespace Emby.TmdbImdbSync.Tests
{
    public class SimpleJsonSerializer : IJsonSerializer
    {
        void IJsonSerializer.SerializeToStream(object obj, Stream stream) => JsonSerializer.Serialize(stream, obj);
        void IJsonSerializer.SerializeToStream(object obj, Stream stream, MediaBrowser.Model.Serialization.JsonSerializerOptions options) => JsonSerializer.Serialize(stream, obj);
        void IJsonSerializer.SerializeToFile(object obj, string file) => File.WriteAllText(file, JsonSerializer.Serialize(obj));
        void IJsonSerializer.SerializeToFile(object obj, string file, MediaBrowser.Model.Serialization.JsonSerializerOptions options) => File.WriteAllText(file, JsonSerializer.Serialize(obj));
        Task<object> IJsonSerializer.DeserializeFromFileAsync(Type type, string file) => throw new NotImplementedException();
        T IJsonSerializer.DeserializeFromFile<T>(string file) => JsonSerializer.Deserialize<T>(File.ReadAllText(file))!;
        Task<T> IJsonSerializer.DeserializeFromFileAsync<T>(string file) => throw new NotImplementedException();
        T IJsonSerializer.DeserializeFromStream<T>(Stream stream) => JsonSerializer.Deserialize<T>(stream)!;
        Task<T> IJsonSerializer.DeserializeFromStreamAsync<T>(Stream stream) => throw new NotImplementedException();
        T IJsonSerializer.DeserializeFromString<T>(string json) => JsonSerializer.Deserialize<T>(json)!;
        object IJsonSerializer.DeserializeFromStream(Stream stream, Type type) => JsonSerializer.Deserialize(stream, type)!;
        Task<object> IJsonSerializer.DeserializeFromStreamAsync(Stream stream, Type type) => throw new NotImplementedException();
        object IJsonSerializer.DeserializeFromString(string json, Type type) => JsonSerializer.Deserialize(json, type)!;
        string IJsonSerializer.SerializeToString(object obj) => JsonSerializer.Serialize(obj);
        string IJsonSerializer.SerializeToString(object obj, MediaBrowser.Model.Serialization.JsonSerializerOptions options) => JsonSerializer.Serialize(obj);
        ReadOnlySpan<char> IJsonSerializer.SerializeToSpan(object obj) => JsonSerializer.Serialize(obj).AsSpan();
        T IJsonSerializer.DeserializeFromSpan<T>(ReadOnlySpan<char> json) => JsonSerializer.Deserialize<T>(json.ToString())!;
        object IJsonSerializer.DeserializeFromSpan(ReadOnlySpan<char> json, Type type) => JsonSerializer.Deserialize(json.ToString(), type)!;
        object IJsonSerializer.DeserializeFromBytes(ReadOnlySpan<byte> json, Type type) => JsonSerializer.Deserialize(json.ToArray(), type)!;
        T IJsonSerializer.DeserializeFromBytes<T>(ReadOnlySpan<byte> json) => JsonSerializer.Deserialize<T>(json.ToArray())!;
        void IJsonSerializer.DeserializePartialJsonInto(string json, object target) { }
    }

    public class SyncOrchestratorTests
    {
        private readonly Mock<ILogger> _loggerMock = new Mock<ILogger>();
        private readonly SimpleJsonSerializer _serializer = new SimpleJsonSerializer();

        [Fact]
        public async Task ProcessDebouncedItemAsync_SkipsIfStateUnchanged()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            try
            {
                var stateStore = new SyncStateStore(tempDir, _serializer, _loggerMock.Object);
                // Pre-populate state
                var initialState = new UserSyncState { EmbyUserId = "user1" };
                initialState.Movies["101"] = new MovieSyncState
                {
                    InternalId = 101,
                    Name = "Test Movie",
                    TmdbId = "500",
                    ImdbId = "tt500",
                    IsFavorite = true,
                    IsPlayed = true
                };
                stateStore.SaveState("user1", initialState);

                var tmdbCalls = 0;
                var mockTmdbHandler = new MockHttpMessageHandler
                {
                    Handler = _ =>
                    {
                        tmdbCalls++;
                        return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK);
                    }
                };

                var tmdbClient = new TmdbClient(new System.Net.Http.HttpClient(mockTmdbHandler), _loggerMock.Object);
                var imdbClient = new ImdbClient(new System.Net.Http.HttpClient(mockTmdbHandler), _loggerMock.Object, () => new ImdbQueryTemplates());

                var testConfig = new PluginConfiguration
                {
                    Users = new List<UserSyncConfig>
                    {
                        new UserSyncConfig
                        {
                            EmbyUserId = "user1",
                            EnableTmdbSync = true,
                            TmdbSessionId = "sess1",
                            TmdbAccountId = 123
                        }
                    }
                };

                var orchestrator = new SyncOrchestrator(tmdbClient, imdbClient, stateStore, _loggerMock.Object, () => testConfig);

                var item = new SyncWorkItem
                {
                    EmbyUserId = "user1",
                    MovieInternalId = 101,
                    MovieName = "Test Movie",
                    TmdbId = "500",
                    ImdbId = "tt500",
                    IsFavorite = true,
                    IsPlayed = true
                };

                await orchestrator.ProcessDebouncedItemAsync(item, CancellationToken.None);

                // Zero TMDb calls made because state was already synced
                Assert.Equal(0, tmdbCalls);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }
    }
}
