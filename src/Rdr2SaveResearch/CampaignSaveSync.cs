using System.Security.Cryptography;
using System.Text.Json;

namespace Rdr2SaveResearch.Persistence;

/// <summary>
/// A deliberately guarded, offline-only tool for studying and applying
/// campaign-state changes in a single RDR2 save-slot file. It never attempts
/// to infer what a changed byte means: every range begins unapproved and must
/// be explicitly reviewed before it can be written to another save.
/// </summary>
public static class CampaignSaveSync
{
    public const int CurrentSchemaVersion = 1;
    public const string ApplyConfirmation = "APPLY_CAMPAIGN_SYNC";

    private const long MaximumSaveBytes = 128L * 1024 * 1024;
    private const int MaximumChangedBytes = 2 * 1024 * 1024;
    private const int MaximumRanges = 16_384;

    public static async Task<CampaignSaveProfile> InspectAsync(
        string beforePath,
        string afterPath,
        CancellationToken cancellationToken = default)
    {
        var before = await ReadSaveAsync(beforePath, cancellationToken)
            .ConfigureAwait(false);
        var after = await ReadSaveAsync(afterPath, cancellationToken)
            .ConfigureAwait(false);
        if (before.Bytes.Length != after.Bytes.Length)
        {
            var sharedLength = Math.Min(before.Bytes.Length, after.Bytes.Length);
            var prefixLength = 0;
            while (prefixLength < sharedLength &&
                   before.Bytes[prefixLength] == after.Bytes[prefixLength])
            {
                prefixLength++;
            }

            var suffixLength = 0;
            while (suffixLength < sharedLength - prefixLength &&
                   before.Bytes[before.Bytes.Length - 1 - suffixLength] ==
                   after.Bytes[after.Bytes.Length - 1 - suffixLength])
            {
                suffixLength++;
            }

            // An insertion/deletion shifts every following byte. It cannot be
            // treated as an offset patch on another save, but reporting the
            // shape gives us evidence for a future format-aware parser.
            return new CampaignSaveProfile
            {
                SchemaVersion = CurrentSchemaVersion,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                HostBeforeSha256 = before.Sha256,
                HostAfterSha256 = after.Sha256,
                SaveLength = before.Bytes.Length,
                HostAfterLength = after.Bytes.Length,
                ChangedByteCount = Math.Max(
                    before.Bytes.Length - prefixLength - suffixLength,
                    after.Bytes.Length - prefixLength - suffixLength),
                StructuralChange = new CampaignSaveStructuralChange
                {
                    Offset = prefixLength,
                    BeforeLength = before.Bytes.Length - prefixLength - suffixLength,
                    AfterLength = after.Bytes.Length - prefixLength - suffixLength,
                    SharedSuffixLength = suffixLength
                },
                Ranges = []
            }.Validate();
        }

        var ranges = new List<CampaignSaveRange>();
        var changedBytes = 0;
        for (var offset = 0; offset < before.Bytes.Length;)
        {
            if (before.Bytes[offset] == after.Bytes[offset])
            {
                offset++;
                continue;
            }

            var start = offset;
            while (offset < before.Bytes.Length &&
                   before.Bytes[offset] != after.Bytes[offset])
            {
                offset++;
            }

            var length = offset - start;
            changedBytes = checked(changedBytes + length);
            if (changedBytes > MaximumChangedBytes ||
                ranges.Count >= MaximumRanges)
            {
                throw new CampaignSaveSyncException(
                    "The snapshots contain too many changes for a safe campaign experiment.");
            }

            ranges.Add(new CampaignSaveRange
            {
                Id = ranges.Count,
                Offset = start,
                BeforeBase64 = Convert.ToBase64String(before.Bytes.AsSpan(start, length)),
                AfterBase64 = Convert.ToBase64String(after.Bytes.AsSpan(start, length)),
                Approved = false,
                Rationale = string.Empty
            });
        }

        return new CampaignSaveProfile
        {
            SchemaVersion = CurrentSchemaVersion,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            HostBeforeSha256 = before.Sha256,
            HostAfterSha256 = after.Sha256,
            SaveLength = before.Bytes.Length,
            HostAfterLength = after.Bytes.Length,
            ChangedByteCount = changedBytes,
            Ranges = ranges
        }.Validate();
    }

