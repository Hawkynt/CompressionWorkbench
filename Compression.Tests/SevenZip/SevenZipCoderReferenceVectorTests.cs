using System.Reflection;
using System.Security.Cryptography;
using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

/// <summary>
/// Archives 7-Zip 26.03, 7-Zip-zstd 26.02 and py7zr 1.1.0 wrote with the coders our reader used to
/// refuse — Deflate64, the ARM64 and RISC-V branch filters, and 7-Zip-zstd's Zstandard / Brotli / LZ4
/// (single- and multi-threaded framing). Provenance: ReferenceVectors/make_vectors.py. The member's
/// size and SHA-256 come from the generator's manifest, so a mis-decoded byte anywhere fails.
/// </summary>
[TestFixture]
public class SevenZipCoderReferenceVectorTests {

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"SevenZipVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded 7z vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static Dictionary<string, (long Size, string Sha256)> Manifest()
    => System.Text.Encoding.ASCII.GetString(Resource("manifest.txt"))
      .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .Select(l => l.Split(' '))
      .ToDictionary(p => p[0], p => (long.Parse(p[1]), p[2]));

  [TestCase("deflate64.7z", "payload.bin", "Deflate64")]
  [TestCase("arm64-lzma2.7z", "payload.bin", "LZMA2")]
  [TestCase("riscv-lzma2.7z", "payload.bin", "LZMA2")]
  [TestCase("zstd.7z", "payload.bin", "Zstd")]
  [TestCase("zstd-mt.7z", "big.bin", "Zstd")]
  [TestCase("brotli.7z", "payload.bin", "Brotli")]
  [TestCase("brotli-mt.7z", "big.bin", "Brotli")]
  [TestCase("brotli-py7zr.7z", "payload.bin", "Brotli")]
  [TestCase("lz4.7z", "payload.bin", "LZ4")]
  [TestCase("lz4-mt.7z", "big.bin", "LZ4")]
  [Category("ThirdPartyVector")]
  public void Given_ReferenceArchive_When_Extracted_Then_MemberMatchesManifest(string vector, string member, string method) {
    var expected = Manifest()[member];
    using var reader = new SevenZipReader(new MemoryStream(Resource(vector)));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Name, Is.EqualTo(member));
    Assert.That(reader.Entries[0].Method, Is.EqualTo(method));
    var data = reader.Extract(0);
    Assert.That(data.LongLength, Is.EqualTo(expected.Size));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(expected.Sha256));
  }

  [Test, Category("EdgeCase")]
  public void Given_BrotliChunkHeaderOverrunningTheStream_When_Decoded_Then_InvalidData() {
    byte[] header = [0x50, 0x2A, 0x4D, 0x18, 8, 0, 0, 0, 0xFF, 0xFF, 0, 0, 0x42, 0x52, 0, 0, 1, 2, 3];
    Assert.Throws<InvalidDataException>(() => SevenZipZstdFamilyCoders.DecodeBrotli(header));
  }

  [Test, Category("EdgeCase")]
  public void Given_BrotliChunkWithoutBrMarker_When_Decoded_Then_InvalidData() {
    byte[] header = [0x50, 0x2A, 0x4D, 0x18, 8, 0, 0, 0, 1, 0, 0, 0, 0x00, 0x00, 0, 0, 0x06];
    Assert.Throws<InvalidDataException>(() => SevenZipZstdFamilyCoders.DecodeBrotli(header));
  }

  [Test, Category("EdgeCase")]
  public void Given_Lz4StreamThatIsNoFrame_When_Decoded_Then_InvalidData() {
    byte[] garbage = [0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3, 4];
    Assert.Throws<InvalidDataException>(() => SevenZipZstdFamilyCoders.DecodeLz4(garbage));
  }

  [Test, Category("EdgeCase")]
  public void Given_Lz4ChunkHeaderTruncated_When_Decoded_Then_InvalidData() {
    byte[] header = [0x50, 0x2A, 0x4D, 0x18, 4, 0, 0, 0, 0x10];
    Assert.Throws<InvalidDataException>(() => SevenZipZstdFamilyCoders.DecodeLz4(header));
  }

  [Test, Category("EdgeCase")]
  public void Given_EmptyLz4Stream_When_Decoded_Then_Empty() {
    Assert.That(SevenZipZstdFamilyCoders.DecodeLz4([]), Is.Empty);
  }
}
