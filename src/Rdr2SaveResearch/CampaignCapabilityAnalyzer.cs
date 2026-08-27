using System.Security.Cryptography;
using System.Text.Json;

namespace Rdr2SaveResearch.Persistence;

/// <summary>
/// Aligns schema-backed PSO records across a medal-only serializer control and
/// a genuine completed-mission save. Results are evidence, not automatically
/// writable unlocks: gameplay during the mission also changes private state.
/// </summary>
public static class CampaignCapabilityAnalyzer
{
    public const int CurrentSchemaVersion = 1;
    public const string ApplyConfirmation = "APPLY_CAPABILITY_PROJECTION";

    public static async Task<CampaignCapabilityReport> AnalyzeAsync(
        string hostBeforePath,
        string medalOnlyPath,
        string hostAfterPath,
        IReadOnlyDictionary<uint, string>? catalogAnnotations = null,
        CancellationToken cancellationToken = default)
    {
        var beforeEncrypted = await File.ReadAllBytesAsync(hostBeforePath, cancellationToken)
            .ConfigureAwait(false);
        var medalEncrypted = await File.ReadAllBytesAsync(medalOnlyPath, cancellationToken)
            .ConfigureAwait(false);
        var afterEncrypted = await File.ReadAllBytesAsync(hostAfterPath, cancellationToken)
            .ConfigureAwait(false);
        var before = Rdr2PcSaveCodec.Decode(beforeEncrypted);
        var medal = Rdr2PcSaveCodec.Decode(medalEncrypted);
        var after = Rdr2PcSaveCodec.Decode(afterEncrypted);
        var beforeReport = Rdr2RsavContentAnalyzer.Analyze(before);
        var medalReport = Rdr2RsavContentAnalyzer.Analyze(medal);
        var afterReport = Rdr2RsavContentAnalyzer.Analyze(after);

        RequireMissionState(beforeReport, expected: false, "host-before");
        RequireMissionState(medalReport, expected: true, "medal-only");
        RequireMissionState(afterReport, expected: true, "host-after");

        var candidates = CompareAlignedScalarFields(
            medal.CopyDecodedBytes(), medalReport,
            after.CopyDecodedBytes(), afterReport, catalogAnnotations);
        var numbered = candidates
            .OrderBy(candidate => candidate.RegionIndex)
            .ThenBy(candidate => candidate.StructureHash)
            .ThenBy(candidate => candidate.MedalOnlyRecordIndex)
            .ThenBy(candidate => candidate.FieldOffset)
            .Select((candidate, id) => candidate with { Id = id })
            .ToArray();
        return new CampaignCapabilityReport(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            Sha(beforeEncrypted),
            Sha(medalEncrypted),
            Sha(afterEncrypted),
            "fud1",
            numbered,
            numbered.Count(candidate => candidate.Classification == "enable_candidate"),
            numbered.Count(candidate => candidate.Classification == "monotonic_increase"),
            string.Empty,
            StructuralCandidates: LocateAppendOnlyStructuralCandidates(
                medal.CopyDecodedBytes(), medalReport,
                after.CopyDecodedBytes(), afterReport));
    }

