using AniSync.Next.Application;
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
    IMetadataService metadataService,
    IAniSyncDiagnostics diagnostics,
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
        var linkedVideoIds = series.Episodes
            .SelectMany(episode => episode.VideoList ?? [])
            .Where(video => video.ID > 0)
            .Select(video => video.ID)
            .ToHashSet();
        var videoData = userDataService.GetVideoUserDataForUser(user)
            .Where(data => linkedVideoIds.Contains(data.VideoID))
            .ToArray();
        var seriesData = userDataService.GetSeriesUserDataForUser(user)
            .FirstOrDefault(data => data.SeriesID == seriesId);

        return Task.FromResult<ShokoSeriesState?>(BuildStateWithDiagnostics(
            shokoUsername, series, episodeData, videoData, seriesData));
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
        var episodeData = allEpisodeData
            .Where(data => data.SeriesID > 0 && data.EpisodeID > 0)
            .GroupBy(data => data.SeriesID)
            .ToDictionary(group => group.Key, group => group.AsEnumerable());
        var allVideoData = userDataService.GetVideoUserDataForUser(user).ToArray();
        var videoData = GroupVideoDataBySeries(allVideoData);
        var invalidRowCount = allSeriesData.Count(data => data.SeriesID <= 0) +
            allEpisodeData.Count(data => data.SeriesID <= 0 || data.EpisodeID <= 0) +
            allVideoData.Count(data => data.VideoID <= 0);
        if (invalidRowCount > 0)
            logger.LogWarning(
                "Skipped {Count} orphaned Shoko user-data rows with invalid series, episode, or video IDs while refreshing {Username}",
                invalidRowCount, shokoUsername);

        // Episode user-data rows survive an unwatch, so their series remain in
        // the refresh set and can produce a decrease review. Video-only rows
        // are included for file-originated watch records. Add separately rated
        // series even when the user has never watched an episode. Read existing
        // rows rather than IShokoSeries.GetUserData(), which creates a row and
        // must not be called while building a preview.
        var seriesIds = episodeData.Keys
            .Concat(videoData.Keys)
            .Concat(seriesData.Values
                .Where(data => data.UserRating.HasValue)
                .Select(data => data.SeriesID))
            .Distinct();

        var states = seriesIds
            .Select(seriesId => new
            {
                Series = metadataService.GetShokoSeriesByID(seriesId),
                EpisodeData = episodeData.GetValueOrDefault(seriesId) ?? [],
                VideoData = videoData.GetValueOrDefault(seriesId) ?? [],
                SeriesData = seriesData.GetValueOrDefault(seriesId),
            })
            .Where(group => group.Series is not null && user.IsAllowedToSee(group.Series))
            .Select(group => BuildStateWithDiagnostics(
                shokoUsername, group.Series!, group.EpisodeData, group.VideoData, group.SeriesData))
            .OrderBy(state => state.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(state => state.SeriesId)
            .ToArray();

        return Task.FromResult<IReadOnlyList<ShokoSeriesState>>(states);
    }

    internal static ShokoSeriesState BuildState(
        string shokoUsername,
        IShokoSeries series,
        IEnumerable<IEpisodeUserData> episodeData,
        double? userRating,
        IEnumerable<IVideoUserData>? videoData = null) =>
        CalculateState(shokoUsername, series, episodeData, videoData ?? [], userRating).State;

    private ShokoSeriesState BuildStateWithDiagnostics(
        string shokoUsername,
        IShokoSeries series,
        IEnumerable<IEpisodeUserData> episodeData,
        IEnumerable<IVideoUserData> videoData,
        ISeriesUserData? seriesData)
    {
        var calculation = CalculateState(
            shokoUsername, series, episodeData, videoData, seriesData?.UserRating);

        diagnostics.Write(shokoUsername, Configuration.DiagnosticLogLevel.Detailed, "shoko.state",
            $"seriesId={series.ID} metadataEpisodes={calculation.MetadataEpisodeCount} " +
            $"normalEpisodes={calculation.NormalEpisodeCount} episodeRows={calculation.EpisodeRowCount} " +
            $"resolvedEpisodeRows={calculation.ResolvedEpisodeRowCount} " +
            $"resolvedWatchedNormalEpisodes={calculation.WatchedNormalEpisodeCount} " +
            $"linkedVideoRows={calculation.VideoRowCount} " +
            $"linkedVideoFallbackEpisodes={calculation.VideoFallbackEpisodeCount} " +
            $"progress={calculation.State.Progress}");

        foreach (var trace in calculation.TraceRows)
        {
            diagnostics.Write(shokoUsername, Configuration.DiagnosticLogLevel.Trace, "shoko.episode-state",
                $"seriesId={series.ID} source={trace.Source} episodeId={trace.EpisodeId} " +
                $"resolved={trace.Resolved} type={trace.Type} number={trace.Number} " +
                $"watched={trace.Watched} fallback={trace.IsFallback}");
        }

        if (seriesData is { WatchedEpisodeCount: > 0 } && calculation.WatchedNormalEpisodeCount == 0)
        {
            logger.LogWarning(
                "AniSync Next could not resolve any watched normal episode for Shoko series {SeriesId} and user {Username}, although Shoko reports {ReportedWatchedEpisodes} watched episodes; metadata episodes={MetadataEpisodeCount}, episode rows={EpisodeRowCount}, linked video rows={VideoRowCount}",
                series.ID,
                shokoUsername,
                seriesData.WatchedEpisodeCount,
                calculation.MetadataEpisodeCount,
                calculation.EpisodeRowCount,
                calculation.VideoRowCount);
        }

        return calculation.State;
    }

    private static StateCalculation CalculateState(
        string shokoUsername,
        IShokoSeries series,
        IEnumerable<IEpisodeUserData> episodeData,
        IEnumerable<IVideoUserData> videoData,
        double? userRating)
    {
        var metadataEpisodes = series.Episodes
            .Where(episode => episode.ID > 0)
            .GroupBy(episode => episode.ID)
            .Select(group => group.First())
            .ToArray();
        var episodeIndex = metadataEpisodes.ToDictionary(episode => episode.ID);
        var normalEpisodeCount = metadataEpisodes.Count(episode =>
            episode.Type == EpisodeType.Episode && episode.EpisodeNumber > 0);
        var episodeRows = episodeData.ToArray();
        var videoRows = videoData.ToArray();
        var watchedEpisodeIds = new HashSet<int>();
        var videoFallbackEpisodeIds = new HashSet<int>();
        var traceRows = new List<StateTraceRow>();
        var resolvedEpisodeRowCount = 0;

        foreach (var data in episodeRows)
        {
            var resolved = episodeIndex.TryGetValue(data.EpisodeID, out var episode);
            if (resolved) resolvedEpisodeRowCount++;
            var watched = data.LastPlayedAt.HasValue;
            if (resolved && watched && episode!.Type == EpisodeType.Episode && episode.EpisodeNumber > 0)
                watchedEpisodeIds.Add(episode.ID);

            traceRows.Add(CreateTrace("episode", data.EpisodeID, resolved, episode, watched, false));
        }

        var seenVideoEpisodePairs = new HashSet<(int VideoId, int EpisodeId)>();
        foreach (var data in videoRows)
        {
            var watched = data.LastPlayedAt.HasValue;
            if (data.Video is not { } video) continue;
            foreach (var linkedEpisode in video.Episodes)
            {
                if (!seenVideoEpisodePairs.Add((data.VideoID, linkedEpisode.ID))) continue;
                var resolved = episodeIndex.TryGetValue(linkedEpisode.ID, out var episode);
                var isFallback = resolved && watched && episode!.Type == EpisodeType.Episode &&
                    episode.EpisodeNumber > 0 && watchedEpisodeIds.Add(episode.ID);
                if (isFallback) videoFallbackEpisodeIds.Add(episode!.ID);
                traceRows.Add(CreateTrace("video", linkedEpisode.ID, resolved, episode, watched, isFallback));
            }
        }

        var progress = watchedEpisodeIds
            .Select(episodeId => episodeIndex[episodeId].EpisodeNumber)
            .DefaultIfEmpty(0)
            .Max();
        var ratingRaw = userRating.HasValue && double.IsFinite(userRating.Value)
            ? (int?)Math.Clamp((int)Math.Round(userRating.Value * 10, MidpointRounding.AwayFromZero), 0, 100)
            : null;
        var state = new ShokoSeriesState(
            shokoUsername,
            series.ID,
            series.AnidbAnimeID,
            series.Title ?? $"AniDB {series.AnidbAnimeID}",
            progress,
            Math.Max(0, series.EpisodeCounts.Episodes),
            ratingRaw);

        return new(
            state,
            metadataEpisodes.Length,
            normalEpisodeCount,
            episodeRows.Length,
            resolvedEpisodeRowCount,
            videoRows.Length,
            watchedEpisodeIds.Count,
            videoFallbackEpisodeIds.Count,
            traceRows);
    }

    private static StateTraceRow CreateTrace(
        string source,
        int episodeId,
        bool resolved,
        IShokoEpisode? episode,
        bool watched,
        bool isFallback) => new(
            source,
            episodeId,
            resolved,
            resolved ? episode!.Type.ToString() : "unknown",
            resolved ? episode!.EpisodeNumber : 0,
            watched,
            isFallback);

    private static IReadOnlyDictionary<int, IEnumerable<IVideoUserData>> GroupVideoDataBySeries(
        IEnumerable<IVideoUserData> videoData)
    {
        var grouped = new Dictionary<int, List<IVideoUserData>>();
        foreach (var data in videoData.Where(data => data.VideoID > 0))
        {
            foreach (var seriesId in GetLinkedSeriesIds(data))
            {
                if (!grouped.TryGetValue(seriesId, out var rows))
                    grouped[seriesId] = rows = [];
                rows.Add(data);
            }
        }

        return grouped.ToDictionary(pair => pair.Key, pair => pair.Value.AsEnumerable());
    }

    private static IReadOnlySet<int> GetLinkedSeriesIds(IVideoUserData data) =>
        data.Video is { } video
            ? video.Episodes
                .Where(episode => episode.SeriesID > 0)
                .Select(episode => episode.SeriesID)
                .ToHashSet()
            : new HashSet<int>();

    private sealed record StateCalculation(
        ShokoSeriesState State,
        int MetadataEpisodeCount,
        int NormalEpisodeCount,
        int EpisodeRowCount,
        int ResolvedEpisodeRowCount,
        int VideoRowCount,
        int WatchedNormalEpisodeCount,
        int VideoFallbackEpisodeCount,
        IReadOnlyList<StateTraceRow> TraceRows);

    private sealed record StateTraceRow(
        string Source,
        int EpisodeId,
        bool Resolved,
        string Type,
        int Number,
        bool Watched,
        bool IsFallback);
}
