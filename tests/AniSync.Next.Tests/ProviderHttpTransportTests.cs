using AniSync.Next.Application;
using AniSync.Next.Domain;
using AniSync.Next.Providers;
using FluentAssertions;
using System.Net;
using System.Net.Http.Headers;

namespace AniSync.Next.Tests;

public sealed class ProviderHttpTransportTests
{
    [Fact]
    public async Task UnauthorizedRefreshesOnceAndReplaysWithFreshToken()
    {
        var handler = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            request =>
            {
                request.Headers.Authorization!.Parameter.Should().Be("fresh");
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        var tokens = new StubTokens();
        var transport = new ProviderHttpTransport(new StubFactory(handler), tokens, new RecordingDelay(), new NullDiagnostics());

        using var response = await transport.SendAsync(ProviderKey.AniList, "alice", "client",
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test"), default);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        tokens.RefreshCount.Should().Be(1);
        handler.Count.Should().Be(2);
    }

    [Fact]
    public async Task RateLimitHonorsRetryAfter()
    {
        var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(9));
        var handler = new SequenceHandler(_ => limited, _ => new HttpResponseMessage(HttpStatusCode.OK));
        var delay = new RecordingDelay();
        var transport = new ProviderHttpTransport(new StubFactory(handler), new StubTokens(), delay, new NullDiagnostics());

        using var response = await transport.SendAsync(ProviderKey.AniList, "alice", "client",
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test"), default);

        response.IsSuccessStatusCode.Should().BeTrue();
        delay.Delays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(9));
    }

    [Fact]
    public async Task PermanentProviderErrorIncludesStatusAndIsNotTransient()
    {
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid payload"),
        });
        var transport = new ProviderHttpTransport(new StubFactory(handler), new StubTokens(), new RecordingDelay(), new NullDiagnostics());

        var action = () => transport.SendAsync(ProviderKey.MyAnimeList, "alice", "client",
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test"), default);

        var exception = await action.Should().ThrowAsync<ProviderException>();
        exception.Which.IsTransient.Should().BeFalse();
        exception.Which.Message.Should().Contain("400").And.Contain("invalid payload");
    }

    [Fact]
    public async Task CallerCancellationIsNotCollapsedIntoProviderFailure()
    {
        var handler = new CancellingHandler();
        var transport = new ProviderHttpTransport(new StubFactory(handler), new StubTokens(), new RecordingDelay(), new NullDiagnostics());
        using var source = new CancellationTokenSource();
        source.Cancel();

        var action = () => transport.SendAsync(ProviderKey.AniList, "alice", "client",
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test"), source.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ProviderTimeoutIsRetriedAsTransientFailure()
    {
        var handler = new AsyncSequenceHandler(
            (_, _) => throw new TaskCanceledException("provider timeout"),
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var delay = new RecordingDelay();
        var transport = new ProviderHttpTransport(new StubFactory(handler), new StubTokens(), delay, new NullDiagnostics());

        using var response = await transport.SendAsync(ProviderKey.AniList, "alice", "client",
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test"), default);

        response.IsSuccessStatusCode.Should().BeTrue();
        delay.Delays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(1));
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubTokens : IProviderTokenService
    {
        public int RefreshCount { get; private set; }
        public Task<string> GetAccessTokenAsync(string username, ProviderKey provider, CancellationToken cancellationToken) => Task.FromResult("old");
        public Task<string> ForceRefreshAsync(string username, ProviderKey provider, CancellationToken cancellationToken)
        {
            RefreshCount++;
            return Task.FromResult("fresh");
        }
        public Task<Configuration.ProviderAuthorization> ExchangeCodeAsync(ProviderKey provider, string username, string code, string redirectUri, string? codeVerifier, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingDelay : IProviderDelay
    {
        public List<TimeSpan> Delays { get; } = [];
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responses[Math.Min(Count, responses.Length - 1)](request);
            Count++;
            return Task.FromResult(response);
        }
    }

    private sealed class CancellingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromCanceled<HttpResponseMessage>(cancellationToken);
    }

    private sealed class AsyncSequenceHandler(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] responses) : HttpMessageHandler
    {
        private int _count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responses[Math.Min(_count++, responses.Length - 1)](request, cancellationToken);
    }
}
