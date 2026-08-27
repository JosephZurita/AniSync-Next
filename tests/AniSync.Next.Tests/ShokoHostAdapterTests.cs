using AniSync.Next.Application;
using AniSync.Next.Configuration;
using AniSync.Next.Domain;
using AniSync.Next.Host;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Enums;
using Shoko.Abstractions.User.Events;
using Shoko.Abstractions.User.Services;
using Shoko.Abstractions.Video;

namespace AniSync.Next.Tests;

public sealed class ShokoHostAdapterTests
{
    [Fact]
    public void SeriesStateJoinsWatchedRowsByStableIdWhenNavigationIsNull()
    {
        var series = Series(9, 100, "Series", 13);
        var episodes = Enumerable.Range(1, 13)
            .Select(number => Episode(series.Object, 1_000 + number, number, EpisodeType.Episode))
            .ToArray();
        SetEpisodes(series, episodes);
        var rows = episodes
            .Select(episode => EpisodeData(series.Object, episode.Object, watched: true, includeNavigation: false))
            .ToArray();

        var state = ShokoStateReader.BuildState("alice", series.Object, rows, null);

        state.Progress.Should().Be(13);
    }

    [Fact]
    public void SeriesStateUsesHighestCurrentlyWatchedNormalEpisodeAndCanonicalRating()
    {
        var series = Series(9, 100, "Series", 12);
        var episode2 = Episode(series.Object, 1_002, 2, EpisodeType.Episode);
        var episode7 = Episode(series.Object, 1_007, 7, EpisodeType.Episode);
        var episode10 = Episode(series.Object, 1_010, 10, EpisodeType.Episode);
        var special = Episode(series.Object, 2_020, 20, EpisodeType.Special);
        SetEpisodes(series, episode2, episode7, episode10, special);
        var rows = new[]
        {
            EpisodeData(series.Object, episode2.Object, watched: true),
            EpisodeData(series.Object, episode7.Object, watched: true),
            EpisodeData(series.Object, episode10.Object, watched: false),
            EpisodeData(series.Object, special.Object, watched: true),
        };

        var state = ShokoStateReader.BuildState("alice", series.Object, rows, 8.45);

        state.Progress.Should().Be(7);
        state.TotalEpisodes.Should().Be(12);
        state.RatingRaw.Should().Be(85);
        state.SeriesId.Should().Be(9);
        state.AniDbAnimeId.Should().Be(100);
    }

    [Fact]
    public void ThirteenWatchedNormalEpisodesExcludeThirteenWatchedSpecials()
    {
        var series = Series(146, 14792, "Re:Zero", 13);
        var normalEpisodes = Enumerable.Range(1, 13)
            .Select(number => Episode(series.Object, 1_000 + number, number, EpisodeType.Episode))
            .ToArray();
        var specials = Enumerable.Range(1, 13)
            .Select(number => Episode(series.Object, 2_000 + number, number, EpisodeType.Special))
            .ToArray();
        SetEpisodes(series, normalEpisodes.Concat(specials).ToArray());
        var rows = normalEpisodes.Concat(specials)
            .Select(episode => EpisodeData(series.Object, episode.Object, watched: true, includeNavigation: false));

        var state = ShokoStateReader.BuildState("alice", series.Object, rows, null);

        state.Progress.Should().Be(13);
        state.TotalEpisodes.Should().Be(13);
    }

    [Fact]
    public void WatchedLinkedVideosProvideDeduplicatedFallbackProgress()
    {
        var series = Series(9, 100, "Series", 12);
        var episode = Episode(series.Object, 1_008, 8, EpisodeType.Episode);
        SetEpisodes(series, episode);
        var videoRows = new[]
        {
            VideoData(200, watched: true, episode.Object, episode.Object),
            VideoData(201, watched: true, episode.Object),
        };

        var state = ShokoStateReader.BuildState("alice", series.Object, [], null, videoRows);

        state.Progress.Should().Be(8);
    }

    [Fact]
    public void EpisodeAndVideoWatchRowsOverlapWithoutChangingProgress()
    {
        var series = Series(9, 100, "Series", 12);
        var episode = Episode(series.Object, 1_006, 6, EpisodeType.Episode);
        SetEpisodes(series, episode);
        var episodeRows = new[] { EpisodeData(series.Object, episode.Object, watched: true) };
        var videoRows = new[] { VideoData(200, watched: true, episode.Object) };

        var state = ShokoStateReader.BuildState("alice", series.Object, episodeRows, null, videoRows);

        state.Progress.Should().Be(6);
    }

