using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rdr2SaveResearch.Persistence;

/// <summary>
/// Extracts a read-only index from an RDR3 NativeDB-style natives.h file.
/// The header is treated as text only: it is never compiled, loaded, or used
/// to invoke game natives.
/// </summary>
public static partial class NativeHeaderCatalogExtractor
{
    public const int CurrentSchemaVersion = 1;
    private const long MaximumInputBytes = 16L * 1024 * 1024;

    public static async Task<NativeHeaderCatalog> ExtractAsync(
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(inputPath);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length is <= 0 or > MaximumInputBytes)
        {
            throw new CampaignSaveSyncException(
                "natives.h must exist and be between 1 byte and 16 MB.");
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken)
            .ConfigureAwait(false);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var entries = new List<NativeHeaderEntry>();
        var currentNamespace = string.Empty;

        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.None))
        {
            var namespaceMatch = NamespacePattern().Match(line);
            if (namespaceMatch.Success)
            {
                currentNamespace = namespaceMatch.Groups["namespace"].Value;
                continue;
            }

            var nativeMatch = NativePattern().Match(line);
            if (!nativeMatch.Success || string.IsNullOrEmpty(currentNamespace))
            {
                continue;
            }

            if (!ulong.TryParse(nativeMatch.Groups["hash"].Value[2..],
                    NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hash))
            {
                continue;
            }

            entries.Add(new NativeHeaderEntry(
                currentNamespace,
                nativeMatch.Groups["name"].Value,
                $"{hash:X16}",
                nativeMatch.Groups["returnType"].Value.Trim()));
        }

        if (entries.Count == 0)
        {
            throw new CampaignSaveSyncException(
                "The supplied file contains no NativeDB-style NATIVE_DECL entries.");
        }

        return new NativeHeaderCatalog(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            Path.GetFileName(fullPath),
            Convert.ToHexString(SHA256.HashData(bytes)),
            entries.DistinctBy(item => (item.Namespace, item.Name, item.Hash, item.ReturnType))
                .OrderBy(item => item.Namespace, StringComparer.Ordinal)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ThenBy(item => item.Hash, StringComparer.Ordinal)
                .ToArray());
    }

    public static async Task<NativeHeaderCatalog> LoadAsync(
        string catalogPath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(catalogPath));
        var catalog = await JsonSerializer.DeserializeAsync<NativeHeaderCatalog>(stream,
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new CampaignSaveSyncException("Native catalog contains JSON null.");
        if (catalog.SchemaVersion != CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(catalog.SourceSha256) ||
            catalog.Entries.Count == 0 ||
            catalog.Entries.Any(entry => string.IsNullOrWhiteSpace(entry.Namespace) ||
                string.IsNullOrWhiteSpace(entry.Name) || entry.Hash.Length != 16 ||
                !ulong.TryParse(entry.Hash, NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out _)))
        {
            throw new CampaignSaveSyncException("Native catalog is invalid or unsupported.");
        }
        return catalog;
    }

    [GeneratedRegex("^\\s*namespace\\s+(?<namespace>[A-Za-z_][A-Za-z0-9_]*)\\s*$")]
    private static partial Regex NamespacePattern();

    [GeneratedRegex("^\\s*NATIVE_DECL\\s+(?<returnType>.+?)\\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*\\([^)]*\\)\\s*\\{.*?invoke(?:<[^>]+>)?\\((?<hash>0x[0-9A-Fa-f]{1,16})")]
    private static partial Regex NativePattern();
}

public sealed record NativeHeaderCatalog(
    int SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string SourceFileName,
    string SourceSha256,
    IReadOnlyList<NativeHeaderEntry> Entries);

public sealed record NativeHeaderEntry(
    string Namespace,
    string Name,
    string Hash,
    string ReturnType);
