const base = '/anisync-next/api'

declare global {
  interface Window {
    shokoApiKey?: string
  }
}

export function readShokoApiKey(): string {
  const injected = window.shokoApiKey?.trim()
  if (injected) return injected

  try {
    const session = localStorage.getItem('apiSession')
    if (!session) return ''
    const apiKey = (JSON.parse(session) as { apikey?: unknown }).apikey
    return typeof apiKey === 'string' ? apiKey.trim() : ''
  } catch {
    return ''
  }
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers)
  if (init?.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  const apiKey = readShokoApiKey()
  if (apiKey && !headers.has('apikey')) headers.set('apikey', apiKey)

  const response = await fetch(`${base}${path}`, {
    credentials: 'same-origin',
    ...init,
    headers,
  })
  if (!response.ok) {
    const error = await response.json().catch(() => ({ error: response.statusText })) as { error?: string }
    throw new Error(error.error || `Request failed (${response.status})`)
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

export function json(method: string, body: unknown): RequestInit {
  return { method, body: JSON.stringify(body) }
}
