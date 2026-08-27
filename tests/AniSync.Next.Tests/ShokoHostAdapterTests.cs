using AniSync.Next.Host;
using AniSync.Next.Domain;
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
        var series = Series(9, 100, "Series", 12);
        var rows = new[]
        {
            EpisodeData(series.Object, 2, EpisodeType.Episode, watched: true),
            EpisodeData(series.Object, 7, EpisodeType.Episode, watched: true),
            EpisodeData(series.Object, 10, EpisodeType.Episode, watched: false),
            EpisodeData(series.Object, 20, EpisodeType.Special, watched: true),
        };

        var state = ShokoStateReader.BuildState("alice", series.Object, rows, 8.45);

        state.Progress.Should().Be(7);
        state.TotalEpisodes.Should().Be(12);
        state.RatingRaw.Should().Be(85);
        state.SeriesId.Should().Be(9);
        state.AniDbAnimeId.Should().Be(100);
    }

    [Fact]
    public async Task LibraryPreviewReadsExistingRatingsWithoutCreatingSeriesUserData()
    {
        var user = User("alice");
        var watchedSeries = Series(9, 100, "Watched", 12);
        var ratedSeries = Series(10, 101, "Rated", 24);
        var episodeData = EpisodeData(watchedSeries.Object, 3, EpisodeType.Episode, true);
        var watchedRating = SeriesData(watchedSeries.Object, 7.5);
        var ratedOnly = SeriesData(ratedSeries.Object, 8.5);
        var users = new Mock<IUserService>();
        users.Setup(service => service.GetUserByUsername("alice")).Returns(user);
        var data = new Mock<IUserDataService>();
        data.Setup(service => service.GetEpisodeUserDataForUser(user)).Returns([episodeData]);
        data.Setup(service => service.GetSeriesUserDataForUser(user)).Returns([watchedRating, ratedOnly]);
        var metadata = new Mock<Shoko.Abstractions.Metadata.Services.IMetadataService>();
        var reader = new ShokoStateReader(users.Object, data.Object, metadata.Object);

        var states = await reader.GetLibraryStateAsync("alice", default);

        states.Should().HaveCount(2);
        states.Single(state => state.SeriesId == 9).Should().Match<ShokoSeriesState>(state =>
            state.Progress == 3 && state.RatingRaw == 75);
        states.Single(state => state.SeriesId == 10).Should().Match<ShokoSeriesState>(state =>
            state.Progress == 0 && state.RatingRaw == 85);
        watchedSeries.Verify(series => series.GetUserData(It.IsAny<IUser>()), Times.Never);
        ratedSeries.Verify(series => series.GetUserData(It.IsAny<IUser>()), Times.Never);
        metadata.Verify(service => service.GetAllShokoSeries(), Times.Never);
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
        user.Setup(value => value.IsAllowedToSee(It.IsAny<IShokoSeries>())).Returns(true);
        return user.Object;
    }

    private static ISeriesUserData SeriesData(IShokoSeries series, double? rating)
    {
        var data = new Mock<ISeriesUserData>();
        data.SetupGet(value => value.SeriesID).Returns(series.ID);
        data.SetupGet(value => value.Series).Returns(series);
        data.SetupGet(value => value.UserRating).Returns(rating);
        return data.Object;
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