    [Fact]
    public void PlaybackCountsAloneNeverMakeAnEpisodeCurrentlyWatched()
    {
        var series = Series(9, 100, "Series", 12);
        var episode = Episode(series.Object, 1_006, 6, EpisodeType.Episode);
        SetEpisodes(series, episode);
        var episodeRows = new[]
        {
            EpisodeData(series.Object, episode.Object, watched: false, playbackCount: 5),
        };
        var videoRows = new[]
        {
            VideoData(200, watched: false, playbackCount: 5, episode.Object),
        };

        var state = ShokoStateReader.BuildState("alice", series.Object, episodeRows, null, videoRows);

        state.Progress.Should().Be(0);
    }

    [Fact]
    public void LinkedEpisodesFromAnotherSeriesAreIgnored()
    {
        var series = Series(9, 100, "Series", 12);
        var ownEpisode = Episode(series.Object, 1_003, 3, EpisodeType.Episode);
        SetEpisodes(series, ownEpisode);
        var otherSeries = Series(10, 101, "Other", 12);
        var otherEpisode = Episode(otherSeries.Object, 2_012, 12, EpisodeType.Episode);
        SetEpisodes(otherSeries, otherEpisode);

        var state = ShokoStateReader.BuildState("alice", series.Object, [], null,
            [VideoData(200, watched: true, otherEpisode.Object)]);

        state.Progress.Should().Be(0);
    }

    [Fact]
    public async Task SingleSeriesAndLibraryRefreshUseTheSamePerUserCalculation()
    {
        var alice = User("alice", 1);
        var bob = User("bob", 2);
        var series = Series(9, 100, "Series", 13);
        var episodes = Enumerable.Range(1, 13)
            .Select(number => Episode(series.Object, 1_000 + number, number, EpisodeType.Episode))
            .ToArray();
        SetEpisodes(series, episodes);
        var aliceRows = episodes
            .Select(episode => EpisodeData(series.Object, episode.Object, watched: true, includeNavigation: false))
            .ToArray();
        var bobRows = episodes.Take(3)
            .Select(episode => EpisodeData(series.Object, episode.Object, watched: true, includeNavigation: false))
            .ToArray();
        var users = new Mock<IUserService>();
        users.Setup(service => service.GetUserByUsername("alice")).Returns(alice);
        users.Setup(service => service.GetUserByUsername("bob")).Returns(bob);
        var data = new Mock<IUserDataService>();
        data.Setup(service => service.GetEpisodeUserDataForUser(alice)).Returns(aliceRows);
        data.Setup(service => service.GetEpisodeUserDataForUser(bob)).Returns(bobRows);
        data.Setup(service => service.GetSeriesUserDataForUser(It.IsAny<IUser>())).Returns([]);
        data.Setup(service => service.GetVideoUserDataForUser(It.IsAny<IUser>())).Returns([]);
        var metadata = new Mock<Shoko.Abstractions.Metadata.Services.IMetadataService>();
        metadata.Setup(service => service.GetShokoSeriesByID(9)).Returns(series.Object);
        var reader = Reader(users, data, metadata);

        var single = await reader.GetSeriesStateAsync("alice", 9, default);
        var library = await reader.GetLibraryStateAsync("alice", default);
        var bobSingle = await reader.GetSeriesStateAsync("bob", 9, default);

        single!.Progress.Should().Be(13);
        library.Should().ContainSingle().Which.Progress.Should().Be(13);
        bobSingle!.Progress.Should().Be(3);
    }