    public static async Task<CampaignCapabilityReport> AnalyzeIsolatedControlAsync(
        string beforePath,
        string afterPath,
        string label,
        IReadOnlyDictionary<uint, string>? catalogAnnotations = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 100)
        {
            throw new CampaignSaveSyncException(
                "Isolated capability control requires a short label.");
        }
        var beforeEncrypted = await File.ReadAllBytesAsync(beforePath, cancellationToken)
            .ConfigureAwait(false);
        var afterEncrypted = await File.ReadAllBytesAsync(afterPath, cancellationToken)
            .ConfigureAwait(false);
        var before = Rdr2PcSaveCodec.Decode(beforeEncrypted);
        var after = Rdr2PcSaveCodec.Decode(afterEncrypted);
        var beforeReport = Rdr2RsavContentAnalyzer.Analyze(before);
        var afterReport = Rdr2RsavContentAnalyzer.Analyze(after);
        var candidates = CompareAlignedScalarFields(
            before.CopyDecodedBytes(), beforeReport,
            after.CopyDecodedBytes(), afterReport, catalogAnnotations)
            .OrderBy(candidate => candidate.RegionIndex)
            .ThenBy(candidate => candidate.StructureHash)
            .ThenBy(candidate => candidate.MedalOnlyRecordIndex)
            .ThenBy(candidate => candidate.FieldOffset)
            .Select((candidate, id) => candidate with { Id = id })
            .ToArray();
        return new CampaignCapabilityReport(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            Sha(beforeEncrypted),
            Sha(beforeEncrypted),
            Sha(afterEncrypted),
            label.Trim(),
            candidates,
            candidates.Count(candidate => candidate.Classification == "enable_candidate"),
            candidates.Count(candidate => candidate.Classification == "monotonic_increase"),
            string.Empty,
            LocateControlDeltas(
                before.CopyDecodedBytes(), beforeReport,
                after.CopyDecodedBytes(), afterReport),
            LocateAppendOnlyStructuralCandidates(
                before.CopyDecodedBytes(), beforeReport,
                after.CopyDecodedBytes(), afterReport));
    }

    public static async Task SaveAsync(
        string outputPath,
        CampaignCapabilityReport report,
        CancellationToken cancellationToken = default)
    {
        var output = Path.GetFullPath(outputPath);
        if (File.Exists(output))
        {
            throw new CampaignSaveSyncException(
                "Capability report output already exists; refusing to overwrite it.");
        }
        await using var stream = new FileStream(
            output, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(
            stream, report,
            new JsonSerializerOptions { WriteIndented = true },
            cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CampaignCapabilityReport> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        var report = await JsonSerializer.DeserializeAsync<CampaignCapabilityReport>(
            stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Validate(report ?? throw new CampaignSaveSyncException(
            "Capability report contains JSON null."));
    }

    public static CampaignCapabilityReport Approve(
        CampaignCapabilityReport report,
        IReadOnlySet<int> ids,
        string rationale)
    {
        report = Validate(report);
        if (ids.Count == 0 || string.IsNullOrWhiteSpace(rationale) || rationale.Length > 500)
        {
            throw new CampaignSaveSyncException(
                "Capability approval requires candidate IDs and a short rationale.");
        }
        var known = report.Candidates.Select(candidate => candidate.Id).ToHashSet();
        if (ids.Any(id => !known.Contains(id)))
        {
            throw new CampaignSaveSyncException("Capability approval contains an unknown ID.");
        }
        var notShareable = report.Candidates.Where(candidate =>
            ids.Contains(candidate.Id) &&
            candidate.Safety != CampaignCapabilitySafety.SharedCapability).ToArray();
        if (notShareable.Length > 0)
        {
            throw new CampaignSaveSyncException(
                "Unknown or private candidates cannot be approved. First classify a verified " +
                "isolated control with classify-capability.");
        }
        return report with
        {
            Candidates = report.Candidates.Select(candidate => candidate with
            {
                ApprovedAsCapability = ids.Contains(candidate.Id)
            }).ToArray(),
            ApprovalRationale = rationale.Trim()
        };
    }

    public static CampaignCapabilityReport ClassifyCandidate(
        CampaignCapabilityReport report,
        int id,
        CampaignRewardKind rewardKind,
        string rationale)
    {
        report = Validate(report);
        if (!CampaignRewardPolicy.IsShareableRewardKind(rewardKind) ||
            string.IsNullOrWhiteSpace(rationale) || rationale.Length > 500)
        {
            throw new CampaignSaveSyncException(
                "A supported reward kind and short isolated-control rationale are required.");
        }
        var candidate = report.Candidates.SingleOrDefault(item => item.Id == id);
        if (candidate is null)
        {
            throw new CampaignSaveSyncException("Capability candidate ID is unknown.");
        }
        if (candidate.Safety == CampaignCapabilitySafety.PrivatePlayerState)
        {
            throw new CampaignSaveSyncException(
                "Private player state can never be promoted to a shared campaign reward.");
        }
        var isSafeSatchelHashReplacement = candidate.Classification ==
            "isolated_hash_replacement" && rewardKind ==
            CampaignRewardKind.SatchelOrTonicUpgrade;
        if (candidate.Classification is not ("enable_candidate" or "monotonic_increase") &&
            !isSafeSatchelHashReplacement)
        {
            throw new CampaignSaveSyncException(
                "Only monotonic/enable fields, or an isolated satchel-upgrade hash replacement, " +
                "can be registered as a campaign reward.");
        }
        return report with
        {
            Candidates = report.Candidates.Select(item => item.Id == id
                ? item with
                {
                    Safety = CampaignCapabilitySafety.SharedCapability,
                    RewardKind = rewardKind,
                    PolicyReason = rationale.Trim()
                }
                : item).ToArray()
        };
    }

    public static async Task<CampaignCapabilityApplyResult> ApplyAsync(
        string guestPath,
        string outputPath,
        CampaignCapabilityReport report,
        string confirmation,
        CancellationToken cancellationToken = default)
    {
        Validate(report);
        if (confirmation != ApplyConfirmation)
        {
            throw new CampaignSaveSyncException(
                $"Capability apply requires --confirm {ApplyConfirmation}.");
        }
        var approved = report.Candidates.Where(candidate => candidate.ApprovedAsCapability)
            .ToArray();
        if (approved.Length == 0 || string.IsNullOrWhiteSpace(report.ApprovalRationale))
        {
            throw new CampaignSaveSyncException("Capability report has no reviewed approvals.");
        }
        var output = Path.GetFullPath(outputPath);
        if (File.Exists(output) || string.Equals(
                output, Path.GetFullPath(guestPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new CampaignSaveSyncException(
                "Capability output must be a new file and cannot overwrite the guest save.");
        }
        var encrypted = await File.ReadAllBytesAsync(guestPath, cancellationToken)
            .ConfigureAwait(false);
        var document = Rdr2PcSaveCodec.Decode(encrypted);
        var decoded = document.CopyDecodedBytes();
        var analyzed = Rdr2RsavContentAnalyzer.Analyze(document);
        foreach (var candidate in approved)
        {
            ApplyCandidate(decoded, analyzed, candidate);
        }
        var rebuilt = Rdr2PcSaveCodec.Encode(new Rdr2PcSaveDocument(decoded, document.Title));
        var verification = Rdr2PcSaveCodec.VerifyRoundTrip(rebuilt);
        if (!verification.IsExactRoundTrip)
        {
            throw new CampaignSaveSyncException("Capability output failed codec verification.");
        }
        await using var stream = new FileStream(
            output, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(rebuilt, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new CampaignCapabilityApplyResult(
            output, approved.Length, Sha(rebuilt), verification.CheckCount);
    }

    private static void ApplyCandidate(
        Span<byte> decoded,
        Rdr2RsavContentReport report,
        CampaignCapabilityCandidate candidate)
    {
        if (candidate.Safety != CampaignCapabilitySafety.SharedCapability ||
            !CampaignRewardPolicy.IsShareableRewardKind(candidate.RewardKind))
        {
            throw new CampaignSaveSyncException(
                $"Candidate {candidate.Id} is not an approved shared campaign capability.");
        }
        var frame = report.PsoFrames.Single(frame => frame.RegionIndex == candidate.RegionIndex);
        var blockMatches = frame.Blocks!.Where(block =>
            block.NameHash == candidate.StructureHash && block.AbsoluteOffset is not null).ToArray();
        var schema = frame.Structures!.SingleOrDefault(structure =>
            structure.NameHash == candidate.StructureHash);
        if (blockMatches.Length != 1 || schema is null ||
            schema.Length != candidate.StructureLength)
        {
            throw new CampaignSaveSyncException(
                $"Guest schema does not uniquely match candidate {candidate.Id}.");
        }
        var field = schema.Fields.SingleOrDefault(item => item.NameHash == candidate.FieldHash);
        var value = Convert.FromHexString(candidate.CompletedValueHex);
        if (field.NameHash != candidate.FieldHash || field.DataType != candidate.DataType ||
            field.DataOffset != candidate.FieldOffset || ScalarLength(field.DataType) != value.Length)
        {
            throw new CampaignSaveSyncException(
                $"Guest field schema conflicts with candidate {candidate.Id}.");
        }
        var block = blockMatches[0];
        var recordIndex = LocateTargetRecord(decoded, block, schema, candidate);
        var absoluteOffset = block.AbsoluteOffset!.Value +
            recordIndex * schema.Length + field.DataOffset;
        var current = decoded.Slice(absoluteOffset, value.Length);
        var baseline = Convert.FromHexString(candidate.MedalOnlyValueHex);
        if (!current.SequenceEqual(baseline) && !current.SequenceEqual(value))
        {
            throw new CampaignSaveSyncException(
                $"Guest value conflicts with candidate {candidate.Id}; no output was written.");
        }
        value.CopyTo(current);
    }

    private static int LocateTargetRecord(
        ReadOnlySpan<byte> decoded,
        Rdr2PsoBlock block,
        Rdr2PsoStructure schema,
        CampaignCapabilityCandidate candidate)
    {
        if (candidate.Alignment == "singleton" && block.Length == schema.Length)
        {
            return 0;
        }
        if (candidate.Alignment == "record_header:8")
        {
            var header = Convert.FromHexString(candidate.RecordIdentityHex);
            if (header.Length != 8)
            {
                throw new CampaignSaveSyncException(
                    $"Candidate {candidate.Id} has an invalid record-header identity.");
            }
            var headerMatches = new List<int>();
            for (var index = 0; index < block.Length / schema.Length; index++)
            {
                var offset = block.AbsoluteOffset!.Value + index * schema.Length;
                if (decoded.Slice(offset, header.Length).SequenceEqual(header))
                {
                    headerMatches.Add(index);
                }
            }
            return headerMatches.Count == 1
                ? headerMatches[0]
                : throw new CampaignSaveSyncException(
                    $"Guest record-header identity for candidate {candidate.Id} is not unique.");
        }
        if (!candidate.Alignment.StartsWith("identity:", StringComparison.Ordinal) ||
            !uint.TryParse(candidate.Alignment.AsSpan("identity:".Length), out var identityHash))
        {
            throw new CampaignSaveSyncException(
                $"Candidate {candidate.Id} has no portable record identity.");
        }
        var identityField = schema.Fields.Single(field => field.NameHash == identityHash);
        var identity = Convert.FromHexString(candidate.RecordIdentityHex);
        var matches = new List<int>();
        for (var index = 0; index < block.Length / schema.Length; index++)
        {
            var offset = block.AbsoluteOffset!.Value + index * schema.Length +
                identityField.DataOffset;
            if (decoded.Slice(offset, identity.Length).SequenceEqual(identity))
            {
                matches.Add(index);
            }
        }
        return matches.Count == 1
            ? matches[0]
            : throw new CampaignSaveSyncException(
                $"Guest record identity for candidate {candidate.Id} is not unique.");
    }

    private static CampaignCapabilityReport Validate(CampaignCapabilityReport report)
    {
        if (report.SchemaVersion != CurrentSchemaVersion ||
            report.Candidates.Select(candidate => candidate.Id).Distinct().Count() !=
            report.Candidates.Count)
        {
            throw new CampaignSaveSyncException("Capability report schema or IDs are invalid.");
        }
        return report with
        {
            Candidates = report.Candidates.Select(candidate =>
            {
                if (candidate.Safety != CampaignCapabilitySafety.Unknown)
                {
                    return candidate;
                }
                var inferred = CampaignRewardPolicy.Infer(candidate.SemanticLabel);
                return inferred.Safety == CampaignCapabilitySafety.Unknown
                    ? candidate
                    : candidate with
                    {
                        Safety = inferred.Safety,
                        RewardKind = inferred.RewardKind,
                        PolicyReason = inferred.Reason
                    };
            }).ToArray()
        };
    }

    private static List<CampaignCapabilityCandidate> CompareAlignedScalarFields(
        byte[] before,
        Rdr2RsavContentReport beforeReport,
        byte[] after,
        Rdr2RsavContentReport afterReport,
        IReadOnlyDictionary<uint, string>? catalogAnnotations)
    {
        var result = new List<CampaignCapabilityCandidate>();
        foreach (var beforeFrame in beforeReport.PsoFrames.Where(frame =>
                     frame.Blocks is not null && frame.Structures is not null))
        {
            // A frame is identified by its checksum region.  Treat malformed
            // or ambiguously parsed reports as non-candidates instead of
            // letting an exploratory control analysis throw (or guessing
            // which frame is safe to compare).
            var matchingFrames = afterReport.PsoFrames
                .Where(frame => frame.RegionIndex == beforeFrame.RegionIndex)
                .Take(2)
                .ToArray();
            if (matchingFrames.Length != 1)
            {
                continue;
            }
            var afterFrame = matchingFrames[0];
            if (afterFrame?.Blocks is null || afterFrame.Structures is null)
            {
                continue;
            }
            var beforeBlocks = UniqueByHash(beforeFrame.Blocks!);
            var afterBlocks = UniqueByHash(afterFrame.Blocks);
            var beforeSchemas = UniqueStructures(beforeFrame.Structures!);
            var afterSchemas = UniqueStructures(afterFrame.Structures);
            foreach (var (hash, beforeBlock) in beforeBlocks)
            {
                if (!afterBlocks.TryGetValue(hash, out var afterBlock) ||
                    !beforeSchemas.TryGetValue(hash, out var beforeSchema) ||
                    !afterSchemas.TryGetValue(hash, out var afterSchema) ||
                    beforeBlock.AbsoluteOffset is not { } beforeOffset ||
                    afterBlock.AbsoluteOffset is not { } afterOffset ||
                    beforeSchema.Length <= 0 || beforeSchema.Length != afterSchema.Length ||
                    beforeBlock.Length % beforeSchema.Length != 0 ||
                    afterBlock.Length % afterSchema.Length != 0 ||
                    !SchemaMatches(beforeSchema, afterSchema))
                {
                    continue;
                }
                var alignments = AlignRecords(
                    before.AsSpan(beforeOffset, beforeBlock.Length),
                    after.AsSpan(afterOffset, afterBlock.Length),
                    beforeSchema);
                foreach (var alignment in alignments)
                {
                    foreach (var field in beforeSchema.Fields)
                    {
                        var length = ScalarLength(field.DataType);
                        if (length == 0 || field.DataOffset < 0 ||
                            field.DataOffset > beforeSchema.Length - length)
                        {
                            continue;
                        }
                        var left = before.AsSpan(
                            beforeOffset + alignment.BeforeIndex * beforeSchema.Length + field.DataOffset,
                            length);
                        var right = after.AsSpan(
                            afterOffset + alignment.AfterIndex * afterSchema.Length + field.DataOffset,
                            length);
                        if (left.SequenceEqual(right))
                        {
                            continue;
                        }
                        var oldValue = ReadUnsignedBigEndian(left);
                        var newValue = ReadUnsignedBigEndian(right);
                        var semanticLabel = ClassifyCapabilityField(
                            before,
                            beforeOffset,
                            beforeSchema,
                            alignment,
                            field);
                        if (semanticLabel is null && field.DataType == 0x1F &&
                            catalogAnnotations is not null &&
                            catalogAnnotations.TryGetValue((uint)newValue, out var catalogLabel))
                        {
                            semanticLabel = catalogLabel;
                        }
                        if (semanticLabel is null && field.DataType == 0x1F &&
                            KnownSatchelUpgradeHashes.TryGetValue((uint)newValue,
                                out var satchelLabel))
                        {
                            // The supplied editor's protected resource table
                            // names FC72CDB2 as the Tonics Satchel. The clean
                            // control independently proves that this exact
                            // record transition is its capability marker.
                            semanticLabel = satchelLabel;
                        }
                        var classification = field.DataType == 0x1F
                            ? "isolated_hash_replacement"
                            : oldValue == 0 && newValue == 1 &&
                            field.DataType is 0x00 or 0x01 or 0x02
                                ? "enable_candidate"
                                : newValue > oldValue && length <= 4
                                    ? "monotonic_increase"
                                    : "changed_scalar";
                        var policy = CampaignRewardPolicy.Infer(semanticLabel);
                        result.Add(new CampaignCapabilityCandidate(
                            beforeFrame.RegionIndex,
                            hash,
                            beforeSchema.Length,
                            alignment.BeforeIndex,
                            alignment.AfterIndex,
                            alignment.IdentityHex,
                            alignment.Kind,
                            field.NameHash,
                            semanticLabel,
                            field.DataType,
                            field.DataOffset,
                            Convert.ToHexString(left),
                            Convert.ToHexString(right),
                            classification,
                            false)
                        {
                            Safety = policy.Safety,
                            RewardKind = policy.RewardKind,
                            PolicyReason = policy.Reason
                        });
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Finds a very narrow, non-writable form of structural control delta:
    /// a mapped PSO block growing by exactly one whole schema record. This is
    /// useful evidence for unlocks represented as array entries (such as
    /// recipes), but the game/editor may rewrite the existing record headers,
    /// so the observation is intentionally labelled as unverified structural
    /// growth rather than a proven insertion. It does not provide a field
    /// path or a write operation. Inserting a record also requires updating
    /// owning array metadata, which must be separately proven.
    /// </summary>
    private static IReadOnlyList<CampaignCapabilityStructuralCandidate>
        LocateAppendOnlyStructuralCandidates(
            byte[] before,
            Rdr2RsavContentReport beforeReport,
            byte[] after,
            Rdr2RsavContentReport afterReport)
    {
        var result = new List<CampaignCapabilityStructuralCandidate>();
        foreach (var beforeFrame in beforeReport.PsoFrames.Where(static frame =>
                     frame.Blocks is not null && frame.Structures is not null))
        {
            var matchingFrames = afterReport.PsoFrames
                .Where(frame => frame.RegionIndex == beforeFrame.RegionIndex &&
                    frame.Blocks is not null && frame.Structures is not null)
                .Take(2)
                .ToArray();
            if (matchingFrames.Length != 1)
            {
                continue;
            }

            var afterFrame = matchingFrames[0];
            foreach (var beforeBlock in beforeFrame.Blocks!)
            {
                var afterBlockMatches = afterFrame.Blocks!
                    .Where(block => block.Index == beforeBlock.Index &&
                        block.NameHash == beforeBlock.NameHash)
                    .Take(2)
                    .ToArray();
                if (afterBlockMatches.Length != 1 ||
                    beforeBlock.AbsoluteOffset is not { } beforeOffset ||
                    afterBlockMatches[0].AbsoluteOffset is not { } afterOffset)
                {
                    continue;
                }

                var afterBlock = afterBlockMatches[0];
                var schemaMatches = beforeFrame.Structures!
                    .Where(schema => schema.NameHash == beforeBlock.NameHash)
                    .Take(2)
                    .ToArray();
                var afterSchemaMatches = afterFrame.Structures!
                    .Where(schema => schema.NameHash == beforeBlock.NameHash)
                    .Take(2)
                    .ToArray();
                if (schemaMatches.Length != 1 || afterSchemaMatches.Length != 1 ||
                    !SchemaMatches(schemaMatches[0], afterSchemaMatches[0]) ||
                    schemaMatches[0].Length <= 0 ||
                    afterBlock.Length != beforeBlock.Length + schemaMatches[0].Length ||
                    beforeBlock.Length % schemaMatches[0].Length != 0)
                {
                    continue;
                }

                var schema = schemaMatches[0];
                var appended = after.AsSpan(
                    afterOffset + beforeBlock.Length, schema.Length);
                result.Add(new CampaignCapabilityStructuralCandidate(
                    beforeFrame.RegionIndex,
                    beforeBlock.Index,
                    beforeBlock.NameHash,
                    schema.Length,
                    beforeBlock.Length / schema.Length,
                    afterBlock.Length / schema.Length - 1,
                    Convert.ToHexString(appended[..Math.Min(8, appended.Length)]),
                    Convert.ToHexString(appended),
                    "single_record_pso_growth_unverified",
                    false,
                    "A mapped block grew by exactly one whole schema record. Existing " +
                    "record headers were rewritten, so this does not prove insertion order. " +
                    "This is evidence only; no array metadata or save is written.",
                    LocateArrayCountEvidence(
                        before, beforeReport, after, afterReport,
                        beforeFrame.RegionIndex, beforeBlock, afterBlock,
                        schema.Length),
                    BuildStructuralLayoutEvidence(
                        beforeReport, afterReport, beforeFrame, afterFrame,
                        beforeBlock, afterBlock, schema.Length)));
            }
        }
        return result;
    }

    private static CampaignCapabilityStructuralLayoutEvidence?
        BuildStructuralLayoutEvidence(
            Rdr2RsavContentReport beforeReport,
            Rdr2RsavContentReport afterReport,
            Rdr2PsoFrame beforeFrame,
            Rdr2PsoFrame afterFrame,
            Rdr2PsoBlock beforeBlock,
            Rdr2PsoBlock afterBlock,
            int growth)
    {
        var beforeSection = beforeReport.Sections.SingleOrDefault(section =>
            section.RegionIndex == beforeFrame.RegionIndex && section.HasExpectedFrame);
        var afterSection = afterReport.Sections.SingleOrDefault(section =>
            section.RegionIndex == afterFrame.RegionIndex && section.HasExpectedFrame);
        var beforeRegions = beforeReport.Regions.Where(region =>
            region.Index == beforeFrame.RegionIndex).Take(2).ToArray();
        var afterRegions = afterReport.Regions.Where(region =>
            region.Index == afterFrame.RegionIndex).Take(2).ToArray();
        var beforeBlocks = beforeFrame.Blocks;
        var afterBlocks = afterFrame.Blocks;
        if (beforeSection is null || afterSection is null ||
            beforeRegions.Length != 1 || afterRegions.Length != 1 ||
            beforeBlocks is null || afterBlocks is null)
        {
            return null;
        }
        var beforeRegion = beforeRegions[0];
        var afterRegion = afterRegions[0];
        var beforePsin = beforeSection.Parts.SingleOrDefault(static part => part.Marker == "PSIN");
        var afterPsin = afterSection.Parts.SingleOrDefault(static part => part.Marker == "PSIN");
        var matchingOffsets = beforeBlocks.Select(beforeMapped => new
        {
            Before = beforeMapped,
            Matches = afterBlocks.Where(afterMapped =>
                afterMapped.Index == beforeMapped.Index &&
                afterMapped.NameHash == beforeMapped.NameHash).Take(2).ToArray()
        }).Where(pair => pair.Matches.Length == 1)
            .Select(pair => new { pair.Before, After = pair.Matches[0] })
            .ToArray();
        var shiftedOffsetsMatch = matchingOffsets.All(pair =>
        {
            var expectedOffset = pair.Before.DataOffset >= beforeBlock.DataOffset + beforeBlock.Length
                ? pair.Before.DataOffset + growth
                : pair.Before.DataOffset;
            return pair.After.DataOffset == expectedOffset;
        });
        var psinGrowthMatches = beforePsin.Length + growth == afterPsin.Length;
        var regionGrowthMatches = beforeRegion.DataLength + growth == afterRegion.DataLength;
        var pmapLayoutMatches = matchingOffsets.Length == beforeBlocks.Count && shiftedOffsetsMatch;
        var evidence = psinGrowthMatches && regionGrowthMatches && pmapLayoutMatches
            ? "PMAP block length and shifted allocation offsets are structurally proven. " +
              "No schema-owned collection descriptor or count/capacity field was resolved."
            : "The target block growth is observed, but the editor globally normalized the " +
              "PSIN/PMAP layout. This control is not a portable structural-write template.";
        return new CampaignCapabilityStructuralLayoutEvidence(
            beforePsin.Length,
            afterPsin.Length,
            beforeRegion.DataLength,
            afterRegion.DataLength,
            beforeBlock.DataOffset,
            afterBlock.DataOffset,
            beforeBlock.Length,
            afterBlock.Length,
            psinGrowthMatches,
            regionGrowthMatches,
            pmapLayoutMatches,
            evidence);
    }

    private static IReadOnlyList<CampaignCapabilityArrayCountEvidence>
        LocateArrayCountEvidence(
            byte[] before,
            Rdr2RsavContentReport beforeReport,
            byte[] after,
            Rdr2RsavContentReport afterReport,
            int regionIndex,
            Rdr2PsoBlock beforeBlock,
            Rdr2PsoBlock afterBlock,
            int recordLength)
    {
        var beforeSection = beforeReport.Sections.SingleOrDefault(section =>
            section.RegionIndex == regionIndex && section.HasExpectedFrame);
        var afterSection = afterReport.Sections.SingleOrDefault(section =>
            section.RegionIndex == regionIndex && section.HasExpectedFrame);
        if (beforeSection is null || afterSection is null)
        {
            return [];
        }
        var beforePart = beforeSection.Parts.SingleOrDefault(static part => part.Marker == "PSIN");
        var afterPart = afterSection.Parts.SingleOrDefault(static part => part.Marker == "PSIN");
        if (beforePart.Length < 8 || afterPart.Length != beforePart.Length + recordLength ||
            beforeBlock.AbsoluteOffset is not { } beforeBlockOffset ||
            afterBlock.AbsoluteOffset is not { } afterBlockOffset)
        {
            return [];
        }

        var beforeCount = beforeBlock.Length / recordLength;
        var afterCount = afterBlock.Length / recordLength;
        if (afterCount != beforeCount + 1)
        {
            return [];
        }

        var result = new List<CampaignCapabilityArrayCountEvidence>();
        var beforeStart = beforePart.Offset + 8;
        var beforeEnd = beforePart.Offset + beforePart.Length;
        var logicalGrowthOffset = beforeBlockOffset + beforeBlock.Length;
        for (var beforeOffset = beforeStart; beforeOffset <= beforeEnd - 4; beforeOffset++)
        {
            // The target record data cannot also be a trustworthy owner
            // descriptor. Excluding it prevents coincidental count values in
            // the new capability record from being reported as metadata.
            if (beforeOffset >= beforeBlockOffset &&
                beforeOffset < beforeBlockOffset + beforeBlock.Length)
            {
                continue;
            }
            var afterOffset = beforeOffset < logicalGrowthOffset
                ? beforeOffset
                : beforeOffset + recordLength;
            if (afterOffset > afterPart.Offset + afterPart.Length - 4)
            {
                continue;
            }
            AddCountEvidenceIfMatch(
                result, before, after, beforeOffset, afterOffset,
                beforeCount, afterCount, bigEndian: true);
            AddCountEvidenceIfMatch(
                result, before, after, beforeOffset, afterOffset,
                beforeCount, afterCount, bigEndian: false);
            AddScalarCountEvidenceIfMatch(
                result, before, after, beforeOffset, afterOffset,
                beforeCount, afterCount, bigEndian: true);
            AddScalarCountEvidenceIfMatch(
                result, before, after, beforeOffset, afterOffset,
                beforeCount, afterCount, bigEndian: false);
        }
        return result;
    }

    private static void AddCountEvidenceIfMatch(
        ICollection<CampaignCapabilityArrayCountEvidence> result,
        ReadOnlySpan<byte> before,
        ReadOnlySpan<byte> after,
        int beforeOffset,
        int afterOffset,
        int beforeCount,
        int afterCount,
        bool bigEndian)
    {
        var firstBefore = bigEndian
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(before.Slice(beforeOffset, 2))
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(before.Slice(beforeOffset, 2));
        var secondBefore = bigEndian
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(before.Slice(beforeOffset + 2, 2))
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(before.Slice(beforeOffset + 2, 2));
        var firstAfter = bigEndian
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(after.Slice(afterOffset, 2))
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(after.Slice(afterOffset, 2));
        var secondAfter = bigEndian
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(after.Slice(afterOffset + 2, 2))
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(after.Slice(afterOffset + 2, 2));
        if (firstBefore != beforeCount || secondBefore != beforeCount ||
            firstAfter != afterCount || secondAfter != afterCount)
        {
            return;
        }
        result.Add(new CampaignCapabilityArrayCountEvidence(
            beforeOffset,
            afterOffset,
            bigEndian ? "big_endian_uint16_pair" : "little_endian_uint16_pair",
            beforeCount,
            afterCount,
            Convert.ToHexString(before.Slice(beforeOffset, 4)),
            Convert.ToHexString(after.Slice(afterOffset, 4)),
            "Candidate count/capacity pair only. It is not a verified owner descriptor " +
            "and cannot be written."));
    }

    private static void AddScalarCountEvidenceIfMatch(
        ICollection<CampaignCapabilityArrayCountEvidence> result,
        ReadOnlySpan<byte> before,
        ReadOnlySpan<byte> after,
        int beforeOffset,
        int afterOffset,
        int beforeCount,
        int afterCount,
        bool bigEndian)
    {
        var beforeValue = bigEndian
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(before.Slice(beforeOffset, 4))
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(before.Slice(beforeOffset, 4));
        var afterValue = bigEndian
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(after.Slice(afterOffset, 4))
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(after.Slice(afterOffset, 4));
        if (beforeValue != beforeCount || afterValue != afterCount)
        {
            return;
        }
        result.Add(new CampaignCapabilityArrayCountEvidence(
            beforeOffset,
            afterOffset,
            bigEndian ? "big_endian_uint32" : "little_endian_uint32",
            beforeCount,
            afterCount,
            Convert.ToHexString(before.Slice(beforeOffset, 4)),
            Convert.ToHexString(after.Slice(afterOffset, 4)),
            "Candidate scalar count only. It is not a verified owner descriptor and " +
            "cannot be written."));
    }

    /// <summary>
    /// An editor control can touch data nested below a PSO structure field.
    /// Those bytes are still valuable evidence even when the current schema
    /// walker cannot give them a portable field path. Keep them in a separate,
    /// deliberately non-applicable collection instead of silently returning an
    /// empty report or pretending that a raw offset is safe to merge.
    /// </summary>
    private static IReadOnlyList<CampaignCapabilityControlDelta> LocateControlDeltas(
        byte[] before,
        Rdr2RsavContentReport beforeReport,
        byte[] after,
        Rdr2RsavContentReport afterReport)
    {
        var result = new List<CampaignCapabilityControlDelta>();
        foreach (var beforeSection in beforeReport.Sections.Where(static section =>
                     section.HasExpectedFrame))
        {
            var afterSection = afterReport.Sections.SingleOrDefault(section =>
                section.RegionIndex == beforeSection.RegionIndex && section.HasExpectedFrame);
            var beforePart = beforeSection.Parts.SingleOrDefault(static part =>
                part.Marker == "PSIN");
            var afterPart = afterSection?.Parts.SingleOrDefault(static part =>
                part.Marker == "PSIN");
            if (beforePart.Length < 8 || afterPart is not { Length: >= 8 } ||
                beforePart.Length != afterPart.Value.Length)
            {
                continue;
            }

            var beforePayload = before.AsSpan(beforePart.Offset + 8, beforePart.Length - 8);
            var afterPayload = after.AsSpan(afterPart.Value.Offset + 8, afterPart.Value.Length - 8);
            var frame = beforeReport.PsoFrames.SingleOrDefault(candidate =>
                candidate.RegionIndex == beforeSection.RegionIndex);
            var cursor = 0;
            while (cursor < beforePayload.Length)
            {
                if (beforePayload[cursor] == afterPayload[cursor])
                {
                    cursor++;
                    continue;
                }
                var start = cursor++;
                while (cursor < beforePayload.Length &&
                       beforePayload[cursor] != afterPayload[cursor])
                {
                    cursor++;
                }
                var length = cursor - start;
                var absoluteOffset = beforePart.Offset + 8 + start;
                var block = frame?.Blocks?
                    .Where(candidate => candidate.AbsoluteOffset is { } offset &&
                        absoluteOffset >= offset &&
                        absoluteOffset < offset + candidate.Length)
                    .OrderBy(candidate => candidate.Length)
                    .FirstOrDefault();
                var blockRelativeOffset = block?.AbsoluteOffset is { } blockOffset
                    ? absoluteOffset - blockOffset
                    : (int?)null;
                var oldBytes = beforePayload.Slice(start, length);
                var newBytes = afterPayload.Slice(start, length);
                var contextStart = Math.Max(0, start - 8);
                var contextEnd = Math.Min(beforePayload.Length, cursor + 8);
                var fixedPoint = FindContainingBigEndianUInt32(
                    beforePayload, afterPayload, start, length);
                result.Add(new CampaignCapabilityControlDelta(
                    beforeSection.RegionIndex,
                    start,
                    absoluteOffset,
                    block?.Index,
                    block?.NameHash,
                    blockRelativeOffset,
                    Convert.ToHexString(oldBytes),
                    Convert.ToHexString(newBytes),
                    DescribeNumericValues(oldBytes),
                    DescribeNumericValues(newBytes),
                    "unresolved_control_delta",
                    false,
                    contextStart,
                    Convert.ToHexString(beforePayload.Slice(
                        contextStart, contextEnd - contextStart)),
                    Convert.ToHexString(afterPayload.Slice(
                        contextStart, contextEnd - contextStart)),
                    fixedPoint?.Before,
                    fixedPoint?.After));
            }
        }
        var duplicateFixedPointValues = result
            .Where(static delta => delta.ContainingBigEndianUInt32Before is not null &&
                delta.ContainingBigEndianUInt32After is not null)
            .GroupBy(static delta => (
                delta.ContainingBigEndianUInt32Before,
                delta.ContainingBigEndianUInt32After))
            .Where(static group => group.Count() >= 2)
            .Select(static group => group.Key)
            .ToHashSet();
        return result.Select(delta => duplicateFixedPointValues.Contains((
                delta.ContainingBigEndianUInt32Before,
                delta.ContainingBigEndianUInt32After))
            ? delta with
            {
                Classification = "protected_private_money_copy",
                InferredRole = "money",
                Inference = "duplicate plausible big-endian uint32 fixed-point value"
            }
            : delta).ToArray();
    }

    private static (uint Before, uint After)? FindContainingBigEndianUInt32(
        ReadOnlySpan<byte> before,
        ReadOnlySpan<byte> after,
        int changedOffset,
        int changedLength)
    {
        if (changedLength > 4)
        {
            return null;
        }
        var matches = new List<(uint Before, uint After)>();
        for (var start = Math.Max(0, changedOffset + changedLength - 4);
             start <= changedOffset && start <= before.Length - 4;
             start++)
        {
            var oldValue = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                before.Slice(start, 4));
            var newValue = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                after.Slice(start, 4));
            // RDR2 stores cash in cents as a non-negative 32-bit value. Keep
            // this deliberately broad; it is called money only when the same
            // plausible value transition is duplicated elsewhere in the save.
            if (oldValue <= 100_000_000 && newValue <= 100_000_000 && oldValue != newValue)
            {
                matches.Add((oldValue, newValue));
            }
        }
        return matches.Count == 0
            ? null
            : matches.OrderBy(static value => Math.Max(value.Before, value.After)).First();
    }

    private static IReadOnlyList<string> DescribeNumericValues(ReadOnlySpan<byte> value)
    {
        var result = new List<string>();
        if (value.Length <= 8)
        {
            result.Add($"be-u{value.Length * 8}:{ReadUnsignedBigEndian(value)}");
            ulong little = 0;
            for (var index = value.Length - 1; index >= 0; index--)
            {
                little = (little << 8) | value[index];
            }
            result.Add($"le-u{value.Length * 8}:{little}");
        }
        return result;
    }

    private static IReadOnlyList<RecordAlignment> AlignRecords(
        ReadOnlySpan<byte> before,
        ReadOnlySpan<byte> after,
        Rdr2PsoStructure schema)
    {
        var beforeCount = before.Length / schema.Length;
        var afterCount = after.Length / schema.Length;
        if (beforeCount == 1 && afterCount == 1)
        {
            return [new RecordAlignment(0, 0, string.Empty, "singleton")];
        }

        IdentityAlignment? best = null;
        foreach (var field in schema.Fields)
        {
            var length = IdentityLength(field.DataType);
            if (length is not (4 or 8) || field.DataOffset < 0 ||
                field.DataOffset > schema.Length - length)
            {
                continue;
            }
            var beforeKeys = BuildUniqueKeys(before, schema.Length, field.DataOffset, length);
            var afterKeys = BuildUniqueKeys(after, schema.Length, field.DataOffset, length);
            if (beforeKeys.Count != beforeCount || afterKeys.Count != afterCount)
            {
                continue;
            }
            var common = beforeKeys.Keys.Intersect(afterKeys.Keys, StringComparer.Ordinal).ToArray();
            var required = Math.Max(2, (int)Math.Ceiling(Math.Min(beforeCount, afterCount) * 0.70));
            if (common.Length < required)
            {
                continue;
            }
            var candidate = new IdentityAlignment(field, beforeKeys, afterKeys, common);
            if (best is null || candidate.Common.Length > best.Common.Length ||
                candidate.Common.Length == best.Common.Length && field.DataOffset == 0)
            {
                best = candidate;
            }
        }
        if (best is not null)
        {
            return best.Common
                .Select(key => new RecordAlignment(
                    best.Before[key], best.After[key], key,
                    $"identity:{best.Field.NameHash}"))
                .OrderBy(alignment => alignment.BeforeIndex)
                .ToArray();
        }

        // Some PSO record blocks carry their unique object key in the first
        // eight bytes before the schema's first declared member. The clean
        // Improved Tonics Satchel control is one of them. This header is
        // portable only when it is unique in both copies, so it gets a
        // distinct alignment mode and is revalidated before any write.
        if (schema.Length < 8)
        {
            return Array.Empty<RecordAlignment>();
        }
        var beforeHeaders = BuildUniqueKeys(before, schema.Length, 0, 8);
        var afterHeaders = BuildUniqueKeys(after, schema.Length, 0, 8);
        if (beforeHeaders.Count != beforeCount || afterHeaders.Count != afterCount)
        {
            return Array.Empty<RecordAlignment>();
        }
        var commonHeaders = beforeHeaders.Keys
            .Intersect(afterHeaders.Keys, StringComparer.Ordinal)
            .ToArray();
        var requiredHeaders = Math.Max(2, (int)Math.Ceiling(
            Math.Min(beforeCount, afterCount) * 0.70));
        return commonHeaders.Length < requiredHeaders
            ? Array.Empty<RecordAlignment>()
            : commonHeaders.Select(key => new RecordAlignment(
                    beforeHeaders[key], afterHeaders[key], key, "record_header:8"))
                .OrderBy(alignment => alignment.BeforeIndex)
                .ToArray();
    }

    private static string? ClassifyCapabilityField(
        ReadOnlySpan<byte> bytes,
        int blockOffset,
        Rdr2PsoStructure schema,
        RecordAlignment alignment,
        Rdr2PsoField field)
    {
        if (field.SemanticLabel is not null)
        {
            return field.SemanticLabel;
        }
        if (!alignment.Kind.StartsWith("identity:", StringComparison.Ordinal) ||
            !uint.TryParse(alignment.Kind.AsSpan("identity:".Length), out var identityHash))
        {
            return null;
        }
        // PSO schemas can contain repeated field hashes.  A repeated identity
        // hash is not a portable field path, so keep it unlabelled rather than
        // selecting an arbitrary occurrence.
        var matchingIdentityFields = schema.Fields
            .Where(candidate => candidate.NameHash == identityHash)
            .Take(2)
            .ToArray();
        if (matchingIdentityFields.Length != 1)
        {
            return null;
        }
        var identityField = matchingIdentityFields[0];
        var length = IdentityLength(identityField.DataType);
        if (length != 4 || identityField.DataOffset < 0 ||
            identityField.DataOffset > schema.Length - length)
        {
            return null;
        }
        var identity = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(
            blockOffset + alignment.BeforeIndex * schema.Length + identityField.DataOffset,
            length));
        return KnownWeaponHashes.Contains(identity)
            ? "weapon_purchase_eligibility"
            : null;
    }

    private static Dictionary<string, int> BuildUniqueKeys(
        ReadOnlySpan<byte> block,
        int recordLength,
        int fieldOffset,
        int fieldLength)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < block.Length / recordLength; index++)
        {
            var key = Convert.ToHexString(block.Slice(
                index * recordLength + fieldOffset, fieldLength));
            if (!result.TryAdd(key, index))
            {
                duplicates.Add(key);
            }
        }
        foreach (var duplicate in duplicates)
        {
            result.Remove(duplicate);
        }
        return result;
    }

    private static Dictionary<uint, Rdr2PsoBlock> UniqueByHash(
        IReadOnlyList<Rdr2PsoBlock> blocks) => blocks
        .GroupBy(block => block.NameHash)
        .Where(group => group.Count() == 1)
        .ToDictionary(group => group.Key, group => group.Single());

    private static Dictionary<uint, Rdr2PsoStructure> UniqueStructures(
        IReadOnlyList<Rdr2PsoStructure> structures) => structures
        .GroupBy(structure => structure.NameHash)
        .Where(group => group.Count() == 1)
        .ToDictionary(group => group.Key, group => group.Single());

    private static bool SchemaMatches(Rdr2PsoStructure left, Rdr2PsoStructure right) =>
        left.Fields.Count == right.Fields.Count &&
        left.Fields.Zip(right.Fields).All(pair =>
            pair.First.NameHash == pair.Second.NameHash &&
            pair.First.DataType == pair.Second.DataType &&
            pair.First.Subtype == pair.Second.Subtype &&
            pair.First.DataOffset == pair.Second.DataOffset &&
            pair.First.ReferenceKey == pair.Second.ReferenceKey);

    private static int ScalarLength(byte type) => type switch
    {
        0x00 or 0x01 or 0x02 => 1,
        0x03 or 0x04 or 0x1E => 2,
        // 0x1F is an inline PSO hash. The clean Improved Tonics Satchel
        // control changes one such value in a keyed record; it is not a
        // pointer and can therefore be compared and applied like the other
        // four-byte scalar encodings once separately classified.
        0x05 or 0x06 or 0x07 or 0x09 or 0x0E or 0x0F or 0x1F => 4,
        0x08 => 8,
        _ => 0
    };

    // In the flat record arrays seen in RDR2 Story Mode saves, a type 0x0B
    // member is used as the four-byte hashed key. It is not copied as a
    // writable scalar, but is safe and necessary as a record identity.
    private static int IdentityLength(byte type) => type == 0x0B ? 4 : ScalarLength(type);

    private static ulong ReadUnsignedBigEndian(ReadOnlySpan<byte> value)
    {
        ulong result = 0;
        foreach (var item in value)
        {
            result = (result << 8) | item;
        }
        return result;
    }

    private static void RequireMissionState(
        Rdr2RsavContentReport report,
        bool expected,
        string role)
    {
        var found = report.PsoFrames
            .SelectMany(frame => frame.MissionRecords ?? Array.Empty<Rdr2PsoMissionRecord>())
            .Any(record => record.MissionScript == "fud1");
        if (found != expected)
        {
            throw new CampaignSaveSyncException(
                $"{role} has unexpected FUD1 medal state; expected present={expected}.");
        }
    }

    private static string Sha(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static readonly HashSet<uint> KnownWeaponHashes = new()
    {
        0x63CA782A, // weapon_shotgun_repeating
        0x1765A8F8, // weapon_shotgun_pump
        0x6DFA071B, // weapon_shotgun_doublebarrel
        0x772C8DD6, // weapon_shotgun_semiauto
        0xDB21AC8C, // weapon_shotgun_sawedoff
        0x5EA2A0C0, // weapon_shotgun_doublebarrel_exotic
    };

    // Names are evidence from the supplied editor's embedded RDR2 supplies
    // resource, not an approval to write arbitrary hashes. Each value still
    // has to appear in a uniquely aligned isolated control before it can be
    // classified and approved.
    private static readonly IReadOnlyDictionary<uint, string> KnownSatchelUpgradeHashes =
        new Dictionary<uint, string>
        {
            [0xFC72CDB2] = "satchel_tonics_upgrade",
            [0xA93ABD4A] = "satchel_kit_upgrade",
            [0x3A8AE9BA] = "satchel_materials_upgrade",
            [0xB39E0D3C] = "satchel_legend_of_the_east_upgrade"
        };

    private sealed record IdentityAlignment(
        Rdr2PsoField Field,
        Dictionary<string, int> Before,
        Dictionary<string, int> After,
        string[] Common);

    private readonly record struct RecordAlignment(
        int BeforeIndex,
        int AfterIndex,
        string IdentityHex,
        string Kind);
}

public sealed record CampaignCapabilityReport(
    int SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string HostBeforeSha256,
    string MedalOnlySha256,
    string HostAfterSha256,
    string MissionScript,
    IReadOnlyList<CampaignCapabilityCandidate> Candidates,
    int EnableCandidateCount,
    int MonotonicIncreaseCount,
    string ApprovalRationale,
    IReadOnlyList<CampaignCapabilityControlDelta>? ControlDeltas = null,
    IReadOnlyList<CampaignCapabilityStructuralCandidate>? StructuralCandidates = null);

/// <summary>
/// A read-only observation of a complete PSO record appended by an isolated
/// control.  It is deliberately separate from <see cref="CampaignCapabilityCandidate"/>
/// so it cannot be classified, approved, or applied by the scalar capability
/// writer.
/// </summary>
public sealed record CampaignCapabilityStructuralCandidate(
    int RegionIndex,
    int BlockIndex,
    uint StructureHash,
    int RecordLength,
    int BeforeRecordCount,
    int ObservedFinalRecordIndex,
    string RecordHeaderHex,
    string RecordHex,
    string Classification,
    bool IsApplicable,
    string Evidence,
    IReadOnlyList<CampaignCapabilityArrayCountEvidence>? ArrayCountEvidence = null,
    CampaignCapabilityStructuralLayoutEvidence? LayoutEvidence = null);

/// <summary>
/// Serializer-layout facts that are required to grow one mapped PSO block.
/// These facts are diagnostic only; they do not establish game semantics or
/// authorize any save mutation.
/// </summary>
public sealed record CampaignCapabilityStructuralLayoutEvidence(
    int BeforePsinLength,
    int AfterPsinLength,
    int BeforeChecksumRegionLength,
    int AfterChecksumRegionLength,
    int BeforeBlockDataOffset,
    int AfterBlockDataOffset,
    int BeforeBlockLength,
    int AfterBlockLength,
    bool PsinLengthGrowthMatches,
    bool ChecksumRegionGrowthMatches,
    bool PmapOffsetsAndLengthsMatch,
    string Evidence);

/// <summary>
/// A possible serialized count/capacity pair associated with structural PSO
/// growth. It is intentionally not considered a field path or write target.
/// </summary>
public sealed record CampaignCapabilityArrayCountEvidence(
    int BeforeAbsoluteOffset,
    int AfterAbsoluteOffset,
    string Encoding,
    int BeforeCount,
    int AfterCount,
    string BeforeValueHex,
    string AfterValueHex,
    string Evidence);

public sealed record CampaignCapabilityControlDelta(
    int RegionIndex,
    int PsinPayloadOffset,
    int AbsoluteDecodedOffset,
    int? BlockIndex,
    uint? StructureHash,
    int? BlockRelativeOffset,
    string BeforeValueHex,
    string AfterValueHex,
    IReadOnlyList<string> BeforeNumericInterpretations,
    IReadOnlyList<string> AfterNumericInterpretations,
    string Classification,
    bool IsApplicable,
    int ContextPsinPayloadOffset = 0,
    string BeforeContextHex = "",
    string AfterContextHex = "",
    uint? ContainingBigEndianUInt32Before = null,
    uint? ContainingBigEndianUInt32After = null,
    string? InferredRole = null,
    string? Inference = null);

public sealed record CampaignCapabilityCandidate(
    int RegionIndex,
    uint StructureHash,
    int StructureLength,
    int MedalOnlyRecordIndex,
    int CompletedRecordIndex,
    string RecordIdentityHex,
    string Alignment,
    uint FieldHash,
    string? SemanticLabel,
    byte DataType,
    short FieldOffset,
    string MedalOnlyValueHex,
    string CompletedValueHex,
    string Classification,
    bool ApprovedAsCapability,
    int Id = -1,
    CampaignCapabilitySafety Safety = CampaignCapabilitySafety.Unknown,
    CampaignRewardKind RewardKind = CampaignRewardKind.Unknown,
    string? PolicyReason = null);

public sealed record CampaignCapabilityApplyResult(
    string OutputPath,
    int AppliedCandidateCount,
    string OutputSha256,
    int CheckCount);
