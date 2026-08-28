using System.Buffers.Binary;
using System.Text;

namespace Rdr2SaveResearch.Persistence;

/// <summary>
/// Read-only RSAV content indexer. Native references are recognized only from
/// an explicitly supplied NativeDB catalog; every other value remains opaque
/// until a field schema is proven from controlled saves.
/// </summary>
public static class Rdr2RsavContentAnalyzer
{
    private const int MaximumStrings = 512;
    private const int MaximumTags = 512;
    private static readonly IReadOnlySet<string> KnownTags = new HashSet<string>(
        StringComparer.Ordinal)
    {
        "RSAV", "PSIN", "PMAP", "PSCH", "PSIG", "CHKS"
    };

    public static Rdr2RsavContentReport Analyze(
        Rdr2PcSaveDocument document,
        IReadOnlyList<NativeHeaderEntry>? nativeCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return AnalyzeDecoded(document.CopyDecodedBytes(), document.Title, document.Checks, nativeCatalog);
    }

    internal static Rdr2RsavContentReport AnalyzeDecoded(
        ReadOnlySpan<byte> decoded,
        string title,
        IReadOnlyList<Rdr2PcSaveCheck> checks,
        IReadOnlyList<NativeHeaderEntry>? nativeCatalog = null)
    {
        if (decoded.Length < Rdr2PcSaveCodec.EncryptedPayloadOffset + 4 ||
            !decoded.Slice(Rdr2PcSaveCodec.EncryptedPayloadOffset, 4)
                .SequenceEqual("RSAV"u8))
        {
            throw new Rdr2RsavContentException("The decoded bytes are not an RSAV container.");
        }

        var regions = checks.Select((check, index) => new Rdr2RsavRegion(
            index,
            check.DataOffset,
            check.DataLength,
            check.Offset)).ToArray();
        var tags = ScanTags(decoded, regions);
        var sections = ParseFramedSections(decoded, regions, tags);
        var psoFrames = Rdr2PsoSchemaAnalyzer.Analyze(decoded, sections);
        var references = ScanNativeReferences(decoded, regions, nativeCatalog);
        var strings = ScanStrings(decoded, regions);
        var envelopeGaps = LocateEnvelopeGaps(decoded, regions);
        return new Rdr2RsavContentReport(
            title,
            decoded.Length,
            regions,
            tags,
            sections,
            psoFrames,
            references,
            strings,
            envelopeGaps,
            Convert.ToHexString(decoded[^Math.Min(decoded.Length, 64)..]));
    }

    private static IReadOnlyList<Rdr2RsavEnvelopeGap> LocateEnvelopeGaps(
        ReadOnlySpan<byte> decoded,
        IReadOnlyList<Rdr2RsavRegion> regions)
    {
        var result = new List<Rdr2RsavEnvelopeGap>();
        for (var index = 0; index < regions.Count - 1; index++)
        {
            var start = checked(regions[index].CheckOffset + 20);
            var end = regions[index + 1].DataOffset;
            if (end < start)
            {
                throw new Rdr2RsavContentException("RSAV checksum regions overlap.");
            }
            result.Add(new Rdr2RsavEnvelopeGap(
                index,
                index + 1,
                start,
                end - start,
                Convert.ToHexString(decoded.Slice(start, Math.Min(end - start, 64)))));
        }
        return result;
    }

