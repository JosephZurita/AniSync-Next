using AniSync.Next.Host;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Enums;
using Shoko.Abstractions.User.Events;
using Shoko.Abstractions.User.Services;

namespace AniSync.Next.Tests;

public sealed class ShokoHostAdapterTests
{
    [Fact]
    public void SeriesStateUsesHighestCurrentlyWatchedNormalEpisodeAndCanonicalRating()
    {
        var user = new Mock<IUser>();
        var seriesUserData = new Mock<ISeriesUserData>();
        seriesUserData.SetupGet(data => data.UserRating).Returns(8.45);
        var series = Series(9, 100, "Series", 12);
        series.Setup(value => value.GetUserData(user.Object)).Returns(seriesUserData.Object);
        var rows = new[]
        {
            EpisodeData(series.Object, 2, EpisodeType.Episode, watched: true),
            EpisodeData(series.Object, 7, EpisodeType.Episode, watched: true),
            EpisodeData(series.Object, 10, EpisodeType.Episode, watched: false),
            EpisodeData(series.Object, 20, EpisodeType.Special, watched: true),
        };

        var state = ShokoStateReader.BuildState("alice", user.Object, series.Object, rows);

        state.Progress.Should().Be(7);
        state.TotalEpisodes.Should().Be(12);
        state.RatingRaw.Should().Be(85);
        state.SeriesId.Should().Be(9);
        state.AniDbAnimeId.Should().Be(100);
    }

    [Fact]
    public async Task EventBridgeOnlyEnqueuesWatchAndSeriesRatingChanges()
    {
        var service = new Mock<IUserDataService>();
        var queue = new SyncTriggerQueue();
        var bridge = new ShokoEventBridge(service.Object, queue, TimeProvider.System,
            NullLogger<ShokoEventBridge>.Instance);
        var user = User("alice");
        var series = Series(9, 100, "Series", 12).Object;
        var episode = Episode(series, 3, EpisodeType.Episode).Object;
        var episodeData = EpisodeData(series, 3, EpisodeType.Episode, true);
        await bridge.StartAsync(default);

        service.Raise(value => value.EpisodeUserDataSaved += null, new EpisodeUserDataSavedEventArgs
        {
            User = user,
            Episode = episode,
            UserData = episodeData,
            Reason = EpisodeUserDataSaveReason.UserRating,
            VideoReason = VideoUserDataSaveReason.None,
        });
        queue.Reader.TryRead(out _).Should().BeFalse();
        service.Raise(value => value.EpisodeUserDataSaved += null, new EpisodeUserDataSavedEventArgs
        {
            User = user,
            Episode = episode,
            UserData = episodeData,
            Reason = EpisodeUserDataSaveReason.LastPlayedAt,
            VideoReason = VideoUserDataSaveReason.UserInteraction,
        });
        queue.Reader.TryRead(out var watch).Should().BeTrue();
        watch!.Reason.Should().Be("watch-state");
        service.Raise(value => value.SeriesUserDataSaved += null, new SeriesUserDataSavedEventArgs
        {
            User = user,
            Series = series,
            UserData = Mock.Of<ISeriesUserData>(),
            Reason = SeriesUserDataSaveReason.UserRating,
            VideoReason = VideoUserDataSaveReason.None,
        });
        queue.Reader.TryRead(out var rating).Should().BeTrue();
        rating!.Reason.Should().Be("rating");

        await bridge.StopAsync(default);
    }

    private static IUser User(string username)
    {
        var user = new Mock<IUser>();
        user.SetupGet(value => value.Username).Returns(username);
        return user.Object;
    }

    private static Mock<IShokoSeries> Series(int id, int aniDbId, string title, int episodeCount)
    {
        var series = new Mock<IShokoSeries>();
        series.SetupGet(value => value.ID).Returns(id);
        series.SetupGet(value => value.AnidbAnimeID).Returns(aniDbId);
        series.SetupGet(value => value.Title).Returns(title);
        series.SetupGet(value => value.EpisodeCounts).Returns(new EpisodeCounts { Episodes = episodeCount });
        return series;
    }

    private static Mock<IShokoEpisode> Episode(IShokoSeries series, int number, EpisodeType type)
    {
        var episode = new Mock<IShokoEpisode>();
        episode.SetupGet(value => value.Series).Returns(series);
        episode.SetupGet(value => value.SeriesID).Returns(series.ID);
        episode.SetupGet(value => value.EpisodeNumber).Returns(number);
        episode.SetupGet(value => value.Type).Returns(type);
        return episode;
    }

    private static IEpisodeUserData EpisodeData(IShokoSeries series, int number, EpisodeType type, bool watched)
    {
        var data = new Mock<IEpisodeUserData>();
        data.SetupGet(value => value.Series).Returns(series);
        data.SetupGet(value => value.SeriesID).Returns(series.ID);
        data.SetupGet(value => value.Episode).Returns(Episode(series, number, type).Object);
        data.SetupGet(value => value.LastPlayedAt).Returns(watched ? new DateTime(2026, 1, 1) : null);
        return data.Object;
    }
}
