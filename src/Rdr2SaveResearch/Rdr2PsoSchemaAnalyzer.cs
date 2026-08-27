using System.Buffers.Binary;

namespace Rdr2SaveResearch.Persistence;

/// <summary>
/// Reads the self-describing PSO layout embedded in an RSAV checksum region.
/// It intentionally stops at schema/block discovery: pointer traversal and
/// field mutation require separately validated type semantics.
/// </summary>
public static class Rdr2PsoSchemaAnalyzer
{
    private static readonly IReadOnlyDictionary<uint, string> SemanticLabels =
        new[]
        {
            "mission", "missions", "completed", "completion", "progress",
            "chapter", "unlock", "unlocked", "weapon", "weapons", "money",
            "inventory", "horse", "bonding"
        }
        .ToDictionary(static value => RageJoaat(value), static value => value);
    private static readonly IReadOnlyDictionary<uint, string> KnownMissionScripts =
        new[] { "fud1" }
            .ToDictionary(static value => RageJoaat(value), static value => value);

    public static IReadOnlyList<Rdr2PsoFrame> Analyze(
        ReadOnlySpan<byte> decoded,
        IReadOnlyList<Rdr2RsavFramedSection> sections)
    {
        var frames = new List<Rdr2PsoFrame>();
        foreach (var section in sections)
        {
            if (!section.HasExpectedFrame)
            {
                continue;
            }
            try
            {
                frames.Add(ParseFrame(decoded, section));
            }
            catch (Rdr2PsoSchemaException exception)
            {
                frames.Add(new Rdr2PsoFrame(
                    section.RegionIndex,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    exception.Message));
            }
        }
        return frames;
    }

    private static Rdr2PsoFrame ParseFrame(
        ReadOnlySpan<byte> bytes,
        Rdr2RsavFramedSection section)
    {
        var parts = section.Parts.ToDictionary(static part => part.Marker);
        var data = RequirePart(parts, "PSIN");
        var map = RequirePart(parts, "PMAP");
        var schema = RequirePart(parts, "PSCH");
        ValidateSection(bytes, data, "PSIN");
        ValidateSection(bytes, map, "PMAP");
        ValidateSection(bytes, schema, "PSCH");
        IReadOnlyList<Rdr2PsoBlock>? mappings = null;
        IReadOnlyList<Rdr2PsoStructure>? structures = null;
        IReadOnlyList<Rdr2PsoLocatedField>? semanticFields = null;
        IReadOnlyList<Rdr2PsoMissionRecord>? missionRecords = null;
        IReadOnlyList<Rdr2PsoUnmappedRange>? unmappedRanges = null;
        var errors = new List<string>();
        try
        {
            mappings = ParseMappings(bytes, data, map);
        }
        catch (Rdr2PsoSchemaException exception)
        {
            errors.Add(exception.Message);
        }
        try
        {
            structures = ParseStructures(bytes, schema);
        }
        catch (Rdr2PsoSchemaException exception)
        {
            errors.Add(exception.Message);
        }
        if (mappings is not null && structures is not null)
        {
            unmappedRanges = LocateUnmappedRanges(bytes, data, mappings);
            semanticFields = LocateSemanticFields(bytes, mappings, structures);
            missionRecords = LocateMissionRecords(
                bytes,
                mappings,
                structures,
                semanticFields);
        }
        return new Rdr2PsoFrame(
            section.RegionIndex,
            new Rdr2PsoDataInfo(data.Offset + 8, data.Length - 8),
            mappings,
            structures,
            semanticFields,
            missionRecords,
            unmappedRanges,
            errors.Count == 0 ? null : string.Join(" ", errors));
    }

