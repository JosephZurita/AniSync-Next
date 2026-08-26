using System.Globalization;

namespace AniSync.Next.ReleaseTool;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new InvalidOperationException("Expected pack or manifest command.");
            var options = Parse(args[1..]);
            return args[0] switch
            {
                "pack" => Pack(options),
                "manifest" => Manifest(options),
                _ => throw new InvalidOperationException($"Unknown command '{args[0]}'."),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"AniSync Next release validation failed: {exception.Message}");
            return 1;
        }
    }

    private static int Pack(IReadOnlyDictionary<string, string> options)
    {
        var contract = Contract(options);
        var assets = PackageAssetBuilder.Create(Required(options, "dll"), Required(options, "output"), contract,
            Timestamp(Required(options, "timestamp")));
        Console.WriteLine($"Created {Path.GetFileName(assets.ZipPath)} ({assets.ZipChecksum})");
        return 0;
    }

    private static int Manifest(IReadOnlyDictionary<string, string> options)
    {
        var contract = Contract(options);
        var dll = Required(options, "dll");
        var zip = Required(options, "zip");
        AssemblyInspector.Validate(dll, contract);
        var checksum = PackageAssetValidator.Validate(dll, dll + ".sha256", zip, zip + ".sha256");
        options.TryGetValue("existing", out var existing);
        var manifest = ManifestManager.LoadOrCreate(existing);
        ManifestManager.AddOrReplace(manifest, contract, Required(options, "tag"),
            Timestamp(Required(options, "released-at")), checksum);
        ManifestValidator.Validate(manifest, currentZip: zip);
        ManifestManager.Save(manifest, Required(options, "output"));
        Console.WriteLine($"Generated manifest.json for {contract.Version}");
        return 0;
    }

    private static ReleaseContract Contract(IReadOnlyDictionary<string, string> options)
    {
        var raw = Required(options, "run-number");
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw new InvalidOperationException($"Development build number '{raw}' is not numeric.");
        return ReleaseContract.Create(number, Required(options, "commit"), Required(options, "abstractions-version"));
    }

    private static DateTimeOffset Timestamp(string value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            throw new InvalidOperationException($"Timestamp '{value}' is not ISO 8601.");
        return timestamp;
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw new InvalidOperationException("Options must use --name value pairs.");
            if (!result.TryAdd(args[index][2..], args[index + 1]))
                throw new InvalidOperationException($"Option {args[index]} was repeated.");
        }
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidOperationException($"Missing --{key}.");
}
