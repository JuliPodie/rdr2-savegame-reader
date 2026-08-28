using System.Security.Cryptography;
using System.Buffers.Binary;
using Rdr2SaveResearch.Persistence;

namespace Rdr2SaveResearch.SelfTest;

internal static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            var encrypted = Encrypt(MinimalDecoded());
            var document = Rdr2PcSaveCodec.Decode(encrypted);
            var rebuilt = Rdr2PcSaveCodec.Encode(document);
            Assert(Rdr2PcSaveCodec.VerifyRoundTrip(rebuilt).IsExactRoundTrip, "codec round trip");
            Assert(Rdr2RsavContentAnalyzer.Analyze(document).Tags.Any(static tag => tag.Value == "RSAV"), "RSAV tag");

            var changed = Rdr2PcSaveCodec.Decode(Encrypt(WithChange(MinimalDecoded())));
            var diff = Rdr2RsavContentDiffer.Compare(document, changed);
            Assert(diff.BeforeDecodedLength == diff.AfterDecodedLength, "diff length");

            var root = Path.Combine(Path.GetTempPath(), "Rdr2SaveResearch.SelfTest", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var before = Path.Combine(root, "before.sav");
                var after = Path.Combine(root, "after.sav");
                var recipient = Path.Combine(root, "recipient.sav");
                var output = Path.Combine(root, "clone.sav");
                var nativeHeader = Path.Combine(root, "natives.h");
                await File.WriteAllBytesAsync(before, encrypted);
                await File.WriteAllBytesAsync(after, Rdr2PcSaveCodec.Encode(changed));
                await File.WriteAllBytesAsync(recipient, encrypted);
                var clone = await CampaignCloneExperiment.CreateAsync(before, after, recipient, output, CampaignCloneExperiment.ConfirmationPhrase);
                Assert(clone.RecipientBeforeExactlyMatchesHostBefore && File.Exists(output), "new-file clone");
                await File.WriteAllTextAsync(nativeHeader, """
                    namespace PLAYER
                    {
                        NATIVE_DECL Ped PLAYER_PED_ID() { return invoke<Ped>(0xD80958FC74E988A6); }
                    }
                    """);
                var nativeCatalog = await NativeHeaderCatalogExtractor.ExtractAsync(nativeHeader);
                Assert(nativeCatalog.Entries.Count == 1 && nativeCatalog.Entries[0].Name == "PLAYER_PED_ID" && nativeCatalog.Entries[0].Hash == "D80958FC74E988A6", "native header catalog");
                var nativeDocument = Rdr2PcSaveCodec.Decode(Encrypt(WithNativeHash(MinimalDecoded())));
                var nativeReport = Rdr2RsavContentAnalyzer.Analyze(nativeDocument, nativeCatalog.Entries);
                Assert(nativeReport.References.Single().Name == "PLAYER_PED_ID", "catalog-driven native reference");
            }
            finally { Directory.Delete(root, recursive: true); }

            Console.WriteLine("SELFTEST total=6 passed=6 failed=0");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SELFTEST FAILED: {exception.Message}");
            return 1;
        }
    }

    private static byte[] MinimalDecoded()
    {
        var bytes = new byte[0x120];
        bytes[3] = 4;
        "RSAV"u8.CopyTo(bytes.AsSpan(Rdr2PcSaveCodec.EncryptedPayloadOffset));
        return bytes;
    }

    private static byte[] WithChange(byte[] bytes) { bytes[0x11F] = 1; return bytes; }

    private static byte[] WithNativeHash(byte[] bytes)
    {
        Array.Resize(ref bytes, 0x130);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x120, 8), 0xD80958FC74E988A6);
        return bytes;
    }

    private static byte[] Encrypt(byte[] decoded)
    {
        var encrypted = (byte[])decoded.Clone();
        using var aes = Aes.Create();
        aes.Key = [0x46, 0xED, 0x8D, 0x3F, 0x94, 0x35, 0xE4, 0xEC, 0x12, 0x2C, 0xB2, 0xE2, 0xAF, 0x97, 0xC5, 0x7E, 0x4C, 0x5A, 0x8C, 0x30, 0x92, 0xC7, 0x84, 0x4E, 0x11, 0xC6, 0x86, 0xFF, 0x41, 0xDF, 0x41, 0x0F];
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var transform = aes.CreateEncryptor();
        transform.TransformFinalBlock(encrypted, Rdr2PcSaveCodec.EncryptedPayloadOffset, encrypted.Length - Rdr2PcSaveCodec.EncryptedPayloadOffset).CopyTo(encrypted, Rdr2PcSaveCodec.EncryptedPayloadOffset);
        return encrypted;
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException($"Assertion failed: {description}");
    }
}
