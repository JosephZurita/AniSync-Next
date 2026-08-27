using AniSync.Next.Application;
using AniSync.Next.Configuration;
using AniSync.Next.Domain;
using AniSync.Next.Persistence;
using AniSync.Next.Providers;
using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.User.Services;

namespace AniSync.Next.Api;

[ApiVersionNeutral]
[Route("anisync-next")]
public sealed class AniSyncNextController(
    IUserService userService,
    IPluginConfigurationService configuration,
    IProviderOAuthService oauth,
    IProviderRegistry providers,
    ISyncCoordinator coordinator,
    IPluginStateStore stateStore,
    IMappingResolver mappingResolver,
    IShokoStateReader shokoStateReader) : Controller
{
    [HttpGet("api/session")]
    public async Task<ActionResult<SessionResponse>> GetSession(CancellationToken cancellationToken)
    {
        var current = CurrentUser();
        if (current is null) return Unauthorized(new ApiError("Authentication required."));
        var reviews = await stateStore.GetForUserAsync(current.Username, cancellationToken);
        var pending = await stateStore.GetPendingAsync(cancellationToken);
        return new SessionResponse(current.Username, current.IsAdmin, GetConnections(current.Username),
            reviews.Count, pending.Count(item => SameUser(item.ShokoUsername, current.Username)));
    }

    [HttpGet("api/settings")]
    public ActionResult<SettingsResponse> GetSettings()
    {
        var current = CurrentUser();
        if (current is null) return Unauthorized(new ApiError("Authentication required."));
        var clients = current.IsAdmin
            ? Enum.GetValues<ProviderKey>().Select(provider =>
            {
                var client = configuration.GetClient(provider);
                return new ProviderClientResponse(provider, client.ClientId,
                    !string.IsNullOrWhiteSpace(client.ClientSecret));
            }).ToArray()
            : null;
        return new SettingsResponse(configuration.GetUserSettings(current.Username),
            GetConnections(current.Username), clients);
    }

    [HttpPut("api/settings")]
    public ActionResult<SettingsResponse> UpdateSettings([FromBody] UpdateSettingsRequest request)
    {
        var current = CurrentUser();
        if (current is null) return Unauthorized(new ApiError("Authentication required."));
        var settings = new UserSyncSettings
        {
            AutoSync = request.AutoSync,
            SyncOnlyOnCompletion = request.SyncOnlyOnCompletion,
            SyncRatings = request.SyncRatings,
            IncludeAdultSearch = request.IncludeAdultSearch,
        };
        configuration.SaveUserSettings(current.Username, settings);
        return new SettingsResponse(settings, GetConnections(current.Username));
    }

    [HttpPut("api/provider-client")]
    public IActionResult UpdateProviderClient([FromBody] UpdateProviderClientRequest request)
    {
        var current = CurrentUser();
        if (current is null) return Unauthorized(new ApiError("Authentication required."));
        if (!current.IsAdmin) return StatusCode(StatusCodes.Status403Forbidden,
            new ApiError("Only a Shoko administrator may change provider credentials."));
        if (request.SecretSpecified && !request.ClearSecret && string.IsNullOrWhiteSpace(request.ClientSecret))
            return BadRequest(new ApiError("A replacement secret cannot be empty."));
        var secret = !request.SecretSpecified
            ? SecretUpdate.Preserve()
            : request.ClearSecret
                ? SecretUpdate.Remove()
                : SecretUpdate.Replace(request.ClientSecret!.Trim());
        configuration.SaveClientSettings(request.Provider, request.ClientId, secret);
        return NoContent();
    }

    [HttpGet("api/providers/{provider}/authorize")]
    public ActionResult<object> Authorize(ProviderKey provider, [FromQuery] string? baseUrl = null)
    {
        var current = CurrentUser();
        if (current is null) return Unauthorized(new ApiError("Authentication required."));
        var effectiveBaseUrl = ResolveOAuthBaseUrl(baseUrl, Request);
        return new { url = oauth.BuildAuthorizeUri(provider, current.Username, effectiveBaseUrl).ToString() };
    }

    [HttpDelete("api/providers/{provider}")]
    public IActionResult Disconnect(ProviderKey provider)
    {
        var current = CurrentUser();
        if (current is null) return Unauthorized(new ApiError("Authentication required."));
        configuration.RemoveAuthorization(current.Username, provider);
        return NoContent();
    }

    [HttpGet("oauth/callback")]
    public async Task<IActionResult> OAuthCallback(string state, string code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(code))
            return BadRequest(new ApiError("The OAuth callback is incomplete."));
        await oauth.CompleteAsync(state, code, cancellationToken);
        return Redirect("/anisync-next/settings?connected=1");
    }

    [HttpGet("api/review")]
    public async Task<ActionResult<IReadOnlyList<ReviewItem>>> GetReview(CancellationToken cancellationToken)
    {
        var username = CurrentUsername();
        if (username is null) return Unauthorized(new ApiError("Authentication required."));
        return Ok(await stateStore.GetForUserAsync(username, cancellationToken));
    }

    [HttpPost("api/review/refresh")]
    public async Task<ActionResult<ReviewRefreshResult>> RefreshReview(CancellationToken cancellationToken)
    {
        var username = CurrentUsername();
        if (username is null) return Unauthorized(new ApiError("Authentication required."));
        return Ok(await coordinator.RefreshAsync(username, cancellationToken));
    }

    [HttpPost("api/review/apply")]
    public async Task<ActionResult<IReadOnlyList<SyncOutcome>>> ApplyReview(
        [FromBody] ApplyReviewRequest request,
        CancellationToken cancellationToken)
    {
        var username = CurrentUsername();
        if (username is null) return Unauthorized(new ApiError("Authentication required."));
        if (request.Ids is null || request.Ids.Count == 0 || request.Ids.Distinct().Count() != request.Ids.Count)
            return BadRequest(new ApiError("Select at least one unique review item."));
        try
        {
            return Ok(await coordinator.ApplyAsync(username, request.Ids, cancellationToken));
        }
        catch (StalePreviewException ex)
        {
            return Conflict(new ApiError(ex.Message));
        }
    }

    [HttpGet("api/jobs")]
    public async Task<ActionResult<IReadOnlyList<PersistedSyncTrigger>>> GetJobs(CancellationToken cancellationToken)
    {
        var username = CurrentUsername();
        if (username is null) return Unauthorized(new ApiError("Authentication required."));
        var pending = await stateStore.GetPendingAsync(cancellationToken);
        return Ok(pending.Where(item => SameUser(item.ShokoUsername, username)).ToArray());
    }

    [HttpGet("api/history")]
    public async Task<ActionResult<IReadOnlyList<SyncOutcome>>> GetHistory(
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var username = CurrentUsername();
        if (username is null) return Unauthorized(new ApiError("Authentication required."));
        if (limit is < 1 or > 500) return BadRequest(new ApiError("Limit must be between 1 and 500."));
        return Ok(await stateStore.GetForUserAsync(username, limit, cancellationToken));
    }

    [HttpDelete("api/history")]
    public async Task<IActionResult> ClearHistory(CancellationToken cancellationToken)
    {
        var username = CurrentUsername();
        if (username is null) return Unauthorized(new ApiError("Authentication required."));
        await stateStore.ClearAsync(username, cancellationToken);
        return NoContent();
    }

    [HttpGet("api/mappings")]
    public async Task<ActionResult<IReadOnlyList<ProviderMapping>>> GetMappings(CancellationToken cancellationToken)
    {
        var username = CurrentUsername();
        if (username is null) return Unauthorized(new ApiError("Authentication required."));
        return Ok(await mappingResolver.GetForUserAsync(username, cancellationToken));
    }

    [HttpPost("api/mappings/search")]
    public async Task<ActionResult<IReadOnlyList<ProviderMediaSearchResult>>> SearchMappings(
        [FromBody] SearchMappingRequest request,
        CancellationToken cancellationToken)
    {
        var username = CurrentUsername();
        if (username is null) return Unauthorized(new ApiError("Authentication required."));
        if (request.SeriesId <= 0 || string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(new ApiError("Series and search text are required."));
        if (await shokoStateReader.GetSeriesStateAsync(username, request.SeriesId, cancellationToken) is null)
            return NotFound(new ApiError("The Shoko series was not found for this user."));
        var settings = configuration.GetUserSettings(username);
        return Ok(await providers.Get(request.Provider).SearchAsync(username, request.Query,
            settings.IncludeAdultSearch, cancellationToken));
    }

    [HttpPut("api/mappings")]
    public async Task<IActionResult> SaveMapping(
        [FromBody] SaveMappingRequest request,
        CancellationToken cancellationToken)
    {
        var username = CurrentUsername();
        if (username is null) return Unauthorized(new ApiError("Authentication required."));
        if (request.SeriesId <= 0 || request.AniDbAnimeId <= 0 || request.MediaId <= 0 ||
            string.IsNullOrWhiteSpace(request.MediaTitle))
            return BadRequest(new ApiError("A valid series, AniDB ID, provider media, and title are required."));
        var source = await shokoStateReader.GetSeriesStateAsync(username, request.SeriesId, cancellationToken);
        if (source is null || source.AniDbAnimeId != request.AniDbAnimeId)
            return Conflict(new ApiError("The Shoko series changed; refresh mappings before saving."));
        await mappingResolver.SaveAsync(new ProviderMapping(username, request.AniDbAnimeId,
            request.Provider, request.MediaId, request.MediaTitle.Trim(), true, DateTimeOffset.UtcNow), cancellationToken);
        return NoContent();
    }

    [HttpDelete("api/mappings/{aniDbAnimeId:int}/{provider}")]
    public async Task<IActionResult> RemoveMapping(
        int aniDbAnimeId,
        ProviderKey provider,
        CancellationToken cancellationToken)
    {
        var username = CurrentUsername();
        if (username is null) return Unauthorized(new ApiError("Authentication required."));
        if (aniDbAnimeId <= 0) return BadRequest(new ApiError("AniDB ID must be positive."));
        await mappingResolver.RemoveAsync(username, aniDbAnimeId, provider, cancellationToken);
        return NoContent();
    }

    [HttpGet("{**path}")]
    public IActionResult Spa(string? path = null)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var asset = GetEmbeddedAsset(path);
            if (asset is not null) return File(asset, ContentTypeFor(path));
            if (Path.HasExtension(path)) return NotFound();
        }
        var index = GetEmbeddedAsset("index.html");
        return index is null ? NotFound() : File(index, "text/html; charset=utf-8");
    }

    internal static byte[]? GetEmbeddedAsset(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        using var stream = typeof(AniSyncNextController).Assembly.GetManifestResourceStream($"app/{normalized}");
        if (stream is null) return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".json" => "application/json",
        _ => "application/octet-stream",
    };

    private IReadOnlyList<ProviderConnectionResponse> GetConnections(string username) =>
        Enum.GetValues<ProviderKey>().Select(provider =>
        {
            var client = configuration.GetClient(provider);
            var authorization = configuration.GetAuthorization(username, provider);
            return new ProviderConnectionResponse(provider,
                !string.IsNullOrWhiteSpace(client.ClientId),
                !string.IsNullOrWhiteSpace(authorization?.AccessToken),
                authorization?.Username);
        }).ToArray();

    private string? CurrentUsername() => CurrentUser()?.Username;
    private Shoko.Abstractions.User.IUser? CurrentUser() =>
        HttpContext is null ? null : userService.GetUserFromHttpContext(HttpContext);
    internal static string ResolveOAuthBaseUrl(string? browserBaseUrl, HttpRequest request)
    {
        var requestBaseUrl = $"{request.Scheme}://{request.Host}";
        if (!Uri.TryCreate(browserBaseUrl, UriKind.Absolute, out var browserUri) ||
            browserUri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(browserUri.UserInfo) ||
            browserUri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(browserUri.Query) ||
            !string.IsNullOrEmpty(browserUri.Fragment) ||
            !browserUri.Host.Equals(request.Host.Host, StringComparison.OrdinalIgnoreCase))
            return requestBaseUrl;

        return browserUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static bool SameUser(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
