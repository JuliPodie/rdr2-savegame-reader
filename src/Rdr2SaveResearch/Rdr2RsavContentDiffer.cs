namespace Rdr2SaveResearch.Persistence;

/// <summary>
/// Compares two decrypted RSAV containers without treating changed bytes as
/// editable fields. Its output is a repeatable evidence record for discovering
/// where a controlled action (such as one completed mission) is serialized.
/// </summary>
public static class Rdr2RsavContentDiffer
{
    public static Rdr2RsavContentDiffReport Compare(
        Rdr2PcSaveDocument before,
        Rdr2PcSaveDocument after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var beforeReport = Rdr2RsavContentAnalyzer.Analyze(before);
        var afterReport = Rdr2RsavContentAnalyzer.Analyze(after);
        return new Rdr2RsavContentDiffReport(
            beforeReport.SaveTitle,
            afterReport.SaveTitle,
            beforeReport.DecodedLength,
            afterReport.DecodedLength,
            CompareRegions(
                before.CopyDecodedBytes(),
                beforeReport.Regions,
                after.CopyDecodedBytes(),
                afterReport.Regions),
            CompareParts(
                before.CopyDecodedBytes(),
                beforeReport.Sections,
                after.CopyDecodedBytes(),
                afterReport.Sections),
            CompareSemanticFields(beforeReport.PsoFrames, afterReport.PsoFrames),
            CompareReferences(beforeReport.References, afterReport.References),
            CompareTags(beforeReport.Tags, afterReport.Tags));
    }

    private static IReadOnlyList<Rdr2RsavFramedPartDiff> CompareParts(
        byte[] before,
        IReadOnlyList<Rdr2RsavFramedSection> beforeSections,
        byte[] after,
        IReadOnlyList<Rdr2RsavFramedSection> afterSections)
    {
        var beforeParts = beforeSections
            .SelectMany(section => section.Parts.Select(part => (
                section.RegionIndex,
                Part: part)))
            .ToDictionary(
                static item => (item.RegionIndex, item.Part.Marker),
                static item => item.Part);
        var afterParts = afterSections
            .SelectMany(section => section.Parts.Select(part => (
                section.RegionIndex,
                Part: part)))
            .ToDictionary(
                static item => (item.RegionIndex, item.Part.Marker),
                static item => item.Part);
        var keys = beforeParts.Keys.Concat(afterParts.Keys).Distinct()
            .OrderBy(static key => key.RegionIndex)
            .ThenBy(static key => key.Marker, StringComparer.Ordinal);
        var result = new List<Rdr2RsavFramedPartDiff>();
        foreach (var key in keys)
        {
            var hasBefore = beforeParts.TryGetValue(key, out var beforePart);
            var hasAfter = afterParts.TryGetValue(key, out var afterPart);
            if (!hasBefore || !hasAfter)
            {
                result.Add(new Rdr2RsavFramedPartDiff(
                    key.RegionIndex,
                    key.Marker,
                    hasBefore ? beforePart.Length : null,
                    hasAfter ? afterPart.Length : null,
                    false,
                    0,
                    0,
                    null,
                    Array.Empty<int>()));
                continue;
            }
            var beforeBytes = before.AsSpan(beforePart.Offset, beforePart.Length);
            var afterBytes = after.AsSpan(afterPart.Offset, afterPart.Length);
            var samePrefix = CountSamePrefix(beforeBytes, afterBytes);
            var sameSuffix = CountSameSuffix(beforeBytes, afterBytes, samePrefix);
            var changedOffsets = beforeBytes.Length == afterBytes.Length
                ? FindChangedOffsets(beforeBytes, afterBytes, 512)
                : Array.Empty<int>();
            int? changedByteCount = beforeBytes.Length == afterBytes.Length
                ? CountChangedBytes(beforeBytes, afterBytes)
                : null;
            result.Add(new Rdr2RsavFramedPartDiff(
                key.RegionIndex,
                key.Marker,
                beforeBytes.Length,
                afterBytes.Length,
                beforeBytes.SequenceEqual(afterBytes),
                samePrefix,
                sameSuffix,
                changedByteCount,
                changedOffsets));
        }
        return result;
    }