    [Fact]
    public async Task LibraryPreviewReadsExistingRatingsAndVideoOnlyRowsWithoutCreatingSeriesUserData()
    {
        var user = User("alice", 1);
        var watchedSeries = Series(9, 100, "Watched", 12);
        var watchedEpisode = Episode(watchedSeries.Object, 1_003, 3, EpisodeType.Episode);
        SetEpisodes(watchedSeries, watchedEpisode);
        var videoSeries = Series(11, 102, "Video", 6);
        var videoEpisode = Episode(videoSeries.Object, 3_004, 4, EpisodeType.Episode);
        SetEpisodes(videoSeries, videoEpisode);
        var ratedSeries = Series(10, 101, "Rated", 24);
        var episodeData = EpisodeData(watchedSeries.Object, watchedEpisode.Object, true);
        var orphanedData = new Mock<IEpisodeUserData>();
        orphanedData.SetupGet(value => value.SeriesID).Returns(0);
        orphanedData.SetupGet(value => value.EpisodeID).Returns(0);
        orphanedData.SetupGet(value => value.Series).Throws(new InvalidOperationException("Series ID 0"));
        var watchedRating = SeriesData(watchedSeries.Object, 7.5, watchedEpisodes: 1);
        var ratedOnly = SeriesData(ratedSeries.Object, 8.5);
        var users = new Mock<IUserService>();
        users.Setup(service => service.GetUserByUsername("alice")).Returns(user);
        var data = new Mock<IUserDataService>();
        data.Setup(service => service.GetEpisodeUserDataForUser(user)).Returns([orphanedData.Object, episodeData]);
        data.Setup(service => service.GetVideoUserDataForUser(user))
            .Returns([VideoData(200, watched: true, videoEpisode.Object)]);
        data.Setup(service => service.GetSeriesUserDataForUser(user)).Returns([watchedRating, ratedOnly]);
        var metadata = new Mock<Shoko.Abstractions.Metadata.Services.IMetadataService>();
        metadata.Setup(service => service.GetShokoSeriesByID(9)).Returns(watchedSeries.Object);
        metadata.Setup(service => service.GetShokoSeriesByID(10)).Returns(ratedSeries.Object);
        metadata.Setup(service => service.GetShokoSeriesByID(11)).Returns(videoSeries.Object);
        var reader = Reader(users, data, metadata);

        var states = await reader.GetLibraryStateAsync("alice", default);

        states.Should().HaveCount(3);
        states.Single(state => state.SeriesId == 9).Should().Match<ShokoSeriesState>(state =>
            state.Progress == 3 && state.RatingRaw == 75);
        states.Single(state => state.SeriesId == 10).Should().Match<ShokoSeriesState>(state =>
            state.Progress == 0 && state.RatingRaw == 85);
        states.Single(state => state.SeriesId == 11).Progress.Should().Be(4);
        watchedSeries.Verify(series => series.GetUserData(It.IsAny<IUser>()), Times.Never);
        ratedSeries.Verify(series => series.GetUserData(It.IsAny<IUser>()), Times.Never);
        videoSeries.Verify(series => series.GetUserData(It.IsAny<IUser>()), Times.Never);
        orphanedData.VerifyGet(value => value.Series, Times.Never);
        metadata.Verify(service => service.GetAllShokoSeries(), Times.Never);
    }

    [Fact]
    public async Task SourceStateDiagnosticsAreRedactedAndWarnOnUnresolvedAggregateWatchState()
    {
        var user = User("alice", 1);
        var series = Series(146, 14792, "Re:Zero", 13);
        var episode = Episode(series.Object, 1_001, 1, EpisodeType.Episode);
        SetEpisodes(series, episode);
        var unresolved = new Mock<IEpisodeUserData>();
        unresolved.SetupGet(value => value.SeriesID).Returns(146);
        unresolved.SetupGet(value => value.EpisodeID).Returns(9_999);
        unresolved.SetupGet(value => value.LastPlayedAt).Returns(new DateTime(2026, 8, 28));
        unresolved.SetupGet(value => value.PlaybackCount).Returns(2);
        var users = new Mock<IUserService>();
        users.Setup(service => service.GetUserByUsername("alice")).Returns(user);
        var data = new Mock<IUserDataService>();
        data.Setup(service => service.GetEpisodeUserDataForUser(user)).Returns([unresolved.Object]);
        data.Setup(service => service.GetVideoUserDataForUser(user)).Returns([]);
        data.Setup(service => service.GetSeriesUserDataForUser(user))
            .Returns([SeriesData(series.Object, null, watchedEpisodes: 13)]);
        var metadata = new Mock<Shoko.Abstractions.Metadata.Services.IMetadataService>();
        metadata.Setup(service => service.GetShokoSeriesByID(146)).Returns(series.Object);
        var diagnostics = new RecordingDiagnostics();
        var logger = new RecordingLogger<ShokoStateReader>();
        var reader = new ShokoStateReader(users.Object, data.Object, metadata.Object, diagnostics, logger);

        var state = await reader.GetSeriesStateAsync("alice", 146, default);

        state!.Progress.Should().Be(0);
        diagnostics.Entries.Should().Contain(entry =>
            entry.Level == DiagnosticLogLevel.Detailed &&
            entry.EventName == "shoko.state" &&
            entry.Details.Contains("seriesId=146") &&
            entry.Details.Contains("resolvedWatchedNormalEpisodes=0") &&
            entry.Details.Contains("progress=0"));
        diagnostics.Entries.Should().Contain(entry =>
            entry.Level == DiagnosticLogLevel.Trace &&
            entry.EventName == "shoko.episode-state" &&
            entry.Details.Contains("episodeId=9999") &&
            entry.Details.Contains("resolved=False") &&
            entry.Details.Contains("watched=True"));
        diagnostics.Entries.Select(entry => entry.Details).Should().OnlyContain(details =>
            !details.Contains("2026") && !details.Contains("path", StringComparison.OrdinalIgnoreCase));
        logger.Messages.Should().ContainSingle(message =>
            message.Level == LogLevel.Warning && message.Text.Contains("series 146"));
    }