    /// <summary>
    /// PMAP does not cover every byte in PSIN. These ranges often contain
    /// allocator or runtime-reference metadata, not reusable capacity. They
    /// are reported separately so a writer can never expand a mapped block
    /// merely because its next PMAP neighbor starts later.
    /// </summary>
    private static IReadOnlyList<Rdr2PsoUnmappedRange> LocateUnmappedRanges(
        ReadOnlySpan<byte> bytes,
        Rdr2RsavFramedPart data,
        IReadOnlyList<Rdr2PsoBlock> blocks)
    {
        var dataStart = data.Offset + 8;
        var dataLength = data.Length - 8;
        var mapped = blocks
            .Where(static block => block.DataOffset >= 0 && block.Length >= 0)
            .OrderBy(static block => block.DataOffset)
            .ToArray();
        var result = new List<Rdr2PsoUnmappedRange>();
        var cursor = 0;
        foreach (var block in mapped)
        {
            if (block.DataOffset > cursor)
            {
                result.Add(CreateUnmappedRange(
                    bytes,
                    dataStart,
                    dataLength,
                    cursor,
                    block.DataOffset - cursor));
            }
            cursor = Math.Max(cursor, checked(block.DataOffset + block.Length));
        }
        if (cursor < dataLength)
        {
            result.Add(CreateUnmappedRange(
                bytes,
                dataStart,
                dataLength,
                cursor,
                dataLength - cursor));
        }
        return result;
    }

    private static Rdr2PsoUnmappedRange CreateUnmappedRange(
        ReadOnlySpan<byte> bytes,
        int dataStart,
        int dataLength,
        int dataOffset,
        int length)
    {
        if (length <= 0 || dataOffset < 0 || dataOffset > dataLength - length)
        {
            throw new Rdr2PsoSchemaException("PMAP has an invalid unmapped-data range.");
        }
        return new Rdr2PsoUnmappedRange(
            dataOffset,
            dataStart + dataOffset,
            length,
            Convert.ToHexString(bytes.Slice(dataStart + dataOffset, Math.Min(length, 32))));
    }

    private static IReadOnlyList<Rdr2PsoMissionRecord> LocateMissionRecords(
        ReadOnlySpan<byte> bytes,
        IReadOnlyList<Rdr2PsoBlock> blocks,
        IReadOnlyList<Rdr2PsoStructure> structures,
        IReadOnlyList<Rdr2PsoLocatedField> semanticFields)
    {
        var schemas = structures.ToDictionary(static structure => structure.NameHash);
        var result = new List<Rdr2PsoMissionRecord>();
        foreach (var descriptor in semanticFields.Where(static field =>
                     field.SemanticLabel == "missions" && field.DataType == 0x0D))
        {
            if (!schemas.TryGetValue(descriptor.StructureHash, out var owner) ||
                descriptor.InlineLength < 8)
            {
                continue;
            }
            var missionField = owner.Fields.SingleOrDefault(field =>
                field.NameHash == descriptor.FieldHash);
            var elementFieldIndex = (int)(missionField.ReferenceKey & 0xFFFF);
            if (missionField.SemanticLabel != "missions" ||
                missionField.Subtype != 0 ||
                elementFieldIndex < 0 || elementFieldIndex >= owner.Fields.Count)
            {
                continue;
            }
            var elementDescriptor = owner.Fields[elementFieldIndex];
            if (elementDescriptor.DataType != 0x0C ||
                !schemas.TryGetValue(elementDescriptor.ReferenceKey, out var recordSchema) ||
                recordSchema.Length <= 0)
            {
                continue;
            }

            var count = BinaryPrimitives.ReadUInt16BigEndian(
                bytes.Slice(descriptor.AbsoluteOffset, 2));
            var capacity = BinaryPrimitives.ReadUInt16BigEndian(
                bytes.Slice(descriptor.AbsoluteOffset + 2, 2));
            if (count == 0 || count != capacity ||
                count > int.MaxValue / recordSchema.Length)
            {
                continue;
            }
            var matchingBlocks = blocks.Where(block =>
                    block.NameHash == recordSchema.NameHash &&
                    block.Length == count * recordSchema.Length &&
                    block.AbsoluteOffset is not null)
                .ToArray();
            if (matchingBlocks.Length != 1)
            {
                continue;
            }
            var recordsBlock = matchingBlocks[0];
            var recordsOffset = recordsBlock.AbsoluteOffset!.Value;
            for (var index = 0; index < count; index++)
            {
                var recordOffset = recordsOffset + index * recordSchema.Length;
                var matchingScript = FindKnownMissionScript(
                    bytes.Slice(recordOffset, recordSchema.Length),
                    recordSchema.Fields);
                if (matchingScript is not { } script)
                {
                    continue;
                }
                var values = new List<Rdr2PsoMissionValue>();
                foreach (var field in recordSchema.Fields)
                {
                    if (field.DataOffset >= 0 && field.DataOffset < recordSchema.Length)
                    {
                        values.Add(ReadRecordField(
                            bytes,
                            recordOffset,
                            recordSchema.Length,
                            field));
                    }
                }
                result.Add(new Rdr2PsoMissionRecord(
                    recordsBlock.Index,
                    recordSchema.NameHash,
                    index,
                    script.Hash,
                    script.Name,
                    recordOffset,
                    Convert.ToHexString(bytes.Slice(recordOffset, recordSchema.Length)),
                    values));
            }
        }
        return result;
    }