    private static IReadOnlyList<Rdr2PsoSemanticFieldDiff> CompareSemanticFields(
        IReadOnlyList<Rdr2PsoFrame> before,
        IReadOnlyList<Rdr2PsoFrame> after)
    {
        var beforeFields = FlattenSemanticFields(before);
        var afterFields = FlattenSemanticFields(after);
        var keys = beforeFields.Keys.Concat(afterFields.Keys)
            .Distinct()
            .OrderBy(static key => key.StructureHash)
            .ThenBy(static key => key.FieldHash);
        var result = new List<Rdr2PsoSemanticFieldDiff>();
        foreach (var key in keys)
        {
            beforeFields.TryGetValue(key, out var beforeField);
            afterFields.TryGetValue(key, out var afterField);
            if (beforeField.InlineValueHex == afterField.InlineValueHex &&
                beforeField.InlineLength == afterField.InlineLength)
            {
                continue;
            }
            result.Add(new Rdr2PsoSemanticFieldDiff(
                key.StructureHash,
                key.FieldHash,
                beforeField.SemanticLabel ?? afterField.SemanticLabel ?? "unknown",
                beforeField.AbsoluteOffset,
                afterField.AbsoluteOffset,
                beforeField.InlineValueHex,
                afterField.InlineValueHex,
                beforeField.DataType == afterField.DataType
                    ? beforeField.DataType
                    : null));
        }
        return result;
    }

    private static Dictionary<(uint StructureHash, uint FieldHash), Rdr2PsoLocatedField>
        FlattenSemanticFields(IReadOnlyList<Rdr2PsoFrame> frames)
    {
        var result = new Dictionary<(uint StructureHash, uint FieldHash), Rdr2PsoLocatedField>();
        foreach (var field in frames.SelectMany(static frame =>
                     frame.SemanticFields ?? Array.Empty<Rdr2PsoLocatedField>()))
        {
            // A duplicate schema field is ambiguous. It is omitted so a later
            // merge can never choose one occurrence by accident.
            var key = (field.StructureHash, field.FieldHash);
            if (!result.TryAdd(key, field))
            {
                result.Remove(key);
            }
        }
        return result;
    }

    private static IReadOnlyList<Rdr2RsavRegionDiff> CompareRegions(
        byte[] before,
        IReadOnlyList<Rdr2RsavRegion> beforeRegions,
        byte[] after,
        IReadOnlyList<Rdr2RsavRegion> afterRegions)
    {
        var result = new List<Rdr2RsavRegionDiff>();
        var count = Math.Max(beforeRegions.Count, afterRegions.Count);
        for (var index = 0; index < count; index++)
        {
            Rdr2RsavRegion? beforeRegion = index < beforeRegions.Count
                ? beforeRegions[index]
                : null;
            Rdr2RsavRegion? afterRegion = index < afterRegions.Count
                ? afterRegions[index]
                : null;
            if (beforeRegion is null || afterRegion is null)
            {
                result.Add(new Rdr2RsavRegionDiff(
                    index,
                    beforeRegion?.DataLength,
                    afterRegion?.DataLength,
                    false,
                    0,
                    0));
                continue;
            }

            var beforeBytes = before.AsSpan(
                beforeRegion.Value.DataOffset,
                beforeRegion.Value.DataLength);
            var afterBytes = after.AsSpan(
                afterRegion.Value.DataOffset,
                afterRegion.Value.DataLength);
            var samePrefix = CountSamePrefix(beforeBytes, afterBytes);
            var sameSuffix = CountSameSuffix(beforeBytes, afterBytes, samePrefix);
            result.Add(new Rdr2RsavRegionDiff(
                index,
                beforeBytes.Length,
                afterBytes.Length,
                beforeBytes.SequenceEqual(afterBytes),
                samePrefix,
                sameSuffix));
        }
        return result;
    }

    private static int CountSamePrefix(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        var count = 0;
        while (count < before.Length && count < after.Length && before[count] == after[count])
        {
            count++;
        }
        return count;
    }

