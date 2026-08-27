import type { ProviderKey } from './types'

export interface ReviewReference {
  seriesId: number
  aniDbAnimeId: number
  title: string
  provider: ProviderKey
  providerMediaId?: number
}

export interface MappingPrefill {
  seriesId: string
  aniDbAnimeId: string
  provider: ProviderKey
  query: string
}

export function shokoSeriesUrl(seriesId: number): string {
  return `/webui/collection/series/${seriesId}/overview`
}

export function aniDbAnimeUrl(aniDbAnimeId: number): string {
  return `https://anidb.net/anime/${aniDbAnimeId}`
}

export function providerAnimeUrl(provider: ProviderKey, mediaId?: number): string | undefined {
  if (!isPositiveInteger(mediaId)) return undefined
  return provider === 'AniList'
    ? `https://anilist.co/anime/${mediaId}`
    : `https://myanimelist.net/anime/${mediaId}`
}

export function mappingPath(reference: ReviewReference): string {
  const query = new URLSearchParams({
    seriesId: String(reference.seriesId),
    aniDbAnimeId: String(reference.aniDbAnimeId),
    provider: reference.provider,
    query: reference.title,
  })
  return `/mappings?${query}`
}

export function mappingPrefill(searchParams: URLSearchParams): MappingPrefill {
  const provider = searchParams.get('provider')
  return {
    seriesId: validId(searchParams.get('seriesId')),
    aniDbAnimeId: validId(searchParams.get('aniDbAnimeId')),
    provider: provider === 'AniList' || provider === 'MyAnimeList' ? provider : 'AniList',
    query: searchParams.get('query')?.trim() ?? '',
  }
}

function validId(value: string | null): string {
  if (!value || !/^\d+$/.test(value)) return ''
  const parsed = Number(value)
  return isPositiveInteger(parsed) ? String(parsed) : ''
}

function isPositiveInteger(value: number | undefined): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value > 0
}
