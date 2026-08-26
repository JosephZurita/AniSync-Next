import type { ReviewItem } from './types'

export function defaultSelected(items: ReviewItem[]): Set<string> {
  return new Set(items
    .filter(({ change }) => change.isActionable && !change.requiresReview)
    .map(item => item.id))
}
