using AniSync.Next.Api;
using AniSync.Next.Application;
using AniSync.Next.Configuration;
using AniSync.Next.Domain;
using AniSync.Next.Persistence;
using AniSync.Next.Providers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Services;

namespace AniSync.Next.Tests;

public sealed class ApiContractTests
{
    [Fact]
    public async Task SessionRequiresAuthenticatedShokoRequestContext()
    {
        using var directory = new TestDirectory();
        var controller = Controller(directory.Path, null);

        var result = await controller.GetSession(default);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public void AdminCanSeeCredentialPresenceButNeverTheRawSecret()
    {
        using var directory = new TestDirectory();
        var controller = Controller(directory.Path, User("admin", true));

        var result = controller.GetSettings();

        var response = result.Value.Should().BeOfType<SettingsResponse>().Subject;
        response.Clients.Should().NotBeNull();
        response.Clients!.Should().OnlyContain(client => client.SecretConfigured);
        response.Clients!.Select(client => client.ToString()).Should().NotContain(value => value!.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void NonAdminCannotUpdateSharedProviderCredentials()
    {
        using var directory = new TestDirectory();
        var controller = Controller(directory.Path, User("alice", false));

        var result = controller.UpdateProviderClient(new UpdateProviderClientRequest(
            ProviderKey.AniList, "id", true, false, "new-secret"));

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task HistoryEndpointIsIsolatedToResolvedShokoUser()
    {
        using var directory = new TestDirectory();
        var store = new JsonPluginStateStore(directory.Path, NullLogger<JsonPluginStateStore>.Instance);
        await store.AppendAsync(Outcome("alice"), default);
        await store.AppendAsync(Outcome("bob"), default);
        var controller = Controller(directory.Path, User("bob", false), store: store);

        var result = await controller.GetHistory(cancellationToken: default);

        var entries = (result.Result as OkObjectResult)!.Value.Should().BeAssignableTo<IReadOnlyList<SyncOutcome>>().Subject;
        entries.Should().ContainSingle().Which.Change.ShokoUsername.Should().Be("bob");
    }

    [Fact]
    public async Task InvalidOrStaleReviewSelectionReturnsClientErrors()
    {
        using var directory = new TestDirectory();
        var coordinator = new Mock<ISyncCoordinator>();
        coordinator.Setup(service => service.ApplyAsync("alice", It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StalePreviewException("refresh required"));
        var controller = Controller(directory.Path, User("alice", false), coordinator: coordinator.Object);

        var empty = await controller.ApplyReview(new ApplyReviewRequest([]), default);
        var stale = await controller.ApplyReview(new ApplyReviewRequest([Guid.NewGuid()]), default);

        empty.Result.Should().BeOfType<BadRequestObjectResult>();
        stale.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task RefreshReturnsProviderFailuresAsTypedResultsInsteadOfServerErrors()
    {
        using var directory = new TestDirectory();
        var coordinator = new Mock<ISyncCoordinator>();
        var expected = new ReviewRefreshResult([],
            [new ProviderRefreshFailure(ProviderKey.AniList, "Reconnect required.", false)]);
        coordinator.Setup(service => service.RefreshAsync("alice", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var controller = Controller(directory.Path, User("alice", false), coordinator: coordinator.Object);

        var result = await controller.RefreshReview(default);

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task MappingSearchDiagnosticsContainOnlyRedactedMetadataAndResultCount()
    {
        using var directory = new TestDirectory();
        const string query = "Sensitive title query";
        var configuration = new FakeConfiguration();
        configuration.SaveUserSettings("alice", new UserSyncSettings { IncludeAdultSearch = true });
        var provider = new Mock<ISyncProvider>();
        provider.Setup(service => service.SearchAsync("alice", query, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ProviderMediaSearchResult(ProviderKey.AniList, 207141,
                "Chainsmoker Cat", 0, 2026, null)]);
        var providers = new Mock<IProviderRegistry>();
        providers.Setup(service => service.Get(ProviderKey.AniList)).Returns(provider.Object);
        var stateReader = new Mock<IShokoStateReader>();
        stateReader.Setup(service => service.GetSeriesStateAsync("alice", 440, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShokoSeriesState("alice", 440, 19873, "Yani Neko", 1, 0, null));
        var diagnostics = new RecordingDiagnostics();
        var controller = Controller(directory.Path, User("alice", false), configuration: configuration,
            providers: providers.Object, stateReader: stateReader.Object, diagnostics: diagnostics);

        var result = await controller.SearchMappings(
            new SearchMappingRequest(440, ProviderKey.AniList, query), default);

        result.Result.Should().BeOfType<OkObjectResult>();
        diagnostics.Entries.Should().ContainSingle();
        diagnostics.Entries[0].EventName.Should().Be("mapping.search");
        diagnostics.Entries[0].Details.Should().Be(
            "provider=AniList seriesId=440 includeAdult=True resultCount=1");
        diagnostics.Entries[0].Details.Should().NotContain(query).And.NotContain("Chainsmoker Cat");
    }

    [Fact]
    public void OAuthUsesValidatedBrowserOriginBehindTlsTerminatingProxy()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("shoko.example.test", 8111);

        var resolved = AniSyncNextController.ResolveOAuthBaseUrl("https://shoko.example.test", context.Request);

        resolved.Should().Be("https://shoko.example.test");
    }

    [Fact]
    public void AuthorizeBindsSignedStateToValidatedPublicOrigin()
    {
        using var directory = new TestDirectory();
        var oauth = new Mock<IProviderOAuthService>();
        oauth.Setup(service => service.BuildAuthorizeUri(
                ProviderKey.AniList, "alice", "https://shoko.example.test"))
            .Returns(new Uri("https://anilist.co/api/v2/oauth/authorize?client_id=1"));
        var controller = Controller(directory.Path, User("alice", false), oauth: oauth.Object);
        controller.Request.Scheme = "http";
        controller.Request.Host = new HostString("shoko.example.test", 8111);

        var result = controller.Authorize(ProviderKey.AniList, "https://shoko.example.test");

        result.Value.Should().NotBeNull();
        oauth.VerifyAll();
    }

    [Theory]
    [InlineData("https://attacker.example")]
    [InlineData("https://user@shoko.example.test")]
    [InlineData("javascript://shoko.example.test")]
    [InlineData("https://shoko.example.test/unexpected-path")]
    public void OAuthRejectsUntrustedBrowserOrigins(string browserBaseUrl)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("shoko.example.test", 8111);

        var resolved = AniSyncNextController.ResolveOAuthBaseUrl(browserBaseUrl, context.Request);

        resolved.Should().Be("http://shoko.example.test:8111");
    }

    private static AniSyncNextController Controller(
        string path,
        IUser? current,
        IPluginStateStore? store = null,
        ISyncCoordinator? coordinator = null,
        IProviderOAuthService? oauth = null,
        IPluginConfigurationService? configuration = null,
        IProviderRegistry? providers = null,
        IShokoStateReader? stateReader = null,
        IAniSyncDiagnostics? diagnostics = null)
    {
        var users = new Mock<IUserService>();
        users.Setup(service => service.GetUserFromHttpContext(It.IsAny<HttpContext>())).Returns(current);
        var state = store ?? new JsonPluginStateStore(path, NullLogger<JsonPluginStateStore>.Instance);
        var controller = new AniSyncNextController(
            users.Object,
            configuration ?? new FakeConfiguration(),
            oauth ?? Mock.Of<IProviderOAuthService>(),
            providers ?? Mock.Of<IProviderRegistry>(),
            coordinator ?? Mock.Of<ISyncCoordinator>(),
            state,
            Mock.Of<IMappingResolver>(),
            stateReader ?? Mock.Of<IShokoStateReader>(),
            diagnostics ?? new NullDiagnostics())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return controller;
    }

    private static IUser User(string username, bool admin)
    {
        var user = new Mock<IUser>();
        user.SetupGet(value => value.Username).Returns(username);
        user.SetupGet(value => value.IsAdmin).Returns(admin);
        return user.Object;
    }

    private static SyncOutcome Outcome(string username)
    {
        var change = new PlannedChange(Guid.NewGuid(), username, 1, 2, "Series", ProviderKey.AniList,
            3, ChangeKind.Advance, ReviewReason.None, 1, 2, CanonicalListStatus.Watching,
            CanonicalListStatus.Watching, null, null, "token", DateTimeOffset.UtcNow);
        return new SyncOutcome(SyncOutcomeKind.Applied, change, CompletedAt: DateTimeOffset.UtcNow);
    }

    private sealed class RecordingDiagnostics : IAniSyncDiagnostics
    {
        public List<(DiagnosticLogLevel Level, string EventName, string Details)> Entries { get; } = [];

        public void Write(string username, DiagnosticLogLevel requiredLevel, string eventName, string details) =>
            Entries.Add((requiredLevel, eventName, details));
    }
}
