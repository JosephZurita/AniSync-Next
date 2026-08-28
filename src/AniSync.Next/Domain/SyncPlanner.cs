using System.Security.Cryptography;
using System.Text;

namespace AniSync.Next.Domain;

public sealed class SyncPlanner : ISyncPlanner
{
    public PlannedChange Plan(
        ShokoSeriesState source,
        ProviderKey provider,
        int? providerMediaId,
        ProviderListState? destination,
        string snapshotToken,
        DateTimeOffset now,
        bool syncOnlyOnCompletion,
        bool syncRatings)
    {
        var target = CreateProviderTarget(source, destination);
        var shouldSyncRating = syncRatings && source.RatingRaw is not null;
        if (providerMediaId is null)
        {
            return ApplyRatingPolicy(Create(source, provider, null, null, ChangeKind.UnresolvedMapping,
                ReviewReason.MissingMapping, snapshotToken, now, target), null, shouldSyncRating);
        }

        if (destination is null || !destination.Exists)
        {
            if (target.Progress <= 0 && shouldSyncRating)
            {
                return Create(source, provider, providerMediaId, destination, ChangeKind.Rating,
                    ReviewReason.RatingWouldCreateEntry, snapshotToken, now, target);
            }

            if (target.Progress <= 0)
            {
                return ApplyRatingPolicy(Create(source, provider, providerMediaId, destination, ChangeKind.NoChange,
                    ReviewReason.None, snapshotToken, now, target), destination, shouldSyncRating);
            }

            if (syncOnlyOnCompletion && source.TotalEpisodes > 0 && source.Progress < source.TotalEpisodes)
            {
                return ApplyRatingPolicy(Create(source, provider, providerMediaId, destination, ChangeKind.NoChange,
                    ReviewReason.None, snapshotToken, now, target), destination, shouldSyncRating);
            }

            var addition = Create(source, provider, providerMediaId, destination,
                target.Status == CanonicalListStatus.Completed ? ChangeKind.Complete : ChangeKind.Add,
                ReviewReason.None, snapshotToken, now, target);
            return ApplyRatingPolicy(addition, destination, shouldSyncRating);
        }

        var progressSyncAllowed = !syncOnlyOnCompletion ||
                                  source.TotalEpisodes <= 0 ||
                                  source.Progress >= source.TotalEpisodes;

        if (progressSyncAllowed && target.Progress < destination.Progress)
        {
            var decrease = Create(source, provider, providerMediaId, destination, ChangeKind.Decrease,
                ReviewReason.ProgressDecrease, snapshotToken, now, target);
            return ApplyRatingPolicy(decrease, destination, shouldSyncRating);
        }

        if (progressSyncAllowed &&
            (target.Progress > destination.Progress || target.Status != destination.Status))
        {
            var kind = target.Status == CanonicalListStatus.Completed
                ? ChangeKind.Complete
                : ChangeKind.Advance;
            var progressChange = Create(source, provider, providerMediaId, destination, kind,
                ReviewReason.None, snapshotToken, now, target);
            return ApplyRatingPolicy(progressChange, destination, shouldSyncRating);
        }

        if (shouldSyncRating && source.RatingRaw != destination.RatingRaw)
        {
            return Create(source, provider, providerMediaId, destination, ChangeKind.Rating,
                ReviewReason.None, snapshotToken, now, target) with
            {
                AfterProgress = destination.Progress,
                AfterStatus = destination.Status,
            };
        }

        return ApplyRatingPolicy(Create(source, provider, providerMediaId, destination, ChangeKind.NoChange,
            ReviewReason.None, snapshotToken, now, target), destination, shouldSyncRating);
    }

    private static ProviderTarget CreateProviderTarget(
        ShokoSeriesState source,
        ProviderListState? destination)
    {
        var progress = destination is { TotalEpisodes: > 0 }
            ? Math.Min(source.Progress, destination.TotalEpisodes)
            : source.Progress;
        var status = destination is { TotalEpisodes: > 0 } && progress >= destination.TotalEpisodes
            ? CanonicalListStatus.Completed
            : source.DesiredStatus;
        return new ProviderTarget(progress, status);
    }

    private static PlannedChange ApplyRatingPolicy(
        PlannedChange change,
        ProviderListState? destination,
        bool shouldSyncRating) => shouldSyncRating
            ? change
            : change with { AfterRatingRaw = destination?.RatingRaw };

    public static string CreateSnapshotToken(ShokoSeriesState source, ProviderListState? destination)
    {
        var material = string.Join('|',
            source.ShokoUsername,
            source.SeriesId,
            source.AniDbAnimeId,
            source.Progress,
            source.TotalEpisodes,
            source.RatingRaw,
            destination?.Provider,
            destination?.MediaId,
            destination?.Progress,
            destination?.TotalEpisodes,
            destination?.Status,
            destination?.RatingRaw,
            destination?.Exists);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static PlannedChange Create(
        ShokoSeriesState source,
        ProviderKey provider,
        int? providerMediaId,
        ProviderListState? destination,
        ChangeKind kind,
        ReviewReason reason,
        string snapshotToken,
        DateTimeOffset now,
        ProviderTarget target) => new(
            Guid.NewGuid(),
            source.ShokoUsername,
            source.SeriesId,
            source.AniDbAnimeId,
            source.Title,
            provider,
            providerMediaId,
            kind,
            reason,
            destination?.Progress ?? 0,
            target.Progress,
            destination?.Status,
            target.Status,
            destination?.RatingRaw,
            source.RatingRaw,
            snapshotToken,
            now,
            source.ImageUrl);

    private readonly record struct ProviderTarget(int Progress, CanonicalListStatus Status);
}