    private static IReadOnlyList<Rdr2RsavFramedSection> ParseFramedSections(
        ReadOnlySpan<byte> decoded,
        IReadOnlyList<Rdr2RsavRegion> regions,
        IReadOnlyList<Rdr2RsavTag> tags)
    {
        var result = new List<Rdr2RsavFramedSection>();
        foreach (var region in regions)
        {
            var markers = tags
                .Where(tag => tag.RegionIndex == region.Index &&
                    tag.Value is "PSIN" or "PMAP" or "PSCH" or "PSIG")
                .OrderBy(static tag => tag.Offset)
                .ToArray();
            if (markers.Length == 0)
            {
                continue;
            }

            var hasExpectedFrame = markers.Length == 4 &&
                markers[0].Value == "PSIN" &&
                markers[1].Value == "PMAP" &&
                markers[2].Value == "PSCH" &&
                markers[3].Value == "PSIG" &&
                markers[0].Offset == region.DataOffset;
            var parts = new List<Rdr2RsavFramedPart>(markers.Length);
            for (var index = 0; index < markers.Length; index++)
            {
                var nextOffset = index + 1 < markers.Length
                    ? markers[index + 1].Offset
                    : region.DataOffset + region.DataLength;
                parts.Add(new Rdr2RsavFramedPart(
                    markers[index].Value,
                    markers[index].Offset,
                    nextOffset - markers[index].Offset,
                    Convert.ToHexString(decoded.Slice(
                        markers[index].Offset,
                        Math.Min(nextOffset - markers[index].Offset, 64))),
                    Convert.ToHexString(decoded.Slice(
                        Math.Max(markers[index].Offset, nextOffset - 32),
                        Math.Min(nextOffset - markers[index].Offset, 32)))));
            }
            result.Add(new Rdr2RsavFramedSection(
                region.Index,
                hasExpectedFrame,
                parts));
        }
        return result;
    }

    private static IReadOnlyList<Rdr2RsavTag> ScanTags(
        ReadOnlySpan<byte> bytes,
        IReadOnlyList<Rdr2RsavRegion> regions)
    {
        var result = new List<Rdr2RsavTag>();
        for (var offset = Rdr2PcSaveCodec.EncryptedPayloadOffset;
             offset <= bytes.Length - 4 && result.Count < MaximumTags;
             offset++)
        {
            var value = Encoding.ASCII.GetString(bytes.Slice(offset, 4));
            if (KnownTags.Contains(value))
            {
                result.Add(new Rdr2RsavTag(offset, value, FindRegion(regions, offset)));
            }
        }
        return result;
    }

    private static IReadOnlyList<Rdr2RsavReference> ScanNativeReferences(
        ReadOnlySpan<byte> bytes,
        IReadOnlyList<Rdr2RsavRegion> regions,
        IReadOnlyList<NativeHeaderEntry>? nativeCatalog)
    {
        if (nativeCatalog is null || nativeCatalog.Count == 0)
        {
            return Array.Empty<Rdr2RsavReference>();
        }
        var catalogByHash = nativeCatalog
            .GroupBy(entry => Convert.ToUInt64(entry.Hash, 16))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var references = new List<Rdr2RsavReference>();
        for (var offset = Rdr2PcSaveCodec.EncryptedPayloadOffset;
             offset <= bytes.Length - 8;
             offset++)
        {
            var littleEndian = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));
            var bigEndian = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(offset, 8));
            AddReference(littleEndian, Rdr2RsavEndianness.LittleEndian);
            if (bigEndian != littleEndian)
            {
                AddReference(bigEndian, Rdr2RsavEndianness.BigEndian);
            }

            void AddReference(ulong value, Rdr2RsavEndianness endianness)
            {
                if (catalogByHash.TryGetValue(value, out var matches))
                {
                    foreach (var match in matches)
                    {
                        references.Add(new Rdr2RsavReference(
                            offset,
                            endianness,
                            $"{value:X16}",
                            match.Namespace,
                            match.Name,
                            FindRegion(regions, offset)));
                    }
                }
            }
        }
        return references;
    }

    private static IReadOnlyList<Rdr2RsavString> ScanStrings(
        ReadOnlySpan<byte> bytes,
        IReadOnlyList<Rdr2RsavRegion> regions)
    {
        var result = new List<Rdr2RsavString>();
        for (var offset = Rdr2PcSaveCodec.EncryptedPayloadOffset;
             offset < bytes.Length && result.Count < MaximumStrings;)
        {
            var asciiEnd = offset;
            while (asciiEnd < bytes.Length && IsPrintableAscii(bytes[asciiEnd]))
            {
                asciiEnd++;
            }
            if (asciiEnd - offset >= 4)
            {
                result.Add(new Rdr2RsavString(
                    offset,
                    Rdr2RsavStringEncoding.Ascii,
                    Encoding.ASCII.GetString(bytes.Slice(offset, asciiEnd - offset)),
                    FindRegion(regions, offset)));
                offset = asciiEnd;
                continue;
            }

            var utf16End = offset;
            while (utf16End + 1 < bytes.Length &&
                   IsPrintableAscii(bytes[utf16End]) && bytes[utf16End + 1] == 0)
            {
                utf16End += 2;
            }
            if ((utf16End - offset) / 2 >= 4)
            {
                result.Add(new Rdr2RsavString(
                    offset,
                    Rdr2RsavStringEncoding.Utf16LittleEndian,
                    Encoding.Unicode.GetString(bytes.Slice(offset, utf16End - offset)),
                    FindRegion(regions, offset)));
                offset = utf16End;
                continue;
            }
            offset++;
        }
        return result;
    }

    private static int? FindRegion(IReadOnlyList<Rdr2RsavRegion> regions, int offset)
    {
        foreach (var region in regions)
        {
            if (offset >= region.DataOffset && offset < region.DataOffset + region.DataLength)
            {
                return region.Index;
            }
        }
        return null;
    }

    private static bool IsPrintableAscii(byte value) => value is >= 0x20 and <= 0x7E;

}

