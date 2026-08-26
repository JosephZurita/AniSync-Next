namespace AniSync.Next.Domain;

public enum ProviderKey
{
    MyAnimeList,
    AniList,
}

public enum CanonicalListStatus
{
    Planning,
    Watching,
    Completed,
    Paused,
    Dropped,
}

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

public enum ReviewReason
{
    None,
    ProgressDecrease,
    MissingMapping,
    RatingWouldCreateEntry,
    StalePreview,
    ManualRetry,
}

public enum SyncOutcomeKind
{
    Applied,
    Unchanged,
    QueuedForReview,
    TransientFailure,
    PermanentFailure,
}