    private static (uint Hash, string Name)? FindKnownMissionScript(
        ReadOnlySpan<byte> record,
        IReadOnlyList<Rdr2PsoField> fields)
    {
        foreach (var field in fields)
        {
            if (field.DataOffset < 0 || field.DataOffset > record.Length - 4)
            {
                continue;
            }
            var littleEndian = BinaryPrimitives.ReadUInt32LittleEndian(
                record.Slice(field.DataOffset, 4));
            if (KnownMissionScripts.TryGetValue(littleEndian, out var name))
            {
                return (littleEndian, name);
            }
            var bigEndian = BinaryPrimitives.ReadUInt32BigEndian(
                record.Slice(field.DataOffset, 4));
            if (KnownMissionScripts.TryGetValue(bigEndian, out name))
            {
                return (bigEndian, name);
            }
        }
        return null;
    }

    private static Rdr2PsoMissionValue ReadRecordField(
        ReadOnlySpan<byte> bytes,
        int recordOffset,
        int recordLength,
        Rdr2PsoField field)
    {
        var length = Math.Min(GetInlineFieldLength(field.DataType),
            recordLength - field.DataOffset);
        return new Rdr2PsoMissionValue(
            field.NameHash,
            field.DataType,
            field.DataOffset,
            length > 0
                ? Convert.ToHexString(bytes.Slice(recordOffset + field.DataOffset, length))
                : string.Empty);
    }

    private static IReadOnlyList<Rdr2PsoLocatedField> LocateSemanticFields(
        ReadOnlySpan<byte> bytes,
        IReadOnlyList<Rdr2PsoBlock> blocks,
        IReadOnlyList<Rdr2PsoStructure> structures)
    {
        var schemas = structures.ToDictionary(static structure => structure.NameHash);
        var result = new List<Rdr2PsoLocatedField>();
        foreach (var block in blocks)
        {
            if (block.AbsoluteOffset is not { } blockOffset ||
                !schemas.TryGetValue(block.NameHash, out var structure))
            {
                continue;
            }
            foreach (var field in structure.Fields.Where(static field => field.SemanticLabel is not null))
            {
                var fieldOffset = field.DataOffset;
                var fieldLength = GetInlineFieldLength(field.DataType);
                if (fieldOffset < 0 || fieldLength <= 0 ||
                    fieldOffset > block.Length - fieldLength)
                {
                    continue;
                }
                var absoluteOffset = blockOffset + fieldOffset;
                result.Add(new Rdr2PsoLocatedField(
                    block.Index,
                    block.NameHash,
                    field.NameHash,
                    field.SemanticLabel!,
                    field.DataType,
                    absoluteOffset,
                    fieldLength,
                    Convert.ToHexString(bytes.Slice(absoluteOffset, fieldLength))));
            }
        }
        return result;
    }

    private static int GetInlineFieldLength(byte dataType) => dataType switch
    {
        0x00 or 0x01 or 0x02 => 1,
        0x03 or 0x04 or 0x1E => 2,
        0x05 or 0x06 or 0x07 or 0x09 or 0x0E or 0x0F => 4,
        0x08 => 8,
        0x0A => 16,
        // Strings, structure pointers, arrays and maps have a two-word
        // inline descriptor; its pointed-to data is deliberately not copied.
        0x0B or 0x0C or 0x0D or 0x10 => 8,
        0x15 => 12,
        0x20 => 8,
        _ => 0
    };

