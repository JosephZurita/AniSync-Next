using AniSync.Next.Application;
using AniSync.Next.Configuration;

namespace AniSync.Next.Tests;

internal sealed class NullDiagnostics : IAniSyncDiagnostics
{
    public void Write(string username, DiagnosticLogLevel requiredLevel, string eventName, string details) { }
}