    public static async Task SaveProfileAsync(
        string path,
        CampaignSaveProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new CampaignSaveSyncException("Profile path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    profile,
                    CampaignSaveJson.Options,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static async Task<CampaignSaveProfile> LoadProfileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var profile = await JsonSerializer.DeserializeAsync<CampaignSaveProfile>(
            stream,
            CampaignSaveJson.Options,
            cancellationToken).ConfigureAwait(false);
        return (profile ?? throw new CampaignSaveSyncException(
            "Campaign profile contains JSON null.")).Validate();
    }

    public static CampaignSaveProfile ApproveRanges(
        CampaignSaveProfile profile,
        IReadOnlySet<int> approvedIds,
        string rationale)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(approvedIds);
        if (string.IsNullOrWhiteSpace(rationale) || rationale.Length > 500)
        {
            throw new CampaignSaveSyncException(
                "Approval requires a short, non-empty campaign-only rationale.");
        }

        var unknownId = approvedIds.FirstOrDefault(
            id => profile.Ranges.All(range => range.Id != id));
        if (approvedIds.Count > 0 &&
            profile.Ranges.All(range => range.Id != unknownId))
        {
            throw new CampaignSaveSyncException(
                $"Range {unknownId} does not exist in this profile.");
        }

        var approved = profile with
        {
            Ranges = profile.Ranges.Select(range =>
                approvedIds.Contains(range.Id)
                    ? range with { Approved = true, Rationale = rationale.Trim() }
                    : range with { Approved = false, Rationale = string.Empty })
                .ToList()
        };
        return approved.Validate();
    }

    public static async Task<CampaignSyncValidation> ValidateTargetAsync(
        string targetPath,
        CampaignSaveProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        if (!profile.CanApplyRawRanges)
        {
            return new CampaignSyncValidation(
                CanApply: false,
                "Host snapshots have a structural length change; a format-aware " +
                "RDR2 save parser is required before any guest save can be modified.",
                Array.Empty<int>());
        }
        var target = await ReadSaveAsync(targetPath, cancellationToken)
            .ConfigureAwait(false);
        if (target.Bytes.Length != profile.SaveLength)
        {
            return new CampaignSyncValidation(
                CanApply: false,
                "Target length differs from the profiled host snapshots.",
                Array.Empty<int>());
        }

        var conflicts = new List<int>();
        foreach (var range in profile.Ranges.Where(static range => range.Approved))
        {
            var before = range.BeforeBytes();
            if (!target.Bytes.AsSpan(range.Offset, before.Length).SequenceEqual(before))
            {
                conflicts.Add(range.Id);
            }
        }

        if (profile.Ranges.All(static range => !range.Approved))
        {
            return new CampaignSyncValidation(
                CanApply: false,
                "No ranges are approved; inspection output is read-only by default.",
                Array.Empty<int>());
        }

        return conflicts.Count == 0
            ? new CampaignSyncValidation(true, "Target matches every approved baseline range.", conflicts)
            : new CampaignSyncValidation(
                false,
                "Target differs in one or more approved ranges; no write is safe.",
                conflicts);
    }

    public static async Task<CampaignSyncApplyResult> ApplyAsync(
        string targetPath,
        CampaignSaveProfile profile,
        string confirmation,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(confirmation, ApplyConfirmation, StringComparison.Ordinal))
        {
            throw new CampaignSaveSyncException(
                $"Apply requires --confirm {ApplyConfirmation}.");
        }

        var validation = await ValidateTargetAsync(
            targetPath,
            profile,
            cancellationToken).ConfigureAwait(false);
        if (!validation.CanApply)
        {
            throw new CampaignSaveSyncException(validation.Message);
        }

        var target = await ReadSaveAsync(targetPath, cancellationToken)
            .ConfigureAwait(false);
        foreach (var range in profile.Ranges.Where(static range => range.Approved))
        {
            range.AfterBytes().CopyTo(target.Bytes, range.Offset);
        }

        var fullTargetPath = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullTargetPath)
            ?? throw new CampaignSaveSyncException("Target save path has no parent directory.");
        var backupPath = Path.Combine(
            directory,
            $"{Path.GetFileName(fullTargetPath)}.coopstory-before-campaign-sync-" +
            $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}.bak");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullTargetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            // Exclusive access prevents a live game from being modified. The
            // game must be at its title screen / closed before this can pass.
            using (new FileStream(
                fullTargetPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
            }

            File.Copy(fullTargetPath, backupPath, overwrite: false);
            await File.WriteAllBytesAsync(
                temporaryPath,
                target.Bytes,
                cancellationToken).ConfigureAwait(false);
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Flush(flushToDisk: true);
            }
            File.Replace(temporaryPath, fullTargetPath, null, ignoreMetadataErrors: true);
            var applied = await ReadSaveAsync(fullTargetPath, cancellationToken)
                .ConfigureAwait(false);
            return new CampaignSyncApplyResult(
                backupPath,
                applied.Sha256,
                profile.Ranges.Count(static range => range.Approved));
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<SaveBytes> ReadSaveAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length is <= 0 or > MaximumSaveBytes)
        {
            throw new CampaignSaveSyncException(
                "Save snapshot must exist and be between 1 byte and 128 MiB.");
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken)
            .ConfigureAwait(false);
        return new SaveBytes(bytes, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private sealed record SaveBytes(byte[] Bytes, string Sha256);
}

