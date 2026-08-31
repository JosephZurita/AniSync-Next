using AniSync.Next.Application;
using AniSync.Next.Domain;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AniSync.Next.Providers;

internal sealed class AniListProvider(ProviderHttpTransport transport) : ISyncProvider
{
    private const string Endpoint = "https://graphql.anilist.co";

    public ProviderKey Key => ProviderKey.AniList;

    public async Task<ProviderAccount?> GetAccountAsync(string shokoUsername, CancellationToken cancellationToken)
    {
        using var document = await PostAsync(shokoUsername,
            "query { Viewer { id name avatar { large } } }", new { }, cancellationToken);
        var viewer = Data(document).GetProperty("Viewer");
        return new ProviderAccount(viewer.GetProperty("id").GetInt32(),
            viewer.GetProperty("name").GetString() ?? string.Empty,
            GetString(viewer, "avatar", "large"));
    }

    public async Task<IReadOnlyDictionary<int, ProviderListState>> GetListAsync(
        string shokoUsername,
        CancellationToken cancellationToken)
    {
        var account = await GetAccountAsync(shokoUsername, cancellationToken)
            ?? throw new ProviderException("AniList did not return the connected account.", true);
        const string query = "query ($userId: Int!, $page: Int!) { Page(page: $page, perPage: 50) { pageInfo { hasNextPage } mediaList(userId: $userId, type: ANIME) { status progress score(format: POINT_100) media { id episodes title { romaji english } } } } }";
        var result = new Dictionary<int, ProviderListState>();
        var pageNumber = 1;
        var hasNextPage = true;
        while (hasNextPage)
        {
            using var document = await PostAsync(shokoUsername, query,
                new { userId = account.Id, page = pageNumber }, cancellationToken);
            var page = Data(document).GetProperty("Page");
            foreach (var entry in page.GetProperty("mediaList").EnumerateArray())
            {
                var state = ToState(entry);
                result[state.MediaId] = state;
            }
            hasNextPage = page.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean();
            pageNumber++;
        }
        return result;
    }

    public async Task<ProviderListState?> GetEntryAsync(
        string shokoUsername,
        int mediaId,
        CancellationToken cancellationToken)
    {
        const string query = "query ($id: Int!) { Media(id: $id, type: ANIME) { id episodes title { romaji english } mediaListEntry { status progress score(format: POINT_100) } } }";
        using var document = await PostAsync(shokoUsername, query, new { id = mediaId }, cancellationToken);
        var media = Data(document).GetProperty("Media");
        if (!media.TryGetProperty("mediaListEntry", out var entry) || entry.ValueKind is JsonValueKind.Null)
            return null;
        return ToState(entry, media);
    }

    public async Task<IReadOnlyList<ProviderMediaSearchResult>> SearchAsync(
        string shokoUsername,
        string query,
        bool includeAdult,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        const string safeSearch = "query ($query: String!) { Page(page: 1, perPage: 20) { media(search: $query, type: ANIME, isAdult: false) { id episodes isAdult startDate { year } coverImage { large } title { romaji english } } } }";
        const string unrestrictedSearch = "query ($query: String!) { Page(page: 1, perPage: 20) { media(search: $query, type: ANIME) { id episodes isAdult startDate { year } coverImage { large } title { romaji english } } } }";
        var gql = includeAdult ? unrestrictedSearch : safeSearch;
        using var document = await PostAsync(shokoUsername, gql,
            new { query = query.Trim() }, cancellationToken);
        return Data(document).GetProperty("Page").GetProperty("media").EnumerateArray()
            .Select(media => new ProviderMediaSearchResult(
                Key,
                media.GetProperty("id").GetInt32(),
                ReadTitle(media),
                GetNullableInt(media, "episodes") ?? 0,
                GetNullableInt(media, "startDate", "year"),
                GetString(media, "coverImage", "large")))
            .ToArray();
    }

