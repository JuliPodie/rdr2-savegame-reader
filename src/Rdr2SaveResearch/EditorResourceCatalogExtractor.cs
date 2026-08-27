using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rdr2SaveResearch.Persistence;

/// <summary>
/// Offline evidence extractor for decoded editor resource blobs. It never
/// loads the blob as an assembly and never deserializes its object graph.
/// </summary>
public static partial class EditorResourceCatalogExtractor
{
    public const int CurrentSchemaVersion = 1;
    private const int MaximumInputBytes = 64 * 1024 * 1024;

    public static async Task<EditorResourceCatalog> ExtractAsync(
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(inputPath, cancellationToken)
            .ConfigureAwait(false);
        if (bytes.Length == 0 || bytes.Length > MaximumInputBytes)
        {
            throw new CampaignSaveSyncException(
                "Editor resource blob is empty or exceeds the 64 MB offline-analysis limit.");
        }
        var strings = ScanUtf16Strings(bytes);
        var hashEvidence = new List<EditorResourceHashEvidence>();
        var labels = new List<EditorResourceLabelEvidence>();
        foreach (var item in strings)
        {
            AddHashEvidence(item.Text, item.ByteOffset, "utf16", hashEvidence);
            if (LooksLikeLabel(item.Text))
            {
                labels.Add(new EditorResourceLabelEvidence(item.Text, item.ByteOffset, "utf16"));
            }
            foreach (Match match in Base64Pattern().Matches(item.Text))
            {
                if (!TryDecodePrintableBase64(match.Value, out var decoded))
                {
                    continue;
                }
                var offset = item.ByteOffset + match.Index * 2;
                AddHashEvidence(decoded, offset, "base64-utf16", hashEvidence);
                if (LooksLikeLabel(decoded))
                {
                    labels.Add(new EditorResourceLabelEvidence(decoded, offset, "base64-utf16"));
                }
            }
        }
        var associations = AssociatePngHashRuns(hashEvidence, labels);
        return new EditorResourceCatalog(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            Path.GetFileName(inputPath),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
            hashEvidence.DistinctBy(item => (item.Hash, item.ByteOffset, item.SourceKind))
                .OrderBy(item => item.ByteOffset).ToArray(),
            labels.DistinctBy(item => (item.Label, item.ByteOffset, item.SourceKind))
                .OrderBy(item => item.ByteOffset).ToArray(),
            associations);
    }

