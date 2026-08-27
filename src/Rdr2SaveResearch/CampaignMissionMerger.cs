using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Rdr2SaveResearch.Persistence;

/// <summary>
/// Guarded vertical-slice merger for the proven FUD1 mission-list record.
/// It only emits a new file and leaves all target bytes intact except for the
/// guest mission array descriptor, its inserted FUD1 record, serializer-backed
/// trailing sentinel, PMAP offsets, and required container integrity records.
/// </summary>
public static class CampaignMissionMerger
{
    public const string Fud1ConfirmationPhrase = "MERGE_FUD1_TEST";

    public static async Task<CampaignMissionMergeResult> MergeFud1Async(
        string hostBeforePath,
        string hostAfterPath,
        string guestBeforePath,
        string serializerAfterPath,
        string outputPath,
        string confirmation,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(
                confirmation,
                Fud1ConfirmationPhrase,
                StringComparison.Ordinal))
        {
            throw new CampaignSaveSyncException(
                $"Refusing FUD1 mission merge. Supply --confirm {Fud1ConfirmationPhrase}.");
        }

        var output = Path.GetFullPath(outputPath);
        var inputs = new[]
        {
            Path.GetFullPath(hostBeforePath),
            Path.GetFullPath(hostAfterPath),
            Path.GetFullPath(guestBeforePath),
            Path.GetFullPath(serializerAfterPath)
        };
        if (inputs.Contains(output, StringComparer.OrdinalIgnoreCase) || File.Exists(output))
        {
            throw new CampaignSaveSyncException(
                "Mission-merge output must be a new file and must not overwrite an input or existing save.");
        }
        var parent = Path.GetDirectoryName(output)
            ?? throw new CampaignSaveSyncException("The output path has no parent directory.");
        if (!Directory.Exists(parent))
        {
            throw new CampaignSaveSyncException("The output directory does not exist.");
        }

        var hostBefore = Rdr2PcSaveCodec.Decode(
            await File.ReadAllBytesAsync(inputs[0], cancellationToken).ConfigureAwait(false));
        var hostAfter = Rdr2PcSaveCodec.Decode(
            await File.ReadAllBytesAsync(inputs[1], cancellationToken).ConfigureAwait(false));
        var guestBefore = Rdr2PcSaveCodec.Decode(
            await File.ReadAllBytesAsync(inputs[2], cancellationToken).ConfigureAwait(false));
        var serializerAfter = Rdr2PcSaveCodec.Decode(
            await File.ReadAllBytesAsync(inputs[3], cancellationToken).ConfigureAwait(false));
        if (!guestBefore.CopyDecodedBytes().AsSpan()
                .SequenceEqual(hostBefore.CopyDecodedBytes()))
        {
            throw new CampaignSaveSyncException(
                "This controlled serializer-backed merge currently requires guest-before to exactly match host-before.");
        }
        var beforeReport = Rdr2RsavContentAnalyzer.Analyze(hostBefore);
        var afterReport = Rdr2RsavContentAnalyzer.Analyze(hostAfter);
        var guestReport = Rdr2RsavContentAnalyzer.Analyze(guestBefore);
        var serializerReport = Rdr2RsavContentAnalyzer.Analyze(serializerAfter);

        var sourceRecord = RequireSingleFud1(afterReport, "host-after");
        if (FindFud1(beforeReport).Count != 0)
        {
            throw new CampaignSaveSyncException(
                "host-before already contains FUD1 in its resolved mission list.");
        }
        if (FindFud1(guestReport).Count != 0)
        {
            throw new CampaignSaveSyncException(
                "guest-before already contains FUD1 in its resolved mission list.");
        }
        if (sourceRecord.RawHex.Length != 24)
        {
            throw new CampaignSaveSyncException(
                "The resolved FUD1 record is not the expected 12-byte PSO mission record.");
        }

