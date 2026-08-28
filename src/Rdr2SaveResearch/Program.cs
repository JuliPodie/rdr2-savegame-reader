using System.Text.Json;
using Rdr2SaveResearch.Persistence;

namespace Rdr2SaveResearch;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                PrintHelp();
                return 0;
            }

            var options = Options.Parse(args[1..]);
            return args[0].ToLowerInvariant() switch
            {
                "verify" => await VerifyAsync(options),
                "inspect" => await InspectAsync(options),
                "diff" => await DiffAsync(options),
                "schema" => await SchemaAsync(options),
                "extract-editor-catalog" => await ExtractEditorCatalogAsync(options),
                "extract-native-catalog" => await ExtractNativeCatalogAsync(options),
                "analyze-control" => await AnalyzeControlAsync(options),
                "analyze-campaign" => await AnalyzeCampaignAsync(options),
                "create-test-clone" => await CreateTestCloneAsync(options),
                _ => FailUsage($"Unknown command '{args[0]}'.")
            };
        }
        catch (Exception exception) when (exception is IOException or FormatException or
                                         CampaignSaveSyncException or Rdr2PcSaveCodecException or
                                         Rdr2RsavContentException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"ERROR: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> VerifyAsync(Options options)
    {
        var bytes = await File.ReadAllBytesAsync(options.Required("save"));
        var result = Rdr2PcSaveCodec.VerifyRoundTrip(bytes);
        Console.WriteLine($"RDR2_CODEC title={result.Title} checks={result.CheckCount} exactRoundTrip={result.IsExactRoundTrip} input={result.InputSha256} output={result.OutputSha256}");
        return result.IsExactRoundTrip ? 0 : 2;
    }

    private static async Task<int> InspectAsync(Options options)
    {
        var document = Rdr2PcSaveCodec.Decode(await File.ReadAllBytesAsync(options.Required("save")));
        var nativeCatalog = await LoadNativeCatalogAsync(options);
        var report = Rdr2RsavContentAnalyzer.Analyze(document, nativeCatalog?.Entries);
        await SaveNewJsonAsync(options.Required("output"), report);
        Console.WriteLine($"RSAV_REPORT_READY={Path.GetFullPath(options.Required("output"))} regions={report.Regions.Count} tags={report.Tags.Count} references={report.References.Count} psoFrames={report.PsoFrames.Count} strings={report.Strings.Count}");
        return 0;
    }

    private static async Task<int> SchemaAsync(Options options) => await InspectAsync(options);

    private static async Task<int> DiffAsync(Options options)
    {
        var nativeCatalog = await LoadNativeCatalogAsync(options);
        var before = Rdr2PcSaveCodec.Decode(await File.ReadAllBytesAsync(options.Required("before")));
        var after = Rdr2PcSaveCodec.Decode(await File.ReadAllBytesAsync(options.Required("after")));
        var report = Rdr2RsavContentDiffer.Compare(before, after, nativeCatalog?.Entries);
        await SaveNewJsonAsync(options.Required("output"), report);
        Console.WriteLine($"RSAV_DIFF_READY={Path.GetFullPath(options.Required("output"))} changedRegions={report.Regions.Count(static x => !x.IsByteIdentical)} changedParts={report.Parts.Count(static x => !x.IsByteIdentical)} changedSemanticFields={report.SemanticFieldChanges.Count} referenceCountChanges={report.ReferenceCountChanges.Count} tagCountChanges={report.TagCountChanges.Count}");
        Console.WriteLine("Read-only result: differences are evidence, not semantic or safe-to-write fields.");
        return 0;
    }

    private static async Task<int> ExtractEditorCatalogAsync(Options options)
    {
        var catalog = await EditorResourceCatalogExtractor.ExtractAsync(options.Required("input"));
        await EditorResourceCatalogExtractor.SaveAsync(options.Required("output"), catalog);
        Console.WriteLine($"EDITOR_RESOURCE_CATALOG_READY={Path.GetFullPath(options.Required("output"))} hashEvidence={catalog.HashEvidence.Count} labels={catalog.LabelEvidence.Count} associations={catalog.Associations.Count}");
        Console.WriteLine("Static extraction only: resource blobs are never loaded as assemblies or deserialized.");
        return 0;
    }

    private static async Task<int> ExtractNativeCatalogAsync(Options options)
    {
        var catalog = await NativeHeaderCatalogExtractor.ExtractAsync(options.Required("input"));
        await SaveNewJsonAsync(options.Required("output"), catalog);
        Console.WriteLine($"NATIVE_HEADER_CATALOG_READY={Path.GetFullPath(options.Required("output"))} entries={catalog.Entries.Count} sourceSha256={catalog.SourceSha256}");
        Console.WriteLine("Static extraction only: natives.h is treated as text and no game native is invoked.");
        return 0;
    }

    private static async Task<int> AnalyzeControlAsync(Options options)
    {
        var report = await CampaignCapabilityAnalyzer.AnalyzeIsolatedControlAsync(options.Required("before"), options.Required("after"), options.Required("label"), await LoadCatalogAsync(options));
        await CampaignCapabilityAnalyzer.SaveAsync(options.Required("output"), report);
        Console.WriteLine($"CAPABILITY_CONTROL_READY={Path.GetFullPath(options.Required("output"))} candidates={report.Candidates.Count} structuralCandidates={report.StructuralCandidates?.Count ?? 0} unresolvedControlDeltas={report.ControlDeltas?.Count ?? 0}");
        Console.WriteLine("Evidence only: this MVP does not expose save mutation commands.");
        return 0;
    }

    private static async Task<int> AnalyzeCampaignAsync(Options options)
    {
        var report = await CampaignCapabilityAnalyzer.AnalyzeAsync(options.Required("before"), options.Required("medal-only"), options.Required("after"), await LoadCatalogAsync(options));
        await CampaignCapabilityAnalyzer.SaveAsync(options.Required("output"), report);
        Console.WriteLine($"CAPABILITY_REPORT_READY={Path.GetFullPath(options.Required("output"))} candidates={report.Candidates.Count} enableCandidates={report.EnableCandidateCount} monotonicIncreases={report.MonotonicIncreaseCount}");
        Console.WriteLine("Evidence only: this MVP does not expose save mutation commands.");
        return 0;
    }

    private static async Task<int> CreateTestCloneAsync(Options options)
    {
        var result = await CampaignCloneExperiment.CreateAsync(options.Required("host-before"), options.Required("host-after"), options.Required("recipient-before"), options.Required("output"), options.Required("confirm"));
        Console.WriteLine($"TEST_CLONE_READY={result.OutputPath} sourceSha256={result.SourceSha256} changedRegions={result.ChangedRegions} changedParts={result.ChangedParts}");
        Console.WriteLine("New-file-only test clone. It is not a merge and never modifies an existing save.");
        return 0;
    }

    private static async Task<IReadOnlyDictionary<uint, string>?> LoadCatalogAsync(Options options)
    {
        if (options.Optional("catalog") is not { } path)
        {
            return null;
        }
        return await EditorResourceCatalogExtractor.LoadHighConfidenceAnnotationsAsync(path);
    }

    private static async Task<NativeHeaderCatalog?> LoadNativeCatalogAsync(Options options) =>
        options.Optional("native-catalog") is { } path
            ? await NativeHeaderCatalogExtractor.LoadAsync(path)
            : null;

    private static async Task SaveNewJsonAsync<T>(string path, T value)
    {
        var output = Path.GetFullPath(path);
        if (File.Exists(output)) throw new CampaignSaveSyncException("Output already exists; refusing to overwrite it.");
        await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, value, new JsonSerializerOptions { WriteIndented = true });
    }

    private static int FailUsage(string message) { Console.Error.WriteLine(message); PrintHelp(); return 64; }

    private static void PrintHelp() => Console.WriteLine("""
        RDR2 Save Research MVP — offline research tooling for PC Story Mode SRDR saves

          dotnet run --project src/Rdr2SaveResearch -- verify --save <save-copy>
          dotnet run --project src/Rdr2SaveResearch -- inspect --save <save-copy> --output <new-report.json> [--native-catalog <native-catalog.json>]
          dotnet run --project src/Rdr2SaveResearch -- diff --before <save-copy> --after <save-copy> --output <new-report.json> [--native-catalog <native-catalog.json>]
          dotnet run --project src/Rdr2SaveResearch -- extract-editor-catalog --input <decoded-resource-bin> --output <new-catalog.json>
          dotnet run --project src/Rdr2SaveResearch -- extract-native-catalog --input <natives.h> --output <new-catalog.json>
          dotnet run --project src/Rdr2SaveResearch -- analyze-control --before <save-copy> --after <save-copy> --label <one-change-description> --output <new-report.json> [--catalog <catalog.json>]
          dotnet run --project src/Rdr2SaveResearch -- analyze-campaign --before <save-copy> --medal-only <save-copy> --after <save-copy> --output <new-report.json> [--catalog <catalog.json>]
          dotnet run --project src/Rdr2SaveResearch -- create-test-clone --host-before <save-copy> --host-after <save-copy> --recipient-before <save-copy> --output <new-save-copy> --confirm CREATE_TEST_CLONE

        Work on copies. Analysis is read-only. The sole file-producing command creates a verified new throwaway clone and never overwrites a save.
        """);

    private sealed class Options
    {
        private readonly Dictionary<string, string> _values;
        private Options(Dictionary<string, string> values) => _values = values;
        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < args.Length; index++)
            {
                var arg = args[index];
                if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2 || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new CampaignSaveSyncException($"Option '{arg}' requires a value.");
                if (!values.TryAdd(arg[2..], args[++index])) throw new CampaignSaveSyncException($"Option '{arg}' was supplied twice.");
            }
            return new Options(values);
        }
        public string Required(string name) => Optional(name) ?? throw new CampaignSaveSyncException($"Missing required option '--{name}'.");
        public string? Optional(string name) => _values.GetValueOrDefault(name);
    }
}
