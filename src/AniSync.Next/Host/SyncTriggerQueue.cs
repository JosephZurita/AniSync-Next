using AniSync.Next.Persistence;
using System.Threading.Channels;

namespace AniSync.Next.Host;

internal interface ISyncTriggerQueue
{
    ChannelReader<PersistedSyncTrigger> Reader { get; }
    bool TryEnqueue(PersistedSyncTrigger trigger);
    void Complete();
}

internal sealed class SyncTriggerQueue : ISyncTriggerQueue
{
    private readonly Channel<PersistedSyncTrigger> _channel = Channel.CreateUnbounded<PersistedSyncTrigger>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });

    public ChannelReader<PersistedSyncTrigger> Reader => _channel.Reader;
    public bool TryEnqueue(PersistedSyncTrigger trigger) => _channel.Writer.TryWrite(trigger);
    public void Complete() => _channel.Writer.TryComplete();
}

