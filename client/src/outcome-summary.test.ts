import { describe, expect, it } from 'vitest'
import { summarizeApplyOutcomes } from './outcome-summary'
import type { SyncOutcome } from './types'

function outcome(kind: string, message?: string): SyncOutcome {
  return {
    kind,
    message,
    change: {
      id: crypto.randomUUID(), seriesId: 1, aniDbAnimeId: 2, title: 'Series',
      provider: 'MyAnimeList', providerMediaId: 3, kind: 'Advance', reviewReason: 'None',
      beforeProgress: 1, afterProgress: 2, beforeStatus: 'Watching', afterStatus: 'Watching',
      requiresReview: false, isActionable: true,
    },
  }
}

describe('apply outcome summaries', () => {
  it('only reports success for provider-verified updates', () => {
    expect(summarizeApplyOutcomes([outcome('Applied')])).toEqual({
      message: '1 applied and verified.',
      isError: false,
    })
  })

  it('surfaces provider failures instead of claiming everything applied', () => {
    const summary = summarizeApplyOutcomes([
      outcome('Applied'),
      outcome('PermanentFailure', 'provider rejected update'),
    ])

    expect(summary.isError).toBe(true)
    expect(summary.message).toContain('1 applied and verified')
    expect(summary.message).toContain('MyAnimeList: provider rejected update')
  })
})
