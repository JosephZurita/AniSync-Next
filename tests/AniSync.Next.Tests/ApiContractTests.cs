using AniSync.Next.Api;
using AniSync.Next.Application;
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

    private static AniSyncNextController Controller(
        string path,
        IUser? current,
        IPluginStateStore? store = null,
        ISyncCoordinator? coordinator = null)
    {
        var users = new Mock<IUserService>();
        users.Setup(service => service.GetUserFromHttpContext(It.IsAny<HttpContext>())).Returns(current);
        var state = store ?? new JsonPluginStateStore(path, NullLogger<JsonPluginStateStore>.Instance);
        var controller = new AniSyncNextController(
            users.Object,
            new FakeConfiguration(),
            Mock.Of<IProviderOAuthService>(),
            Mock.Of<IProviderRegistry>(),
            coordinator ?? Mock.Of<ISyncCoordinator>(),
            state,
            Mock.Of<IMappingResolver>(),
            Mock.Of<IShokoStateReader>())
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
}
