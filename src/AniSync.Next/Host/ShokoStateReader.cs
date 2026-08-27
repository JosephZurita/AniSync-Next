using AniSync.Next.Domain;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Services;
using Microsoft.Extensions.Logging;

namespace AniSync.Next.Host;

internal sealed class ShokoStateReader(
    IUserService userService,
    IUserDataService userDataService,
    IMetadataService metadataService,
    ILogger<ShokoStateReader> logger) : IShokoStateReader
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
            .Where(data => data.SeriesID == seriesId && data.EpisodeID > 0)
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

        var allSeriesData = userDataService.GetSeriesUserDataForUser(user).ToArray();
        var seriesData = allSeriesData
            .Where(data => data.SeriesID > 0)
            .GroupBy(data => data.SeriesID)
            .ToDictionary(group => group.Key, group => group.First());
        var allEpisodeData = userDataService.GetEpisodeUserDataForUser(user).ToArray();
        var invalidRowCount = allSeriesData.Count(data => data.SeriesID <= 0) +
            allEpisodeData.Count(data => data.SeriesID <= 0 || data.EpisodeID <= 0);
        if (invalidRowCount > 0)
            logger.LogWarning(
                "Skipped {Count} orphaned Shoko user-data rows with invalid series or episode IDs while refreshing {Username}",
                invalidRowCount, shokoUsername);

        var episodeStates = allEpisodeData
            .Where(data => data.SeriesID > 0 && data.EpisodeID > 0)
            .GroupBy(data => data.SeriesID)
            .Select(group => new
            {
                Series = metadataService.GetShokoSeriesByID(group.Key),
                EpisodeData = group.AsEnumerable(),
                Rating = seriesData.GetValueOrDefault(group.Key)?.UserRating,
            })
            .Where(group => group.Series is not null && user.IsAllowedToSee(group.Series))
            .Select(group => BuildState(shokoUsername, group.Series!, group.EpisodeData, group.Rating))
            .ToDictionary(state => state.SeriesId);

        // Episode user-data rows survive an unwatch, so their series remain in
        // the refresh set and can produce a decrease review. Add separately
        // rated series even when the user has never watched an episode. Read
        // the existing rows rather than IShokoSeries.GetUserData(), which
        // creates a row and must not be called while building a preview.
        foreach (var data in seriesData.Values.Where(data => data.UserRating.HasValue))
        {
            if (episodeStates.ContainsKey(data.SeriesID)) continue;
            var series = metadataService.GetShokoSeriesByID(data.SeriesID);
            if (series is null || !user.IsAllowedToSee(series)) continue;
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
