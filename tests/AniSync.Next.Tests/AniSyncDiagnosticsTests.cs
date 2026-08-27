using AniSync.Next.Application;
using AniSync.Next.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace AniSync.Next.Tests;

public sealed class AniSyncDiagnosticsTests
{
    [Fact]
    public void ConfiguredLevelControlsWhichDiagnosticEventsReachTheShokoLogger()
    {
        var configuration = new FakeConfiguration();
        configuration.SaveUserSettings("alice", new UserSyncSettings
        {
            DiagnosticLogLevel = DiagnosticLogLevel.Basic,
        });
        var logger = new RecordingLogger<AniSyncDiagnostics>();
        var diagnostics = new AniSyncDiagnostics(configuration, logger);

        diagnostics.Write("alice", DiagnosticLogLevel.Basic, "sync.apply-started",
            "provider=MyAnimeList mediaId=55888");
        diagnostics.Write("alice", DiagnosticLogLevel.Detailed, "provider.response",
            "provider=MyAnimeList status=200");

        logger.Messages.Should().ContainSingle()
            .Which.Should().Contain("sync.apply-started").And.Contain("mediaId=55888");
    }

    [Fact]
    public void OffSuppressesInformationalDiagnostics()
    {
        var configuration = new FakeConfiguration();
        configuration.SaveUserSettings("alice", new UserSyncSettings
        {
            DiagnosticLogLevel = DiagnosticLogLevel.Off,
        });
        var logger = new RecordingLogger<AniSyncDiagnostics>();
        var diagnostics = new AniSyncDiagnostics(configuration, logger);

        diagnostics.Write("alice", DiagnosticLogLevel.Basic, "sync.apply-started", "provider=AniList");

        logger.Messages.Should().BeEmpty();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
