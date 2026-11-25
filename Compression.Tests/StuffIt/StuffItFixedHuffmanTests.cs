using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FileFormat.StuffIt;

namespace Compression.Tests.StuffIt;

/// <summary>
/// StuffIt method 6 (fixed Huffman). Hand-built blocks pin the layout of docs/STUFFIT-METHOD6-ON-DISK.md
/// (block sizes, translation table, the fixed code, PackBits); an archive StuffIt 1.x wrote with its
/// "Faster" setting (ObsoleteMadness/StuffIt-Go <c>samples/v1-fhf-faster.sit</c>, revision 915dc49)
/// pins it on real data: every fork must pass the CRC-16 StuffIt stored and equal the output of
/// StuffIt-Go's extractor. That archive holds Apple software, so it stays out of the repository: set
/// <c>CWB_STUFFIT_FHF_DIR</c> to a folder holding it.
/// </summary>
[TestFixture]
public class StuffItFixedHuffmanTests {

  private const string SampleName = "v1-fhf-faster.sit";
  private const string SampleSha256 = "d3f14868f8742a13d8d2cc914b59b173789c7c2f8a7bb293e8d4a3cc17d7211d";

  /// <summary>Codes of the fixed table: symbol 0..3 and the end code 256.</summary>
  private const string Sym0 = "000", Sym1 = "0010", Sym2 = "00110", Sym3 = "00111", End = "111111111111";

  /// <summary>Packs a string of '0'/'1' most-significant bit first, padding the last byte with zeros.</summary>
  private static byte[] Bits(string bits) {
    var bytes = new byte[(bits.Length + 7) / 8];
    for (var i = 0; i < bits.Length; ++i)
      if (bits[i] == '1') bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
    return bytes;
  }

