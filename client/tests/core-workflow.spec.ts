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
  settings: { autoSync: true, syncOnlyOnCompletion: false, syncRatings: true, includeAdultSearch: false, diagnosticLogLevel: 'Basic' },
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
    if (path.endsWith('/review/refresh')) return route.fulfill({ json: { items: reviews, failures: [] } })
    if (path.endsWith('/review')) return route.fulfill({ json: reviews })
    return route.fulfill({ status: 404, json: { error: 'not mocked' } })
  })

  await page.goto('/anisync-next/dashboard')
  await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible()
  await expect(page.getByText('alice-list')).toBeVisible()
  await expect.poll(() => authenticatedRequests).toBeGreaterThan(0)

  await page.getByRole('link', { name: 'Review' }).click()
  await expect(page.getByText('Safe Series')).toBeVisible()
  const safeGroup = page.getByRole('heading', { name: 'Safe Series' }).locator('xpath=ancestor::section')
  const shokoLink = safeGroup.getByRole('link', { name: 'Open in Shoko' })
  await expect(shokoLink).toHaveAttribute('href', '/webui/collection/series/1/overview')
  await expect(shokoLink).toHaveAttribute('target', '_blank')
  await expect(shokoLink).toHaveAttribute('rel', 'noopener noreferrer')
  const aniDbLink = safeGroup.getByRole('link', { name: 'AniDB 10' })
  await expect(aniDbLink).toHaveAttribute('href', 'https://anidb.net/anime/10')
  await expect(aniDbLink).toHaveAttribute('target', '_blank')
  await expect(aniDbLink).toHaveAttribute('rel', 'noopener noreferrer')
  await expect(safeGroup.getByText(/Preview updated/)).toBeVisible()
  const providerLink = safeGroup.getByRole('link', { name: 'AniList #20' })
  await expect(providerLink).toHaveAttribute('href', 'https://anilist.co/anime/20')
  await expect(providerLink).toHaveAttribute('target', '_blank')
  await expect(providerLink).toHaveAttribute('rel', 'noopener noreferrer')
  const boxes = page.getByRole('checkbox')
  const safeCheckbox = page.getByRole('checkbox', { name: /AL Advance/ })
  await expect(boxes.nth(0)).not.toBeChecked()
  await expect(boxes.nth(1)).not.toBeChecked()
  await providerLink.evaluate(element => element.addEventListener('click', event => event.preventDefault(), { once: true }))
  await providerLink.click()
  await expect(safeCheckbox).not.toBeChecked()
  await page.getByRole('button', { name: 'Refresh from Shoko' }).click()
  await expect(page.getByRole('checkbox', { name: /AL Advance/ })).toBeChecked()
  await expect(page.getByRole('checkbox', { name: /MAL Decrease/ })).not.toBeChecked()
  await page.getByRole('button', { name: 'Apply selected' }).click()
  await expect.poll(() => applied).toEqual(['11111111-1111-1111-1111-111111111111'])

  await page.getByRole('link', { name: 'Settings' }).click()
  await expect(page.getByLabel('Diagnostic log level')).toHaveValue('Basic')
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect.poll(() => authorizeBaseUrl).toBe('http://127.0.0.1:4173')
})

test('failed provider update remains reviewable and is never reported as applied', async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.setItem('apiSession', JSON.stringify({ apikey: 'browser-session-key' }))
  })
  await page.route('**/anisync-next/api/**', async route => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (path.endsWith('/session')) return route.fulfill({ json: session })
    if (path.endsWith('/review/refresh')) return route.fulfill({ json: { items: [reviews[0]], failures: [] } })
    if (path.endsWith('/review/apply')) return route.fulfill({ json: [{
      kind: 'PermanentFailure',
      change: reviews[0].change,
      message: 'MyAnimeList rejected the update (400).',
      completedAt: '2026-08-27T12:00:01Z',
    }] })
    if (path.endsWith('/review')) return route.fulfill({ json: [reviews[0]] })
    return route.fulfill({ status: 404, json: { error: 'not mocked' } })
  })

  await page.goto('/anisync-next/review')
  await page.getByRole('button', { name: 'Refresh from Shoko' }).click()
  await page.getByRole('button', { name: 'Apply selected' }).click()

  await expect(page.getByText(/1 failed or still needs review/)).toBeVisible()
  await expect(page.getByText(/MyAnimeList rejected the update \(400\)/)).toBeVisible()
  await expect(page.getByText('Safe Series')).toBeVisible()
  await expect(page.getByText('Selected changes applied.')).not.toBeVisible()
})

test('refresh keeps successful previews and displays a provider failure', async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.setItem('apiSession', JSON.stringify({ apikey: 'browser-session-key' }))
  })
  await page.route('**/anisync-next/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/session')) return route.fulfill({ json: session })
    if (path.endsWith('/review/refresh')) return route.fulfill({ json: {
      items: [reviews[0]],
      failures: [{ provider: 'MyAnimeList', error: 'Reconnect required.', isTransient: false }],
    } })
    if (path.endsWith('/review')) return route.fulfill({ json: reviews })
    return route.fulfill({ status: 404, json: { error: 'not mocked' } })
  })

  await page.goto('/anisync-next/review')
  await page.getByRole('button', { name: 'Refresh from Shoko' }).click()

  await expect(page.getByText('MyAnimeList: Reconnect required.')).toBeVisible()
  await expect(page.getByText('Safe Series')).toBeVisible()
  await expect(page.getByText('Decrease Series')).not.toBeVisible()
  await page.setViewportSize({ width: 390, height: 844 })
  await expect(page.getByRole('link', { name: 'Open in Shoko' })).toBeVisible()
})