        var guestMission = RequireMissionArray(guestReport, "guest-before");
        var hostBeforeMission = RequireMissionArray(beforeReport, "host-before");
        var serializerMission = RequireMissionArray(serializerReport, "serializer-after");
        if (guestMission.Count != hostBeforeMission.Count ||
            guestMission.Count + 1 != sourceRecord.RecordIndex + 2)
        {
            throw new CampaignSaveSyncException(
                "Mission-list counts do not match the controlled one-mission FUD1 transition.");
        }
        if (serializerMission.Count != guestMission.Count + 1)
        {
            throw new CampaignSaveSyncException(
                "serializer-after does not contain exactly one additional mission-list record.");
        }
        var serializerFud1 = RequireSingleFud1(serializerReport, "serializer-after");
        if (serializerFud1.RecordIndex != sourceRecord.RecordIndex ||
            serializerFud1.RawHex != sourceRecord.RawHex)
        {
            throw new CampaignSaveSyncException(
                "serializer-after FUD1 record differs from the real host-after transition.");
        }

        var guestFrame = guestReport.PsoFrames.SingleOrDefault(frame =>
            frame.RegionIndex == guestMission.RegionIndex)
            ?? throw new CampaignSaveSyncException("Guest PSO frame for missions is missing.");
        var guestRecordSchema = guestFrame.Structures?.SingleOrDefault(structure =>
            structure.NameHash == sourceRecord.RecordStructureHash)
            ?? throw new CampaignSaveSyncException("Guest FUD1 record schema is missing.");
        if (guestRecordSchema.Length != sourceRecord.RawHex.Length / 2)
        {
            throw new CampaignSaveSyncException("Guest FUD1 record schema length differs from host-after.");
        }
        var serializerFrame = serializerReport.PsoFrames.Single(frame =>
            frame.RegionIndex == serializerMission.RegionIndex);
        var serializerBlock = serializerFrame.Blocks?.SingleOrDefault(block =>
            block.NameHash == sourceRecord.RecordStructureHash &&
            block.Length == serializerMission.Count * guestRecordSchema.Length &&
            block.AbsoluteOffset is not null)
            ?? throw new CampaignSaveSyncException(
                "serializer-after mission-array backing block is not uniquely resolvable.");
        var serializerBytes = serializerAfter.CopyDecodedBytes();
        var sentinelOffset = serializerBlock.AbsoluteOffset!.Value +
            (serializerMission.Count - 1) * guestRecordSchema.Length;
        var sentinelBytes = serializerBytes.AsSpan(
            sentinelOffset,
            guestRecordSchema.Length).ToArray();
        var guestRecordsBlock = guestFrame.Blocks?.Where(block =>
                block.NameHash == sourceRecord.RecordStructureHash &&
                block.Length == guestMission.Count * guestRecordSchema.Length &&
                block.AbsoluteOffset is not null)
            .ToArray()
            ?? [];
        if (guestRecordsBlock.Length != 1)
        {
            throw new CampaignSaveSyncException(
                "Guest mission-array backing block is not uniquely resolvable.");
        }
        var targetBlock = guestRecordsBlock[0];
        var targetRegion = guestReport.Regions.Single(region =>
            region.Index == guestMission.RegionIndex);
        var targetSection = guestReport.Sections.Single(section =>
            section.RegionIndex == guestMission.RegionIndex);
        var dataPart = targetSection.Parts.Single(part => part.Marker == "PSIN");
        var mapPart = targetSection.Parts.Single(part => part.Marker == "PMAP");
        var recordBytes = Convert.FromHexString(sourceRecord.RawHex);
        var decoded = guestBefore.CopyDecodedBytes();

        // The editor-produced control save proves that the array is grown by
        // inserting the 12-byte record and shifting later PMAP allocations.
        // PSIN and checksum region 0 grow by 12 only. Four 0x70 bytes are then
        // added to the inter-region envelope gap so region 1 remains aligned
        // to the AES/save-container 16-byte boundary.
        if (sourceRecord.RecordIndex < 0 || sourceRecord.RecordIndex > guestMission.Count)
        {
            throw new CampaignSaveSyncException(
                "The host-after FUD1 insertion index is outside the guest mission array.");
        }
        var insertionOffset = targetBlock.AbsoluteOffset!.Value +
            sourceRecord.RecordIndex * guestRecordSchema.Length;
        var relativeInsertionOffset = targetBlock.DataOffset +
            sourceRecord.RecordIndex * guestRecordSchema.Length;
        decoded = InsertBytes(decoded, insertionOffset, recordBytes);
        var mergedSentinelOffset = targetBlock.AbsoluteOffset!.Value +
            guestMission.Count * guestRecordSchema.Length;
        sentinelBytes.CopyTo(decoded.AsSpan(mergedSentinelOffset));