  private static byte[] CodedBlock(byte[] translation, uint count, string bits) {
    var code = Bits(bits);
    var block = new byte[10 + translation.Length + code.Length];
    BinaryPrimitives.WriteInt32BigEndian(block, block.Length);
    BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(4), count);
    BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(8), (short)translation.Length);
    translation.CopyTo(block, 10);
    code.CopyTo(block, 10 + translation.Length);
    return block;
  }

  private static byte[] PackBitsBlock(params byte[] packBits) {
    var block = new byte[4 + packBits.Length];
    BinaryPrimitives.WriteInt32BigEndian(block, -block.Length);
    packBits.CopyTo(block, 4);
    return block;
  }

  private static string Decode(byte[] fork, int length) => Encoding.Latin1.GetString(StuffItFixedHuffmanDecoder.Decode(fork, length));

  [Test, Category("HappyPath")]
  public void GivenACodedBlock_WhenDecoded_ThenSymbolsMapThroughTheTableIntoPackBits() {
    // Symbols 0..3 pick table bytes 02 'H' 'i' '!': a PackBits literal of three bytes.
    var fork = CodedBlock([0x02, (byte)'H', (byte)'i', (byte)'!'], 4, Sym0 + Sym1 + Sym2 + Sym3);
    Assert.That(Decode(fork, 3), Is.EqualTo("Hi!"));
  }

  [Test, Category("HappyPath")]
  public void GivenAPackBitsOnlyBlock_WhenDecoded_ThenItIsExpandedWithoutTheCode() {
    // FD = repeat the next byte 1-(-3) = 4 times; 80 is a no-op; 00 copies one literal.
    var fork = PackBitsBlock(0xFD, (byte)'z', 0x80, 0x00, (byte)'.');
    Assert.That(Decode(fork, 5), Is.EqualTo("zzzz."));
  }

  [Test, Category("HappyPath")]
  public void GivenTheEndCode_WhenDecoded_ThenTheBlockStopsBeforeItsCount() {
    // The block announces 9 bytes but ends after 4; the next block supplies the rest.
    var fork = CodedBlock([0x02, (byte)'a', (byte)'b', (byte)'c'], 9, Sym0 + Sym1 + Sym2 + Sym3 + End)
      .Concat(PackBitsBlock(0x00, (byte)'d')).ToArray();
    Assert.That(Decode(fork, 4), Is.EqualTo("abcd"));
  }

  [Test, Category("HappyPath")]
  public void GivenABlockWithAShortTable_WhenDecoded_ThenEntriesItLeavesOutKeepTheirEarlierBytes() {
    var first = CodedBlock([0x00, (byte)'a'], 2, Sym0 + Sym1);
    var second = CodedBlock([0x00], 2, Sym0 + Sym1); // entry 1 is still 'a'
    Assert.That(Decode([.. first, .. second], 2), Is.EqualTo("aa"));
  }

  [Test, Category("Boundary")]
  public void GivenMoreBytesThanDeclared_WhenDecoded_ThenTheForkIsCutAtItsLength() =>
    Assert.That(Decode(PackBitsBlock(0x81, (byte)'q'), 3), Is.EqualTo("qqq"));

  [Test, Category("Boundary")]
  public void GivenAnEmptyFork_WhenDecoded_ThenNothingIsRead() =>
    Assert.That(StuffItFixedHuffmanDecoder.Decode([], 0), Is.Empty);

  [Test, Category("Exception")]
  public void GivenAForkThatEndsBeforeItsLength_WhenDecoded_ThenItIsRefused() =>
    Assert.That(() => StuffItFixedHuffmanDecoder.Decode(PackBitsBlock(0x00, (byte)'x'), 2), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("Exception")]
  public void GivenABlockLongerThanTheFork_WhenDecoded_ThenItIsRefused() {
    var fork = CodedBlock([0x00, (byte)'a'], 2, Sym0 + Sym1);
    BinaryPrimitives.WriteInt32BigEndian(fork, fork.Length + 1);
    Assert.That(() => StuffItFixedHuffmanDecoder.Decode(fork, 1), Throws.InstanceOf<InvalidDataException>());
  }

  [TestCase(-1)]
  [TestCase(257)]
  [Category("Exception")]
  public void GivenATableSizeOutOfRange_WhenDecoded_ThenItIsRefused(int entries) {
    var fork = CodedBlock([0x00, (byte)'a'], 2, Sym0 + Sym1);
    BinaryPrimitives.WriteInt16BigEndian(fork.AsSpan(8), (short)entries);
    Assert.That(() => StuffItFixedHuffmanDecoder.Decode(fork, 1), Throws.InstanceOf<InvalidDataException>());
  }

  [TestCase(0)]
  [TestCase(9)]
  [TestCase(-3)]
  [Category("Exception")]
  public void GivenABlockSizeTooSmallForItsHeader_WhenDecoded_ThenItIsRefused(int size) {
    var fork = new byte[16];
    BinaryPrimitives.WriteInt32BigEndian(fork, size);
    Assert.That(() => StuffItFixedHuffmanDecoder.Decode(fork, 1), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void GivenAPackBitsLiteralPastItsBlock_WhenDecoded_ThenItIsRefused() =>
    Assert.That(() => StuffItFixedHuffmanDecoder.Decode(PackBitsBlock(0x05, (byte)'x'), 6), Throws.InstanceOf<InvalidDataException>());

  /// <summary>SHA-256 of each fork as StuffIt-Go (revision 915dc49) extracts it from the sample.</summary>
  private static readonly (string Name, string Data, string Resource)[] OracleForks = [
    ("About System 7.5", "d78c0b5547fc230e734ce2fc05ae7fada9f075e601a819a9d4d0f2b33b883bef", "828a2d431f43b6d0d01ea3307972a75e24a8e27683a505c69d6c9448b6b384e7"),
    ("SimpleText", "", "e723b7dba9c45f352e366898dc3b9d377f6b7531a7a365c6e51b40368af79ced"),
  ];

  [Test, Category("ThemVsUs")]
  public void GivenTheArchiveStuffIt1WroteWithFaster_WhenItsForksAreExtracted_ThenTheyPassTheirCrcsAndEqualTheOracle() {
    var dir = Environment.GetEnvironmentVariable("CWB_STUFFIT_FHF_DIR");
    if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, SampleName))) {
      Assert.Ignore("CWB_STUFFIT_FHF_DIR does not hold v1-fhf-faster.sit.");
      return;
    }

    var bytes = File.ReadAllBytes(Path.Combine(dir, SampleName));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(SampleSha256), "not the pinned sample");
    using var reader = new StuffItReader(new MemoryStream(bytes));
    static string Hash(byte[] fork) => fork.Length == 0 ? "" : Convert.ToHexStringLower(SHA256.HashData(fork));
    foreach (var (name, data, resource) in OracleForks) {
      var entry = reader.Entries.Single(e => e.FileName == name);
      Assert.That(entry.ResourceMethod, Is.EqualTo(6), name);
      // Extract verifies each fork's CRC-16 and throws on a mismatch.
      Assert.That(Hash(reader.Extract(entry)), Is.EqualTo(data), $"{name} data fork");
      Assert.That(Hash(reader.ExtractResourceFork(entry)), Is.EqualTo(resource), $"{name} resource fork");
    }
  }
}
