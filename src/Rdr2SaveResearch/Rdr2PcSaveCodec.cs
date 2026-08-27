using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Rdr2SaveResearch.Persistence;

/// <summary>
/// Versioned container codec for PC Story Mode SRDR saves. The body is AES-256
/// ECB encrypted after the plaintext title/date header. This class only owns
/// the container and integrity records; game-state records remain opaque until
/// they have an independently verified schema.
/// </summary>
public static class Rdr2PcSaveCodec
{
    public const int EncryptedPayloadOffset = 0x110;
    private static readonly byte[] PcKey =
    [
        0x46, 0xED, 0x8D, 0x3F, 0x94, 0x35, 0xE4, 0xEC,
        0x12, 0x2C, 0xB2, 0xE2, 0xAF, 0x97, 0xC5, 0x7E,
        0x4C, 0x5A, 0x8C, 0x30, 0x92, 0xC7, 0x84, 0x4E,
        0x11, 0xC6, 0x86, 0xFF, 0x41, 0xDF, 0x41, 0x0F
    ];
    private static ReadOnlySpan<byte> RsavMagic => "RSAV"u8;
    private static ReadOnlySpan<byte> ChksMagic => "CHKS"u8;

    public static Rdr2PcSaveDocument Decode(ReadOnlySpan<byte> encrypted)
    {
        ValidateEnvelope(encrypted);
        var decoded = encrypted.ToArray();
        TransformInPlace(decoded.AsSpan(EncryptedPayloadOffset), decrypt: true);
        if (!decoded.AsSpan(EncryptedPayloadOffset, RsavMagic.Length)
            .SequenceEqual(RsavMagic))
        {
            throw new Rdr2PcSaveCodecException(
                "The save did not decrypt to an RSAV PC RDR2 container.");
        }
        return new Rdr2PcSaveDocument(decoded, ReadTitle(decoded));
    }

    public static byte[] Encode(Rdr2PcSaveDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var decoded = document.CopyDecodedBytes();
        ValidateDecoded(decoded);
        UpdateDateChecksum(decoded);
        foreach (var check in FindChecks(decoded))
        {
            var dataSize = BinaryPrimitives.ReadUInt32BigEndian(
                decoded.AsSpan(check.Offset + 8, 4));
            decoded.AsSpan(check.Offset + 8, 8).Clear();
            var checksum = ComputeJooat(
                decoded.AsSpan(check.DataOffset, check.DataLength));
            BinaryPrimitives.WriteUInt32BigEndian(
                decoded.AsSpan(check.Offset + 8, 4), dataSize);
            BinaryPrimitives.WriteUInt32BigEndian(
                decoded.AsSpan(check.Offset + 12, 4), checksum);
        }
        TransformInPlace(decoded.AsSpan(EncryptedPayloadOffset), decrypt: false);
        return decoded;
    }

    public static Rdr2PcSaveCodecVerification VerifyRoundTrip(ReadOnlySpan<byte> encrypted)
    {
        var document = Decode(encrypted);
        var rebuilt = Encode(document);
        return new Rdr2PcSaveCodecVerification(
            document.Title,
            document.Checks.Count,
            encrypted.SequenceEqual(rebuilt),
            Convert.ToHexString(SHA256.HashData(encrypted)),
            Convert.ToHexString(SHA256.HashData(rebuilt)));
    }

