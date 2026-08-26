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
        if (providerMediaId is null)
        {
            return Create(source, provider, null, null, ChangeKind.UnresolvedMapping,
                ReviewReason.MissingMapping, snapshotToken, now);
        }

        if (destination is null || !destination.Exists)
        {
            if (source.Progress <= 0 && source.RatingRaw is not null && syncRatings)
            {
                return Create(source, provider, providerMediaId, destination, ChangeKind.Rating,
                    ReviewReason.RatingWouldCreateEntry, snapshotToken, now);
            }

            if (source.Progress <= 0)
            {
                return Create(source, provider, providerMediaId, destination, ChangeKind.NoChange,
                    ReviewReason.None, snapshotToken, now);
            }

            if (syncOnlyOnCompletion && source.TotalEpisodes > 0 && source.Progress < source.TotalEpisodes)
            {
                return Create(source, provider, providerMediaId, destination, ChangeKind.NoChange,
                    ReviewReason.None, snapshotToken, now);
            }

            var addition = Create(source, provider, providerMediaId, destination,
                source.DesiredStatus == CanonicalListStatus.Completed ? ChangeKind.Complete : ChangeKind.Add,
                ReviewReason.None, snapshotToken, now);
            return syncRatings ? addition : addition with { AfterRatingRaw = null };
        }

        var progressSyncAllowed = !syncOnlyOnCompletion ||
                                  source.TotalEpisodes <= 0 ||
                                  source.Progress >= source.TotalEpisodes;

        if (progressSyncAllowed && source.Progress < destination.Progress)
        {
            var decrease = Create(source, provider, providerMediaId, destination, ChangeKind.Decrease,
                ReviewReason.ProgressDecrease, snapshotToken, now);
            return syncRatings ? decrease : decrease with { AfterRatingRaw = destination.RatingRaw };
        }

        if (progressSyncAllowed &&
            (source.Progress > destination.Progress || source.DesiredStatus != destination.Status))
        {
            var kind = source.DesiredStatus == CanonicalListStatus.Completed
                ? ChangeKind.Complete
                : ChangeKind.Advance;
            var progressChange = Create(source, provider, providerMediaId, destination, kind,
                ReviewReason.None, snapshotToken, now);
            return syncRatings ? progressChange : progressChange with { AfterRatingRaw = destination.RatingRaw };
        }

        if (syncRatings && source.RatingRaw != destination.RatingRaw)
        {
            return Create(source, provider, providerMediaId, destination, ChangeKind.Rating,
                ReviewReason.None, snapshotToken, now) with
            {
                AfterProgress = destination.Progress,
                AfterStatus = destination.Status,
            };
        }

        return Create(source, provider, providerMediaId, destination, ChangeKind.NoChange,
            ReviewReason.None, snapshotToken, now);
    }

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
