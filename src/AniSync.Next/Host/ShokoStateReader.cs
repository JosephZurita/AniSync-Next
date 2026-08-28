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

        var episodeResolution = ResolveEpisodeData(userDataService.GetEpisodeUserDataForUser(user));
        WriteEpisodeResolutionDiagnostics(shokoUsername, episodeResolution, seriesId);
        var episodeData = episodeResolution.RowsBySeries.GetValueOrDefault(seriesId) ?? [];
        var videoData = userDataService.GetVideoUserDataForUser(user)
            .Where(data => data.VideoID > 0 && GetLinkedSeriesIds(data).Contains(seriesId))
            .ToArray();
        var seriesData = userDataService.GetSeriesUserDataForUser(user)
            .FirstOrDefault(data => data.SeriesID == seriesId);

        return Task.FromResult<ShokoSeriesState?>(BuildStateWithDiagnostics(
            shokoUsername, series, episodeData, videoData, seriesData,
            episodeResolution.RecoveredRowsBySeries.GetValueOrDefault(seriesId)));
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
        var episodeResolution = ResolveEpisodeData(allEpisodeData);
        WriteEpisodeResolutionDiagnostics(shokoUsername, episodeResolution);
        var episodeData = episodeResolution.RowsBySeries;
        var allVideoData = userDataService.GetVideoUserDataForUser(user).ToArray();
        var videoData = GroupVideoDataBySeries(allVideoData);
        var invalidRowCount = allSeriesData.Count(data => data.SeriesID <= 0) +
            episodeResolution.UnresolvedRowCount +
            allVideoData.Count(data => data.VideoID <= 0);
        if (invalidRowCount > 0)
            logger.LogWarning(
                "Skipped {Count} unresolved Shoko user-data rows with invalid IDs or missing metadata while refreshing {Username}",
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
                shokoUsername, group.Series!, group.EpisodeData, group.VideoData, group.SeriesData,
                episodeResolution.RecoveredRowsBySeries.GetValueOrDefault(group.Series!.ID)))
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
        ISeriesUserData? seriesData,
        int recoveredEpisodeRows)
    {
        var calculation = CalculateState(
            shokoUsername, series, episodeData, videoData, seriesData?.UserRating);

        diagnostics.Write(shokoUsername, Configuration.DiagnosticLogLevel.Detailed, "shoko.state",
            $"seriesId={series.ID} metadataEpisodes={calculation.MetadataEpisodeCount} " +
            $"normalEpisodes={calculation.NormalEpisodeCount} episodeRows={calculation.EpisodeRowCount} " +
            $"resolvedEpisodeRows={calculation.ResolvedEpisodeRowCount} " +
            $"recoveredEpisodeRows={recoveredEpisodeRows} " +
            $"resolvedWatchedNormalEpisodes={calculation.WatchedNormalEpisodeCount} " +
            $"resolvedWatchedSpecialEpisodes={calculation.WatchedSpecialEpisodeCount} " +
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

        var resolvedWatchedCount = calculation.WatchedNormalEpisodeCount + calculation.WatchedSpecialEpisodeCount;
        if (seriesData is { } && seriesData.WatchedEpisodeCount != resolvedWatchedCount)
        {
            logger.LogWarning(
                "AniSync Next resolved watch state differs from Shoko aggregate statistics for series {SeriesId} and user {Username}; aggregate watched episodes or specials={ReportedWatchedEpisodes}, resolved normal={ResolvedNormalEpisodes}, resolved specials={ResolvedSpecialEpisodes}, metadata episodes={MetadataEpisodeCount}, episode rows={EpisodeRowCount}, linked video rows={VideoRowCount}",
                series.ID,
                shokoUsername,
                seriesData.WatchedEpisodeCount,
                calculation.WatchedNormalEpisodeCount,
                calculation.WatchedSpecialEpisodeCount,
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
        var watchedSpecialEpisodeIds = new HashSet<int>();
        var videoFallbackEpisodeIds = new HashSet<int>();
        var traceRows = new List<StateTraceRow>();
        var resolvedEpisodeRowCount = 0;

        foreach (var data in episodeRows)
        {
            var resolved = episodeIndex.TryGetValue(data.EpisodeID, out var episode);
            if (resolved) resolvedEpisodeRowCount++;
            var watched = data.LastPlayedAt.HasValue;
            if (resolved && watched && episode!.EpisodeNumber > 0)
            {
                if (episode.Type == EpisodeType.Episode)
                    watchedEpisodeIds.Add(episode.ID);
                else if (episode.Type == EpisodeType.Special)
                    watchedSpecialEpisodeIds.Add(episode.ID);
            }

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
                if (resolved && watched && episode!.Type == EpisodeType.Special && episode.EpisodeNumber > 0)
                    watchedSpecialEpisodeIds.Add(episode.ID);
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
            watchedSpecialEpisodeIds.Count,
            videoFallbackEpisodeIds.Count,
            traceRows);
    }

    private EpisodeDataResolution ResolveEpisodeData(IEnumerable<IEpisodeUserData> episodeData)
    {
        var grouped = new Dictionary<int, List<IEpisodeUserData>>();
        var recoveredRowsBySeries = new Dictionary<int, int>();
        var episodeIndex = new Dictionary<int, IShokoEpisode?>();
        var traceRows = new List<EpisodeResolutionTrace>();
        var totalRowCount = 0;
        var resolvedRowCount = 0;
        var recoveredRowCount = 0;
        var unresolvedRowCount = 0;

        foreach (var data in episodeData)
        {
            totalRowCount++;
            IShokoEpisode? episode = null;
            if (data.EpisodeID > 0)
            {
                if (!episodeIndex.TryGetValue(data.EpisodeID, out episode))
                {
                    episode = metadataService.GetShokoEpisodeByID(data.EpisodeID);
                    episodeIndex[data.EpisodeID] = episode;
                }
            }

            if (episode is null || episode.SeriesID <= 0)
            {
                unresolvedRowCount++;
                traceRows.Add(new(data.EpisodeID, data.SeriesID, null, false, data.LastPlayedAt.HasValue));
                continue;
            }

            resolvedRowCount++;
            if (!grouped.TryGetValue(episode.SeriesID, out var rows))
                grouped[episode.SeriesID] = rows = [];
            rows.Add(data);

            if (data.SeriesID == episode.SeriesID) continue;
            recoveredRowCount++;
            recoveredRowsBySeries[episode.SeriesID] = recoveredRowsBySeries.GetValueOrDefault(episode.SeriesID) + 1;
            traceRows.Add(new(data.EpisodeID, data.SeriesID, episode.SeriesID, true, data.LastPlayedAt.HasValue));
        }

        return new(
            grouped.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<IEpisodeUserData>)pair.Value),
            recoveredRowsBySeries,
            totalRowCount,
            resolvedRowCount,
            recoveredRowCount,
            unresolvedRowCount,
            traceRows);
    }

    private void WriteEpisodeResolutionDiagnostics(
        string shokoUsername,
        EpisodeDataResolution resolution,
        int? seriesId = null)
    {
        var traceRows = seriesId.HasValue
            ? resolution.TraceRows.Where(row =>
                row.ResolvedSeriesId == seriesId || row.ReportedSeriesId == seriesId).ToArray()
            : resolution.TraceRows;
        var resolvedRowCount = seriesId.HasValue
            ? resolution.RowsBySeries.GetValueOrDefault(seriesId.Value)?.Count ?? 0
            : resolution.ResolvedRowCount;
        var recoveredRowCount = seriesId.HasValue
            ? resolution.RecoveredRowsBySeries.GetValueOrDefault(seriesId.Value)
            : resolution.RecoveredRowCount;
        var unresolvedRowCount = seriesId.HasValue
            ? traceRows.Count(row => row.ResolvedSeriesId is null)
            : resolution.UnresolvedRowCount;
        var totalRowCount = seriesId.HasValue
            ? resolvedRowCount + unresolvedRowCount
            : resolution.TotalRowCount;

        diagnostics.Write(shokoUsername, Configuration.DiagnosticLogLevel.Detailed, "shoko.episode-resolution",
            $"scopeSeriesId={(seriesId?.ToString() ?? "all")} rows={totalRowCount} " +
            $"resolved={resolvedRowCount} recoveredRows={recoveredRowCount} unresolved={unresolvedRowCount}");

        foreach (var trace in traceRows)
        {
            diagnostics.Write(shokoUsername, Configuration.DiagnosticLogLevel.Trace, "shoko.episode-resolution-row",
                $"episodeId={trace.EpisodeId} reportedSeriesId={trace.ReportedSeriesId} " +
                $"resolvedSeriesId={(trace.ResolvedSeriesId?.ToString() ?? "none")} " +
                $"recovered={trace.Recovered} watched={trace.Watched}");
        }
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
        int WatchedSpecialEpisodeCount,
        int VideoFallbackEpisodeCount,
        IReadOnlyList<StateTraceRow> TraceRows);

    private sealed record EpisodeDataResolution(
        IReadOnlyDictionary<int, IReadOnlyList<IEpisodeUserData>> RowsBySeries,
        IReadOnlyDictionary<int, int> RecoveredRowsBySeries,
        int TotalRowCount,
        int ResolvedRowCount,
        int RecoveredRowCount,
        int UnresolvedRowCount,
        IReadOnlyList<EpisodeResolutionTrace> TraceRows);

    private sealed record EpisodeResolutionTrace(
        int EpisodeId,
        int ReportedSeriesId,
        int? ResolvedSeriesId,
        bool Recovered,
        bool Watched);

    private sealed record StateTraceRow(
        string Source,
        int EpisodeId,
        bool Resolved,
        string Type,
        int Number,
        bool Watched,
        bool IsFallback);
}