    private static void ValidateEnvelope(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length <= EncryptedPayloadOffset ||
            (bytes.Length - EncryptedPayloadOffset) % 16 != 0 ||
            !bytes[..4].SequenceEqual(new byte[] { 0, 0, 0, 4 }))
        {
            throw new Rdr2PcSaveCodecException("Not a supported PC SRDR save envelope.");
        }
    }

    private static void ValidateDecoded(ReadOnlySpan<byte> bytes)
    {
        ValidateEnvelope(bytes);
        if (!bytes.Slice(EncryptedPayloadOffset, RsavMagic.Length).SequenceEqual(RsavMagic))
        {
            throw new Rdr2PcSaveCodecException("Decoded save has no RSAV container.");
        }
    }

    private static void TransformInPlace(Span<byte> bytes, bool decrypt)
    {
        using var aes = Aes.Create();
        aes.Key = PcKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var transform = decrypt ? aes.CreateDecryptor() : aes.CreateEncryptor();
        var transformed = transform.TransformFinalBlock(bytes.ToArray(), 0, bytes.Length);
        transformed.CopyTo(bytes);
    }

    private static string ReadTitle(ReadOnlySpan<byte> bytes)
    {
        var titleBytes = bytes.Slice(4, 0x100);
        var terminator = titleBytes.IndexOf("\0\0"u8);
        if (terminator < 0)
        {
            terminator = titleBytes.Length;
        }
        terminator &= ~1;
        return Encoding.Unicode.GetString(titleBytes[..terminator]);
    }

    private static void UpdateDateChecksum(Span<byte> bytes)
    {
        Span<byte> iv = stackalloc byte[4];
        bytes[..4].CopyTo(iv);
        iv.Reverse();
        Span<byte> date = stackalloc byte[8];
        bytes.Slice(0x104, 8).CopyTo(date);
        date[..4].Reverse();
        date[4..].Reverse();
        var jooat = new Jooat(0);
        jooat.Update(iv);
        jooat.FinalizeHash();
        jooat.Update(date);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.Slice(0x10C, 4), jooat.FinalizeHash());
    }

    internal static IReadOnlyList<Rdr2PcSaveCheck> FindChecks(ReadOnlySpan<byte> bytes)
    {
        var checks = new List<Rdr2PcSaveCheck>();
        for (var offset = EncryptedPayloadOffset;
             offset <= bytes.Length - 20;
             offset++)
        {
            if (!bytes.Slice(offset, 4).SequenceEqual(ChksMagic) ||
                BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 4, 4)) != 0x14)
            {
                continue;
            }
            var dataLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8, 4));
            if (dataLength > int.MaxValue)
            {
                throw new Rdr2PcSaveCodecException("CHKS data length exceeds supported limits.");
            }
            var dataOffset = checked(offset - (int)dataLength + 0x14);
            if (dataOffset < EncryptedPayloadOffset || dataOffset > offset)
            {
                throw new Rdr2PcSaveCodecException("CHKS points outside the decrypted payload.");
            }
            checks.Add(new Rdr2PcSaveCheck(offset, dataOffset, (int)dataLength));
            offset += 0x13;
        }
        return checks;
    }

    private static uint ComputeJooat(ReadOnlySpan<byte> bytes, uint initial = 0x3FAC7125)
    {
        var jooat = new Jooat(initial);
        jooat.Update(bytes);
        return jooat.FinalizeHash();
    }

    private sealed class Jooat(uint initial)
    {
        private uint _hash = initial;

        public void Update(ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes)
            {
                _hash += unchecked((uint)(sbyte)value);
                _hash += _hash << 10;
                _hash ^= _hash >> 6;
            }
        }

        public uint FinalizeHash()
        {
            _hash += _hash << 3;
            _hash ^= _hash >> 11;
            _hash += _hash << 15;
            return _hash;
        }
    }
}

public sealed class Rdr2PcSaveDocument
{
    private readonly byte[] _decodedBytes;

    internal Rdr2PcSaveDocument(byte[] decodedBytes, string title)
    {
        _decodedBytes = decodedBytes;
        Title = title;
        Checks = GetChecks(decodedBytes);
    }

    public string Title { get; }
    public int Length => _decodedBytes.Length;
    public IReadOnlyList<Rdr2PcSaveCheck> Checks { get; }

    internal byte[] CopyDecodedBytes() => (byte[])_decodedBytes.Clone();

    private static IReadOnlyList<Rdr2PcSaveCheck> GetChecks(byte[] bytes)
    {
        // Keeping discovery encapsulated allows later semantic readers to use
        // the same validated document without exposing mutable raw data.
        return Rdr2PcSaveCodec.FindChecks(bytes);
    }
}

public readonly record struct Rdr2PcSaveCheck(int Offset, int DataOffset, int DataLength);

public readonly record struct Rdr2PcSaveCodecVerification(
    string Title,
    int CheckCount,
    bool IsExactRoundTrip,
    string InputSha256,
    string OutputSha256);

public sealed class Rdr2PcSaveCodecException : Exception
{
    public Rdr2PcSaveCodecException(string message) : base(message)
    {
    }
}
