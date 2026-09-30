using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.Mobi;
using NUnit.Framework;

namespace Compression.Tests.Mobi;

/// <summary>
/// MOBI checked against bytes nobody here wrote: a book calibre produced, and KindleUnpack's reading
/// of what our writer produces. A round trip through our own writer and reader is not enough — the
/// first version of the writer put every MOBI header field from 0x50 on sixteen bytes too far, and
/// the reader looked in the same wrong places, so the round trip passed.
/// </summary>
[TestFixture]
public sealed class MobiConformanceTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"MobiVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded MOBI vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static string Metadata(MobiFormatDescriptor d, Stream s)
    => Encoding.UTF8.GetString(d.ExtractEntryToMemory(s, "metadata.ini", null));

  // ── A book written by calibre 4.17 (Project Gutenberg #1065, see ReferenceVectors/README.md) ──

  [Test]
  public void GivenABookFromCalibre_WhenItsTextIsExtracted_ThenItMatchesKindleUnpackByteForByte() {
    var d = new MobiFormatDescriptor();
    using var s = new MemoryStream(Vector("pg1065.mobi"), writable: false);
    var text = d.ExtractEntryToMemory(s, "book.html", null);
    // KindleUnpack 0.4.1 (python "mobi" package) MobiHeader.getRawML() on the same file:
    // 33778 bytes, SHA-1 4d1697b820c78335a429b97382ce9881b9c49a28.
    Assert.That(text, Has.Length.EqualTo(33778));
    Assert.That(Convert.ToHexStringLower(SHA1.HashData(text)), Is.EqualTo("4d1697b820c78335a429b97382ce9881b9c49a28"));
  }

  [Test]
  public void GivenABookFromCalibre_WhenInspected_ThenHeaderFieldsAndExthAreReadFromTheirOffsets() {
    var d = new MobiFormatDescriptor();
    using var s = new MemoryStream(Vector("pg1065.mobi"), writable: false);
    var meta = Metadata(d, s);
    Assert.That(meta, Does.Contain("text_encoding=UTF-8"));
    Assert.That(meta, Does.Contain("full_name=The Raven"));
    Assert.That(meta, Does.Contain("title=The Raven"));
    Assert.That(meta, Does.Contain("author=Edgar Allan Poe"));
    Assert.That(meta, Does.Contain("extra_data_flags=0x0003"));   // multibyte overlap + indexing entries
    Assert.That(meta, Does.Contain("compression=2"));
  }

  // ── Our writer, field by field at the MobileRead offsets ─────────────────────────────

  private static byte[] Create(string html, string method, Action<FormatCreateOptions>? configure = null) {
    var options = new FormatCreateOptions(method);
    options.FormatSpecific["title"] = "Conformance";
    options.FormatSpecific["author"] = "A. Writer";
    configure?.Invoke(options);
    using var output = new MemoryStream();
    new MobiFormatDescriptor().Create(output, [ArchiveInputInfo.InMemory("book.html", Encoding.UTF8.GetBytes(html))], options);
    return output.ToArray();
  }

  private static (int Count, int[] Offsets) Records(byte[] book) {
    var count = BinaryPrimitives.ReadUInt16BigEndian(book.AsSpan(76));
    var offsets = new int[count + 1];
    for (var i = 0; i < count; ++i)
      offsets[i] = (int)BinaryPrimitives.ReadUInt32BigEndian(book.AsSpan(78 + i * 8));
    offsets[count] = book.Length;
    return (count, offsets);
  }

  [TestCase(0, 1)]
  [TestCase(1, 1)]
  [TestCase(4096, 1)]
  [TestCase(4097, 2)]
  [TestCase(3 * 4096, 3)]
  public void GivenTextOfSomeLength_WhenWritten_ThenRecordZeroDescribesItAtTheMobileReadOffsets(int length, int textRecords) {
    var html = new string('x', length);
    var book = Create(html, "palmdoc");
    var (count, offsets) = Records(book);
    var r0 = book.AsMemory(offsets[0], offsets[1] - offsets[0]);

    Assert.Multiple(() => {
      Assert.That(count, Is.EqualTo(1 + textRecords + 1), "record 0, text records, end-of-file record");
      Assert.That(book.AsSpan(offsets[count - 1]).ToArray(), Is.EqualTo(new byte[] { 0xE9, 0x8E, 0x0D, 0x0A }));
      Assert.That(BinaryPrimitives.ReadUInt16BigEndian(r0.Span), Is.EqualTo(2));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(r0.Span[4..]), Is.EqualTo((uint)length));
      Assert.That(BinaryPrimitives.ReadUInt16BigEndian(r0.Span[8..]), Is.EqualTo(textRecords));
      Assert.That(BinaryPrimitives.ReadUInt16BigEndian(r0.Span[10..]), Is.EqualTo(4096));
      Assert.That(r0.Span[0x10..0x14].ToArray(), Is.EqualTo("MOBI"u8.ToArray()));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(r0.Span[0x14..]), Is.EqualTo(232u));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(r0.Span[0x1C..]), Is.EqualTo(65001u));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(r0.Span[0x50..]), Is.EqualTo((uint)(textRecords + 1)));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(r0.Span[0x5C..]), Is.EqualTo(1033u));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(r0.Span[0x80..]) & 0x40, Is.EqualTo(0x40u));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(r0.Span[0xA8..]), Is.EqualTo(uint.MaxValue), "no DRM");
      Assert.That(BinaryPrimitives.ReadUInt16BigEndian(r0.Span[0xC0..]), Is.EqualTo(1));
      Assert.That(BinaryPrimitives.ReadUInt16BigEndian(r0.Span[0xC2..]), Is.EqualTo(textRecords));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(r0.Span[0xF0..]), Is.EqualTo(0u), "no trailing entries");
      Assert.That(r0.Span[(16 + 232)..(16 + 232 + 4)].ToArray(), Is.EqualTo("EXTH"u8.ToArray()));
      var nameOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(r0.Span[0x54..]);
      var nameLength = (int)BinaryPrimitives.ReadUInt32BigEndian(r0.Span[0x58..]);
      Assert.That(Encoding.UTF8.GetString(r0.Span.Slice(nameOffset, nameLength)), Is.EqualTo("Conformance"));
      Assert.That(r0.Length % 4, Is.EqualTo(0));
    });
  }

  [Test]
  public void GivenALanguageOption_WhenWritten_ThenTheLocaleLandsAt0x5C() {
    var book = Create("<p/>", "stored", o => o.FormatSpecific["language"] = "2057");
    var (_, offsets) = Records(book);
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(book.AsSpan(offsets[0] + 0x5C)), Is.EqualTo(2057u));
  }

  // ── KindleUnpack as a black-box reader of our output ─────────────────────────────────

  /// <summary>
  /// KindleUnpack is not on CI runners. Point <c>CWB_KINDLEUNPACK_PATH</c> at a directory holding the
  /// python <c>mobi</c> package (<c>pip install --target DIR mobi</c>) to run this locally.
  /// </summary>
  [TestCase("stored")]
  [TestCase("palmdoc")]
  [Category("ExternalTool")]
  public void GivenABookWeWrote_WhenKindleUnpackReadsIt_ThenTitleAuthorAndTextAgree(string method) {
    var modulePath = Environment.GetEnvironmentVariable("CWB_KINDLEUNPACK_PATH");
    if (string.IsNullOrEmpty(modulePath) || !Directory.Exists(Path.Combine(modulePath, "mobi")))
      Assert.Ignore("Set CWB_KINDLEUNPACK_PATH to a directory containing the python 'mobi' (KindleUnpack) package.");

    var html = "<html><body>" + string.Concat(Enumerable.Range(0, 900).Select(i => $"<p>Paragraph {i} – ünïcödé</p>")) + "</body></html>";
    var dir = Path.Combine(Path.GetTempPath(), "cwb_mobi_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var bookPath = Path.Combine(dir, "book.mobi");
      var rawPath = Path.Combine(dir, "book.rawml");
      File.WriteAllBytes(bookPath, Create(html, method));
      var script = string.Join('\n',
        "import sys",
        "sys.path.insert(0, sys.argv[1])",
        "from mobi.mobi_sectioner import Sectionizer",
        "from mobi.mobi_header import MobiHeader",
        "mh = MobiHeader(Sectionizer(sys.argv[2]), 0)",
        "open(sys.argv[3], 'wb').write(mh.getRawML())",
        "print(mh.title)",
        "print(mh.codec)",
        "print(mh.metadata['Creator'][0])");
      var scriptPath = Path.Combine(dir, "oracle.py");
      File.WriteAllText(scriptPath, script);
      var psi = new ProcessStartInfo("python") {
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        StandardOutputEncoding = Encoding.UTF8,
      };
      psi.Environment["PYTHONIOENCODING"] = "utf-8";
      foreach (var a in new[] { scriptPath, modulePath, bookPath, rawPath })
        psi.ArgumentList.Add(a);
      using var p = Process.Start(psi)!;
      var stdout = p.StandardOutput.ReadToEnd();
      var stderr = p.StandardError.ReadToEnd();
      p.WaitForExit();
      Assert.That(p.ExitCode, Is.EqualTo(0), stderr);
      var lines = stdout.Split('\n', StringSplitOptions.TrimEntries);
      Assert.That(lines[0], Is.EqualTo("Conformance"));
      Assert.That(lines[1], Is.EqualTo("utf-8"));
      Assert.That(lines[2], Is.EqualTo("A. Writer"));
      Assert.That(File.ReadAllBytes(rawPath), Is.EqualTo(Encoding.UTF8.GetBytes(html)));
    } finally {
      try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
  }

  // ── Trailing entries (MobileRead "Trailing entries", "Variable-width integers") ──────

  private static IEnumerable<TestCaseData> TrailingEntryCases() {
    byte[] text = [.. "text"u8];
    yield return new TestCaseData(text, (ushort)0).SetName("GivenNoFlags_WhenStripped_ThenTheRecordIsUnchanged");
    yield return new TestCaseData((byte[])[.. text, 0x00], (ushort)1).SetName("GivenMultibyteOverlapOfNone_WhenStripped_ThenOnlyTheCountByteGoes");
    yield return new TestCaseData((byte[])[.. text, 0xA1, 0xA2, 0xA3, 0x03], (ushort)1).SetName("GivenMultibyteOverlapOfThree_WhenStripped_ThenAllFourBytesGo");
    yield return new TestCaseData((byte[])[.. text, 0x11, 0x22, 0x83], (ushort)2).SetName("GivenAnIndexingEntry_WhenStripped_ThenItsBackwardSizeCountsItself");
    yield return new TestCaseData((byte[])[.. text, 0x00, 0x55, 0x82], (ushort)(1 | 4)).SetName("GivenOverlapThenIndexingEntry_WhenStripped_ThenTheHigherBitIsTakenFirst");
    // 130 bytes of entry: size 130 = 0x82, backward-encoded as 0x81 0x02.
    yield return new TestCaseData((byte[])[.. text, .. new byte[128], 0x81, 0x02], (ushort)2).SetName("GivenAnEntryOver127Bytes_WhenStripped_ThenATwoByteSizeIsRead");
    yield return new TestCaseData((byte[])[.. text, 0x81, 0x00, 0x82], (ushort)(2 | 4)).SetName("GivenTwoSizedEntries_WhenStripped_ThenBothGo");
  }

  [TestCaseSource(nameof(TrailingEntryCases))]
  public void TrailingEntriesAreStripped(byte[] record, ushort flags) {
    Assert.That(PalmDocCodec.StripTrailingEntries(record, flags).ToArray(), Is.EqualTo("text"u8.ToArray()));
  }

  private static IEnumerable<TestCaseData> MalformedTrailingEntries() {
    yield return new TestCaseData(new byte[] { 0x01, 0x02, 0x03 }, (ushort)2).SetName("GivenASizeWithoutItsTerminatingBit_WhenStripped_ThenItIsRejected");
    yield return new TestCaseData(new byte[] { 0x41, 0x89 }, (ushort)2).SetName("GivenASizeLargerThanTheRecord_WhenStripped_ThenItIsRejected");
    yield return new TestCaseData(new byte[] { 0x41, 0x80 }, (ushort)2).SetName("GivenASizeSmallerThanItsOwnBytes_WhenStripped_ThenItIsRejected");
    yield return new TestCaseData(new byte[] { 0x03 }, (ushort)1).SetName("GivenAnOverlapLongerThanTheRecord_WhenStripped_ThenItIsRejected");
    yield return new TestCaseData(Array.Empty<byte>(), (ushort)1).SetName("GivenAnEmptyRecordWithOverlapFlag_WhenStripped_ThenItIsRejected");
  }

  [TestCaseSource(nameof(MalformedTrailingEntries))]
  public void MalformedTrailingEntriesAreRejected(byte[] record, ushort flags) {
    Assert.Throws<InvalidDataException>(() => PalmDocCodec.StripTrailingEntries(record, flags));
  }

  // ── PalmDOC codec ─────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenASpacePairByte_WhenDecoded_ThenItBecomesASpaceAndACharacter() {
    Assert.That(PalmDocCodec.Decode([(byte)'a', (byte)('b' ^ 0x80)]), Is.EqualTo("a b"u8.ToArray()));
  }

  [Test]
  public void GivenAZeroByte_WhenDecoded_ThenItIsALiteralZero()
    => Assert.That(PalmDocCodec.Decode([0x00, (byte)'z']), Is.EqualTo(new byte[] { 0, (byte)'z' }));

  private static IEnumerable<TestCaseData> MalformedPalmDoc() {
    yield return new TestCaseData(new byte[] { 0x05, 1, 2 }).SetName("GivenALiteralRunPastTheEnd_WhenDecoded_ThenItIsRejected");
    yield return new TestCaseData(new byte[] { 0x80 }).SetName("GivenAHalfBackReference_WhenDecoded_ThenItIsRejected");
    yield return new TestCaseData(new byte[] { (byte)'a', 0x80, 0x10 }).SetName("GivenABackReferenceBeforeTheStart_WhenDecoded_ThenItIsRejected");
    yield return new TestCaseData(new byte[] { (byte)'a', 0x80, 0x00 }).SetName("GivenABackReferenceOfDistanceZero_WhenDecoded_ThenItIsRejected");
  }

  [TestCaseSource(nameof(MalformedPalmDoc))]
  public void MalformedPalmDocIsRejected(byte[] data) {
    Assert.Throws<InvalidDataException>(() => PalmDocCodec.Decode(data));
  }

  [Test]
  public void GivenARecordThatExpandsPastTheCap_WhenDecoded_ThenItIsRejected() {
    // 'a' then back-references of distance 1, length 10: each two input bytes add ten output bytes.
    var bomb = new List<byte> { (byte)'a' };
    for (var i = 0; i < PalmDocCodec.MaxRecordOutput / 10 + 1; ++i) {
      bomb.Add(0x80);
      bomb.Add((1 << 3) | 7);
    }
    Assert.Throws<InvalidDataException>(() => PalmDocCodec.Decode(bomb.ToArray()));
  }

  private static IEnumerable<TestCaseData> EncoderInputs() {
    yield return new TestCaseData(Array.Empty<byte>()).SetName("GivenNothing_WhenEncodedAndDecoded_ThenNothingComesBack");
    yield return new TestCaseData(new byte[] { 0x41 }).SetName("GivenOneByte_WhenEncodedAndDecoded_ThenItComesBack");
    yield return new TestCaseData(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()).SetName("GivenEveryByteValue_WhenEncodedAndDecoded_ThenTheyComeBack");
    yield return new TestCaseData(Enumerable.Repeat((byte)'r', 4096).ToArray()).SetName("GivenAFullRepetitiveBlock_WhenEncodedAndDecoded_ThenItComesBack");
    var rnd = new byte[4096];
    new Random(11).NextBytes(rnd);
    yield return new TestCaseData(rnd).SetName("GivenAFullRandomBlock_WhenEncodedAndDecoded_ThenItComesBack");
  }

  [TestCaseSource(nameof(EncoderInputs))]
  public void EncoderRoundTrips(byte[] data) {
    Assert.That(PalmDocCodec.Decode(PalmDocCodec.Encode(data)), Is.EqualTo(data));
  }
}