public readonly record struct Rdr2RsavRegion(int Index, int DataOffset, int DataLength, int CheckOffset);

public readonly record struct Rdr2RsavReference(
    int Offset,
    Rdr2RsavEndianness Endianness,
    string Hash,
    string Namespace,
    string Name,
    int? RegionIndex);

/// <summary>
/// A confirmed four-character container marker. A marker names a container,
/// not an interpreted game-state field; PSIN for example is still opaque.
/// </summary>
public readonly record struct Rdr2RsavTag(
    int Offset,
    string Value,
    int? RegionIndex);

/// <summary>
/// The verified inner framing of one CHKS-protected region. Part names are
/// container markers only; their payload schemas have not been inferred.
/// </summary>
public sealed record Rdr2RsavFramedSection(
    int RegionIndex,
    bool HasExpectedFrame,
    IReadOnlyList<Rdr2RsavFramedPart> Parts);

public readonly record struct Rdr2RsavFramedPart(
    string Marker,
    int Offset,
    int Length,
    string PreviewHex,
    string SuffixHex);

public readonly record struct Rdr2RsavString(
    int Offset,
    Rdr2RsavStringEncoding Encoding,
    string Value,
    int? RegionIndex);

public sealed record Rdr2RsavContentReport(
    string SaveTitle,
    int DecodedLength,
    IReadOnlyList<Rdr2RsavRegion> Regions,
    IReadOnlyList<Rdr2RsavTag> Tags,
    IReadOnlyList<Rdr2RsavFramedSection> Sections,
    IReadOnlyList<Rdr2PsoFrame> PsoFrames,
    IReadOnlyList<Rdr2RsavReference> References,
    IReadOnlyList<Rdr2RsavString> Strings,
    IReadOnlyList<Rdr2RsavEnvelopeGap> EnvelopeGaps,
    string DecodedSuffixHex);

public readonly record struct Rdr2RsavEnvelopeGap(
    int BeforeRegionIndex,
    int AfterRegionIndex,
    int AbsoluteOffset,
    int Length,
    string PreviewHex);

public enum Rdr2RsavEndianness
{
    LittleEndian,
    BigEndian
}

public enum Rdr2RsavStringEncoding
{
    Ascii,
    Utf16LittleEndian
}

public sealed class Rdr2RsavContentException : Exception
{
    public Rdr2RsavContentException(string message) : base(message)
    {
    }
}
