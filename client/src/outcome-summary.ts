import type { SyncOutcome } from './types'

export interface ApplyOutcomeSummary {
  message: string
  isError: boolean
}

export function summarizeApplyOutcomes(outcomes: SyncOutcome[]): ApplyOutcomeSummary {
  const applied = outcomes.filter(outcome => outcome.kind === 'Applied').length
  const unchanged = outcomes.filter(outcome => outcome.kind === 'Unchanged').length
  const failures = outcomes.filter(outcome => outcome.kind !== 'Applied' && outcome.kind !== 'Unchanged')
  const completed = [
    applied ? `${applied} applied and verified` : '',
    unchanged ? `${unchanged} already current` : '',
  ].filter(Boolean)

  if (failures.length === 0) {
    return {
      message: `${completed.join('; ') || 'No changes were required'}.`,
      isError: false,
    }
  }

  const details = failures.map(outcome =>
    `${outcome.change.provider === 'MyAnimeList' ? 'MyAnimeList' : 'AniList'}: ${outcome.message || outcome.kind}`)
    .join(' ')
  return {
    message: `${completed.length ? `${completed.join('; ')}. ` : ''}${failures.length} failed or still needs review. ${details}`,
    isError: true,
  }
}
