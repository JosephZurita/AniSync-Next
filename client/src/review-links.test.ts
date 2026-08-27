import { aniDbAnimeUrl, mappingPath, mappingPrefill, providerAnimeUrl, shokoSeriesUrl } from './review-links'
import { describe, expect, it } from 'vitest'

describe('review reference links', () => {
  it('builds stable Shoko, AniDB, AniList, and MyAnimeList URLs', () => {
    expect(shokoSeriesUrl(42)).toBe('/webui/collection/series/42/overview')
    expect(aniDbAnimeUrl(1535)).toBe('https://anidb.net/anime/1535')
    expect(providerAnimeUrl('AniList', 20)).toBe('https://anilist.co/anime/20')
    expect(providerAnimeUrl('MyAnimeList', 21)).toBe('https://myanimelist.net/anime/21')
  })

  it('omits provider URLs for missing or invalid media IDs', () => {
    expect(providerAnimeUrl('AniList')).toBeUndefined()
    expect(providerAnimeUrl('AniList', 0)).toBeUndefined()
    expect(providerAnimeUrl('MyAnimeList', -1)).toBeUndefined()
  })

  it('builds and reads the mapping prefill contract', () => {
    const path = mappingPath({
      seriesId: 3,
      aniDbAnimeId: 4,
      title: 'Title & sequel',
      provider: 'MyAnimeList',
    })
    const params = new URLSearchParams(path.split('?')[1])

    expect(mappingPrefill(params)).toEqual({
      seriesId: '3',
      aniDbAnimeId: '4',
      provider: 'MyAnimeList',
      query: 'Title & sequel',
    })
  })

  it('ignores invalid mapping IDs and provider names', () => {
    const params = new URLSearchParams({
      seriesId: '0',
      aniDbAnimeId: 'not-a-number',
      provider: 'Unknown',
      query: '  Series  ',
    })

    expect(mappingPrefill(params)).toEqual({
      seriesId: '',
      aniDbAnimeId: '',
      provider: 'AniList',
      query: 'Series',
    })
  })
})