    private static int CountChangedBytes(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        var count = 0;
        for (var index = 0; index < before.Length; index++)
        {
            if (before[index] != after[index])
            {
                count++;
            }
        }
        return count;
    }

    private static int[] FindChangedOffsets(
        ReadOnlySpan<byte> before,
        ReadOnlySpan<byte> after,
        int maximum)
    {
        var result = new List<int>(Math.Min(maximum, before.Length));
        for (var index = 0; index < before.Length && result.Count < maximum; index++)
        {
            if (before[index] != after[index])
            {
                result.Add(index);
            }
        }
        return result.ToArray();
    }

    private static int CountSameSuffix(
        ReadOnlySpan<byte> before,
        ReadOnlySpan<byte> after,
        int sharedPrefix)
    {
        var count = 0;
        while (count < before.Length - sharedPrefix &&
               count < after.Length - sharedPrefix &&
               before[^(count + 1)] == after[^(count + 1)])
        {
            count++;
        }
        return count;
    }

    private static IReadOnlyList<Rdr2RsavNamedCountDiff> CompareReferences(
        IReadOnlyList<Rdr2RsavReference> before,
        IReadOnlyList<Rdr2RsavReference> after) =>
        CompareNamedCounts(
            before.Select(static reference => reference.Name),
            after.Select(static reference => reference.Name));

    private static IReadOnlyList<Rdr2RsavNamedCountDiff> CompareTags(
        IReadOnlyList<Rdr2RsavTag> before,
        IReadOnlyList<Rdr2RsavTag> after) =>
        CompareNamedCounts(
            before.Select(static tag => tag.Value),
            after.Select(static tag => tag.Value));

    private static IReadOnlyList<Rdr2RsavNamedCountDiff> CompareNamedCounts(
        IEnumerable<string> before,
        IEnumerable<string> after)
    {
        var beforeCounts = before.GroupBy(static value => value, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        var afterCounts = after.GroupBy(static value => value, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        return beforeCounts.Keys.Concat(afterCounts.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .Select(value => new Rdr2RsavNamedCountDiff(
                value,
                beforeCounts.GetValueOrDefault(value),
                afterCounts.GetValueOrDefault(value)))
            .Where(static difference => difference.BeforeCount != difference.AfterCount)
            .ToArray();
    }
}

public sealed record Rdr2RsavContentDiffReport(
    string BeforeTitle,
    string AfterTitle,
    int BeforeDecodedLength,
    int AfterDecodedLength,
    IReadOnlyList<Rdr2RsavRegionDiff> Regions,
    IReadOnlyList<Rdr2RsavFramedPartDiff> Parts,
    IReadOnlyList<Rdr2PsoSemanticFieldDiff> SemanticFieldChanges,
    IReadOnlyList<Rdr2RsavNamedCountDiff> ReferenceCountChanges,
    IReadOnlyList<Rdr2RsavNamedCountDiff> TagCountChanges);

public readonly record struct Rdr2RsavRegionDiff(
    int Index,
    int? BeforeLength,
    int? AfterLength,
    bool IsByteIdentical,
    int SharedPrefixBytes,
    int SharedSuffixBytes);

public readonly record struct Rdr2RsavFramedPartDiff(
    int RegionIndex,
    string Marker,
    int? BeforeLength,
    int? AfterLength,
    bool IsByteIdentical,
    int SharedPrefixBytes,
    int SharedSuffixBytes,
    int? ChangedByteCount,
    IReadOnlyList<int> FirstChangedOffsets);

/// <summary>
/// A schema-labelled field changed across saves. This is evidence only; array
/// and pointer fields require their backing blocks to be resolved before any
/// save mutation can be considered.
/// </summary>
public readonly record struct Rdr2PsoSemanticFieldDiff(
    uint StructureHash,
    uint FieldHash,
    string SemanticLabel,
    int BeforeAbsoluteOffset,
    int AfterAbsoluteOffset,
    string BeforeInlineValueHex,
    string AfterInlineValueHex,
    byte? DataType);

public readonly record struct Rdr2RsavNamedCountDiff(
    string Name,
    int BeforeCount,
    int AfterCount);
