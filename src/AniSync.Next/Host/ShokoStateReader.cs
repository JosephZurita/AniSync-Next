using AniSync.Next.Domain;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Services;

namespace AniSync.Next.Host;

internal sealed class ShokoStateReader(
    IUserService userService,
    IUserDataService userDataService,
    IMetadataService metadataService) : IShokoStateReader
{
    public Task<ShokoSeriesState?> GetSeriesStateAsync(
        string shokoUsername,
        int seriesId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = userService.GetUserByUsername(shokoUsername);
        var series = metadataService.GetShokoSeriesByID(seriesId);
        if (user is null || series is null || !user.IsAllowedToSee(series))
            return Task.FromResult<ShokoSeriesState?>(null);

        var episodeData = userDataService.GetEpisodeUserDataForUser(user)
            .Where(data => data.SeriesID == seriesId)
            .ToArray();
        var rating = userDataService.GetSeriesUserDataForUser(user)
            .FirstOrDefault(data => data.SeriesID == seriesId)
            ?.UserRating;
        return Task.FromResult<ShokoSeriesState?>(BuildState(shokoUsername, series, episodeData, rating));
    }

    public Task<IReadOnlyList<ShokoSeriesState>> GetLibraryStateAsync(
        string shokoUsername,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = userService.GetUserByUsername(shokoUsername);
        if (user is null) return Task.FromResult<IReadOnlyList<ShokoSeriesState>>([]);

        var seriesData = userDataService.GetSeriesUserDataForUser(user)
            .GroupBy(data => data.SeriesID)
            .ToDictionary(group => group.Key, group => group.First());
        var episodeStates = userDataService.GetEpisodeUserDataForUser(user)
            .Where(data => data.Series is not null)
            .GroupBy(data => data.SeriesID)
            .Where(group => user.IsAllowedToSee(group.First().Series!))
            .Select(group => BuildState(shokoUsername, group.First().Series!, group,
                seriesData.GetValueOrDefault(group.Key)?.UserRating))
            .ToDictionary(state => state.SeriesId);

        // Episode user-data rows survive an unwatch, so their series remain in
        // the refresh set and can produce a decrease review. Add separately
        // rated series even when the user has never watched an episode. Read
        // the existing rows rather than IShokoSeries.GetUserData(), which
        // creates a row and must not be called while building a preview.
        foreach (var data in seriesData.Values.Where(data => data.UserRating.HasValue && data.Series is not null))
        {
            var series = data.Series!;
            if (episodeStates.ContainsKey(series.ID) || !user.IsAllowedToSee(series)) continue;
            episodeStates[series.ID] = BuildState(shokoUsername, series, [], data.UserRating);
        }

        var states = episodeStates.Values
            .OrderBy(state => state.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(state => state.SeriesId)
            .ToArray();
        return Task.FromResult<IReadOnlyList<ShokoSeriesState>>(states);
    }

    internal static ShokoSeriesState BuildState(
        string shokoUsername,
        IShokoSeries series,
        IEnumerable<IEpisodeUserData> episodeData,
        double? userRating)
    {
        var progress = episodeData
            .Where(data => data.LastPlayedAt.HasValue && data.Episode is
            {
                Type: EpisodeType.Episode,
                EpisodeNumber: > 0,
            })
            .Select(data => data.Episode!.EpisodeNumber)
            .DefaultIfEmpty(0)
            .Max();

        var ratingRaw = userRating.HasValue && double.IsFinite(userRating.Value)
            ? (int?)Math.Clamp((int)Math.Round(userRating.Value * 10, MidpointRounding.AwayFromZero), 0, 100)
            : null;

        return new ShokoSeriesState(
            shokoUsername,
            series.ID,
            series.AnidbAnimeID,
            series.Title ?? $"AniDB {series.AnidbAnimeID}",
            progress,
            Math.Max(0, series.EpisodeCounts.Episodes),
            ratingRaw);
    }
}
