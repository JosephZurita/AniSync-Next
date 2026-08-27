using AniSync.Next.Domain;
using AniSync.Next.Persistence;

namespace AniSync.Next.Application;

internal sealed class SyncExecutor(
    IProviderRegistry providerRegistry,
    IPluginStateStore stateStore,
    IClock clock,
    IAniSyncDiagnostics diagnostics,
    ILogger<SyncExecutor> logger) : ISyncExecutor
{
    public async Task<SyncOutcome> ExecuteAsync(
        PlannedChange change,
        bool confirmedReview,
        CancellationToken cancellationToken)
    {
        if (change.Kind == ChangeKind.NoChange)
            return await RecordAsync(new SyncOutcome(SyncOutcomeKind.Unchanged, change, CompletedAt: clock.UtcNow,
                GroupId: change.GroupId ?? change.Id), cancellationToken);

        if (change.RequiresReview && !confirmedReview)
        {
            var outcome = new SyncOutcome(SyncOutcomeKind.QueuedForReview, change, CompletedAt: clock.UtcNow,
                GroupId: change.GroupId ?? change.Id);
            await stateStore.UpsertAsync(new ReviewItem(change.Id, change, clock.UtcNow), cancellationToken);
            return await RecordAsync(outcome, cancellationToken);
        }

        if (!change.IsActionable || change.ProviderMediaId is null)
        {
            var unresolved = new SyncOutcome(SyncOutcomeKind.QueuedForReview, change,
                "A verified provider mapping is required.", clock.UtcNow, change.GroupId ?? change.Id);
            await stateStore.UpsertAsync(new ReviewItem(change.Id, change, clock.UtcNow, unresolved.Message), cancellationToken);
            return await RecordAsync(unresolved, cancellationToken);
        }

        try
        {
            var provider = providerRegistry.Get(change.Provider);
            diagnostics.Write(change.ShokoUsername, Configuration.DiagnosticLogLevel.Basic, "sync.apply-started",
                $"provider={change.Provider} seriesId={change.SeriesId} mediaId={change.ProviderMediaId} kind={change.Kind} " +
                $"progress={change.BeforeProgress}->{change.AfterProgress} status={change.BeforeStatus}->{change.AfterStatus} " +
                $"rating={Value(change.BeforeRatingRaw)}->{Value(change.AfterRatingRaw)}");

            var acknowledged = await provider.ApplyAsync(change.ShokoUsername, change, cancellationToken);
            ValidateAcknowledgement(change, acknowledged);
            diagnostics.Write(change.ShokoUsername, Configuration.DiagnosticLogLevel.Detailed, "sync.apply-acknowledged",
                $"provider={change.Provider} seriesId={change.SeriesId} mediaId={acknowledged.MediaId} state={State(acknowledged)}");

            var verified = await provider.GetEntryAsync(change.ShokoUsername, change.ProviderMediaId.Value, cancellationToken);
            if (verified is null || !MatchesAcknowledgement(acknowledged, verified))
                throw new ProviderException(
                    $"{change.Provider} accepted the update but read-back verification did not match. " +
                    $"Acknowledged {State(acknowledged)}; read back {(verified is null ? "not listed" : State(verified))}.",
                    true);

            diagnostics.Write(change.ShokoUsername, Configuration.DiagnosticLogLevel.Basic, "sync.apply-verified",
                $"provider={change.Provider} seriesId={change.SeriesId} mediaId={verified.MediaId} state={State(verified)}");
            return await RecordAsync(new SyncOutcome(SyncOutcomeKind.Applied, change, CompletedAt: clock.UtcNow,
                GroupId: change.GroupId ?? change.Id), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderException ex) when (ex.IsTransient && !confirmedReview)
        {
            logger.LogWarning(ex,
                "AniSync Next will retry {Provider} update for {Username}/{SeriesId} media {MediaId}",
                change.Provider, change.ShokoUsername, change.SeriesId, change.ProviderMediaId);
            throw;
        }
        catch (ProviderException ex)
        {
            var kind = ex.IsTransient ? SyncOutcomeKind.TransientFailure : SyncOutcomeKind.PermanentFailure;
            logger.LogWarning(ex,
                "AniSync Next {OutcomeKind} applying {Provider} update for {Username}/{SeriesId} media {MediaId}",
                kind, change.Provider, change.ShokoUsername, change.SeriesId, change.ProviderMediaId);
            diagnostics.Write(change.ShokoUsername, Configuration.DiagnosticLogLevel.Basic, "sync.apply-failed",
                $"provider={change.Provider} seriesId={change.SeriesId} mediaId={change.ProviderMediaId} outcome={kind} transient={ex.IsTransient}");
            var outcome = new SyncOutcome(kind, change, ex.Message, clock.UtcNow,
                change.GroupId ?? change.Id);
            await stateStore.UpsertAsync(new ReviewItem(change.Id, change, clock.UtcNow, ex.Message, 1), cancellationToken);
            return await RecordAsync(outcome, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "AniSync Next encountered an unexpected error applying {Provider} update for {Username}/{SeriesId} media {MediaId}",
                change.Provider, change.ShokoUsername, change.SeriesId, change.ProviderMediaId);
            const string message = "AniSync Next encountered an unexpected provider response. Check the Shoko log for details.";
            var outcome = new SyncOutcome(SyncOutcomeKind.PermanentFailure, change, message, clock.UtcNow,
                change.GroupId ?? change.Id);
            await stateStore.UpsertAsync(new ReviewItem(change.Id, change, clock.UtcNow, message, 1), cancellationToken);
            return await RecordAsync(outcome, cancellationToken);
        }
    }

    private async Task<SyncOutcome> RecordAsync(SyncOutcome outcome, CancellationToken cancellationToken)
    {
        await stateStore.AppendAsync(outcome, cancellationToken);
        return outcome;
    }

    private static void ValidateAcknowledgement(PlannedChange change, ProviderListState acknowledged)
    {
        var expectedRating = ExpectedProviderRating(change);
        if (acknowledged.MediaId != change.ProviderMediaId ||
            acknowledged.Progress != change.AfterProgress ||
            acknowledged.Status != change.AfterStatus ||
            expectedRating is not null && acknowledged.RatingRaw != expectedRating)
            throw new ProviderException(
                $"{change.Provider} returned an unexpected state after the update. " +
                $"Requested progress={change.AfterProgress}, status={change.AfterStatus}, rating={Value(expectedRating)}; " +
                $"received {State(acknowledged)}.",
                true);
    }

    private static int? ExpectedProviderRating(PlannedChange change)
    {
        if (change.AfterRatingRaw is { } rating)
            return change.Provider == ProviderKey.MyAnimeList
                ? Math.Clamp((int)Math.Round(rating / 10d, MidpointRounding.AwayFromZero), 0, 10) * 10
                : rating;
        return change.BeforeRatingRaw is not null ? 0 : null;
    }

    private static bool MatchesAcknowledgement(ProviderListState acknowledged, ProviderListState verified) =>
        acknowledged.MediaId == verified.MediaId &&
        acknowledged.Progress == verified.Progress &&
        acknowledged.Status == verified.Status &&
        acknowledged.RatingRaw == verified.RatingRaw;

    private static string State(ProviderListState state) =>
        $"progress={state.Progress}, status={state.Status}, rating={Value(state.RatingRaw)}";

    private static string Value(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";
}
