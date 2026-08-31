using AniSync.Next.Configuration;

namespace AniSync.Next.Application;

public interface IAniSyncDiagnostics
{
    void Write(string username, DiagnosticLogLevel requiredLevel, string eventName, string details);
}

internal sealed class AniSyncDiagnostics(
    IPluginConfigurationService configuration,
    ILogger<AniSyncDiagnostics> logger) : IAniSyncDiagnostics
{
    public void Write(string username, DiagnosticLogLevel requiredLevel, string eventName, string details)
    {
        if (requiredLevel == DiagnosticLogLevel.Off ||
            configuration.GetUserSettings(username).DiagnosticLogLevel < requiredLevel)
            return;

        // Emit enabled diagnostics at Information so they remain visible with
        // Shoko's normal production log filter. Callers provide redacted data;
        // tokens, secrets, OAuth codes, and response bodies are never logged.
        logger.LogInformation(
            "AniSync Next diagnostic [{DiagnosticLevel}] {EventName} user={Username}: {Details}",
            requiredLevel, eventName, username, details);
    }
}
