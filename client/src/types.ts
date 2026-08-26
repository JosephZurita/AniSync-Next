export type ProviderKey = 'MyAnimeList' | 'AniList'
export type ChangeKind = 'Add' | 'Advance' | 'Complete' | 'Decrease' | 'Rating' | 'NoChange' | 'UnresolvedMapping'
export type ReviewReason = 'None' | 'ProgressDecrease' | 'MissingMapping' | 'RatingWouldCreateEntry' | 'StalePreview' | 'ManualRetry'

export interface ProviderConnection {
  provider: ProviderKey
  configured: boolean
  connected: boolean
  username?: string
}

export interface Session {
  shokoUsername: string
  isAdmin: boolean
  providers: ProviderConnection[]
  pendingReviewCount: number
  pendingJobCount: number
}

export interface UserSettings {
  autoSync: boolean
  syncOnlyOnCompletion: boolean
  syncRatings: boolean
  includeAdultSearch: boolean
}

export interface ClientSettings {
  provider: ProviderKey
  clientId?: string
  secretConfigured: boolean
}

export interface SettingsResponse {
  settings: UserSettings
  providers: ProviderConnection[]
  clients?: ClientSettings[]
}

export interface PlannedChange {
  id: string
  seriesId: number
  aniDbAnimeId: number
  title: string
  provider: ProviderKey
  providerMediaId?: number
  kind: ChangeKind
  reviewReason: ReviewReason
  beforeProgress: number
  afterProgress: number
  beforeStatus?: string
  afterStatus: string
  beforeRatingRaw?: number
  afterRatingRaw?: number
  imageUrl?: string
  groupId?: string
  requiresReview: boolean
  isActionable: boolean
}

export interface ReviewItem {
  id: string
  change: PlannedChange
  updatedAt: string
  error?: string
}

export interface Mapping {
  aniDbAnimeId: number
  provider: ProviderKey
  mediaId: number
  mediaTitle: string
  isUserVerified: boolean
  updatedAt: string
}

export interface SearchResult {
  provider: ProviderKey
  mediaId: number
  title: string
  totalEpisodes: number
  startYear?: number
  imageUrl?: string
}

export interface SyncOutcome {
  kind: string
  change: PlannedChange
  message?: string
  completedAt?: string
  groupId?: string
}
