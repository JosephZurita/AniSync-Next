import { defaultSelected } from './review-selection'
import type { ReviewItem } from './types'
import { expect, test } from 'vitest'

function item(id: string, requiresReview: boolean, isActionable = true): ReviewItem {
  return {
    id,
    updatedAt: new Date(0).toISOString(),
    change: {
      id,
      seriesId: 1,
      aniDbAnimeId: 2,
      title: id,
      provider: 'AniList',
      kind: requiresReview ? 'Decrease' : 'Advance',
      reviewReason: requiresReview ? 'ProgressDecrease' : 'None',
      beforeProgress: 1,
      afterProgress: 2,
      afterStatus: 'Watching',
      requiresReview,
      isActionable,
    },
  }
}

test('selects safe forward changes and leaves risky or unresolved changes clear', () => {
  expect([...defaultSelected([item('safe', false), item('decrease', true), item('missing', true, false)])])
    .toEqual(['safe'])
})
