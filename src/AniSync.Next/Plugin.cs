using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Plugin.Models;
using Shoko.Abstractions.Utilities;

namespace AniSync.Next;

public sealed class Plugin : IPlugin
{
    public Guid ID => UuidUtility.GetV5(GetType().FullName!);
    public string Name => "AniSync Next";
    public string Description => "Deterministically synchronizes Shoko watch state and ratings to AniList and MyAnimeList.";
    public string EmbeddedThumbnailResourceName => string.Empty;

    public IReadOnlyList<PluginPage> GetPages() =>
    [
        new()
        {
            Name = "AniSync Next",
            Url = "/anisync-next",
            CanEmbed = false,
        },
    ];
}
