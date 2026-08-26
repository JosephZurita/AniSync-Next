using System.Reflection;
using System.Runtime.Loader;

namespace AniSync.Next.ReleaseTool;

internal sealed record PluginAssemblyIdentity(
    Version AssemblyVersion,
    string FileVersion,
    string InformationalVersion,
    string PackageID,
    string PackageName,
    string RuntimeIdentifier,
    string ReleaseChannel,
    string SourceRevision,
    Version AbstractionVersion);

internal static class AssemblyInspector
{
    public static PluginAssemblyIdentity Inspect(string dllPath)
    {
        var fullPath = Path.GetFullPath(dllPath);
        var assemblyName = AssemblyName.GetAssemblyName(fullPath);
        var shadowDirectory = Path.Combine(Path.GetTempPath(), $"AniSync.Next.ReleaseTool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(shadowDirectory);
        var shadowPath = Path.Combine(shadowDirectory, Path.GetFileName(fullPath));
        File.Copy(fullPath, shadowPath);
        var context = new AssemblyLoadContext($"AniSync.Next.Inspection.{Guid.NewGuid():N}", true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(shadowPath);
            var attributes = assembly.GetCustomAttributesData();
            var metadata = attributes
                .Where(attribute => attribute.AttributeType.FullName == typeof(AssemblyMetadataAttribute).FullName)
                .Where(attribute => attribute.ConstructorArguments.Count == 2)
                .ToDictionary(attribute => (string)attribute.ConstructorArguments[0].Value!,
                    attribute => (string)attribute.ConstructorArguments[1].Value!, StringComparer.Ordinal);
            var abstraction = assembly.GetReferencedAssemblies().Single(reference => reference.Name == "Shoko.Abstractions").Version
                ?? throw new InvalidOperationException("Shoko.Abstractions reference has no assembly version.");
            return new PluginAssemblyIdentity(
                assemblyName.Version ?? throw new InvalidOperationException("Plugin DLL has no assembly version."),
                Attribute(attributes, typeof(AssemblyFileVersionAttribute)),
                Attribute(attributes, typeof(AssemblyInformationalVersionAttribute)),
                Metadata(metadata, "PackageID"), Metadata(metadata, "PackageName"),
                Metadata(metadata, "RuntimeIdentifier"), Metadata(metadata, "ReleaseChannel"),
                Metadata(metadata, "SourceRevision"), abstraction);
        }
        finally
        {
            context.Unload();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { Directory.Delete(shadowDirectory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static void Validate(string dllPath, ReleaseContract contract)
    {
        var identity = Inspect(dllPath);
        Require(identity.AssemblyVersion == contract.AssemblyVersion, $"AssemblyVersion {identity.AssemblyVersion} does not match {contract.AssemblyVersion}.");
        Require(identity.FileVersion == contract.AssemblyVersion.ToString(), $"AssemblyFileVersion {identity.FileVersion} does not match {contract.AssemblyVersion}.");
        Require(identity.InformationalVersion.StartsWith(contract.Version, StringComparison.Ordinal), $"AssemblyInformationalVersion must start with {contract.Version}.");
        Require(identity.InformationalVersion.Contains(contract.CommitSHA, StringComparison.Ordinal), "AssemblyInformationalVersion must preserve the full commit SHA.");
        Require(identity.PackageID == ReleaseContract.PackageID, $"PackageID {identity.PackageID} does not match {ReleaseContract.PackageID}.");
        Require(identity.PackageName == ReleaseContract.PackageName, $"PackageName {identity.PackageName} does not match {ReleaseContract.PackageName}.");
        Require(identity.RuntimeIdentifier == ReleaseContract.RuntimeIdentifier, "RuntimeIdentifier must be any.");
        Require(identity.ReleaseChannel == ReleaseContract.Channel, "ReleaseChannel must be Dev.");
        Require(identity.SourceRevision == contract.CommitSHA, "SourceRevision does not match the release commit.");
        Require(identity.AbstractionVersion.Major == 6 && identity.AbstractionVersion.Minor == 0 && identity.AbstractionVersion.Build == 0,
            $"Referenced Shoko abstraction {identity.AbstractionVersion} does not represent 6.0.0.");
    }

    private static string Attribute(IList<CustomAttributeData> attributes, Type type) =>
        (string)attributes.Single(attribute => attribute.AttributeType.FullName == type.FullName).ConstructorArguments.Single().Value!;
    private static string Metadata(IReadOnlyDictionary<string, string> metadata, string key) => metadata.TryGetValue(key, out var value)
        ? value : throw new InvalidOperationException($"Plugin DLL is missing assembly metadata '{key}'.");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
