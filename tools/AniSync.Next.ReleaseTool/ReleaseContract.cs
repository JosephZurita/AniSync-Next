using System.Globalization;
using System.Text.RegularExpressions;

namespace AniSync.Next.ReleaseTool;

internal sealed record ReleaseContract
{
    public const string PackageID = "8eea2528-a2f8-543a-8bc5-a06bb5a138bd";
    public const string PackageName = "AniSync Next";
    public const string RepositoryUrl = "https://github.com/JosephZurita/AniSync-Next";
    public const string ImageUrl = "https://raw.githubusercontent.com/JosephZurita/AniSync-Next/master/docs/banner.svg";
    public const string RuntimeIdentifier = "any";
    public const string AbstractionVersion = "6.0.0";
    public const string Channel = "Dev";
    public const int MaximumBuildNumber = 65534;
    public const int RetainedReleaseCount = 30;

    private static readonly Regex CommitPattern = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex AbstractionsPattern = new("^6\\.0\\.0-[0-9A-Za-z.-]+$", RegexOptions.CultureInvariant);

    public required int BuildNumber { get; init; }
    public required string CommitSHA { get; init; }
    public required string AbstractionsPackageVersion { get; init; }
    public string Version => $"0.1.0-dev.{BuildNumber.ToString(CultureInfo.InvariantCulture)}";
    public Version AssemblyVersion => new(0, 1, 0, BuildNumber);
    public string DLLFileName => "AniSync.Next.dll";
    public string ZipFileName => $"AniSync.Next-{Version}.zip";

    public static ReleaseContract Create(int buildNumber, string commitSHA, string abstractionsPackageVersion)
    {
        if (buildNumber is < 0 or > MaximumBuildNumber)
            throw new InvalidOperationException($"Development build number {buildNumber} is outside the CLR revision range 0-{MaximumBuildNumber}.");
        if (!CommitPattern.IsMatch(commitSHA))
            throw new InvalidOperationException("Source revision must be a lowercase full 40-character Git commit SHA.");
        if (!AbstractionsPattern.IsMatch(abstractionsPackageVersion))
            throw new InvalidOperationException($"Shoko.Abstractions '{abstractionsPackageVersion}' must be an exact 6.0.0 prerelease.");
        return new() { BuildNumber = buildNumber, CommitSHA = commitSHA, AbstractionsPackageVersion = abstractionsPackageVersion };
    }
}