    [Fact]
    public async Task SourceStateDiagnosticsReportResolvedEpisodeAndVideoFallbackCounts()
    {
        var user = User("alice", 1);
        var series = Series(9, 100, "Series", 2);
        var episode1 = Episode(series.Object, 1_001, 1, EpisodeType.Episode);
        var episode2 = Episode(series.Object, 1_002, 2, EpisodeType.Episode);
        var video = new Mock<IVideo>();
        video.SetupGet(value => value.ID).Returns(200);
        video.SetupGet(value => value.Episodes).Returns([episode2.Object]);
        episode2.SetupGet(value => value.VideoList).Returns([video.Object]);
        SetEpisodes(series, episode1, episode2);
        var videoData = new Mock<IVideoUserData>();
        videoData.SetupGet(value => value.VideoID).Returns(200);
        videoData.SetupGet(value => value.Video).Returns(video.Object);
        videoData.SetupGet(value => value.LastPlayedAt).Returns(new DateTime(2026, 8, 28));
        var users = new Mock<IUserService>();
        users.Setup(service => service.GetUserByUsername("alice")).Returns(user);
        var data = new Mock<IUserDataService>();
        data.Setup(service => service.GetEpisodeUserDataForUser(user))
            .Returns([EpisodeData(series.Object, episode1.Object, watched: true, includeNavigation: false)]);
        data.Setup(service => service.GetVideoUserDataForUser(user)).Returns([videoData.Object]);
        data.Setup(service => service.GetSeriesUserDataForUser(user))
            .Returns([SeriesData(series.Object, null, watchedEpisodes: 2)]);
        var metadata = new Mock<Shoko.Abstractions.Metadata.Services.IMetadataService>();
        metadata.Setup(service => service.GetShokoSeriesByID(9)).Returns(series.Object);
        var diagnostics = new RecordingDiagnostics();
        var reader = new ShokoStateReader(users.Object, data.Object, metadata.Object, diagnostics,
            NullLogger<ShokoStateReader>.Instance);

        var state = await reader.GetSeriesStateAsync("alice", 9, default);

        state!.Progress.Should().Be(2);
        diagnostics.Entries.Should().Contain(entry =>
            entry.Level == DiagnosticLogLevel.Detailed &&
            entry.Details.Contains("resolvedEpisodeRows=1") &&
            entry.Details.Contains("resolvedWatchedNormalEpisodes=2") &&
            entry.Details.Contains("linkedVideoFallbackEpisodes=1") &&
            entry.Details.Contains("progress=2"));
        diagnostics.Entries.Should().Contain(entry =>
            entry.Level == DiagnosticLogLevel.Trace &&
            entry.Details.Contains("source=video") &&
            entry.Details.Contains("episodeId=1002") &&
            entry.Details.Contains("fallback=True"));
    }

