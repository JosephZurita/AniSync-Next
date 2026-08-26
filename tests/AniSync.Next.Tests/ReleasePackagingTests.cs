using AniSync.Next.ReleaseTool;
using FluentAssertions;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using AniSync.Next.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Shoko.Abstractions.Plugin;

namespace AniSync.Next.Tests;

public sealed class ReleasePackagingTests
{
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void EmbeddedPluginIdentityMatchesLockedPackageContract()
    {
        var plugin = new Plugin();
        var metadata = typeof(Plugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(item => item.Key, item => item.Value);

        plugin.ID.Should().Be(Guid.Parse(ReleaseContract.PackageID));
        plugin.Name.Should().Be(ReleaseContract.PackageName);
        metadata["PackageID"].Should().Be(ReleaseContract.PackageID);
        metadata["PackageName"].Should().Be(ReleaseContract.PackageName);
        metadata["ReleaseChannel"].Should().Be("Dev");
        metadata["RuntimeIdentifier"].Should().Be("any");
    }

    [Fact]
    public void PluginRegistersItsPageCoreServicesAndTrackedHostedAdapters()
    {
        using var directory = new TestDirectory();
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(value => value.PluginsPath).Returns(directory.Path);
        var services = new ServiceCollection();

        PluginServiceRegistration.RegisterServices(services, paths.Object);

        new Plugin().GetPages().Should().ContainSingle().Which.Url.Should().Be("/anisync-next");
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(ISyncPlanner));
        services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)).Should().Be(2);
    }

    [Fact]
    public void ManifestUsesCurrentShokoPackageSchemaContract()
    {
        var manifest = ManifestManager.BaseManifest();
        var contract = ReleaseContract.Create(7, Revision, "6.0.0-alpha.81");
        ManifestManager.AddOrReplace(manifest, contract, "dev-7", DateTimeOffset.UnixEpoch, new string('a', 64));

        ManifestValidator.Validate(manifest);
        manifest.Type.Should().Be("package");
        manifest.ID.Should().Be(Guid.Parse("8eea2528-a2f8-543a-8bc5-a06bb5a138bd"));
        manifest.Name.Should().Be("AniSync Next");
        manifest.Authors.Should().Be("JosephZurita");
        manifest.Releases.Single().Archives.Single().Runtime.Should().Be("any");
        manifest.Releases.Single().Archives.Single().Abstraction.Should().Be("6.0.0");
        manifest.Releases.Single().ReleaseNotes.Should().Contain("Shoko.Abstractions 6.0.0-alpha.81");
    }

    [Fact]
    public void ManifestRerunReplacesSameVersionWithoutChangingReleaseTime()
    {
        var manifest = ManifestManager.BaseManifest();
        var contract = ReleaseContract.Create(7, Revision, "6.0.0-alpha.81");
        ManifestManager.AddOrReplace(manifest, contract, "dev-7", DateTimeOffset.UnixEpoch, new string('a', 64));

        ManifestManager.AddOrReplace(manifest, contract, "dev-7", DateTimeOffset.UnixEpoch.AddDays(1), new string('b', 64));

        manifest.Releases.Should().ContainSingle();
        manifest.Releases[0].ReleasedAt.Should().Be(DateTimeOffset.UnixEpoch);
        manifest.Releases[0].Archives[0].Checksum.Should().Be(new string('b', 64));
    }

    [Fact]
    public void ManifestRetainsNewestThirtyReleasesInDeterministicOrder()
    {
        var manifest = ManifestManager.BaseManifest();
        for (var build = 1; build <= 35; build++)
        {
            var contract = ReleaseContract.Create(build, Revision, "6.0.0-alpha.81");
            ManifestManager.AddOrReplace(manifest, contract, $"dev-{build}",
                DateTimeOffset.UnixEpoch.AddMinutes(build), new string('a', 64));
        }

        manifest.Releases.Should().HaveCount(30);
        manifest.Releases.Select(item => item.Version).Should().StartWith("0.1.0-dev.35").And.EndWith("0.1.0-dev.6");
        ManifestManager.Serialize(manifest).Should().Be(ManifestManager.Serialize(manifest));
    }

    [Fact]
    public void ZipMustContainExactlyTheReleasedDllAndManifestChecksumMustMatch()
    {
        using var directory = new TestDirectory();
        var dll = Path.Combine(directory.Path, "AniSync.Next.dll");
        var zip = Path.Combine(directory.Path, "AniSync.Next-0.1.0-dev.7.zip");
        File.WriteAllBytes(dll, [1, 2, 3, 4, 5]);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("AniSync.Next.dll");
            using var stream = entry.Open();
            stream.Write([1, 2, 3, 4, 5]);
        }
        WriteChecksum(dll);
        WriteChecksum(zip);

        var checksum = PackageAssetValidator.Validate(dll, dll + ".sha256", zip, zip + ".sha256");
        var manifest = ManifestManager.BaseManifest();
        ManifestManager.AddOrReplace(manifest, ReleaseContract.Create(7, Revision, "6.0.0-alpha.81"),
            "dev-7", DateTimeOffset.UnixEpoch, checksum);

        ManifestValidator.Validate(manifest, currentZip: zip);
        using var reopened = ZipFile.OpenRead(zip);
        reopened.Entries.Select(item => item.FullName).Should().Equal("AniSync.Next.dll");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65535)]
    public void InvalidClrRevisionIsRejectedBeforePublishing(int build)
    {
        var action = () => ReleaseContract.Create(build, Revision, "6.0.0-alpha.81");
        action.Should().Throw<InvalidOperationException>().WithMessage("*CLR revision range*");
    }

    private static void WriteChecksum(string path)
    {
        using var stream = File.OpenRead(path);
        var checksum = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        File.WriteAllText(path + ".sha256", $"{checksum}  {Path.GetFileName(path)}\n");
    }
}
