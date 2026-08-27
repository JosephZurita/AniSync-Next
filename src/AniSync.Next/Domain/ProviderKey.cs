using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace AniSync.Next.Domain;

[JsonConverter(typeof(StringEnumConverter))]
public enum ProviderKey
{
    MyAnimeList,
    AniList,
}

[JsonConverter(typeof(StringEnumConverter))]
public enum CanonicalListStatus
{
    Planning,
    Watching,
    Completed,
    Paused,
    Dropped,
}

[JsonConverter(typeof(StringEnumConverter))]
public enum ChangeKind
{
    Add,
    Advance,
    Complete,
    Decrease,
    Rating,
    NoChange,
    UnresolvedMapping,
}

[JsonConverter(typeof(StringEnumConverter))]
public enum ReviewReason
{
    None,
    ProgressDecrease,
    MissingMapping,
    RatingWouldCreateEntry,
    StalePreview,
    ManualRetry,
}

[JsonConverter(typeof(StringEnumConverter))]
public enum SyncOutcomeKind
{
    Applied,
    Unchanged,
    QueuedForReview,
    TransientFailure,
    PermanentFailure,
}
