using AniSync.Next.Domain;

namespace AniSync.Next.Application;

public interface IProviderRegistry
{
    IReadOnlyList<ISyncProvider> All { get; }
    ISyncProvider Get(ProviderKey key);
}

internal sealed class ProviderRegistry(IEnumerable<ISyncProvider> providers) : IProviderRegistry
{
    private readonly IReadOnlyDictionary<ProviderKey, ISyncProvider> _providers =
        providers.ToDictionary(provider => provider.Key);

    public IReadOnlyList<ISyncProvider> All => _providers.Values.OrderBy(provider => provider.Key).ToArray();

    public ISyncProvider Get(ProviderKey key) => _providers.TryGetValue(key, out var provider)
        ? provider
        : throw new KeyNotFoundException($"Provider {key} is not registered.");
}
