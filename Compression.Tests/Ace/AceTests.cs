using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Dictionary.Ace;
using Compression.Registry;
using FileFormat.Ace;
using AceConstants = Compression.Core.Dictionary.Ace.AceConstants;

namespace Compression.Tests.Ace;

/// <summary>
/// ACE, against acefile (the reference reader the decoder is converted from). Fixtures: two archives
/// from acefile's own test corpus (one an ACE 2.0 solid archive WinAce wrote, cut after its first 35
/// members) and archives our writer produced that acefile verified; <c>acefile-listing.txt</c> is
/// acefile's reading of every one. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class AceTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"AceVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded ACE vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static List<string[]> Listing(string vector)
    => Encoding.UTF8.GetString(Vector("acefile-listing.txt"))
      .Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r').Split('\t'))
      .Where(p => p[0] == vector)
      .ToList();

  // ── the archives our writer produced for acefile (regenerate with OursFixtures.Write) ──────────

  internal static readonly DateTime FixedTime = new(2024, 5, 6, 7, 8, 10);
  internal static readonly byte[] Text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 400).Select(i => $"line {i % 37} of the ACE writer test, repeated often enough to compress\r\n")));
  internal static readonly byte[] Noise = [.. Enumerable.Range(0, 9000).Select(i => (byte)((i * 2654435761u) >> 24))];

  internal static byte[] OursArchive(string name) {
    var (solid, store, password) = name switch {
      "ours-lz77.ace" => (false, false, (string?)null),
      "ours-store.ace" => (false, true, null),
      "ours-solid.ace" => (true, false, null),
      "ours-encrypted.ace" => (false, false, "secret"),
      _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
    var w = new AceWriter(dictionaryBits: 16, password: password, solid: solid, store: store) { CreatedTime = FixedTime };
    w.AddDirectory("docs", FixedTime);
    w.AddFile("docs/text.txt", Text, FixedTime);
    w.AddFile("noise.bin", Noise, FixedTime);
    w.AddFile("empty.txt", [], FixedTime);
    w.AddFile("again.txt", Text, FixedTime);
    return w.ToArray();
  }

  private static readonly string[] OursNames = ["ours-lz77.ace", "ours-store.ace", "ours-solid.ace", "ours-encrypted.ace"];

  [TestCase("cafe.ace")]
  [TestCase("winappdbg-head.ace")]
  [TestCase("ours-lz77.ace")]
  [TestCase("ours-store.ace")]
  [TestCase("ours-solid.ace")]
  [TestCase("ours-encrypted.ace")]
  [Category("ThirdPartyVector")]
  public void Given_Archive_When_Read_Then_MatchesAcefile(string vector) {
    var expected = Listing(vector).Where(p => !p[0].StartsWith('#')).ToList();
    using var reader = new AceReader(new MemoryStream(Vector(vector)), password: vector == "ours-encrypted.ace" ? "secret" : null);
    Assert.That(reader.Entries.Select(e => e.FileName), Is.EqualTo(expected.Select(p => p[1])));
    for (var i = 0; i < expected.Count; ++i) {
      var e = reader.Entries[i];
      var p = expected[i];
      Assert.That(e.OriginalSize, Is.EqualTo(long.Parse(p[2])), e.FileName);
      Assert.That(e.CompressionType, Is.EqualTo(int.Parse(p[3])), e.FileName);
      Assert.That(e.DictionaryBits, Is.EqualTo(int.Parse(p[5])), e.FileName);
      Assert.That(e.IsDirectory, Is.EqualTo(p[6] == "1"), e.FileName);
      if (!e.IsDirectory)
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(reader.ExtractEntry(e))), Is.EqualTo(p[7]), e.FileName);
    }
  }

  [TestCaseSource(nameof(OursNames))]
  [Category("ThirdPartyVector")]
  public void Given_SameInputs_When_Written_Then_ByteIdenticalToWhatAcefileVerified(string vector)
    => Assert.That(OursArchive(vector), Is.EqualTo(Vector(vector)));

  [Test, Category("ThirdPartyVector")]
  public void Given_WinAceSolidArchive_When_Read_Then_HeaderFieldsMatch() {
    using var reader = new AceReader(new MemoryStream(Vector("winappdbg-head.ace")));
    Assert.That(reader.IsSolid, Is.True);
    Assert.That(reader.Comment, Is.EqualTo("default"));
    Assert.That(reader.ExtractVersion, Is.EqualTo(20));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_SolidArchive_When_ExtractedOutOfOrder_Then_StillCorrect() {
    var expected = Listing("winappdbg-head.ace").Where(p => !p[0].StartsWith('#') && p[6] == "0").ToList();
    using var reader = new AceReader(new MemoryStream(Vector("winappdbg-head.ace")));
    var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
    var last = files[^1];
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(reader.ExtractEntry(last))), Is.EqualTo(expected[^1][7]));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(reader.ExtractEntry(files[0]))), Is.EqualTo(expected[0][7]));
  }

  // ── reference vectors from acefile's doctests ────────────────────────────────────────────────

  [Test, Category("ThirdPartyVector")]
  public void Given_Password_When_KeyDerived_Then_MatchesAcefile()
    => Assert.That(AceBlowfish.DeriveKey("123456789"u8), Is.EqualTo(new uint[] { 3071200156, 3325860325, 4058316933, 1308772094, 896611998 }));

  [Test, Category("ThirdPartyVector")]
  public void Given_Ciphertext_When_Decrypted_Then_MatchesAcefileWithChaining() {
    var bf = new AceBlowfish("123456789");
    var a = Enumerable.Repeat((byte)0xFF, 8).ToArray();
    bf.Decrypt(a);
    Assert.That(a, Is.EqualTo(new byte[] { 0xB7, 0x77, 0x46, 0x40, 0x35, 0x2E, 0x65, 0x72 }));
    var b = Enumerable.Repeat((byte)0xC7, 8).ToArray();
    bf.Decrypt(b);
    Assert.That(b, Is.EqualTo(new byte[] { 0x65, 0x45, 0x05, 0xC4, 0xA5, 0x85, 0x29, 0xBC }));
  }

  [Test, Category("RoundTrip")]
  public void Given_Plaintext_When_EncryptedThenDecrypted_Then_Restored() {
    var plain = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
    var data = (byte[])plain.Clone();
    new AceBlowfish("pw").Encrypt(data);
    Assert.That(data, Is.Not.EqualTo(plain));
    new AceBlowfish("pw").Decrypt(data);
    Assert.That(data, Is.EqualTo(plain));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_DoctestKeysAndValues_When_Quicksorted_Then_AcefileOrder() {
    int[] keys = [1, 0, 0, 1, 2, 0, 0];
    int[] values = [0, 1, 2, 3, 4, 5, 6];
    AceHuffman.Quicksort(keys, values);
    Assert.That(keys, Is.EqualTo(new[] { 2, 1, 1, 0, 0, 0, 0 }));
    Assert.That(values, Is.EqualTo(new[] { 4, 0, 3, 5, 6, 2, 1 }));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_DoctestBytes_When_BitStreamRead_Then_AcefileValues() {
    var bs = new AceBitStream("01234567"u8.ToArray());
    Assert.That(bs.PeekBits(31), Is.EqualTo(429463704u));
    Assert.That(bs.ReadBits(31), Is.EqualTo(429463704u));
    bs.SkipBits(3);
    Assert.That(bs.ReadBits(5), Is.EqualTo(27u));
    Assert.That(bs.ReadGolombRice(3), Is.EqualTo(20));
    Assert.That(bs.ReadGolombRice(2, signed: true), Is.EqualTo(-2));
    Assert.That(bs.ReadKnownWidthUInt(10), Is.EqualTo(618u));
    Assert.That(bs.ReadBits(7), Is.EqualTo(52u));
    Assert.That(bs.PeekBits(31), Is.Zero);
    Assert.Throws<EndOfStreamException>(() => bs.ReadBits(1));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_CheckString_When_Checksummed_Then_AceCrcValues() {
    Assert.That(~Compression.Core.Checksums.Crc32.Compute("123456789"u8), Is.EqualTo(873187033u));
    Assert.That((ushort)~Compression.Core.Checksums.Crc32.Compute("123456789"u8), Is.EqualTo((ushort)50905));
  }

  // ── round trips, boundaries, malformed input ─────────────────────────────────────────────────

  private static IEnumerable<TestCaseData> Payloads() {
    yield return new TestCaseData(Array.Empty<byte>()).SetName("Empty");
    yield return new TestCaseData(new byte[] { 42 }).SetName("OneByte");
    yield return new TestCaseData(Enumerable.Repeat((byte)'a', 70000).ToArray()).SetName("LongRun");
    yield return new TestCaseData(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()).SetName("AllByteValues");
    yield return new TestCaseData(Text).SetName("Text");
    var rnd = new Random(7);
    var big = new byte[300_000];
    rnd.NextBytes(big);
    yield return new TestCaseData(big).SetName("Random300K");
    var mixed = new byte[200_000];
    for (var i = 0; i < mixed.Length; ++i)
      mixed[i] = (byte)(i % 1000 < 500 ? rnd.Next(4) : i / 1000);
    yield return new TestCaseData(mixed).SetName("MixedPatterns");
  }

  [TestCaseSource(nameof(Payloads)), Category("RoundTrip")]
  public void Given_Payload_When_Lz77CompressedAndDecoded_Then_Identical(byte[] payload) {
    foreach (var bits in new[] { AceConstants.MinDictBits, 15, AceConstants.MaxDictBits }) {
      var packed = new AceCompressor(bits).Compress(payload);
      Assert.That(packed.Length % 4, Is.Zero);
      var restored = new AceDecompressor().Decompress(packed, payload.Length, AceConstants.CompAce10, bits);
      Assert.That(restored, Is.EqualTo(payload), $"dictionary bits {bits}");
    }
  }

  [Test, Category("RoundTrip")]
  public void Given_SolidMembers_When_Written_Then_LaterMembersMatchIntoEarlierOnes() {
    var solid = new AceWriter(solid: true);
    var plain = new AceWriter();
    foreach (var w in new[] { solid, plain }) {
      w.AddFile("a.txt", Text);
      w.AddFile("b.txt", Text);
    }
    var solidBytes = solid.ToArray();
    Assert.That(solidBytes.Length, Is.LessThan(plain.ToArray().Length), "the second member should be nearly free in a solid archive");
    using var reader = new AceReader(new MemoryStream(solidBytes));
    Assert.That(reader.IsSolid, Is.True);
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(Text));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(Text));
  }

  [Test, Category("RoundTrip")]
  public void Given_Descriptor_When_CreatedWithOptions_Then_ListsAndExtracts() {
    var d = new AceFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("dir/a.txt", Text), ArchiveInputInfo.InMemory("b.bin", Noise)],
      new FormatCreateOptions { Password = "pw", FormatSpecific = new() { ["solid"] = "true" } });
    ms.Position = 0;
    var list = d.List(ms, "pw");
    Assert.That(list.Select(e => e.Name), Is.EqualTo(new[] { "dir/a.txt", "b.bin" }));
    Assert.That(list.All(e => e.IsEncrypted), Is.True);
    ms.Position = 0;
    Assert.That(d.ExtractEntryToMemory(ms, "b.bin", "pw"), Is.EqualTo(Noise));
  }

  [Test, Category("EdgeCase")]
  public void Given_EncryptedMember_When_WrongOrNoPassword_Then_Refused() {
    var bytes = Vector("ours-encrypted.ace");
    using var none = new AceReader(new MemoryStream(bytes));
    Assert.Throws<InvalidOperationException>(() => none.ExtractEntry(none.Entries[1]));
    using var wrong = new AceReader(new MemoryStream(bytes), password: "nope");
    Assert.Throws<InvalidDataException>(() => wrong.ExtractEntry(wrong.Entries[1]));
  }

  [Test, Category("EdgeCase")]
  public void Given_FlippedHeaderByte_When_Read_Then_InvalidData() {
    var header = Vector("ours-lz77.ace");
    header[40] ^= 0x10; // inside the first file header (the main header is bytes 0..30)
    Assert.Throws<InvalidDataException>(() => _ = new AceReader(new MemoryStream(header)));
  }

  [Test, Category("EdgeCase")]
  public void Given_CorruptCompressedData_When_Extracted_Then_InvalidData() {
    var bytes = Vector("ours-lz77.ace");
    using var probe = new AceReader(new MemoryStream(bytes));
    var e = probe.Entries.First(x => x.FileName == "docs/text.txt");
    bytes[(int)e.DataOffset + 10] ^= 0xFF;
    using var reader = new AceReader(new MemoryStream(bytes));
    Assert.Throws<InvalidDataException>(() => reader.ExtractEntry(reader.Entries.First(x => x.FileName == "docs/text.txt")));
  }

  [Test, Category("EdgeCase")]
  public void Given_NoMainHeader_When_Read_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => _ = new AceReader(new MemoryStream(Encoding.ASCII.GetBytes("not an ACE archive at all, just text"))));

  [Test, Category("EdgeCase")]
  public void Given_SfxStub_When_Read_Then_MainHeaderFound() {
    var archive = Vector("ours-store.ace");
    var withStub = new byte[3000].Concat(archive).ToArray();
    using var reader = new AceReader(new MemoryStream(withStub));
    Assert.That(reader.ArchiveOffset, Is.EqualTo(3000));
    Assert.That(reader.ExtractEntry(reader.Entries.First(x => x.FileName == "noise.bin")), Is.EqualTo(Noise));
  }

  [Test, Category("EdgeCase")]
  public void Given_TruncatedArchive_When_Read_Then_InvalidData() {
    var bytes = Vector("ours-lz77.ace");
    Assert.Throws<InvalidDataException>(() => _ = new AceReader(new MemoryStream(bytes[..(bytes.Length - 5)])));
  }

  [Test, Category("RoundTrip")]
  public void Given_BuildingBlock_When_RoundTripped_Then_Identical() {
    var bb = new AceBuildingBlock();
    Assert.That(bb.Decompress(bb.Compress(Text)), Is.EqualTo(Text));
    Assert.That(bb.Decompress(bb.Compress([])), Is.Empty);
  }
}
