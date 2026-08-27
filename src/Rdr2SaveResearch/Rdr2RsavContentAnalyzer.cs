using System.Buffers.Binary;
using System.Text;

namespace Rdr2SaveResearch.Persistence;

/// <summary>
/// Read-only RSAV content indexer. It recognizes only catalogued hashes and
/// literal strings; every other value remains opaque until a field schema is
/// proven from controlled saves.
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
    private static readonly IReadOnlyDictionary<uint, Rdr2KnownReference> Known =
        CreateKnownReferences();

    public static Rdr2RsavContentReport Analyze(Rdr2PcSaveDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return AnalyzeDecoded(document.CopyDecodedBytes(), document.Title, document.Checks);
    }

    internal static Rdr2RsavContentReport AnalyzeDecoded(
        ReadOnlySpan<byte> decoded,
        string title,
        IReadOnlyList<Rdr2PcSaveCheck> checks)
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
        var references = ScanKnownReferences(decoded, regions);
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

    private static IReadOnlyList<Rdr2RsavReference> ScanKnownReferences(
        ReadOnlySpan<byte> bytes,
        IReadOnlyList<Rdr2RsavRegion> regions)
    {
        var references = new List<Rdr2RsavReference>();
        for (var offset = Rdr2PcSaveCodec.EncryptedPayloadOffset;
             offset <= bytes.Length - 4;
             offset++)
        {
            var littleEndian = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
            var bigEndian = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            AddReference(littleEndian, Rdr2RsavEndianness.LittleEndian);
            if (bigEndian != littleEndian)
            {
                AddReference(bigEndian, Rdr2RsavEndianness.BigEndian);
            }

            void AddReference(uint value, Rdr2RsavEndianness endianness)
            {
                if (Known.TryGetValue(value, out var known))
                {
                    references.Add(new Rdr2RsavReference(
                        offset,
                        endianness,
                        value,
                        known.Name,
                        known.Category,
                        FindRegion(regions, offset)));
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

    private static IReadOnlyDictionary<uint, Rdr2KnownReference> CreateKnownReferences()
    {
        var references = new List<Rdr2KnownReference>
        {
            new(0xCE548CF5, "player_money", "economy"),
            new(0xA69B4C37, "camp_money", "economy"),
            new(0xE2AC0A03, "built_in_cheats", "progression"),
            new(0xB39E0D3C, "satchel_legend_of_the_east", "inventory"),
            new(0x00C6B33D, "player_stats", "player"),
            new(0x53303030, "player_core_stats", "player"),
            // Opaque values observed in the controlled FUD1 mission-list
            // transition. Names describe only where they were observed; their
            // engine-level meaning is not asserted yet.
            new(0x023F45B2, "observed_fud1_slot_key", "mission_observed"),
            new(0x025B7398, "observed_editor_next_slot_key", "mission_observed"),
            new(0x023F95CA, "observed_game_next_slot_key", "mission_observed")
        };
        foreach (var weapon in new[]
                 {
                     "weapon_unarmed", "weapon_lasso", "weapon_revolver_cattleman",
                     "weapon_revolver_schofield", "weapon_revolver_doubleaction",
                     "weapon_pistol_volcanic", "weapon_pistol_semiauto",
                     "weapon_repeater_carbine", "weapon_repeater_winchester",
                     "weapon_repeater_henry", "weapon_rifle_varmint",
                     "weapon_rifle_springfield", "weapon_rifle_boltaction",
                     "weapon_shotgun_doublebarrel", "weapon_shotgun_pump"
                 })
        {
            references.Add(new Rdr2KnownReference(RageJoaat(weapon), weapon, "weapon"));
        }
        // Script identifiers are hashes in many RAGE serializations. This is
        // a locator only: a hit means the script is referenced, not that it
        // has completed or that its unlocks are active.
        foreach (var mission in new[] { "fud1" })
        {
            references.Add(new Rdr2KnownReference(
                RageJoaat(mission),
                mission,
                "mission_script"));
        }
        return references.ToDictionary(static item => item.Value);
    }

    private static uint RageJoaat(string value)
    {
        uint hash = 0;
        foreach (var character in value)
        {
            var normalized = char.ToLowerInvariant(character);
            hash += normalized;
            hash += hash << 10;
            hash ^= hash >> 6;
        }
        hash += hash << 3;
        hash ^= hash >> 11;
        hash += hash << 15;
        return hash;
    }

    private readonly record struct Rdr2KnownReference(uint Value, string Name, string Category);
}

public readonly record struct Rdr2RsavRegion(int Index, int DataOffset, int DataLength, int CheckOffset);

public readonly record struct Rdr2RsavReference(
    int Offset,
    Rdr2RsavEndianness Endianness,
    uint Value,
    string Name,
    string Category,
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