    public static async Task SaveAsync(string outputPath, EditorResourceCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        var output = Path.GetFullPath(outputPath);
        if (File.Exists(output))
        {
            throw new CampaignSaveSyncException("Catalog output already exists; refusing to overwrite it.");
        }
        await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, catalog,
            new JsonSerializerOptions { WriteIndented = true }, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyDictionary<uint, string>> LoadHighConfidenceAnnotationsAsync(
        string catalogPath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(catalogPath));
        var catalog = await JsonSerializer.DeserializeAsync<EditorResourceCatalog>(stream,
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new CampaignSaveSyncException("Editor catalog contains JSON null.");
        if (catalog.SchemaVersion != CurrentSchemaVersion)
        {
            throw new CampaignSaveSyncException("Editor catalog schema version is unsupported.");
        }
        return catalog.Associations.Where(item => item.Confidence.StartsWith("high_",
                StringComparison.Ordinal) && uint.TryParse(item.Hash,
                System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            .ToDictionary(item => Convert.ToUInt32(item.Hash, 16), item =>
                "catalog_" + NormalizeSemanticLabel(item.Label),
                EqualityComparer<uint>.Default);
    }

    private static string NormalizeSemanticLabel(string value) =>
        NonAlphaNumericPattern().Replace(value.ToLowerInvariant(), "_").Trim('_');

    private static IReadOnlyList<EditorResourceText> ScanUtf16Strings(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.Unicode.GetString(bytes);
        var result = new List<EditorResourceText>();
        var start = 0;
        while (start < text.Length)
        {
            while (start < text.Length && !IsPrintable(text[start])) start++;
            var end = start;
            while (end < text.Length && IsPrintable(text[end])) end++;
            if (end - start >= 4)
            {
                result.Add(new EditorResourceText(text[start..end], start * 2));
            }
            start = end + 1;
        }
        return result;
    }

    private static void AddHashEvidence(string text, int offset, string source,
        ICollection<EditorResourceHashEvidence> result)
    {
        foreach (Match match in HashPattern().Matches(text))
        {
            result.Add(new EditorResourceHashEvidence(match.Value.ToUpperInvariant(),
                offset + match.Index * (source == "utf16" ? 2 : 1), source,
                text.Contains(".png", StringComparison.OrdinalIgnoreCase) ? "asset-name" : "raw-hash"));
        }
    }

    private static IReadOnlyList<EditorResourceHashAssociation> AssociatePngHashRuns(
        IReadOnlyList<EditorResourceHashEvidence> hashes,
        IReadOnlyList<EditorResourceLabelEvidence> labels)
    {
        var assets = hashes.Where(item => item.Evidence == "asset-name").OrderBy(item => item.ByteOffset).ToArray();
        var result = new List<EditorResourceHashAssociation>();
        for (var start = 0; start < assets.Length;)
        {
            var end = start + 1;
            while (end < assets.Length && assets[end].ByteOffset - assets[end - 1].ByteOffset <= 128)
            {
                end++;
            }
            var assetRun = assets[start..end];
            var labelRun = labels.Where(item => item.Label.EndsWith(" Satchel",
                        StringComparison.OrdinalIgnoreCase) &&
                    item.ByteOffset >= assetRun[^1].ByteOffset &&
                    item.ByteOffset - assetRun[^1].ByteOffset <= 4096)
                .OrderBy(item => item.ByteOffset).ToArray();
            if (assetRun.Length >= 2 && labelRun.Length >= assetRun.Length)
            {
                for (var index = 0; index < assetRun.Length; index++)
                {
                    result.Add(new EditorResourceHashAssociation(assetRun[index].Hash,
                        labelRun[index].Label, "satchel", "high_ordered_asset_label_run",
                        assetRun[index].ByteOffset, labelRun[index].ByteOffset));
                }
            }
            start = end;
        }
        return result.DistinctBy(item => (item.Hash, item.Label)).ToArray();
    }

    private static bool TryDecodePrintableBase64(string value, out string decoded)
    {
        decoded = string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length is < 3 or > 4096 || bytes.Any(item => item is < 0x20 or > 0x7E)) return false;
            decoded = Encoding.ASCII.GetString(bytes);
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static bool LooksLikeLabel(string value) => value.Length is >= 4 and <= 160 &&
        value.Any(char.IsLetter) && value.All(character => char.IsLetterOrDigit(character) ||
            character is ' ' or '-' or '\'' or '&');
    private static bool IsPrintable(char value) => value is >= ' ' and <= '~';
    [GeneratedRegex("(?<![0-9A-Fa-f])[0-9A-Fa-f]{8}(?![0-9A-Fa-f])")]
    private static partial Regex HashPattern();
    [GeneratedRegex("(?<![A-Za-z0-9+/])[A-Za-z0-9+/]{8,}={0,2}(?![A-Za-z0-9+/])")]
    private static partial Regex Base64Pattern();
    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphaNumericPattern();
    private readonly record struct EditorResourceText(string Text, int ByteOffset);
}

public sealed record EditorResourceCatalog(int SchemaVersion, DateTimeOffset CreatedAtUtc,
    string SourceFileName, string SourceSha256, IReadOnlyList<EditorResourceHashEvidence> HashEvidence,
    IReadOnlyList<EditorResourceLabelEvidence> LabelEvidence,
    IReadOnlyList<EditorResourceHashAssociation> Associations);
public sealed record EditorResourceHashEvidence(string Hash, int ByteOffset, string SourceKind, string Evidence);
public sealed record EditorResourceLabelEvidence(string Label, int ByteOffset, string SourceKind);
public sealed record EditorResourceHashAssociation(string Hash, string Label, string Category,
    string Confidence, int HashByteOffset, int LabelByteOffset);
