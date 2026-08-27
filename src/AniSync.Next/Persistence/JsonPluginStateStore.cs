using AniSync.Next.Domain;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AniSync.Next.Persistence;

internal sealed class JsonPluginStateStore : IPluginStateStore
{
    private const int MaxHistoryEntries = 5_000;
    private readonly string _statePath;
    private readonly ILogger<JsonPluginStateStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private PluginStateDocument _state = new();
    private bool _initialized;

    public JsonPluginStateStore(string pluginDataPath, ILogger<JsonPluginStateStore> logger)
    {
        Directory.CreateDirectory(pluginDataPath);
        _statePath = Path.Combine(pluginDataPath, "state-v1.json");
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            if (!File.Exists(_statePath))
            {
                _initialized = true;
                await SaveUnsafeAsync(cancellationToken);
                return;
            }

            try
            {
                await using var stream = File.OpenRead(_statePath);
                _state = await JsonSerializer.DeserializeAsync<PluginStateDocument>(stream, _jsonOptions, cancellationToken)
                    ?? new PluginStateDocument();
                if (_state.SchemaVersion != 1)
                    throw new InvalidDataException($"Unsupported AniSync Next state schema {_state.SchemaVersion}.");
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
            {
                var backupPath = _statePath + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
                File.Copy(_statePath, backupPath, overwrite: false);
                _logger.LogError(ex, "AniSync Next state is corrupt; backed it up to {BackupPath} and started with empty state", backupPath);
                _state = new PluginStateDocument();
                await SaveUnsafeAsync(cancellationToken);
            }
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ProviderMapping?> GetMappingAsync(string username, int aniDbAnimeId, ProviderKey provider, CancellationToken cancellationToken) =>
        ReadAsync(state => state.Mappings.FirstOrDefault(mapping =>
            mapping.ShokoUsername.Equals(username, StringComparison.OrdinalIgnoreCase) &&
            mapping.AniDbAnimeId == aniDbAnimeId && mapping.Provider == provider), cancellationToken);

    public Task<IReadOnlyList<ProviderMapping>> GetMappingsAsync(string username, CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<ProviderMapping>>(state => state.Mappings
            .Where(mapping => mapping.ShokoUsername.Equals(username, StringComparison.OrdinalIgnoreCase))
            .OrderBy(mapping => mapping.Provider).ThenBy(mapping => mapping.MediaTitle)
            .ToArray(), cancellationToken);

    public Task SaveMappingAsync(ProviderMapping mapping, CancellationToken cancellationToken) => MutateAsync(state =>
    {
        state.Mappings.RemoveAll(item =>
            item.ShokoUsername.Equals(mapping.ShokoUsername, StringComparison.OrdinalIgnoreCase) &&
            item.AniDbAnimeId == mapping.AniDbAnimeId && item.Provider == mapping.Provider);
        state.Mappings.Add(mapping);
    }, cancellationToken);

    public Task RemoveMappingAsync(string username, int aniDbAnimeId, ProviderKey provider, CancellationToken cancellationToken) => MutateAsync(state =>
        state.Mappings.RemoveAll(item =>
            item.ShokoUsername.Equals(username, StringComparison.OrdinalIgnoreCase) &&
            item.AniDbAnimeId == aniDbAnimeId && item.Provider == provider), cancellationToken);

    public Task ReplaceForUserAsync(string username, IReadOnlyCollection<ReviewItem> items, CancellationToken cancellationToken) => MutateAsync(state =>
    {
        state.Reviews.RemoveAll(item => item.Change.ShokoUsername.Equals(username, StringComparison.OrdinalIgnoreCase));
        state.Reviews.AddRange(items);
    }, cancellationToken);

    public Task UpsertAsync(ReviewItem item, CancellationToken cancellationToken) => MutateAsync(state =>
    {
        state.Reviews.RemoveAll(existing => existing.Id == item.Id ||
            (existing.Change.ShokoUsername.Equals(item.Change.ShokoUsername, StringComparison.OrdinalIgnoreCase) &&
             existing.Change.SeriesId == item.Change.SeriesId &&
             existing.Change.Provider == item.Change.Provider));
        state.Reviews.Add(item);
    }, cancellationToken);

    public Task RemoveAsync(string username, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) => MutateAsync(state =>
        state.Reviews.RemoveAll(item =>
            item.Change.ShokoUsername.Equals(username, StringComparison.OrdinalIgnoreCase) && ids.Contains(item.Id)), cancellationToken);

    public Task<IReadOnlyList<ReviewItem>> GetForUserAsync(string username, CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<ReviewItem>>(state => state.Reviews
            .Where(item => item.Change.ShokoUsername.Equals(username, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.UpdatedAt)
            .ToArray(), cancellationToken);

    public Task AppendAsync(SyncOutcome outcome, CancellationToken cancellationToken) => MutateAsync(state =>
    {
        state.History.Insert(0, outcome);
        if (state.History.Count > MaxHistoryEntries)
            state.History.RemoveRange(MaxHistoryEntries, state.History.Count - MaxHistoryEntries);
    }, cancellationToken);

    public Task<IReadOnlyList<SyncOutcome>> GetForUserAsync(string username, int limit, CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<SyncOutcome>>(state => state.History
            .Where(item => item.Change.ShokoUsername.Equals(username, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit, 1, 500))
            .ToArray(), cancellationToken);

    public Task ClearAsync(string username, CancellationToken cancellationToken) => MutateAsync(state =>
        state.History.RemoveAll(item => item.Change.ShokoUsername.Equals(username, StringComparison.OrdinalIgnoreCase)), cancellationToken);

    public Task UpsertPendingAsync(PersistedSyncTrigger trigger, CancellationToken cancellationToken) => MutateAsync(state =>
    {
        state.PendingWork.RemoveAll(item => item.Id == trigger.Id ||
            (item.ShokoUsername.Equals(trigger.ShokoUsername, StringComparison.OrdinalIgnoreCase) && item.SeriesId == trigger.SeriesId));
        state.PendingWork.Add(trigger);
    }, cancellationToken);

    public Task RemovePendingAsync(Guid id, CancellationToken cancellationToken) => MutateAsync(state =>
        state.PendingWork.RemoveAll(item => item.Id == id), cancellationToken);

    public Task<IReadOnlyList<PersistedSyncTrigger>> GetPendingAsync(CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<PersistedSyncTrigger>>(state => state.PendingWork.OrderBy(item => item.CreatedAt).ToArray(), cancellationToken);

    private async Task<T> ReadAsync<T>(Func<PluginStateDocument, T> read, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try { return read(_state); }
        finally { _gate.Release(); }
    }

    private async Task MutateAsync(Action<PluginStateDocument> mutate, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            mutate(_state);
            await SaveUnsafeAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private Task EnsureInitializedAsync(CancellationToken cancellationToken) =>
        _initialized ? Task.CompletedTask : InitializeAsync(cancellationToken);

    private async Task SaveUnsafeAsync(CancellationToken cancellationToken)
    {
        var tempPath = _statePath + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
        {
            await JsonSerializer.SerializeAsync(stream, _state, _jsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(tempPath, _statePath, overwrite: true);
    }
}
