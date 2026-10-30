using System.Text;
using Compression.Core.Dictionary.Ha;
using Compression.Registry;
using FileFormat.Ha;

namespace Compression.Tests.Ha;

[TestFixture]
public class HaTests {
  private static readonly DateTime Stamp = new(2024, 5, 6, 7, 8, 10, DateTimeKind.Utc);

  private static byte[] Noise(int length, int seed) {
    var rng = new Random(seed);
    var data = new byte[length];
    rng.NextBytes(data);
    return data;
  }

  private static byte[] Text(int length) {
    var words = "the quick brown fox jumps over a lazy dog while archivers count bits ".Split(' ');
    var sb = new StringBuilder();
    var rng = new Random(7);
    while (sb.Length < length)
      sb.Append(words[rng.Next(words.Length)]).Append(rng.Next(9) == 0 ? ".\r\n" : " ");
    return Encoding.ASCII.GetBytes(sb.ToString()[..length]);
  }

  private static MemoryStream Build(Action<HaWriter> fill) {
    var ms = new MemoryStream();
    using (var w = new HaWriter(ms, leaveOpen: true))
      fill(w);
    ms.Position = 0;
    return ms;
  }

  // ── HSC codec ────────────────────────────────────────────────────────────

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(2)]
  [TestCase(255)]
  [TestCase(4096)]
  [Category("RoundTrip")]
  public void Given_Text_When_HscEncodedAndDecoded_Then_Identical(int length) {
    var data = Text(length);
    Assert.That(HaHsc.Decode(HaHsc.Encode(data), data.Length), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void Given_EveryByteValueAndLongRuns_When_HscRoundTripped_Then_Identical() {
    var data = new List<byte>();
    for (var i = 0; i < 256; ++i)
      data.Add((byte)i);
    data.AddRange(Enumerable.Repeat((byte)0xAA, 20_000));
    data.AddRange(Enumerable.Range(0, 5000).Select(i => (byte)(255 - i % 256)));
    var bytes = data.ToArray();
    Assert.That(HaHsc.Decode(HaHsc.Encode(bytes), bytes.Length), Is.EqualTo(bytes));
  }

  // Random bytes exhaust the 32 760 frequency nodes and the 10 000 contexts, so node release and
  // context recycling (including the drop to order 3) all run.
  [Test, Category("RoundTrip")]
  public void Given_NoiseThenText_When_HscRoundTripped_Then_PoolRecyclingIsLossless() {
    var data = Noise(150_000, 3).Concat(Text(120_000)).Concat(Noise(40_000, 4)).ToArray();
    Assert.That(HaHsc.Decode(HaHsc.Encode(data), data.Length), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase")]
  public void Given_HscStream_When_ExpectedLengthTooShort_Then_InvalidData() {
    var data = Text(1000);
    var packed = HaHsc.Encode(data);
    Assert.Throws<InvalidDataException>(() => HaHsc.Decode(packed, 999));
  }

  [Test, Category("EdgeCase")]
  public void Given_HscStream_When_ExpectedLengthTooLong_Then_InvalidData() {
    var data = Text(1000);
    var packed = HaHsc.Encode(data);
    Assert.Throws<InvalidDataException>(() => HaHsc.Decode(packed, 1001));
  }

  [Test, Category("EdgeCase")]
  public void Given_NegativeLength_When_Decoded_Then_Rejected() {
    Assert.Throws<ArgumentOutOfRangeException>(() => HaHsc.Decode(new byte[4], -1));
    Assert.Throws<ArgumentOutOfRangeException>(() => HaAsc.Decode(new byte[4], -1));
  }

  // ── Archive round trips ──────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Given_FilesAndDirectory_When_Written_Then_ReadBackWithNamesTimesAndMethods() {
    var text = Text(30_000);
    var noise = Noise(3000, 9);
    using var ms = Build(w => {
      w.AddDirectory("docs", Stamp);
      w.AddFile("docs/readme.txt", text, Stamp);
      w.AddFile("noise.bin", noise, Stamp);
      w.AddFile("empty.txt", [], Stamp);
    });

    using var r = new HaReader(ms, leaveOpen: true);
    Assert.That(r.DeclaredCount, Is.EqualTo(4));
    Assert.That(r.Entries.Select(e => (e.FileName, e.MethodName)), Is.EqualTo(new[] {
      ("docs", "DIR"), ("docs/readme.txt", "HSC"), ("noise.bin", "CPY"), ("empty.txt", "CPY"),
    }));
    Assert.That(r.Entries.All(e => e.LastModified == Stamp), Is.True);
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(text));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(noise), "noise does not shrink, so it is stored");
    Assert.That(r.Extract(r.Entries[3]), Is.Empty);
    Assert.That(r.Extract(r.Entries[0]), Is.Empty);
  }

  [Test, Category("RoundTrip")]
  public void Given_Descriptor_When_CreatedFromInputs_Then_ListMatches() {
    var d = new HaFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("a/b.txt", Text(500)), ArchiveInputInfo.InMemory("c.txt", Text(10))], new FormatCreateOptions());
    ms.Position = 0;
    var list = d.List(ms, null);
    Assert.That(list.Select(e => e.Name), Is.EqualTo(new[] { "a/b.txt", "c.txt" }));
    Assert.That(list.Select(e => e.OriginalSize), Is.EqualTo(new long[] { 500, 10 }));
  }

  [Test, Category("HappyPath")]
  public void Given_NestedName_When_Written_Then_PathUsesFfSeparatorsAndDosMachineInfo() {
    using var ms = Build(w => w.AddFile("x/y/z.txt", "hi"u8, Stamp));
    var bytes = ms.ToArray();
    Assert.That(bytes[..4], Is.EqualTo("HA\x01\x00"u8.ToArray()));
    Assert.That(bytes[4] >> 4, Is.EqualTo(2), "record version");
    var strings = bytes[21..];
    Assert.That(strings[..5], Is.EqualTo(new byte[] { (byte)'x', 0xFF, (byte)'y', 0xFF, 0 }));
    Assert.That(strings[5..11], Is.EqualTo("z.txt\0"u8.ToArray()));
    Assert.That(strings[11..14], Is.EqualTo(new byte[] { 2, 1, 0x20 }), "MS-DOS machine information, archive attribute");
  }

  // ── Malformed input ──────────────────────────────────────────────────────

  [Test, Category("EdgeCase")]
  public void Given_NoSignature_When_Opened_Then_InvalidData() {
    Assert.Throws<InvalidDataException>(() => new HaReader(new MemoryStream("HX\0\0"u8.ToArray())));
    Assert.Throws<InvalidDataException>(() => new HaReader(new MemoryStream("HA"u8.ToArray())));
  }

  [Test, Category("EdgeCase")]
  public void Given_TruncatedRecord_When_Opened_Then_EarlierMembersListedAndTruncationReported() {
    using var ms = Build(w => {
      w.AddFile("one.txt", Text(100), Stamp);
      w.AddFile("two.txt", Text(5000), Stamp);
    });
    var cut = new MemoryStream(ms.ToArray()[..^10]);
    using var r = new HaReader(cut);
    Assert.That(r.IsTruncated, Is.True);
    Assert.That(r.Entries.Select(e => e.FileName), Is.EqualTo(new[] { "one.txt" }));
  }

  [Test, Category("EdgeCase")]
  public void Given_UnknownMethod_When_Opened_Then_ParsingStops() {
    using var ms = Build(w => w.AddFile("one.txt", Text(100), Stamp));
    var bytes = ms.ToArray();
    bytes[4] = 0x23; // version 2, method 3: not an HA method
    using var r = new HaReader(new MemoryStream(bytes));
    Assert.That(r.IsTruncated, Is.True);
    Assert.That(r.Entries, Is.Empty);
  }

  [Test, Category("EdgeCase")]
  public void Given_DeletedRecord_When_Opened_Then_Skipped() {
    using var ms = Build(w => {
      w.AddFile("gone.txt", Text(300), Stamp);
      w.AddFile("kept.txt", Text(200), Stamp);
    });
    var bytes = ms.ToArray();
    bytes[4] = 0xFF; // what `ha d` leaves behind
    using var r = new HaReader(new MemoryStream(bytes));
    Assert.That(r.IsTruncated, Is.False);
    Assert.That(r.Entries.Select(e => e.FileName), Is.EqualTo(new[] { "kept.txt" }));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(Text(200)));
  }

  [Test, Category("EdgeCase")]
  public void Given_CorruptHscData_When_Extracted_Then_InvalidData() {
    using var ms = Build(w => w.AddFile("t.txt", Text(8000), Stamp));
    var bytes = ms.ToArray();
    bytes[^40] ^= 0x55;
    using var r = new HaReader(new MemoryStream(bytes));
    Assert.Throws<InvalidDataException>(() => r.Extract(r.Entries[0]));
  }

  [Test, Category("EdgeCase")]
  public void Given_WrongCrc_When_Extracted_Then_InvalidData() {
    using var ms = Build(w => w.AddFile("t.txt", Text(100), Stamp));
    var bytes = ms.ToArray();
    bytes[13] ^= 1; // CRC field of the first record
    using var r = new HaReader(new MemoryStream(bytes));
    Assert.Throws<InvalidDataException>(() => r.Extract(r.Entries[0]));
  }

  [Test, Category("EdgeCase")]
  public void Given_SpecialRecord_When_Extracted_Then_Refused() {
    using var ms = Build(w => w.AddFile("dev", [], Stamp));
    var bytes = ms.ToArray();
    bytes[4] = 0x2F; // version 2, SPECIAL
    using var r = new HaReader(new MemoryStream(bytes));
    Assert.That(r.Entries[0].MethodName, Is.EqualTo("SPECIAL"));
    Assert.Throws<NotSupportedException>(() => r.Extract(r.Entries[0]));
  }

  [Test, Category("EdgeCase")]
  public void Given_65535Members_When_OneMoreAdded_Then_Refused() {
    using var ms = new MemoryStream();
    using var w = new HaWriter(ms, leaveOpen: true);
    for (var i = 0; i < ushort.MaxValue; ++i)
      w.AddFile("f" + i, [], Stamp);
    Assert.Throws<NotSupportedException>(() => w.AddFile("one-too-many", [], Stamp));
  }

  // ── Edits ────────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Given_Archive_When_FileAdded_Then_OtherRecordsByteIdenticalAndCountUpdated() {
    using var ms = Build(w => {
      w.AddFile("a.txt", Text(700), Stamp);
      w.AddFile("b.txt", Text(900), Stamp);
    });
    var before = ms.ToArray();
    HaModifier.Add(ms, [("c.txt", Text(50), Stamp)]);
    var after = ms.ToArray();

    Assert.That(after[4..before.Length], Is.EqualTo(before[4..]), "existing records are copied verbatim");
    using var r = new HaReader(new MemoryStream(after));
    Assert.That(r.DeclaredCount, Is.EqualTo(3));
    Assert.That(r.Entries.Select(e => e.FileName), Is.EqualTo(new[] { "a.txt", "b.txt", "c.txt" }));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(Text(50)));
  }

  [Test, Category("RoundTrip")]
  public void Given_Archive_When_SameNameAdded_Then_Replaced() {
    using var ms = Build(w => w.AddFile("a.txt", Text(700), Stamp));
    HaModifier.Add(ms, [("A.TXT", Text(20), Stamp)]);
    using var r = new HaReader(ms, leaveOpen: true);
    Assert.That(r.Entries.Select(e => e.FileName), Is.EqualTo(new[] { "A.TXT" }));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(Text(20)));
  }

  [Test, Category("RoundTrip")]
  public void Given_Archive_When_MemberRemoved_Then_RestKeptAndStreamShrinks() {
    using var ms = Build(w => {
      w.AddFile("a.txt", Text(700), Stamp);
      w.AddFile("b.txt", Text(900), Stamp);
    });
    var length = ms.Length;
    Assert.That(HaModifier.Remove(ms, ["a.txt"]), Is.EqualTo(1));
    Assert.That(ms.Length, Is.LessThan(length));
    using var r = new HaReader(ms, leaveOpen: true);
    Assert.That(r.DeclaredCount, Is.EqualTo(1));
    Assert.That(r.Extract(r.Entries.Single()), Is.EqualTo(Text(900)));
  }

  [Test, Category("EdgeCase")]
  public void Given_Archive_When_MissingMemberRemoved_Then_BytesUnchanged() {
    using var ms = Build(w => w.AddFile("a.txt", Text(70), Stamp));
    var before = ms.ToArray();
    Assert.That(HaModifier.Remove(ms, ["nope.txt"]), Is.Zero);
    Assert.That(ms.ToArray(), Is.EqualTo(before));
  }

  [Test, Category("EdgeCase")]
  public void Given_TruncatedArchive_When_Edited_Then_RefusedAndUntouched() {
    using var built = Build(w => {
      w.AddFile("a.txt", Text(100), Stamp);
      w.AddFile("b.txt", Text(5000), Stamp);
    });
    var bytes = built.ToArray()[..^10];
    using var ms = new MemoryStream();
    ms.Write(bytes);
    Assert.Throws<NotSupportedException>(() => HaModifier.Remove(ms, ["a.txt"]));
    Assert.That(ms.ToArray(), Is.EqualTo(bytes));
  }

  [Test, Category("RoundTrip")]
  public void Given_DeletedRecord_When_Defragmented_Then_CompactedAndLiveMembersIntact() {
    using var built = Build(w => {
      w.AddFile("gone.txt", Text(300), Stamp);
      w.AddFile("kept.txt", Text(200), Stamp);
    });
    var bytes = built.ToArray();
    bytes[4] = 0xFF;
    bytes[2] = 1; // `ha d` also decrements the count
    using var ms = new MemoryStream();
    ms.Write(bytes);
    new HaFormatDescriptor().Defragment(ms);
    using var r = new HaReader(ms, leaveOpen: true);
    Assert.That(ms.Length, Is.LessThan(bytes.Length));
    Assert.That(r.Entries.Select(e => e.FileName), Is.EqualTo(new[] { "kept.txt" }));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(Text(200)));
  }
}
