using AniSync.Next.Application;
using AniSync.Next.Domain;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AniSync.Next.Providers;

internal sealed class MyAnimeListProvider(ProviderHttpTransport transport) : ISyncProvider
{
    private const string Api = "https://api.myanimelist.net/v2";

    public ProviderKey Key => ProviderKey.MyAnimeList;

    public async Task<ProviderAccount?> GetAccountAsync(string shokoUsername, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(shokoUsername,
            () => new HttpRequestMessage(HttpMethod.Get, $"{Api}/users/@me"), cancellationToken);
        var dto = await ReadAsync<MalUser>(response, cancellationToken);
        return new ProviderAccount(dto.Id, dto.Name, dto.Picture);
    }

    public async Task<IReadOnlyDictionary<int, ProviderListState>> GetListAsync(
        string shokoUsername,
        CancellationToken cancellationToken)
    {
        var entries = new Dictionary<int, ProviderListState>();
        string? url = $"{Api}/users/@me/animelist?fields=list_status,num_episodes&limit=1000";
        while (!string.IsNullOrWhiteSpace(url))
        {
            var pageUrl = url;
            using var response = await SendAsync(shokoUsername,
                () => new HttpRequestMessage(HttpMethod.Get, pageUrl), cancellationToken);
            var page = await ReadAsync<MalListPage>(response, cancellationToken);
            foreach (var item in page.Data)
            {
                if (item.Node.Id <= 0) continue;
                entries[item.Node.Id] = ToState(item.Node, item.ListStatus);
            }
            url = page.Paging.Next;
        }
        return entries;
    }

    public async Task<ProviderListState?> GetEntryAsync(
        string shokoUsername,
        int mediaId,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(shokoUsername,
            () => new HttpRequestMessage(HttpMethod.Get,
                $"{Api}/anime/{mediaId}?fields=title,num_episodes,my_list_status"), cancellationToken);
        var anime = await ReadAsync<MalAnime>(response, cancellationToken);
        return anime.MyListStatus is null ? null : ToState(anime, anime.MyListStatus);
    }

    public async Task<IReadOnlyList<ProviderMediaSearchResult>> SearchAsync(
        string shokoUsername,
        string query,
        bool includeAdult,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var normalized = query.Trim();
        if (normalized.Length > 64) normalized = normalized[..64];
        var url = $"{Api}/anime?q={Uri.EscapeDataString(normalized)}&limit=20&fields=id,title,num_episodes,start_date,main_picture";
        if (includeAdult) url += "&nsfw=true";
        using var response = await SendAsync(shokoUsername,
            () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
        var page = await ReadAsync<MalListPage>(response, cancellationToken);
        return page.Data.Select(item => new ProviderMediaSearchResult(
                Key,
                item.Node.Id,
                item.Node.Title,
                item.Node.NumEpisodes,
                ParseYear(item.Node.StartDate),
                item.Node.MainPicture?.Large ?? item.Node.MainPicture?.Medium))
            .ToArray();
    }

    public async Task<ProviderListState> ApplyAsync(
        string shokoUsername,
        PlannedChange change,
        CancellationToken cancellationToken)
    {
        var mediaId = change.ProviderMediaId
            ?? throw new ProviderException("A MyAnimeList mapping is required before applying this change.", false);
        var fields = new Dictionary<string, string>
        {
            ["num_watched_episodes"] = change.AfterProgress.ToString(CultureInfo.InvariantCulture),
            ["status"] = ToMalStatus(change.AfterStatus),
        };
        if (change.AfterRatingRaw is { } rating)
            fields["score"] = Math.Clamp((int)Math.Round((rating) / 10d, MidpointRounding.AwayFromZero), 0, 10)
                .ToString(CultureInfo.InvariantCulture);
        else if (change.BeforeRatingRaw is not null)
            fields["score"] = "0";

        using var response = await SendAsync(shokoUsername, () => new HttpRequestMessage(
            HttpMethod.Put,
            $"{Api}/anime/{mediaId}/my_list_status")
        {
            Content = new FormUrlEncodedContent(fields),
        }, cancellationToken);
        var updated = await ReadAsync<MalListStatus>(response, cancellationToken);
        return new ProviderListState(Key, mediaId, change.Title, updated.NumEpisodesWatched,
            change.AfterProgress, FromMalStatus(updated.Status), updated.Score * 10, true);
    }

    private Task<HttpResponseMessage> SendAsync(
        string username,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken) => transport.SendAsync(
            Key, username, HttpClientNames.MyAnimeList, requestFactory, cancellationToken);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
        ?? throw new ProviderException("MyAnimeList returned an empty response.", true);

    private ProviderListState ToState(MalAnime anime, MalListStatus status) => new(
        Key,
        anime.Id,
        anime.Title,
        status.NumEpisodesWatched,
        anime.NumEpisodes,
        FromMalStatus(status.Status),
        status.Score * 10,
        true);

    private static string ToMalStatus(CanonicalListStatus status) => status switch
    {
        CanonicalListStatus.Planning => "plan_to_watch",
        CanonicalListStatus.Watching => "watching",
        CanonicalListStatus.Completed => "completed",
        CanonicalListStatus.Paused => "on_hold",
        CanonicalListStatus.Dropped => "dropped",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static CanonicalListStatus FromMalStatus(string? status) => status switch
    {
        "plan_to_watch" => CanonicalListStatus.Planning,
        "completed" => CanonicalListStatus.Completed,
        "on_hold" => CanonicalListStatus.Paused,
        "dropped" => CanonicalListStatus.Dropped,
        _ => CanonicalListStatus.Watching,
    };

    private static int? ParseYear(string? value) =>
        DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.Year
            : null;

    private sealed class MalUser
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("picture")] public string? Picture { get; set; }
    }

    private sealed class MalListPage
    {
        [JsonPropertyName("data")] public List<MalListItem> Data { get; set; } = [];
        [JsonPropertyName("paging")] public MalPaging Paging { get; set; } = new();
    }

    private sealed class MalListItem
    {
        [JsonPropertyName("node")] public MalAnime Node { get; set; } = new();
        [JsonPropertyName("list_status")] public MalListStatus ListStatus { get; set; } = new();
    }

    private sealed class MalPaging
    {
        [JsonPropertyName("next")] public string? Next { get; set; }
    }

    private sealed class MalAnime
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("num_episodes")] public int NumEpisodes { get; set; }
        [JsonPropertyName("start_date")] public string? StartDate { get; set; }
        [JsonPropertyName("main_picture")] public MalPicture? MainPicture { get; set; }
        [JsonPropertyName("my_list_status")] public MalListStatus? MyListStatus { get; set; }
    }

    private sealed class MalPicture
    {
        [JsonPropertyName("medium")] public string? Medium { get; set; }
        [JsonPropertyName("large")] public string? Large { get; set; }
    }

    private sealed class MalListStatus
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("score")] public int Score { get; set; }
        [JsonPropertyName("num_episodes_watched")] public int NumEpisodesWatched { get; set; }
    }
}
