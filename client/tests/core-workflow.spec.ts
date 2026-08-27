import { expect, test } from '@playwright/test'

const session = {
  shokoUsername: 'alice',
  isAdmin: true,
  pendingReviewCount: 2,
  pendingJobCount: 0,
  providers: [
    { provider: 'AniList', configured: true, connected: true, username: 'alice-list' },
    { provider: 'MyAnimeList', configured: true, connected: false },
  ],
}

const reviews = [
  {
    id: '11111111-1111-1111-1111-111111111111',
    updatedAt: '2026-08-26T12:00:00Z',
    change: {
      id: '11111111-1111-1111-1111-111111111111', seriesId: 1, aniDbAnimeId: 10,
      title: 'Safe Series', provider: 'AniList', providerMediaId: 20,
      kind: 'Advance', reviewReason: 'None', beforeProgress: 2, afterProgress: 3,
      beforeStatus: 'Watching', afterStatus: 'Watching', requiresReview: false, isActionable: true,
    },
  },
  {
    id: '22222222-2222-2222-2222-222222222222',
    updatedAt: '2026-08-26T12:00:00Z',
    change: {
      id: '22222222-2222-2222-2222-222222222222', seriesId: 2, aniDbAnimeId: 11,
      title: 'Decrease Series', provider: 'MyAnimeList', providerMediaId: 21,
      kind: 'Decrease', reviewReason: 'ProgressDecrease', beforeProgress: 8, afterProgress: 4,
      beforeStatus: 'Watching', afterStatus: 'Watching', requiresReview: true, isActionable: true,
    },
  },
]

const settings = {
  settings: { autoSync: true, syncOnlyOnCompletion: false, syncRatings: true, includeAdultSearch: false },
  providers: session.providers,
  clients: [],
}

test('dashboard loads and review defaults only safe changes', async ({ page }) => {
  let applied: string[] = []
  let authenticatedRequests = 0
  let authorizeBaseUrl = ''
  await page.addInitScript(() => {
    localStorage.setItem('apiSession', JSON.stringify({ apikey: 'browser-session-key' }))
  })
  await page.route('**/anisync-next/api/**', async route => {
    const request = route.request()
    if (request.headers().apikey === 'browser-session-key') authenticatedRequests += 1
    const url = new URL(request.url())
    const path = url.pathname
    if (path.endsWith('/session')) return route.fulfill({ json: session })
    if (path.endsWith('/settings')) return route.fulfill({ json: settings })
    if (path.endsWith('/providers/MyAnimeList/authorize')) {
      authorizeBaseUrl = url.searchParams.get('baseUrl') ?? ''
      return route.fulfill({ json: { url: 'http://127.0.0.1:4173/anisync-next/settings' } })
    }
    if (path.endsWith('/review/apply')) {
      applied = (request.postDataJSON() as { ids: string[] }).ids
      return route.fulfill({ json: [] })
    }
    if (path.endsWith('/review/refresh')) return route.fulfill({ json: reviews })
    if (path.endsWith('/review')) return route.fulfill({ json: reviews })
    return route.fulfill({ status: 404, json: { error: 'not mocked' } })
  })

  await page.goto('/anisync-next/dashboard')
  await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible()
  await expect(page.getByText('alice-list')).toBeVisible()
  await expect.poll(() => authenticatedRequests).toBeGreaterThan(0)

  await page.getByRole('link', { name: 'Review' }).click()
  await expect(page.getByText('Safe Series')).toBeVisible()
  const boxes = page.getByRole('checkbox')
  await expect(boxes.nth(0)).not.toBeChecked()
  await expect(boxes.nth(1)).not.toBeChecked()
  await page.getByRole('button', { name: 'Refresh from Shoko' }).click()
  await expect(page.getByRole('checkbox', { name: /AL Advance/ })).toBeChecked()
  await expect(page.getByRole('checkbox', { name: /MAL Decrease/ })).not.toBeChecked()
  await page.getByRole('button', { name: 'Apply selected' }).click()
  await expect.poll(() => applied).toEqual(['11111111-1111-1111-1111-111111111111'])

  await page.getByRole('link', { name: 'Settings' }).click()
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect.poll(() => authorizeBaseUrl).toBe('http://127.0.0.1:4173')
})