    [Fact]
    public void CorrectedSourceStatesProduceNoOpForwardCompletionAndGenuineDecreasePlans()
    {
        var series = Series(146, 14792, "Re:Zero", 13);
        var episodes = Enumerable.Range(1, 13)
            .Select(number => Episode(series.Object, 1_000 + number, number, EpisodeType.Episode))
            .ToArray();
        SetEpisodes(series, episodes);
        var watchedRows = episodes
            .Select(episode => EpisodeData(series.Object, episode.Object, watched: true, includeNavigation: false))
            .ToArray();
        var completedSource = ShokoStateReader.BuildState("alice", series.Object, watchedRows, null);
        var unwatchedSource = ShokoStateReader.BuildState("alice", series.Object,
            episodes.Select(episode => EpisodeData(series.Object, episode.Object, watched: false)), null);
        var planner = new SyncPlanner();
        var now = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var completedDestination = Destination(13, CanonicalListStatus.Completed);
        var lowerDestination = Destination(10, CanonicalListStatus.Watching);

        var noChange = planner.Plan(completedSource, ProviderKey.MyAnimeList, 39587,
            completedDestination, "same", now, false, true);
        var completion = planner.Plan(completedSource, ProviderKey.MyAnimeList, 39587,
            lowerDestination, "forward", now, false, true);
        var decrease = planner.Plan(unwatchedSource, ProviderKey.MyAnimeList, 39587,
            completedDestination, "decrease", now, false, true);

        noChange.Kind.Should().Be(ChangeKind.NoChange);
        completion.Kind.Should().Be(ChangeKind.Complete);
        completion.RequiresReview.Should().BeFalse();
        decrease.Kind.Should().Be(ChangeKind.Decrease);
        decrease.RequiresReview.Should().BeTrue();
    }

    [Fact]
    public async Task EventBridgeOnlyEnqueuesWatchAndSeriesRatingChanges()
    {
        var service = new Mock<IUserDataService>();
        var queue = new SyncTriggerQueue();
        var bridge = new ShokoEventBridge(service.Object, queue, TimeProvider.System,
            NullLogger<ShokoEventBridge>.Instance);
        var user = User("alice", 1);
        var series = Series(9, 100, "Series", 12);
        var episode = Episode(series.Object, 1_003, 3, EpisodeType.Episode);
        SetEpisodes(series, episode);
        var episodeData = EpisodeData(series.Object, episode.Object, true);
        await bridge.StartAsync(default);

        service.Raise(value => value.EpisodeUserDataSaved += null, new EpisodeUserDataSavedEventArgs
        {
            User = user,
            Episode = episode.Object,
            UserData = episodeData,
            Reason = EpisodeUserDataSaveReason.UserRating,
            VideoReason = VideoUserDataSaveReason.None,
        });
        queue.Reader.TryRead(out _).Should().BeFalse();
        service.Raise(value => value.EpisodeUserDataSaved += null, new EpisodeUserDataSavedEventArgs
        {
            User = user,
            Episode = episode.Object,
            UserData = episodeData,
            Reason = EpisodeUserDataSaveReason.LastPlayedAt,
            VideoReason = VideoUserDataSaveReason.UserInteraction,
        });
        queue.Reader.TryRead(out var watch).Should().BeTrue();
        watch!.Reason.Should().Be("watch-state");
        service.Raise(value => value.SeriesUserDataSaved += null, new SeriesUserDataSavedEventArgs
        {
            User = user,
            Series = series.Object,
            UserData = Mock.Of<ISeriesUserData>(),
            Reason = SeriesUserDataSaveReason.UserRating,
            VideoReason = VideoUserDataSaveReason.None,
        });
        queue.Reader.TryRead(out var rating).Should().BeTrue();
        rating!.Reason.Should().Be("rating");

        await bridge.StopAsync(default);
    }

    private static ShokoStateReader Reader(
        Mock<IUserService> users,
        Mock<IUserDataService> data,
        Mock<Shoko.Abstractions.Metadata.Services.IMetadataService> metadata) =>
        new(users.Object, data.Object, metadata.Object, new NullDiagnostics(),
            NullLogger<ShokoStateReader>.Instance);

    private static IUser User(string username, int id)
    {
        var user = new Mock<IUser>();
        user.SetupGet(value => value.ID).Returns(id);
        user.SetupGet(value => value.Username).Returns(username);
        user.Setup(value => value.IsAllowedToSee(It.IsAny<IShokoSeries>())).Returns(true);
        return user.Object;
    }