    private static Rdr2RsavFramedPart RequirePart(
        IReadOnlyDictionary<string, Rdr2RsavFramedPart> parts,
        string marker) =>
        parts.TryGetValue(marker, out var part)
            ? part
            : throw new Rdr2PsoSchemaException($"The framed PSO region has no {marker} part.");

    private static void ValidateSection(
        ReadOnlySpan<byte> bytes,
        Rdr2RsavFramedPart part,
        string marker)
    {
        if (part.Length < 8 || part.Offset < 0 || part.Offset > bytes.Length - part.Length ||
            !bytes.Slice(part.Offset, 4).SequenceEqual(System.Text.Encoding.ASCII.GetBytes(marker)) ||
            ReadInt32(bytes, part.Offset + 4) != part.Length)
        {
            throw new Rdr2PsoSchemaException(
                $"{marker} has an invalid PSO section header or declared length.");
        }
    }

    private static IReadOnlyList<Rdr2PsoBlock> ParseMappings(
        ReadOnlySpan<byte> bytes,
        Rdr2RsavFramedPart data,
        Rdr2RsavFramedPart map)
    {
        if (map.Length < 24)
        {
            throw new Rdr2PsoSchemaException("PMAP is smaller than its header.");
        }
        var rootIndex = ReadInt32(bytes, map.Offset + 8);
        var count = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(map.Offset + 16, 2));
        if (24L + count * 16L != map.Length)
        {
            throw new Rdr2PsoSchemaException(
                $"PMAP entry count {count} does not match section length {map.Length}; " +
                $"header={Convert.ToHexString(bytes.Slice(map.Offset, Math.Min(map.Length, 96)))}.");
        }
        if (rootIndex < 1 || rootIndex > count)
        {
            throw new Rdr2PsoSchemaException("PMAP root index is outside its entry list.");
        }

        var result = new List<Rdr2PsoBlock>(count);
        var dataStart = data.Offset + 8;
        var dataLength = data.Length - 8;
        for (var index = 0; index < count; index++)
        {
            var offset = map.Offset + 24 + index * 16;
            var nameHash = ReadUInt32(bytes, offset);
            var dataOffset = ReadInt32(bytes, offset + 4);
            var unknown = ReadUInt32(bytes, offset + 8);
            var length = ReadInt32(bytes, offset + 12);
            var absoluteOffset = dataOffset >= 0 && length >= 0 &&
                dataOffset <= dataLength && length <= dataLength - dataOffset
                ? dataStart + dataOffset
                : (int?)null;
            result.Add(new Rdr2PsoBlock(
                index + 1,
                nameHash,
                dataOffset,
                length,
                unknown,
                absoluteOffset,
                index + 1 == rootIndex,
                absoluteOffset is { } payloadOffset
                    ? Convert.ToHexString(bytes.Slice(payloadOffset, Math.Min(length, 32)))
                    : string.Empty,
                absoluteOffset is { } suffixOffset
                    ? Convert.ToHexString(bytes.Slice(
                        suffixOffset + Math.Max(0, length - 32),
                        Math.Min(length, 32)))
                    : string.Empty));
        }
        return result;
    }

    private static IReadOnlyList<Rdr2PsoStructure> ParseStructures(
        ReadOnlySpan<byte> bytes,
        Rdr2RsavFramedPart schema)
    {
        if (schema.Length < 12)
        {
            throw new Rdr2PsoSchemaException("PSCH is smaller than its header.");
        }
        var count = ReadInt32(bytes, schema.Offset + 8);
        if (count < 0 || 12L + count * 8L > schema.Length)
        {
            throw new Rdr2PsoSchemaException("PSCH entry index is outside the section.");
        }

        var result = new List<Rdr2PsoStructure>();
        for (var index = 0; index < count; index++)
        {
            var indexOffset = schema.Offset + 12 + index * 8;
            var nameHash = ReadUInt32(bytes, indexOffset);
            var definitionRelativeOffset = ReadInt32(bytes, indexOffset + 4);
            if (definitionRelativeOffset < 0 || definitionRelativeOffset > schema.Length - 12)
            {
                throw new Rdr2PsoSchemaException("PSCH definition points outside the section.");
            }
            var definitionOffset = schema.Offset + definitionRelativeOffset;
            var descriptor = ReadUInt32(bytes, definitionOffset);
            var type = (byte)(descriptor >> 24);
            if (type != 0)
            {
                continue; // Enum definitions have no writable structure fields.
            }
            var fieldCount = (ushort)descriptor;
            if (definitionOffset > schema.Offset + schema.Length - 12L - fieldCount * 12L)
            {
                throw new Rdr2PsoSchemaException("PSCH structure field list is outside the section.");
            }
            var structureLength = ReadInt32(bytes, definitionOffset + 4);
            var fields = new List<Rdr2PsoField>(fieldCount);
            for (var fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
            {
                var fieldOffset = definitionOffset + 12 + fieldIndex * 12;
                var fieldHash = ReadUInt32(bytes, fieldOffset);
                var dataType = bytes[fieldOffset + 4];
                var subtype = bytes[fieldOffset + 5];
                var dataOffset = BinaryPrimitives.ReadInt16BigEndian(
                    bytes.Slice(fieldOffset + 6, 2));
                var referenceKey = ReadUInt32(bytes, fieldOffset + 8);
                fields.Add(new Rdr2PsoField(
                    fieldHash,
                    SemanticLabels.GetValueOrDefault(fieldHash),
                    dataType,
                    subtype,
                    dataOffset,
                    referenceKey));
            }
            result.Add(new Rdr2PsoStructure(nameHash, structureLength, fields));
        }
        return result;
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(offset, 4));

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));

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
}

