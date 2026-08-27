using AniSync.Next.Application;
using AniSync.Next.Domain;
using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;

namespace AniSync.Next.Providers;

internal interface IProviderDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class ProviderDelay : IProviderDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

internal sealed class ProviderHttpTransport(
    IHttpClientFactory httpClientFactory,
    IProviderTokenService tokenService,
    IProviderDelay delay,
    IAniSyncDiagnostics diagnostics)
{
    public async Task<HttpResponseMessage> SendAsync(
        ProviderKey provider,
        string shokoUsername,
        string clientName,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        var refreshed = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = refreshed
                ? await tokenService.ForceRefreshAsync(shokoUsername, provider, cancellationToken)
                : await tokenService.GetAccessTokenAsync(shokoUsername, provider, cancellationToken);
            using var request = requestFactory();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var target = SafeTarget(request.RequestUri);
            diagnostics.Write(shokoUsername, Configuration.DiagnosticLogLevel.Trace, "provider.request",
                $"provider={provider} attempt={attempt + 1} method={request.Method.Method} target={target}");
            var stopwatch = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await httpClientFactory.CreateClient(clientName)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                diagnostics.Write(shokoUsername, Configuration.DiagnosticLogLevel.Detailed, "provider.transport-failure",
                    $"provider={provider} attempt={attempt + 1} target={target} elapsedMs={stopwatch.ElapsedMilliseconds} errorType={ex.GetType().Name}");
                if (attempt == 2)
                    throw new ProviderException($"{provider} could not be reached.", true, innerException: ex);
                await delay.DelayAsync(TimeSpan.FromSeconds(1 << attempt), cancellationToken);
                continue;
            }

            diagnostics.Write(shokoUsername, Configuration.DiagnosticLogLevel.Detailed, "provider.response",
                $"provider={provider} attempt={attempt + 1} target={target} status={(int)response.StatusCode} elapsedMs={stopwatch.ElapsedMilliseconds}");

            if (response.IsSuccessStatusCode) return response;

            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
            {
                response.Dispose();
                refreshed = true;
                attempt--;
                continue;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                var retryAfter = GetRetryAfter(response.Headers.RetryAfter) ?? TimeSpan.FromSeconds(1 << attempt);
                response.Dispose();
                if (attempt == 2)
                    throw new ProviderException($"{provider} is temporarily unavailable.", true, retryAfter);
                await delay.DelayAsync(retryAfter, cancellationToken);
                continue;
            }

            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = response.StatusCode;
            response.Dispose();
            throw new ProviderException(
                $"{provider} rejected the request ({(int)statusCode}): {Truncate(error)}", false);
        }

        throw new ProviderException($"{provider} request failed after retries.", true);
    }

    private static TimeSpan? GetRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta) return delta;
        if (retryAfter?.Date is { } date)
        {
            var difference = date - DateTimeOffset.UtcNow;
            return difference > TimeSpan.Zero ? difference : TimeSpan.Zero;
        }
        return null;
    }

    private static string Truncate(string value)
    {
        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ');
        return sanitized.Length <= 300 ? sanitized : sanitized[..300];
    }

    private static string SafeTarget(Uri? uri) => uri is null
        ? "unknown"
        : $"{uri.Host}{uri.AbsolutePath}";
}