        BinaryPrimitives.WriteUInt16BigEndian(
            decoded.AsSpan(guestMission.AbsoluteOffset, 2),
            checked((ushort)(guestMission.Count + 1)));
        BinaryPrimitives.WriteUInt16BigEndian(
            decoded.AsSpan(guestMission.AbsoluteOffset + 2, 2),
            checked((ushort)(guestMission.Count + 1)));
        BinaryPrimitives.WriteInt32BigEndian(
            decoded.AsSpan(dataPart.Offset + 4, 4),
            checked(dataPart.Length + recordBytes.Length));

        var mapOffset = mapPart.Offset + recordBytes.Length;
        var mapCount = BinaryPrimitives.ReadUInt16BigEndian(decoded.AsSpan(mapOffset + 16, 2));
        if (mapCount != guestFrame.Blocks!.Count)
        {
            throw new CampaignSaveSyncException("Guest PMAP changed while preparing the mission merge.");
        }
        for (var index = 0; index < mapCount; index++)
        {
            var entryOffset = mapOffset + 24 + index * 16;
            var dataOffset = BinaryPrimitives.ReadInt32BigEndian(
                decoded.AsSpan(entryOffset + 4, 4));
            if (dataOffset >= relativeInsertionOffset)
            {
                BinaryPrimitives.WriteInt32BigEndian(
                    decoded.AsSpan(entryOffset + 4, 4),
                    checked(dataOffset + recordBytes.Length));
            }
        }
        var targetEntryOffset = mapOffset + 24 + (targetBlock.Index - 1) * 16;
        BinaryPrimitives.WriteInt32BigEndian(
            decoded.AsSpan(targetEntryOffset + 12, 4),
            checked(targetBlock.Length + recordBytes.Length));

        var shiftedCheckOffset = targetRegion.CheckOffset + recordBytes.Length;
        var priorProtectedLength = BinaryPrimitives.ReadInt32BigEndian(
            decoded.AsSpan(shiftedCheckOffset + 8, 4));
        if (priorProtectedLength != targetRegion.DataLength)
        {
            throw new CampaignSaveSyncException(
                "CHKS protected length changed while preparing the mission merge.");
        }
        BinaryPrimitives.WriteInt32BigEndian(
            decoded.AsSpan(shiftedCheckOffset + 8, 4),
            checked(priorProtectedLength + recordBytes.Length));
        decoded = InsertBytes(decoded, shiftedCheckOffset + 20, [0x70, 0x70, 0x70, 0x70]);

        // The editor also normalizes opaque PSIN serialization in all
        // regions even though the analyzer reports no semantic field, tag, or
        // reference-count changes there. The rejected A/B candidate isolated
        // the remaining payload difference to one byte in the mission region.
        // This test merger already requires the
        // recipient to byte-match the serializer's source baseline, so adopting
        // those serializer-produced bytes cannot replace independent guest data.
        // It is necessary to test the complete serializer envelope rather than
        // a hybrid of game- and editor-produced PSO encodings.
        ApplySerializerPsinNormalization(
            decoded,
            serializerBytes,
            guestBefore.Title);
        ApplySerializerEnvelopeDirectory(decoded, serializerBytes);

        // CHKS has a serializer-owned byte at +16 in addition to its length and
        // JOOAT value. Genuine game saves in this fixture use 0x79, while the
        // known-loadable editor serialization consistently rewrites every CHKS
        // record to 0x6F. The rejected candidate mixed the editor's resized PSO
        // layout with the game's old marker despite being checksum-consistent.
        // Adopt the marker from the required serializer control, then Encode()
        // recalculates each JOOAT over the complete, marker-bearing region.
        ApplySerializerIntegrityMarker(decoded, serializerBytes);

