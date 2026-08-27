import { useCallback, useEffect, useMemo, useState, type FormEvent, type ReactNode } from 'react'
import { Link, NavLink, Navigate, Route, Routes, useSearchParams } from 'react-router-dom'
import { api, json } from './api'
import { aniDbAnimeUrl, mappingPath, mappingPrefill, providerAnimeUrl, shokoSeriesUrl } from './review-links'
import { defaultSelected } from './review-selection'
import type { ClientSettings, Mapping, PlannedChange, ProviderKey, ReviewItem, ReviewRefreshResult, SearchResult, Session, SettingsResponse, SyncOutcome, UserSettings } from './types'

function useRemote<T>(path: string) {
  const [data, setData] = useState<T>()
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(true)
  const reload = useCallback(async () => {
    setLoading(true); setError('')
    try { setData(await api<T>(path)) } catch (value) { setError(asMessage(value)) } finally { setLoading(false) }
  }, [path])
  useEffect(() => {
    let active = true
    void api<T>(path)
      .then(value => { if (active) setData(value) })
      .catch(value => { if (active) setError(asMessage(value)) })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [path])
  return { data, error, loading, reload, setData }
}

function Shell({ session, children }: { session?: Session, children: ReactNode }) {
  return <div className="shell">
    <aside>
      <div className="brand"><span className="brand-mark">A</span><div><strong>AniSync Next</strong><small>Shoko outbound sync</small></div></div>
      <nav>{['Dashboard', 'Review', 'Mappings', 'Settings', 'History'].map(name =>
        <NavLink key={name} to={`/${name.toLowerCase()}`}>{name}</NavLink>)}</nav>
      <div className="identity"><span className="status-dot" />{session?.shokoUsername ?? 'Connecting…'}</div>
    </aside>
    <main>{children}</main>
  </div>
}

function Page({ title, subtitle, action, children }: { title: string, subtitle: string, action?: ReactNode, children: ReactNode }) {
  return <><header className="page-head"><div><h1>{title}</h1><p>{subtitle}</p></div>{action}</header>{children}</>
}

function Dashboard({ session }: { session?: Session }) {
  return <Page title="Dashboard" subtitle="The current state of outbound synchronization.">
    <div className="metrics">
      <Metric value={session?.pendingReviewCount ?? '—'} label="Pending changes" />
      <Metric value={session?.pendingJobCount ?? '—'} label="Queued jobs" />
      <Metric value={session?.providers.filter(p => p.connected).length ?? '—'} label="Connected providers" />
    </div>
    <section className="panel"><h2>Connections</h2><div className="provider-grid">
      {session?.providers.map(provider => <article key={provider.provider} className="provider-card">
        <span className={`pill ${provider.connected ? 'good' : ''}`}>{provider.connected ? 'Connected' : 'Disconnected'}</span>
        <h3>{prettyProvider(provider.provider)}</h3>
        <p>{provider.username || (provider.configured ? 'Ready to connect' : 'Admin setup required')}</p>
      </article>)}
    </div></section>
    <section className="notice"><strong>One-way by design.</strong> Shoko is the only source of truth. AniSync Next never imports provider changes into Shoko.</section>
  </Page>
}

function Review() {
  const remote = useRemote<ReviewItem[]>('/review')
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const [refreshFailed, setRefreshFailed] = useState(false)
  const refresh = async () => {
    setBusy(true); setMessage(''); setRefreshFailed(false)
    try {
      const result = await api<ReviewRefreshResult>('/review/refresh', { method: 'POST' })
      remote.setData(result.items); setSelected(defaultSelected(result.items))
      if (result.failures.length) {
        setRefreshFailed(true)
        setMessage(result.failures.map(failure => `${prettyProvider(failure.provider)}: ${failure.error}`).join(' '))
      } else {
        setMessage(result.items.length ? 'Preview refreshed from current Shoko and provider state.' : 'Everything is in sync.')
      }
    } catch (value) { setRefreshFailed(true); setMessage(asMessage(value)) } finally { setBusy(false) }
  }
  const apply = async () => {
    setBusy(true); setMessage('')
    try {
      await api('/review/apply', json('POST', { ids: [...selected] }))
      await remote.reload(); setSelected(new Set()); setMessage('Selected changes applied.')
    } catch (value) { setMessage(asMessage(value)) } finally { setBusy(false) }
  }
  const groups = useMemo(() => groupBySeries(remote.data ?? []), [remote.data])
  return <Page title="Review" subtitle="Only differences that still need action are shown."
    action={<button onClick={() => void refresh()} disabled={busy}>Refresh from Shoko</button>}>
    {(remote.error || message) && <div className={`notice ${remote.error || refreshFailed ? 'error' : ''}`}>{remote.error || message}</div>}
    {remote.loading ? <Empty text="Loading current preview…" /> : groups.length === 0 ? <Empty text="No pending changes. Refresh whenever Shoko watch state changes." /> :
      <div className="review-list">{groups.map(group => <section className="panel review-group" key={group.seriesId}>
        <div className="series-title"><div><h2>{group.title}</h2><div className="reference-bar">
          <a href={shokoSeriesUrl(group.seriesId)} target="_blank" rel="noopener noreferrer">Open in Shoko</a>
          <a href={aniDbAnimeUrl(group.aniDbAnimeId)} target="_blank" rel="noopener noreferrer">AniDB {group.aniDbAnimeId}</a>
          <span>Preview updated {formatTimestamp(group.updatedAt)}</span>
        </div></div></div>
        {group.items.map(item => <ReviewRow key={item.id} item={item} selected={selected.has(item.id)} onToggle={() => {
          if (!item.change.isActionable) return
          setSelected(current => {
            const next = new Set(current)
            if (next.has(item.id)) next.delete(item.id)
            else next.add(item.id)
            return next
          })
        }} />)}
      </section>)}</div>}
    <div className="action-bar"><span>{selected.size} selected</span><button onClick={() => void apply()} disabled={busy || selected.size === 0}>Apply selected</button></div>
  </Page>
}

function ReviewRow({ item, selected, onToggle }: { item: ReviewItem, selected: boolean, onToggle: () => void }) {
  const change = item.change
  const providerUrl = providerAnimeUrl(change.provider, change.providerMediaId)
  return <div className={`review-row ${change.requiresReview ? 'risky' : ''}`}>
    <input type="checkbox" aria-label={`${shortProvider(change.provider)} ${change.kind} ${change.title}`} checked={selected} disabled={!change.isActionable} onChange={onToggle} />
    <div className="provider-badge">{shortProvider(change.provider)}</div>
    <div className="change-main"><strong>{change.kind}</strong><span>{describeChange(change)}</span>{item.error && <small>{item.error}</small>}<div className="provider-reference">
      {providerUrl
        ? <a href={providerUrl} target="_blank" rel="noopener noreferrer">{prettyProvider(change.provider)} #{change.providerMediaId}</a>
        : <Link to={mappingPath(change)}>Resolve mapping</Link>}
    </div></div>
    <span className={`pill ${change.requiresReview ? 'warn' : 'good'}`}>{reason(change)}</span>
  </div>
}

function Mappings() {
  const [searchParams] = useSearchParams()
  const remote = useRemote<Mapping[]>('/mappings')
  const [form, setForm] = useState(() => mappingPrefill(searchParams))
  const [results, setResults] = useState<SearchResult[]>([])
  const [error, setError] = useState('')
  const search = async (event: FormEvent) => {
    event.preventDefault(); setError('')
    try { setResults(await api('/mappings/search', json('POST', { seriesId: Number(form.seriesId), provider: form.provider, query: form.query }))) }
    catch (value) { setError(asMessage(value)) }
  }
  const save = async (result: SearchResult) => {
    try {
      await api('/mappings', json('PUT', { seriesId: Number(form.seriesId), aniDbAnimeId: Number(form.aniDbAnimeId), provider: form.provider, mediaId: result.mediaId, mediaTitle: result.title }))
      setResults([]); await remote.reload()
    } catch (value) { setError(asMessage(value)) }
  }
  return <Page title="Mappings" subtitle="Confirm unresolved provider IDs; fuzzy matches are never selected automatically.">
    {error && <div className="notice error">{error}</div>}
    <section className="panel"><h2>Find a provider title</h2><form className="mapping-form" onSubmit={event => void search(event)}>
      <input aria-label="Shoko series ID" placeholder="Shoko series ID" value={form.seriesId} onChange={e => setForm({ ...form, seriesId: e.target.value })} />
      <input aria-label="AniDB anime ID" placeholder="AniDB anime ID" value={form.aniDbAnimeId} onChange={e => setForm({ ...form, aniDbAnimeId: e.target.value })} />
      <select value={form.provider} onChange={e => setForm({ ...form, provider: e.target.value as ProviderKey })}><option>AniList</option><option>MyAnimeList</option></select>
      <input aria-label="Search title" placeholder="Exact title to search" value={form.query} onChange={e => setForm({ ...form, query: e.target.value })} />
      <button>Search</button>
    </form>
    {results.map(result => <div className="mapping-result" key={result.mediaId}><div><strong>{result.title}</strong><small>{result.startYear || 'Year unknown'} · {result.totalEpisodes || '?'} episodes</small></div><button onClick={() => void save(result)}>Use this match</button></div>)}</section>
    <section className="panel"><h2>Saved mappings</h2>{remote.data?.length ? <div className="table">
      {remote.data.map(mapping => <div className="table-row" key={`${mapping.aniDbAnimeId}-${mapping.provider}`}><strong>{mapping.mediaTitle}</strong><span>AniDB {mapping.aniDbAnimeId}</span><span>{prettyProvider(mapping.provider)} {mapping.mediaId}</span><button className="secondary" onClick={() => void api(`/mappings/${mapping.aniDbAnimeId}/${mapping.provider}`, { method: 'DELETE' }).then(remote.reload).catch(value => setError(asMessage(value)))}>{mapping.isUserVerified ? 'Remove verified' : 'Forget database match'}</button></div>)}
    </div> : <Empty text="No mappings have been persisted yet." />}</section>
  </Page>
}

function Settings() {
  const remote = useRemote<SettingsResponse>('/settings')
  const [message, setMessage] = useState('')
  const saveSettings = async (settings: UserSettings) => {
    try { const saved = await api<SettingsResponse>('/settings', json('PUT', settings)); remote.setData(saved); setMessage('Settings saved.') }
    catch (value) { setMessage(asMessage(value)) }
  }
  return <Page title="Settings" subtitle="A deliberately small set of settings with real runtime behavior.">
    {message && <div className="notice">{message}</div>}
    {remote.data && <>
      <section className="panel"><h2>Synchronization</h2>{([
        ['autoSync', 'Automatic sync', 'Queue changes when Shoko watch state or rating changes.'],
        ['syncOnlyOnCompletion', 'Only sync progress on completion', 'Rating changes may still sync to an existing provider entry.'],
        ['syncRatings', 'Synchronize ratings', 'Uses a canonical 0–100 score and converts inside each provider.'],
        ['includeAdultSearch', 'Include adult titles in search', 'Affects manual mapping searches only.'],
      ] as const).map(([key, title, description]) => <label className="toggle-row" key={key}><div><strong>{title}</strong><small>{description}</small></div><input type="checkbox" checked={remote.data!.settings[key]} onChange={e => void saveSettings({ ...remote.data!.settings, [key]: e.target.checked })} /></label>)}</section>
      <section className="panel"><h2>Provider connections</h2>{remote.data.providers.map(connection => <div className="connection" key={connection.provider}><div><strong>{prettyProvider(connection.provider)}</strong><small>{connection.connected ? `Connected as ${connection.username}` : connection.configured ? 'Ready to connect' : 'Client credentials required'}</small></div><div className="button-group"><button disabled={!connection.configured} onClick={() => void connect(connection.provider)}>{connection.connected ? 'Reconnect' : 'Connect'}</button>{connection.connected && <button className="secondary" onClick={() => void api(`/providers/${connection.provider}`, { method: 'DELETE' }).then(remote.reload).catch(value => setMessage(asMessage(value)))}>Disconnect</button>}</div></div>)}</section>
      {remote.data.clients && <AdminClients clients={remote.data.clients} onSaved={() => void remote.reload()} />}
    </>}
  </Page>
}

function AdminClients({ clients, onSaved }: { clients: ClientSettings[], onSaved: () => void }) {
  const [drafts, setDrafts] = useState<Record<string, { clientId: string, secret: string }>>(() => Object.fromEntries(clients.map(client => [client.provider, { clientId: client.clientId ?? '', secret: '' }])))
  const save = async (client: ClientSettings) => {
    const draft = drafts[client.provider]
    await api('/provider-client', json('PUT', { provider: client.provider, clientId: draft.clientId, secretSpecified: !!draft.secret, clearSecret: false, clientSecret: draft.secret || null }))
    setDrafts(current => ({ ...current, [client.provider]: { ...current[client.provider], secret: '' } })); onSaved()
  }
  const clear = async (client: ClientSettings) => {
    await api('/provider-client', json('PUT', { provider: client.provider, clientId: drafts[client.provider].clientId, secretSpecified: true, clearSecret: true, clientSecret: null }))
    onSaved()
  }
  return <section className="panel"><h2>Provider API clients <span className="pill">Admin</span></h2><p className="muted">Saved secrets are never returned. Leave the secret empty to preserve it.</p>{clients.map(client => <div className="client-form" key={client.provider}><strong>{prettyProvider(client.provider)}</strong><input aria-label={`${client.provider} client ID`} placeholder="Client ID" value={drafts[client.provider].clientId} onChange={e => setDrafts({ ...drafts, [client.provider]: { ...drafts[client.provider], clientId: e.target.value } })} /><input aria-label={`${client.provider} client secret`} type="password" placeholder={client.secretConfigured ? 'Secret configured (leave blank to preserve)' : 'Client secret'} value={drafts[client.provider].secret} onChange={e => setDrafts({ ...drafts, [client.provider]: { ...drafts[client.provider], secret: e.target.value } })} /><div className="button-group"><button onClick={() => void save(client)}>Save</button>{client.secretConfigured && <button className="secondary" onClick={() => void clear(client)}>Clear secret</button>}</div></div>)}</section>
}

function History() {
  const remote = useRemote<SyncOutcome[]>('/history?limit=200')
  return <Page title="History" subtitle="Applied changes and failures, grouped by the series and provider that produced them.">
    {remote.data?.length ? <div className="history-groups">{groupHistory(remote.data).map(group => <section className="panel" key={group.id}>
      <div className="history-head"><div><h2>{group.entries[0].change.title}</h2><small>{group.completedAt ? new Date(group.completedAt).toLocaleString() : 'Pending'}</small></div><span>{group.entries.length} provider result{group.entries.length === 1 ? '' : 's'}</span></div>
      {group.entries.map((entry, index) => <div className="history-entry" key={`${entry.change.id}-${index}`}><span className={`pill ${entry.kind === 'Applied' ? 'good' : 'warn'}`}>{entry.kind}</span><strong>{prettyProvider(entry.change.provider)}</strong><span>{entry.message || describeChange(entry.change)}</span></div>)}
    </section>)}</div> : <section className="panel"><Empty text="No synchronization history yet." /></section>}
  </Page>
}

export default function App() {
  const session = useRemote<Session>('/session')
  return <Shell session={session.data}>{session.error ? <Empty text={session.error} /> : <Routes>
    <Route path="/dashboard" element={<Dashboard session={session.data} />} />
    <Route path="/review" element={<Review />} />
    <Route path="/mappings" element={<Mappings />} />
    <Route path="/settings" element={<Settings />} />
    <Route path="/history" element={<History />} />
    <Route path="*" element={<Navigate to="/dashboard" replace />} />
  </Routes>}</Shell>
}

function Metric({ value, label }: { value: string | number, label: string }) { return <div className="metric"><strong>{value}</strong><span>{label}</span></div> }
function Empty({ text }: { text: string }) { return <div className="empty">{text}</div> }
function asMessage(value: unknown) { return value instanceof Error ? value.message : 'Something went wrong.' }
function prettyProvider(provider: ProviderKey) { return provider === 'MyAnimeList' ? 'MyAnimeList' : 'AniList' }
function shortProvider(provider: ProviderKey) { return provider === 'MyAnimeList' ? 'MAL' : 'AL' }
function connect(provider: ProviderKey) {
  const query = new URLSearchParams({ baseUrl: window.location.origin })
  void api<{ url: string }>(`/providers/${provider}/authorize?${query}`).then(result => { window.location.href = result.url })
}
function describeChange(change: PlannedChange) {
  if (change.kind === 'Rating') return `Rating ${change.beforeRatingRaw ?? 'none'} → ${change.afterRatingRaw ?? 'none'}`
  if (change.kind === 'UnresolvedMapping') return 'Provider ID needs a manual match'
  return `Progress ${change.beforeProgress} → ${change.afterProgress} · ${change.beforeStatus ?? 'not listed'} → ${change.afterStatus}`
}
function reason(change: PlannedChange) {
  if (change.reviewReason === 'ProgressDecrease') return 'Decrease—review'
  if (change.reviewReason === 'MissingMapping') return 'Mapping required'
  if (change.reviewReason === 'RatingWouldCreateEntry') return 'Would add entry'
  return 'Safe forward change'
}
function groupBySeries(items: ReviewItem[]) {
  const grouped = new Map<number, { seriesId: number, aniDbAnimeId: number, title: string, updatedAt: string, items: ReviewItem[] }>()
  for (const item of items) {
    const group = grouped.get(item.change.seriesId) ?? { seriesId: item.change.seriesId, aniDbAnimeId: item.change.aniDbAnimeId, title: item.change.title, updatedAt: item.updatedAt, items: [] }
    group.items.push(item); grouped.set(item.change.seriesId, group)
    if (Date.parse(item.updatedAt) > Date.parse(group.updatedAt)) group.updatedAt = item.updatedAt
  }
  return [...grouped.values()].sort((a, b) => a.title.localeCompare(b.title))
}
function groupHistory(entries: SyncOutcome[]) {
  const groups = new Map<string, { id: string, completedAt?: string, entries: SyncOutcome[] }>()
  for (const entry of entries) {
    const id = entry.groupId || entry.change.groupId || entry.change.id
    const group = groups.get(id) ?? { id, completedAt: entry.completedAt, entries: [] }
    group.entries.push(entry)
    if (!group.completedAt && entry.completedAt) group.completedAt = entry.completedAt
    groups.set(id, group)
  }
  return [...groups.values()].sort((a, b) => (b.completedAt || '').localeCompare(a.completedAt || ''))
}
function formatTimestamp(value: string) {
  const timestamp = new Date(value)
  return Number.isNaN(timestamp.getTime()) ? 'unknown' : timestamp.toLocaleString()
}
