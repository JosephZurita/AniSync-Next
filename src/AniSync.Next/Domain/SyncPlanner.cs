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
        var shouldSyncRating = syncRatings && source.RatingRaw is not null;
        if (providerMediaId is null)
        {
            return ApplyRatingPolicy(Create(source, provider, null, null, ChangeKind.UnresolvedMapping,
                ReviewReason.MissingMapping, snapshotToken, now), null, shouldSyncRating);
        }

        if (destination is null || !destination.Exists)
        {
            if (source.Progress <= 0 && shouldSyncRating)
            {
                return Create(source, provider, providerMediaId, destination, ChangeKind.Rating,
                    ReviewReason.RatingWouldCreateEntry, snapshotToken, now);
            }

            if (source.Progress <= 0)
            {
                return ApplyRatingPolicy(Create(source, provider, providerMediaId, destination, ChangeKind.NoChange,
                    ReviewReason.None, snapshotToken, now), destination, shouldSyncRating);
            }

            if (syncOnlyOnCompletion && source.TotalEpisodes > 0 && source.Progress < source.TotalEpisodes)
            {
                return ApplyRatingPolicy(Create(source, provider, providerMediaId, destination, ChangeKind.NoChange,
                    ReviewReason.None, snapshotToken, now), destination, shouldSyncRating);
            }

            var addition = Create(source, provider, providerMediaId, destination,
                source.DesiredStatus == CanonicalListStatus.Completed ? ChangeKind.Complete : ChangeKind.Add,
                ReviewReason.None, snapshotToken, now);
            return ApplyRatingPolicy(addition, destination, shouldSyncRating);
        }

        var progressSyncAllowed = !syncOnlyOnCompletion ||
                                  source.TotalEpisodes <= 0 ||
                                  source.Progress >= source.TotalEpisodes;

        if (progressSyncAllowed && source.Progress < destination.Progress)
        {
            var decrease = Create(source, provider, providerMediaId, destination, ChangeKind.Decrease,
                ReviewReason.ProgressDecrease, snapshotToken, now);
            return ApplyRatingPolicy(decrease, destination, shouldSyncRating);
        }

        if (progressSyncAllowed &&
            (source.Progress > destination.Progress || source.DesiredStatus != destination.Status))
        {
            var kind = source.DesiredStatus == CanonicalListStatus.Completed
                ? ChangeKind.Complete
                : ChangeKind.Advance;
            var progressChange = Create(source, provider, providerMediaId, destination, kind,
                ReviewReason.None, snapshotToken, now);
            return ApplyRatingPolicy(progressChange, destination, shouldSyncRating);
        }

        if (shouldSyncRating && source.RatingRaw != destination.RatingRaw)
        {
            return Create(source, provider, providerMediaId, destination, ChangeKind.Rating,
                ReviewReason.None, snapshotToken, now) with
            {
                AfterProgress = destination.Progress,
                AfterStatus = destination.Status,
            };
        }

        return ApplyRatingPolicy(Create(source, provider, providerMediaId, destination, ChangeKind.NoChange,
            ReviewReason.None, snapshotToken, now), destination, shouldSyncRating);
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
        DateTimeOffset now) => new(
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
            source.Progress,
            destination?.Status,
            source.DesiredStatus,
            destination?.RatingRaw,
            source.RatingRaw,
            snapshotToken,
            now,
            source.ImageUrl);
}
