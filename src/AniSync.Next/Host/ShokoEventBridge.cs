using AniSync.Next.Persistence;
using Shoko.Abstractions.User.Enums;
using Shoko.Abstractions.User.Events;
using Shoko.Abstractions.User.Services;

namespace AniSync.Next.Host;

internal sealed class ShokoEventBridge(
    IUserDataService userDataService,
    ISyncTriggerQueue queue,
    TimeProvider timeProvider,
    ILogger<ShokoEventBridge> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        userDataService.EpisodeUserDataSaved += OnEpisodeUserDataSaved;
        userDataService.SeriesUserDataSaved += OnSeriesUserDataSaved;
        logger.LogInformation("AniSync Next is listening for Shoko watch and rating changes");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        userDataService.EpisodeUserDataSaved -= OnEpisodeUserDataSaved;
        userDataService.SeriesUserDataSaved -= OnSeriesUserDataSaved;
        return Task.CompletedTask;
    }

    private void OnEpisodeUserDataSaved(object? sender, EpisodeUserDataSavedEventArgs args)
    {
        if (args.IsImport) return;
        const EpisodeUserDataSaveReason relevant = EpisodeUserDataSaveReason.LastPlayedAt |
                                                   EpisodeUserDataSaveReason.PlaybackCount;
        if ((args.Reason & relevant) == 0) return;
        Enqueue(args.User.Username, args.Episode.SeriesID, "watch-state");
    }

    private void OnSeriesUserDataSaved(object? sender, SeriesUserDataSavedEventArgs args)
    {
        if (args.IsImport || !args.Reason.HasFlag(SeriesUserDataSaveReason.UserRating)) return;
        Enqueue(args.User.Username, args.Series.ID, "rating");
    }

    private void Enqueue(string username, int seriesId, string reason)
    {
        var trigger = new PersistedSyncTrigger(Guid.NewGuid(), username, seriesId, reason,
            timeProvider.GetUtcNow());
        if (!queue.TryEnqueue(trigger))
            logger.LogError("AniSync Next rejected a {Reason} trigger for {User}/{SeriesId}", reason, username, seriesId);
    }
}
