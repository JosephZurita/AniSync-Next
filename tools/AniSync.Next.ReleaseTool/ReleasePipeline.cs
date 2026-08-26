using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AniSync.Next.ReleaseTool;

internal sealed record PackageAssets(string DLLPath, string DLLChecksumPath, string ZipPath, string ZipChecksumPath, string ZipChecksum);

internal static class PackageAssetBuilder
{
    public static PackageAssets Create(string sourceDLL, string outputDirectory, ReleaseContract contract, DateTimeOffset timestamp)
    {
        AssemblyInspector.Validate(sourceDLL, contract);
        Directory.CreateDirectory(outputDirectory);
        var dllPath = Path.Combine(outputDirectory, contract.DLLFileName);
        if (!Path.GetFullPath(sourceDLL).Equals(Path.GetFullPath(dllPath), StringComparison.OrdinalIgnoreCase))
            File.Copy(sourceDLL, dllPath, true);
        var dllChecksumPath = dllPath + ".sha256";
        WriteChecksum(dllPath, dllChecksumPath);
        var zipPath = Path.Combine(outputDirectory, contract.ZipFileName);
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using (var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(contract.DLLFileName, CompressionLevel.Optimal);
            entry.LastWriteTime = ZipTimestamp(timestamp);
            using var output = entry.Open();
            using var input = File.OpenRead(dllPath);
            input.CopyTo(output);
        }
        var zipChecksumPath = zipPath + ".sha256";
        var checksum = WriteChecksum(zipPath, zipChecksumPath);
        PackageAssetValidator.Validate(dllPath, dllChecksumPath, zipPath, zipChecksumPath);
        return new(dllPath, dllChecksumPath, zipPath, zipChecksumPath, checksum);
    }

    internal static string SHA256For(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string WriteChecksum(string path, string checksumPath)
    {
        var checksum = SHA256For(path);
        File.WriteAllText(checksumPath, $"{checksum}  {Path.GetFileName(path)}\n");
        return checksum;
    }

    private static DateTimeOffset ZipTimestamp(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        if (utc.Year < 1980) utc = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return new(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second - utc.Second % 2, TimeSpan.Zero);
    }
}

internal static class PackageAssetValidator
{
    public static string Validate(string dllPath, string dllChecksumPath, string zipPath, string zipChecksumPath)
    {
        ValidateChecksum(dllPath, dllChecksumPath);
        var zipChecksum = ValidateChecksum(zipPath, zipChecksumPath);
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count != 1 || archive.Entries[0].FullName != ReleaseContract.Create(0, new string('a', 40), "6.0.0-alpha.1").DLLFileName)
            throw new InvalidOperationException("Package ZIP must contain exactly AniSync.Next.dll at its root.");
        using var zipDLL = archive.Entries[0].Open();
        using var releasedDLL = File.OpenRead(dllPath);
        if (!SHA256.HashData(zipDLL).SequenceEqual(SHA256.HashData(releasedDLL)))
            throw new InvalidOperationException("The packaged DLL does not match the released DLL.");
        return zipChecksum;
    }

    private static string ValidateChecksum(string asset, string checksumFile)
    {
        if (!File.Exists(checksumFile)) throw new InvalidOperationException($"Missing checksum file {Path.GetFileName(checksumFile)}.");
        var fields = File.ReadAllText(checksumFile).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2 || fields[1] != Path.GetFileName(asset))
            throw new InvalidOperationException($"Checksum file {Path.GetFileName(checksumFile)} has an invalid format.");
        var actual = PackageAssetBuilder.SHA256For(asset);
        if (fields[0] != actual) throw new InvalidOperationException($"Checksum mismatch for {Path.GetFileName(asset)}.");
        return actual;
    }
}