test('unresolved review opens a validated prefilled mapping form', async ({ page }) => {
  const unresolved = {
    id: '33333333-3333-3333-3333-333333333333',
    updatedAt: '2026-08-27T12:00:00Z',
    change: {
      id: '33333333-3333-3333-3333-333333333333', seriesId: 3, aniDbAnimeId: 12,
      title: 'Unresolved & Series', provider: 'MyAnimeList',
      kind: 'UnresolvedMapping', reviewReason: 'MissingMapping', beforeProgress: 0, afterProgress: 2,
      afterStatus: 'Watching', requiresReview: true, isActionable: false,
    },
  }
  await page.addInitScript(() => {
    localStorage.setItem('apiSession', JSON.stringify({ apikey: 'browser-session-key' }))
  })
  await page.route('**/anisync-next/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/session')) return route.fulfill({ json: session })
    if (path.endsWith('/review')) return route.fulfill({ json: [unresolved] })
    if (path.endsWith('/mappings')) return route.fulfill({ json: [] })
    return route.fulfill({ status: 404, json: { error: 'not mocked' } })
  })

  await page.goto('/anisync-next/review')
  await page.getByRole('link', { name: 'Resolve mapping' }).click()

  await expect(page).toHaveURL(/\/anisync-next\/mappings\?/)
  await expect(page.getByLabel('Shoko series ID')).toHaveValue('3')
  await expect(page.getByLabel('AniDB anime ID')).toHaveValue('12')
  await expect(page.getByRole('combobox')).toHaveValue('MyAnimeList')
  await expect(page.getByLabel('Search title')).toHaveValue('Unresolved & Series')
})

test('mapping search reports loading, results, empty responses, failures, and requires confirmation', async ({ page }) => {
  const savedMappings: Array<Record<string, unknown>> = []
  await page.addInitScript(() => {
    localStorage.setItem('apiSession', JSON.stringify({ apikey: 'browser-session-key' }))
  })
  await page.route('**/anisync-next/api/**', async route => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (path.endsWith('/session')) return route.fulfill({ json: session })
    if (path.endsWith('/mappings/search')) {
      const { query } = request.postDataJSON() as { query: string }
      if (query === 'Failure') return route.fulfill({ status: 502, json: { error: 'AniList search is unavailable.' } })
      if (query === 'No Match') return route.fulfill({ json: [] })
      await new Promise(resolve => setTimeout(resolve, 400))
      return route.fulfill({ json: [{
        provider: 'AniList', mediaId: 207141, title: 'Chainsmoker Cat',
        totalEpisodes: 0, startYear: 2026,
      }] })
    }
    if (path.endsWith('/mappings') && request.method() === 'PUT') {
      savedMappings.push(request.postDataJSON() as Record<string, unknown>)
      return route.fulfill({ status: 204 })
    }
    if (path.endsWith('/mappings')) return route.fulfill({ json: [] })
    return route.fulfill({ status: 404, json: { error: 'not mocked' } })
  })

  await page.goto('/anisync-next/mappings?seriesId=440&aniDbAnimeId=19873&provider=AniList&query=Yani+Neko')
  await page.getByRole('button', { name: 'Search', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Searching…' })).toBeDisabled()
  await expect(page.getByText('Chainsmoker Cat')).toBeVisible()
  expect(savedMappings).toHaveLength(0)

  await page.getByLabel('Search title').fill('Slow old query')
  await page.getByRole('button', { name: 'Search', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Searching…' })).toBeDisabled()
  await page.getByLabel('Search title').fill('No Match')
  await page.waitForTimeout(450)
  await expect(page.getByText('Chainsmoker Cat')).not.toBeVisible()
  await page.getByRole('button', { name: 'Search', exact: true }).click()
  await expect(page.getByText('No AniList matches found. Try another title.')).toBeVisible()

  await page.getByLabel('Search title').fill('Failure')
  await expect(page.getByText('No AniList matches found. Try another title.')).not.toBeVisible()
  await page.getByRole('button', { name: 'Search', exact: true }).click()
  await expect(page.getByText('AniList search is unavailable.')).toBeVisible()

  await page.getByLabel('Search title').fill('Yani Neko')
  await page.getByRole('button', { name: 'Search', exact: true }).click()
  await page.getByRole('button', { name: 'Use this match' }).click()
  await expect.poll(() => savedMappings).toHaveLength(1)
  expect(savedMappings[0]).toMatchObject({
    seriesId: 440, aniDbAnimeId: 19873, provider: 'AniList', mediaId: 207141,
    mediaTitle: 'Chainsmoker Cat',
  })
})