    private static ISeriesUserData SeriesData(
        IShokoSeries series,
        double? rating,
        int watchedEpisodes = 0)
    {
        var data = new Mock<ISeriesUserData>();
        data.SetupGet(value => value.SeriesID).Returns(series.ID);
        data.SetupGet(value => value.Series).Returns(series);
        data.SetupGet(value => value.UserRating).Returns(rating);
        data.SetupGet(value => value.WatchedEpisodeCount).Returns(watchedEpisodes);
        return data.Object;
    }

    private static Mock<IShokoSeries> Series(int id, int aniDbId, string title, int episodeCount)
    {
        var series = new Mock<IShokoSeries>();
        series.SetupGet(value => value.ID).Returns(id);
        series.SetupGet(value => value.AnidbAnimeID).Returns(aniDbId);
        series.SetupGet(value => value.Title).Returns(title);
        series.SetupGet(value => value.EpisodeCounts).Returns(new EpisodeCounts { Episodes = episodeCount });
        series.SetupGet(value => value.Episodes).Returns([]);
        return series;
    }

    private static void SetEpisodes(Mock<IShokoSeries> series, params Mock<IShokoEpisode>[] episodes) =>
        series.SetupGet(value => value.Episodes).Returns(episodes.Select(episode => episode.Object).ToArray());

    private static Mock<IShokoEpisode> Episode(
        IShokoSeries series,
        int id,
        int number,
        EpisodeType type)
    {
        var episode = new Mock<IShokoEpisode>();
        episode.SetupGet(value => value.ID).Returns(id);
        episode.SetupGet(value => value.Series).Returns(series);
        episode.SetupGet(value => value.SeriesID).Returns(series.ID);
        episode.SetupGet(value => value.EpisodeNumber).Returns(number);
        episode.SetupGet(value => value.Type).Returns(type);
        return episode;
    }

    private static IEpisodeUserData EpisodeData(
        IShokoSeries series,
        IShokoEpisode episode,
        bool watched,
        bool includeNavigation = true,
        int playbackCount = 0)
    {
        var data = new Mock<IEpisodeUserData>();
        data.SetupGet(value => value.Series).Returns(series);
        data.SetupGet(value => value.SeriesID).Returns(series.ID);
        data.SetupGet(value => value.EpisodeID).Returns(episode.ID);
        if (includeNavigation) data.SetupGet(value => value.Episode).Returns(episode);
        data.SetupGet(value => value.LastPlayedAt).Returns(watched ? new DateTime(2026, 1, 1) : null);
        data.SetupGet(value => value.PlaybackCount).Returns(playbackCount);
        return data.Object;
    }

    private static IVideoUserData VideoData(
        int videoId,
        bool watched,
        params IShokoEpisode[] episodes) => VideoData(videoId, watched, 0, episodes);

    private static IVideoUserData VideoData(
        int videoId,
        bool watched,
        int playbackCount,
        params IShokoEpisode[] episodes)
    {
        var video = new Mock<IVideo>();
        video.SetupGet(value => value.ID).Returns(videoId);
        video.SetupGet(value => value.Episodes).Returns(episodes);
        var data = new Mock<IVideoUserData>();
        data.SetupGet(value => value.VideoID).Returns(videoId);
        data.SetupGet(value => value.Video).Returns(video.Object);
        data.SetupGet(value => value.LastPlayedAt).Returns(watched ? new DateTime(2026, 1, 1) : null);
        data.SetupGet(value => value.PlaybackCount).Returns(playbackCount);
        return data.Object;
    }

    private static ProviderListState Destination(int progress, CanonicalListStatus status) =>
        new(ProviderKey.MyAnimeList, 39587, "Re:Zero", progress, 13, status, null);

    private sealed class RecordingDiagnostics : IAniSyncDiagnostics
    {
        public List<(DiagnosticLogLevel Level, string EventName, string Details)> Entries { get; } = [];

        public void Write(
            string username,
            DiagnosticLogLevel requiredLevel,
            string eventName,
            string details) => Entries.Add((requiredLevel, eventName, details));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Text)> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add((logLevel, formatter(state, exception)));
    }
}