internal static class ManifestManager
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static PackageManifest LoadOrCreate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return BaseManifest();
        var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(path), Options)
            ?? throw new InvalidOperationException("Existing manifest could not be parsed.");
        ManifestValidator.Validate(manifest, false);
        return manifest;
    }

    public static void AddOrReplace(PackageManifest manifest, ReleaseContract contract, string tag, DateTimeOffset releasedAt, string checksum)
    {
        if (releasedAt.Offset != TimeSpan.Zero) throw new InvalidOperationException("Release timestamp must be UTC.");
        var existing = manifest.Releases.SingleOrDefault(release => release.Version == contract.Version);
        if (existing is not null && existing.SourceRevision != contract.CommitSHA)
            throw new InvalidOperationException($"Version {contract.Version} is already assigned to another source revision.");
        var release = new PackageRelease
        {
            Version = contract.Version,
            Tag = tag,
            SourceRevision = contract.CommitSHA,
            ReleasedAt = existing?.ReleasedAt ?? releasedAt,
            Channel = ReleaseContract.Channel,
            ReleaseNotes = $"Automated AniSync Next {contract.Version} build from {contract.CommitSHA} using Shoko.Abstractions {contract.AbstractionsPackageVersion}.",
            Archives = [new()
            {
                Runtime = ReleaseContract.RuntimeIdentifier,
                Abstraction = ReleaseContract.AbstractionVersion,
                Url = $"{ReleaseContract.RepositoryUrl}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(contract.ZipFileName)}",
                Checksum = checksum,
            }],
        };
        if (existing is not null) manifest.Releases.Remove(existing);
        manifest.Releases.Add(release);
        manifest.Releases = manifest.Releases.OrderByDescending(item => BuildNumber(item.Version))
            .ThenByDescending(item => item.ReleasedAt).Take(ReleaseContract.RetainedReleaseCount).ToList();
        ApplyMetadata(manifest);
        ManifestValidator.Validate(manifest);
    }

    public static void Save(PackageManifest manifest, string output)
    {
        ManifestValidator.Validate(manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var serialized = Serialize(manifest);
        File.WriteAllText(output, serialized);
        var roundTrip = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(output), Options)
            ?? throw new InvalidOperationException("Generated manifest could not be parsed.");
        if (Serialize(roundTrip) != serialized) throw new InvalidOperationException("Manifest serialization is not deterministic.");
    }

    internal static string Serialize(PackageManifest manifest) =>
        JsonSerializer.Serialize(manifest, Options).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    internal static PackageManifest BaseManifest() => new()
    {
        Type = "package",
        ID = Guid.Parse(ReleaseContract.PackageID),
        Name = ReleaseContract.PackageName,
        Overview = "Synchronizes Shoko watch state, completion status, and ratings to AniList and MyAnimeList with reviewable decreases and mappings.",
        Authors = "JosephZurita",
        RepositoryUrl = ReleaseContract.RepositoryUrl,
        HomepageUrl = ReleaseContract.RepositoryUrl,
        ImageUrl = ReleaseContract.ImageUrl,
        Tags = ["shoko", "anime", "watch-state", "sync", "anilist", "myanimelist", "mal", "plugin"],
        Releases = [],
    };

    internal static int BuildNumber(string version)
    {
        const string prefix = "0.1.0-dev.";
        return version.StartsWith(prefix, StringComparison.Ordinal) &&
               int.TryParse(version[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number : throw new InvalidOperationException($"Invalid AniSync Next development version '{version}'.");
    }

    private static void ApplyMetadata(PackageManifest manifest)
    {
        var expected = BaseManifest();
        manifest.Type = expected.Type; manifest.ID = expected.ID; manifest.Name = expected.Name;
        manifest.Overview = expected.Overview; manifest.Authors = expected.Authors;
        manifest.RepositoryUrl = expected.RepositoryUrl; manifest.HomepageUrl = expected.HomepageUrl;
        manifest.ImageUrl = expected.ImageUrl; manifest.Tags = expected.Tags;
    }
}

internal static class ManifestValidator
{
    private static readonly Regex SHA = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex Revision = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);

    public static void Validate(PackageManifest manifest, bool enforceRetention = true, string? currentZip = null)
    {
        Require(manifest.Type == "package", "Manifest type must be package.");
        Require(manifest.ID == Guid.Parse(ReleaseContract.PackageID), "Manifest package ID is incorrect.");
        Require(manifest.Name == ReleaseContract.PackageName, "Manifest package name is incorrect.");
        Require(manifest.Authors == "JosephZurita", "Manifest author is incorrect.");
        Require(manifest.RepositoryUrl == ReleaseContract.RepositoryUrl && manifest.HomepageUrl == ReleaseContract.RepositoryUrl, "Manifest repository URLs are incorrect.");
        Require(Uri.TryCreate(manifest.ImageUrl, UriKind.Absolute, out var image) && image.Scheme == Uri.UriSchemeHttps, "Manifest image URL must use HTTPS.");
        Require(manifest.Tags is { Count: > 0 and <= 20 }, "Manifest search tags are required.");
        if (enforceRetention) Require(manifest.Releases.Count <= ReleaseContract.RetainedReleaseCount, "Manifest retains more than 30 releases.");
        Require(manifest.Releases.Select(release => release.Version).Distinct(StringComparer.Ordinal).Count() == manifest.Releases.Count, "Manifest contains duplicate versions.");
        var previous = int.MaxValue;
        foreach (var release in manifest.Releases)
        {
            var build = ManifestManager.BuildNumber(release.Version);
            Require(build <= previous && build <= ReleaseContract.MaximumBuildNumber, "Manifest releases are not newest first or exceed the revision limit.");
            previous = build;
            Require(Revision.IsMatch(release.SourceRevision), $"Release {release.Version} has an invalid source revision.");
            Require(release.ReleasedAt.Offset == TimeSpan.Zero, $"Release {release.Version} timestamp is not UTC.");
            Require(release.Channel == ReleaseContract.Channel, $"Release {release.Version} is not Dev.");
            Require(release.ReleaseNotes.Contains("Shoko.Abstractions 6.0.0-", StringComparison.Ordinal), $"Release {release.Version} does not identify its exact Shoko abstraction.");
            Require(release.Archives.Count == 1, $"Release {release.Version} must have one archive.");
            var archive = release.Archives.Single();
            Require(archive.Runtime == "any" && archive.Abstraction == "6.0.0", $"Release {release.Version} has incompatible archive metadata.");
            Require(SHA.IsMatch(archive.Checksum), $"Release {release.Version} checksum is not lowercase SHA-256.");
            var expected = $"{ReleaseContract.RepositoryUrl}/releases/download/{Uri.EscapeDataString(release.Tag)}/{Uri.EscapeDataString($"AniSync.Next-{release.Version}.zip")}";
            Require(archive.Url == expected, $"Release {release.Version} asset URL is incorrect.");
            if (currentZip is not null && Path.GetFileName(currentZip) == $"AniSync.Next-{release.Version}.zip")
                Require(PackageAssetBuilder.SHA256For(currentZip) == archive.Checksum, "Manifest ZIP checksum does not match the package.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