public sealed record Rdr2PsoFrame(
    int RegionIndex,
    Rdr2PsoDataInfo? Data,
    IReadOnlyList<Rdr2PsoBlock>? Blocks,
    IReadOnlyList<Rdr2PsoStructure>? Structures,
    IReadOnlyList<Rdr2PsoLocatedField>? SemanticFields,
    IReadOnlyList<Rdr2PsoMissionRecord>? MissionRecords,
    IReadOnlyList<Rdr2PsoUnmappedRange>? UnmappedRanges,
    string? ParseError);

public readonly record struct Rdr2PsoDataInfo(int PayloadOffset, int PayloadLength);

public readonly record struct Rdr2PsoBlock(
    int Index,
    uint NameHash,
    int DataOffset,
    int Length,
    uint Unknown,
    int? AbsoluteOffset,
    bool IsRoot,
    string PreviewHex,
    string SuffixHex);

public readonly record struct Rdr2PsoUnmappedRange(
    int DataOffset,
    int AbsoluteOffset,
    int Length,
    string PreviewHex);

public sealed record Rdr2PsoStructure(
    uint NameHash,
    int Length,
    IReadOnlyList<Rdr2PsoField> Fields);

public readonly record struct Rdr2PsoField(
    uint NameHash,
    string? SemanticLabel,
    byte DataType,
    byte Subtype,
    short DataOffset,
    uint ReferenceKey);

public readonly record struct Rdr2PsoLocatedField(
    int BlockIndex,
    uint StructureHash,
    uint FieldHash,
    string SemanticLabel,
    byte DataType,
    int AbsoluteOffset,
    int InlineLength,
    string InlineValueHex);

public sealed record Rdr2PsoMissionRecord(
    int BlockIndex,
    uint RecordStructureHash,
    int RecordIndex,
    uint MissionScriptHash,
    string MissionScript,
    int AbsoluteOffset,
    string RawHex,
    IReadOnlyList<Rdr2PsoMissionValue> Fields);

public readonly record struct Rdr2PsoMissionValue(
    uint FieldHash,
    byte DataType,
    int Offset,
    string ValueHex);

public sealed class Rdr2PsoSchemaException : Exception
{
    public Rdr2PsoSchemaException(string message) : base(message)
    {
    }
}
