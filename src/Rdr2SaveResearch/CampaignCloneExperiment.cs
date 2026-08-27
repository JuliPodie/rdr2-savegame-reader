using System.Security.Cryptography;

namespace Rdr2SaveResearch.Persistence;

/// <summary>
/// Creates an isolated copy of a verified host-after save for loader testing.
/// This is intentionally not a merge: it proves the container survives a
/// copy, while leaving every existing guest save untouched.
/// </summary>
public static class CampaignCloneExperiment
{
    public const string ConfirmationPhrase = "CREATE_TEST_CLONE";

    public static async Task<CampaignCloneResult> CreateAsync(
        string hostBeforePath,
        string hostAfterPath,
        string recipientBeforePath,
        string outputPath,
        string confirmation,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(
                confirmation,
                ConfirmationPhrase,
                StringComparison.Ordinal))
        {
            throw new CampaignSaveSyncException(
                $"Refusing to create a campaign test clone. Supply --confirm {ConfirmationPhrase}.");
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        var inputs = new[]
        {
            Path.GetFullPath(hostBeforePath),
            Path.GetFullPath(hostAfterPath),
            Path.GetFullPath(recipientBeforePath)
        };
        if (inputs.Contains(fullOutputPath, StringComparer.OrdinalIgnoreCase))
        {
            throw new CampaignSaveSyncException(
                "The output must be a new file, not any input save copy.");
        }
        if (File.Exists(fullOutputPath))
        {
            throw new CampaignSaveSyncException(
                "Test-clone output already exists; refusing to overwrite it.");
        }

        var hostBefore = await File.ReadAllBytesAsync(inputs[0], cancellationToken)
            .ConfigureAwait(false);
        var hostAfter = await File.ReadAllBytesAsync(inputs[1], cancellationToken)
            .ConfigureAwait(false);
        var recipientBefore = await File.ReadAllBytesAsync(inputs[2], cancellationToken)
            .ConfigureAwait(false);

        // Decode all three inputs before writing anything. This detects wrong
        // profile files and prevents this command becoming a generic copier.
        var hostBeforeDocument = Rdr2PcSaveCodec.Decode(hostBefore);
        var hostAfterDocument = Rdr2PcSaveCodec.Decode(hostAfter);
        var recipientBeforeDocument = Rdr2PcSaveCodec.Decode(recipientBefore);
        var hostAfterVerification = Rdr2PcSaveCodec.VerifyRoundTrip(hostAfter);
        if (!hostAfterVerification.IsExactRoundTrip)
        {
            throw new CampaignSaveSyncException(
                "The host-after copy failed codec round-trip verification.");
        }
        var diff = Rdr2RsavContentDiffer.Compare(
            hostBeforeDocument,
            hostAfterDocument);
        if (hostBefore.AsSpan().SequenceEqual(hostAfter))
        {
            throw new CampaignSaveSyncException(
                "The host before/after copies are identical; refusing to create a misleading test clone.");
        }

        var parent = Path.GetDirectoryName(fullOutputPath)
            ?? throw new CampaignSaveSyncException("The output path has no parent directory.");
        if (!Directory.Exists(parent))
        {
            throw new CampaignSaveSyncException(
                "The output directory does not exist.");
        }

        await using (var stream = new FileStream(
            fullOutputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.WriteThrough))
        {
            await stream.WriteAsync(hostAfter, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var outputBytes = await File.ReadAllBytesAsync(fullOutputPath, cancellationToken)
            .ConfigureAwait(false);
        if (!outputBytes.AsSpan().SequenceEqual(hostAfter))
        {
            throw new CampaignSaveSyncException(
                "The test-clone verification read did not match the verified host-after source.");
        }

        return new CampaignCloneResult(
            fullOutputPath,
            hostBeforeDocument.Title,
            hostAfterDocument.Title,
            recipientBeforeDocument.Title,
            Convert.ToHexString(SHA256.HashData(hostAfter)),
            diff.Regions.Count(static region => !region.IsByteIdentical),
            diff.Parts.Count(static part => !part.IsByteIdentical),
            hostBefore.AsSpan().SequenceEqual(recipientBefore));
    }
}

public sealed record CampaignCloneResult(
    string OutputPath,
    string HostBeforeTitle,
    string HostAfterTitle,
    string RecipientBeforeTitle,
    string SourceSha256,
    int ChangedRegions,
    int ChangedParts,
    bool RecipientBeforeExactlyMatchesHostBefore);