public sealed record CampaignSaveProfile
{
    public int SchemaVersion { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public string HostBeforeSha256 { get; init; } = string.Empty;
    public string HostAfterSha256 { get; init; } = string.Empty;
    public int SaveLength { get; init; }
    public int HostAfterLength { get; init; }
    public int ChangedByteCount { get; init; }
    public CampaignSaveStructuralChange? StructuralChange { get; init; }
    public IReadOnlyList<CampaignSaveRange> Ranges { get; init; } = [];

    public bool CanApplyRawRanges =>
        HostAfterLength == SaveLength && StructuralChange is null;

    public CampaignSaveProfile Validate()
    {
        if (SchemaVersion != CampaignSaveSync.CurrentSchemaVersion ||
            SaveLength <= 0 || HostAfterLength <= 0 || ChangedByteCount < 0 ||
            !IsSha256(HostBeforeSha256) || !IsSha256(HostAfterSha256) ||
            Ranges.Count > 16_384)
        {
            throw new CampaignSaveSyncException("Campaign save profile is invalid.");
        }

        if (StructuralChange is not null)
        {
            StructuralChange.Validate(SaveLength, HostAfterLength);
            if (CanApplyRawRanges || Ranges.Count != 0)
            {
                throw new CampaignSaveSyncException(
                    "Structural campaign profiles cannot contain raw patch ranges.");
            }
            return this;
        }

        if (HostAfterLength != SaveLength)
        {
            throw new CampaignSaveSyncException(
                "A differing save length requires structural-change metadata.");
        }

        var lastEnd = 0;
        var expectedId = 0;
        foreach (var range in Ranges)
        {
            if (range.Id != expectedId++ || range.Offset < lastEnd ||
                range.Offset < 0 || range.Length <= 0 ||
                range.Offset > SaveLength - range.Length ||
                (range.Approved && string.IsNullOrWhiteSpace(range.Rationale)))
            {
                throw new CampaignSaveSyncException("Campaign save ranges are invalid.");
            }
            lastEnd = checked(range.Offset + range.Length);
        }
        return this;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
}

public sealed record CampaignSaveStructuralChange
{
    public int Offset { get; init; }
    public int BeforeLength { get; init; }
    public int AfterLength { get; init; }
    public int SharedSuffixLength { get; init; }

    internal void Validate(int beforeSaveLength, int afterSaveLength)
    {
        if (Offset < 0 || BeforeLength < 0 || AfterLength < 0 ||
            SharedSuffixLength < 0 ||
            Offset > beforeSaveLength - BeforeLength - SharedSuffixLength ||
            Offset > afterSaveLength - AfterLength - SharedSuffixLength ||
            Offset + BeforeLength + SharedSuffixLength != beforeSaveLength ||
            Offset + AfterLength + SharedSuffixLength != afterSaveLength)
        {
            throw new CampaignSaveSyncException(
                "Structural campaign save change is invalid.");
        }
    }
}

public sealed record CampaignSaveRange
{
    public int Id { get; init; }
    public int Offset { get; init; }
    public string BeforeBase64 { get; init; } = string.Empty;
    public string AfterBase64 { get; init; } = string.Empty;
    public bool Approved { get; init; }
    public string Rationale { get; init; } = string.Empty;

    public int Length => BeforeBytes().Length;

    public byte[] BeforeBytes() => Decode(BeforeBase64, "before");

    public byte[] AfterBytes()
    {
        var bytes = Decode(AfterBase64, "after");
        if (bytes.Length != Length)
        {
            throw new CampaignSaveSyncException("Campaign range before/after lengths differ.");
        }
        return bytes;
    }

    private static byte[] Decode(string value, string name)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length == 0)
            {
                throw new CampaignSaveSyncException(
                    $"Campaign range {name} data is empty.");
            }
            return bytes;
        }
        catch (FormatException exception)
        {
            throw new CampaignSaveSyncException(
                $"Campaign range {name} data is not base64.", exception);
        }
    }
}

public readonly record struct CampaignSyncValidation(
    bool CanApply,
    string Message,
    IReadOnlyList<int> ConflictingRangeIds);

public readonly record struct CampaignSyncApplyResult(
    string BackupPath,
    string AppliedSha256,
    int AppliedRangeCount);

public sealed class CampaignSaveSyncException : Exception
{
    public CampaignSaveSyncException(string message) : base(message)
    {
    }

    public CampaignSaveSyncException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal static class CampaignSaveJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
