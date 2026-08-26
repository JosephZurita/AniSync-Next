using AniSync.Next.Domain;
using AniSync.Next.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniSync.Next.Tests;

public sealed class JsonPluginStateStoreTests
{
    [Fact]
    public async Task StateSurvivesRestartAndWritesAtomically()
    {
        using var directory = new TestDirectory();
        var first = Create(directory.Path);
        await first.InitializeAsync(default);
        var mapping = new ProviderMapping("alice", 5, ProviderKey.AniList, 10,
            "Title", true, DateTimeOffset.UtcNow);
        await first.SaveMappingAsync(mapping, default);
        await first.UpsertPendingAsync(new PersistedSyncTrigger(Guid.NewGuid(), "alice", 7,
            "watch", DateTimeOffset.UtcNow), default);

        File.Exists(System.IO.Path.Combine(directory.Path, "state-v1.json.tmp")).Should().BeFalse();
        var reloaded = Create(directory.Path);
        await reloaded.InitializeAsync(default);
        (await reloaded.GetMappingAsync("ALICE", 5, ProviderKey.AniList, default)).Should().Be(mapping);
        (await reloaded.GetPendingAsync(default)).Should().ContainSingle();
    }

    [Fact]
    public async Task CorruptStateIsBackedUpAndFailsClosedToEmptyState()
    {
        using var directory = new TestDirectory();
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory.Path, "state-v1.json"), "{ definitely not json");

        var store = Create(directory.Path);
        await store.InitializeAsync(default);

        Directory.GetFiles(directory.Path, "state-v1.json.corrupt-*").Should().ContainSingle();
        (await store.GetMappingsAsync("alice", default)).Should().BeEmpty();
        (await store.GetPendingAsync(default)).Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewReplacementIsIsolatedByUser()
    {
        using var directory = new TestDirectory();
        var store = Create(directory.Path);
        var alice = Review("alice", 1);
        var bob = Review("bob", 2);
        await store.ReplaceForUserAsync("alice", [alice], default);
        await store.ReplaceForUserAsync("bob", [bob], default);
        await store.ReplaceForUserAsync("alice", [], default);

        (await store.GetForUserAsync("alice", default)).Should().BeEmpty();
        (await store.GetForUserAsync("bob", default)).Should().ContainSingle().Which.Should().Be(bob);
    }

    private static JsonPluginStateStore Create(string path) =>
        new(path, NullLogger<JsonPluginStateStore>.Instance);

    private static ReviewItem Review(string username, int seriesId)
    {
        var change = new PlannedChange(Guid.NewGuid(), username, seriesId, seriesId, "Title",
            ProviderKey.AniList, 3, ChangeKind.Advance, ReviewReason.None, 1, 2,
            CanonicalListStatus.Watching, CanonicalListStatus.Watching, null, null, "token",
            DateTimeOffset.UtcNow);
        return new ReviewItem(change.Id, change, DateTimeOffset.UtcNow);
    }
}

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anisync-next-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, true);
    }
}
