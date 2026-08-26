using AniSync.Next.Configuration;
using AniSync.Next.Domain;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AniSync.Next.Providers;

internal sealed record VerifiedOAuthState(
    string Username,
    ProviderKey Provider,
    string BaseUrl,
    string? CodeVerifier);

internal interface IOAuthStateService
{
    string Create(string username, ProviderKey provider, string baseUrl, out string? codeChallenge);
    bool TryVerify(string state, out VerifiedOAuthState? verified);
}

internal sealed class OAuthStateService(
    IPluginConfigurationService configuration,
    IMemoryCache cache,
    TimeProvider timeProvider) : IOAuthStateService
{
    public string Create(string username, ProviderKey provider, string baseUrl, out string? codeChallenge)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string? verifier = null;
        codeChallenge = null;
        if (provider == ProviderKey.MyAnimeList)
        {
            verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
            codeChallenge = verifier;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new OAuthStatePayload
        {
            Username = username,
            Provider = provider,
            BaseUrl = baseUrl,
            Nonce = nonce,
            ExpiresAt = timeProvider.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds(),
        });
        using var hmac = new HMACSHA256(configuration.GetOrCreateStateSigningKey());
        var state = $"{Base64Url(payload)}.{Base64Url(hmac.ComputeHash(payload))}";
        cache.Set($"anisync-next-oauth:{nonce}", verifier ?? string.Empty, TimeSpan.FromMinutes(10));
        return state;
    }

    public bool TryVerify(string state, out VerifiedOAuthState? verified)
    {
        verified = null;
        try
        {
            var parts = state.Split('.');
            if (parts.Length != 2) return false;
            var payloadBytes = DecodeBase64Url(parts[0]);
            var providedSignature = DecodeBase64Url(parts[1]);
            using var hmac = new HMACSHA256(configuration.GetOrCreateStateSigningKey());
            if (!CryptographicOperations.FixedTimeEquals(providedSignature, hmac.ComputeHash(payloadBytes)))
                return false;
            var payload = JsonSerializer.Deserialize<OAuthStatePayload>(payloadBytes);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Username) ||
                string.IsNullOrWhiteSpace(payload.BaseUrl) ||
                timeProvider.GetUtcNow().ToUnixTimeSeconds() > payload.ExpiresAt)
                return false;
            if (!cache.TryGetValue<string>($"anisync-next-oauth:{payload.Nonce}", out var verifier))
                return false;
            cache.Remove($"anisync-next-oauth:{payload.Nonce}");
            verified = new VerifiedOAuthState(payload.Username, payload.Provider, payload.BaseUrl,
                string.IsNullOrEmpty(verifier) ? null : verifier);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException)
        {
            return false;
        }
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(padded);
    }

    private sealed class OAuthStatePayload
    {
        public string Username { get; set; } = string.Empty;
        public ProviderKey Provider { get; set; }
        public string BaseUrl { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public long ExpiresAt { get; set; }
    }
}