    public async Task<ProviderListState> ApplyAsync(
        string shokoUsername,
        PlannedChange change,
        CancellationToken cancellationToken)
    {
        var mediaId = change.ProviderMediaId
            ?? throw new ProviderException("An AniList mapping is required before applying this change.", false);
        const string mutation = "mutation ($mediaId: Int!, $progress: Int!, $status: MediaListStatus!, $scoreRaw: Float) { SaveMediaListEntry(mediaId: $mediaId, progress: $progress, status: $status, scoreRaw: $scoreRaw) { status progress score(format: POINT_100) media { id episodes title { romaji english } } } }";
        using var document = await PostAsync(shokoUsername, mutation, new
        {
            mediaId,
            progress = change.AfterProgress,
            status = ToAniListStatus(change.AfterStatus),
            scoreRaw = change.AfterRatingRaw ?? (change.BeforeRatingRaw is not null ? 0 : (int?)null),
        }, cancellationToken);
        return ToState(Data(document).GetProperty("SaveMediaListEntry"));
    }

    private async Task<JsonDocument> PostAsync(
        string username,
        string query,
        object variables,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { query, variables });
        using var response = await transport.SendAsync(Key, username, HttpClientNames.AniList,
            () => new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            }, cancellationToken);
        var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (document.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
        {
            var message = string.Join("; ", errors.EnumerateArray().Select(error =>
                error.TryGetProperty("message", out var value) ? value.GetString() : error.ToString()));
            document.Dispose();
            throw new ProviderException($"AniList returned a GraphQL error: {message}", false);
        }
        if (!document.RootElement.TryGetProperty("data", out _))
        {
            document.Dispose();
            throw new ProviderException("AniList returned a response without data.", true);
        }
        return document;
    }

    private ProviderListState ToState(JsonElement entry)
    {
        var media = entry.GetProperty("media");
        return ToState(entry, media);
    }

    private ProviderListState ToState(JsonElement entry, JsonElement media) => new(
        Key,
        media.GetProperty("id").GetInt32(),
        ReadTitle(media),
        GetNullableInt(entry, "progress") ?? 0,
        GetNullableInt(media, "episodes") ?? 0,
        FromAniListStatus(entry.GetProperty("status").GetString()),
        GetNullableScore(entry, "score"),
        true);

    private static JsonElement Data(JsonDocument document) => document.RootElement.GetProperty("data");

    private static string ReadTitle(JsonElement media)
    {
        var title = media.GetProperty("title");
        return GetString(title, "english") ?? GetString(title, "romaji") ?? "Untitled";
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetString(JsonElement element, string parent, string property) =>
        element.TryGetProperty(parent, out var child) && child.ValueKind == JsonValueKind.Object
            ? GetString(child, property)
            : null;

    private static int? GetNullableInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static int? GetNullableInt(JsonElement element, string parent, string property) =>
        element.TryGetProperty(parent, out var child) && child.ValueKind == JsonValueKind.Object
            ? GetNullableInt(child, property)
            : null;

    private static int? GetNullableScore(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(value.GetDouble(), MidpointRounding.AwayFromZero)
            : null;

    private static string ToAniListStatus(CanonicalListStatus status) => status switch
    {
        CanonicalListStatus.Planning => "PLANNING",
        CanonicalListStatus.Watching => "CURRENT",
        CanonicalListStatus.Completed => "COMPLETED",
        CanonicalListStatus.Paused => "PAUSED",
        CanonicalListStatus.Dropped => "DROPPED",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static CanonicalListStatus FromAniListStatus(string? status) => status switch
    {
        "PLANNING" => CanonicalListStatus.Planning,
        "COMPLETED" or "REPEATING" => CanonicalListStatus.Completed,
        "PAUSED" => CanonicalListStatus.Paused,
        "DROPPED" => CanonicalListStatus.Dropped,
        _ => CanonicalListStatus.Watching,
    };
}