        var mergedDocument = new Rdr2PcSaveDocument(decoded, guestBefore.Title);
        var encoded = Rdr2PcSaveCodec.Encode(mergedDocument);
        var verification = Rdr2PcSaveCodec.VerifyRoundTrip(encoded);
        if (!verification.IsExactRoundTrip)
        {
            throw new CampaignSaveSyncException("Merged save failed codec round-trip verification.");
        }
        var mergedReport = Rdr2RsavContentAnalyzer.Analyze(Rdr2PcSaveCodec.Decode(encoded));
        var mergedRecord = RequireSingleFud1(mergedReport, "merged output");
        if (mergedRecord.RawHex != sourceRecord.RawHex ||
            mergedRecord.RecordIndex != sourceRecord.RecordIndex)
        {
            throw new CampaignSaveSyncException(
                "Merged FUD1 record or ordering differs from the host-after record.");
        }
        var mergedDecoded = Rdr2PcSaveCodec.Decode(encoded).CopyDecodedBytes();
        var mergedFrame = mergedReport.PsoFrames.Single(frame =>
            frame.RegionIndex == guestMission.RegionIndex);
        var mergedBlock = mergedFrame.Blocks?.SingleOrDefault(block =>
            block.NameHash == sourceRecord.RecordStructureHash &&
            block.Length == serializerBlock.Length &&
            block.AbsoluteOffset is not null)
            ?? throw new CampaignSaveSyncException(
                "Merged mission-array backing block is not uniquely resolvable.");
        if (!mergedDecoded.AsSpan(mergedBlock.AbsoluteOffset!.Value, mergedBlock.Length)
                .SequenceEqual(serializerBytes.AsSpan(
                    serializerBlock.AbsoluteOffset!.Value,
                    serializerBlock.Length)))
        {
            throw new CampaignSaveSyncException(
                "Merged mission-array block does not byte-match serializer-after.");
        }
        RequireSerializerSectionMatch(
            mergedDecoded,
            mergedReport,
            serializerBytes,
            serializerReport,
            guestMission.RegionIndex,
            "PMAP");
        RequireSerializerSectionMatch(
            mergedDecoded,
            mergedReport,
            serializerBytes,
            serializerReport,
            guestMission.RegionIndex,
            "PSCH");
        RequireSerializerSecondaryRegionsMatch(
            mergedDecoded,
            mergedReport,
            serializerBytes,
            serializerReport,
            guestMission.RegionIndex);
        if (!mergedDecoded.AsSpan().SequenceEqual(serializerBytes))
        {
            var changedOffsets = Enumerable.Range(0, mergedDecoded.Length)
                .Where(index => mergedDecoded[index] != serializerBytes[index])
                .ToArray();
            throw new CampaignSaveSyncException(
                "Controlled merge did not reproduce serializer-after exactly; " +
                $"changedBytes={changedOffsets.Length}, firstOffsets=" +
                string.Join(',', changedOffsets.Take(32).Select(index =>
                    $"{index}:{mergedDecoded[index]:X2}>{serializerBytes[index]:X2}")) + ".");
        }

        await using (var stream = new FileStream(
            output,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.WriteThrough))
        {
            await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        return new CampaignMissionMergeResult(
            output,
            sourceRecord.RecordIndex,
            sourceRecord.RawHex,
            Convert.ToHexString(SHA256.HashData(encoded)),
            verification.CheckCount);
    }

    private static Rdr2PsoMissionRecord RequireSingleFud1(
        Rdr2RsavContentReport report,
        string role)
    {
        var records = FindFud1(report);
        return records.Count == 1
            ? records[0]
            : throw new CampaignSaveSyncException(
                $"{role} must resolve exactly one FUD1 mission record; found {records.Count}.");
    }

    private static List<Rdr2PsoMissionRecord> FindFud1(Rdr2RsavContentReport report) =>
        report.PsoFrames
            .SelectMany(static frame => frame.MissionRecords ?? Array.Empty<Rdr2PsoMissionRecord>())
            .Where(static record => record.MissionScript == "fud1")
            .ToList();

