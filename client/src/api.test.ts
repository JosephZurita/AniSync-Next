import { afterEach, describe, expect, it, vi } from 'vitest'
import { api, readShokoApiKey } from './api'

function successfulFetch() {
  const mock = vi.fn<typeof fetch>().mockResolvedValue(new Response(JSON.stringify({ ok: true }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  }))
  vi.stubGlobal('fetch', mock)
  return mock
}

afterEach(() => {
  delete window.shokoApiKey
  localStorage.clear()
  vi.unstubAllGlobals()
})

describe('Shoko API authentication', () => {
  it('forwards an API key injected by the Shoko host', async () => {
    window.shokoApiKey = 'injected-key'
    const fetchMock = successfulFetch()

    await api('/session')

    const headers = new Headers(fetchMock.mock.calls[0][1]?.headers)
    expect(headers.get('apikey')).toBe('injected-key')
    expect(fetchMock.mock.calls[0][0]).toBe('/anisync-next/api/session')
  })

  it('forwards the API key from the active Shoko Web UI session', async () => {
    localStorage.setItem('apiSession', JSON.stringify({ apikey: 'stored-key' }))
    const fetchMock = successfulFetch()

    await api('/settings', { method: 'PUT', body: JSON.stringify({ autoSync: true }) })

    const headers = new Headers(fetchMock.mock.calls[0][1]?.headers)
    expect(headers.get('apikey')).toBe('stored-key')
    expect(headers.get('Content-Type')).toBe('application/json')
  })

  it('fails closed when the stored Shoko session is malformed', () => {
    localStorage.setItem('apiSession', '{not-json')

    expect(readShokoApiKey()).toBe('')
  })
})