    private static MissionArrayLocation RequireMissionArray(
        Rdr2RsavContentReport report,
        string role)
    {
        var matches = report.PsoFrames
            .SelectMany(frame => (frame.SemanticFields ?? Array.Empty<Rdr2PsoLocatedField>())
                .Where(field => field.SemanticLabel == "missions" && field.DataType == 0x0D)
                .Select(field => new MissionArrayLocation(
                    frame.RegionIndex,
                    field.AbsoluteOffset,
                    ReadCount(field.InlineValueHex))))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new CampaignSaveSyncException(
                $"{role} must contain exactly one validated missions array; found {matches.Length}.");
        }
        return matches[0];
    }

    private static int ReadCount(string valueHex)
    {
        var bytes = Convert.FromHexString(valueHex);
        if (bytes.Length < 4 ||
            BinaryPrimitives.ReadUInt16BigEndian(bytes) !=
            BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2)))
        {
            throw new CampaignSaveSyncException(
                "Missions array descriptor does not have matching count and capacity.");
        }
        return BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    private static void RequireSerializerSectionMatch(
        byte[] merged,
        Rdr2RsavContentReport mergedReport,
        byte[] serializer,
        Rdr2RsavContentReport serializerReport,
        int regionIndex,
        string marker)
    {
        var mergedPart = mergedReport.Sections.Single(section =>
                section.RegionIndex == regionIndex)
            .Parts.Single(part => part.Marker == marker);
        var serializerPart = serializerReport.Sections.Single(section =>
                section.RegionIndex == regionIndex)
            .Parts.Single(part => part.Marker == marker);
        if (mergedPart.Length != serializerPart.Length ||
            !merged.AsSpan(mergedPart.Offset, mergedPart.Length)
                .SequenceEqual(serializer.AsSpan(serializerPart.Offset, serializerPart.Length)))
        {
            var compareLength = Math.Min(mergedPart.Length, serializerPart.Length);
            var firstDifference = 0;
            while (firstDifference < compareLength &&
                   merged[mergedPart.Offset + firstDifference] ==
                   serializer[serializerPart.Offset + firstDifference])
            {
                firstDifference++;
            }
            var previewLength = Math.Min(16, compareLength - firstDifference);
            throw new CampaignSaveSyncException(
                $"Merged {marker} does not byte-match the serializer-after control; " +
                $"firstDifference={firstDifference}, merged=" +
                Convert.ToHexString(merged.AsSpan(
                    mergedPart.Offset + firstDifference,
                    Math.Max(0, previewLength))) +
                ", serializer=" +
                Convert.ToHexString(serializer.AsSpan(
                    serializerPart.Offset + firstDifference,
                    Math.Max(0, previewLength))) + ".");
        }
    }

    private static void ApplySerializerIntegrityMarker(
        Span<byte> merged,
        ReadOnlySpan<byte> serializer)
    {
        var serializerChecks = Rdr2PcSaveCodec.FindChecks(serializer);
        var mergedChecks = Rdr2PcSaveCodec.FindChecks(merged);
        if (serializerChecks.Count == 0 || serializerChecks.Count != mergedChecks.Count)
        {
            throw new CampaignSaveSyncException(
                "Serializer and merged save do not have matching CHKS records.");
        }

        var marker = serializer[serializerChecks[0].Offset + 16];
        for (var index = 0; index < serializerChecks.Count; index++)
        {
            var serializerOffset = serializerChecks[index].Offset;
            var mergedOffset = mergedChecks[index].Offset;
            if (serializer[serializerOffset + 16] != marker ||
                !serializer.Slice(serializerOffset + 17, 3)
                    .SequenceEqual(new byte[] { 0x70, 0x70, 0x70 }) ||
                !merged.Slice(mergedOffset + 17, 3)
                    .SequenceEqual(new byte[] { 0x70, 0x70, 0x70 }))
            {
                throw new CampaignSaveSyncException(
                    "Serializer CHKS integrity marker/trailer is inconsistent.");
            }
            merged[mergedOffset + 16] = marker;
        }
    }

    private static void ApplySerializerEnvelopeDirectory(
        Span<byte> merged,
        ReadOnlySpan<byte> serializer)
    {
        var mergedChecks = Rdr2PcSaveCodec.FindChecks(merged);
        var serializerChecks = Rdr2PcSaveCodec.FindChecks(serializer);
        if (mergedChecks.Count == 0 || mergedChecks.Count != serializerChecks.Count ||
            mergedChecks[0].DataOffset != serializerChecks[0].DataOffset)
        {
            throw new CampaignSaveSyncException(
                "Serializer and merged save do not have matching RSAV region directories.");
        }

        var directoryOffset = Rdr2PcSaveCodec.EncryptedPayloadOffset;
        var directoryLength = mergedChecks[0].DataOffset - directoryOffset;
        if (directoryLength <= 0)
        {
            throw new CampaignSaveSyncException("RSAV region directory has an invalid length.");
        }
        serializer.Slice(directoryOffset, directoryLength)
            .CopyTo(merged.Slice(directoryOffset, directoryLength));
    }

    private static void ApplySerializerPsinNormalization(
        Span<byte> merged,
        ReadOnlySpan<byte> serializer,
        string title)
    {
        var mergedReport = Rdr2RsavContentAnalyzer.Analyze(
            new Rdr2PcSaveDocument(merged.ToArray(), title));
        var serializerReport = Rdr2RsavContentAnalyzer.Analyze(
            new Rdr2PcSaveDocument(serializer.ToArray(), title));

        foreach (var serializerSection in serializerReport.Sections)
        {
            var serializerPart = serializerSection.Parts.SingleOrDefault(part =>
                part.Marker == "PSIN");
            if (serializerPart.Marker is null)
            {
                continue;
            }
            var mergedPart = mergedReport.Sections.Single(section =>
                    section.RegionIndex == serializerSection.RegionIndex)
                .Parts.Single(part => part.Marker == "PSIN");
            if (mergedPart.Length != serializerPart.Length)
            {
                throw new CampaignSaveSyncException(
                    $"Serializer PSIN length differs in secondary region {serializerSection.RegionIndex}.");
            }
            serializer.Slice(serializerPart.Offset, serializerPart.Length)
                .CopyTo(merged.Slice(mergedPart.Offset, mergedPart.Length));
        }
    }

    private static void RequireSerializerSecondaryRegionsMatch(
        ReadOnlySpan<byte> merged,
        Rdr2RsavContentReport mergedReport,
        ReadOnlySpan<byte> serializer,
        Rdr2RsavContentReport serializerReport,
        int missionRegionIndex)
    {
        foreach (var serializerRegion in serializerReport.Regions.Where(region =>
                     region.Index != missionRegionIndex))
        {
            var mergedRegion = mergedReport.Regions.Single(region =>
                region.Index == serializerRegion.Index);
            var serializerLength = serializerRegion.CheckOffset + 20 - serializerRegion.DataOffset;
            var mergedLength = mergedRegion.CheckOffset + 20 - mergedRegion.DataOffset;
            if (mergedLength != serializerLength ||
                !merged.Slice(mergedRegion.DataOffset, mergedLength).SequenceEqual(
                    serializer.Slice(serializerRegion.DataOffset, serializerLength)))
            {
                throw new CampaignSaveSyncException(
                    $"Merged secondary region {serializerRegion.Index} does not byte-match serializer-after.");
            }
        }
    }

    private static byte[] InsertBytes(byte[] source, int offset, ReadOnlySpan<byte> inserted)
    {
        if (offset < 0 || offset > source.Length || inserted.Length == 0)
        {
            throw new CampaignSaveSyncException("Invalid PSO insertion range.");
        }
        var result = new byte[checked(source.Length + inserted.Length)];
        source.AsSpan(0, offset).CopyTo(result);
        inserted.CopyTo(result.AsSpan(offset));
        source.AsSpan(offset).CopyTo(result.AsSpan(offset + inserted.Length));
        return result;
    }

    private readonly record struct MissionArrayLocation(
        int RegionIndex,
        int AbsoluteOffset,
        int Count);
}

public sealed record CampaignMissionMergeResult(
    string OutputPath,
    int Fud1RecordIndex,
    string Fud1RecordHex,
    string OutputSha256,
    int CheckCount);
