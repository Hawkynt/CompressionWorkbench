using System.Text;
using FileFormat.Pack200;

namespace Compression.Tests.Pack200;

/// <summary>
/// Unit tests for the Pack200 (JSR-200) band codings, the segment decoder, and the
/// format descriptor. A hand-built minimal segment exercises the full header +
/// constant-pool + class band walk without any external tooling.
/// </summary>
[TestFixture]
public class Pack200Tests {

  // ── BHSD coding encoder (test-side inverse of the reader) ─────────────────

  /// <summary>Encodes the unsigned magnitude <paramref name="u"/> with a (B,H) coding.</summary>
  private static void EncodeMagnitude(List<byte> outp, long u, int b, int h) {
    var l = 256 - h;
    for (var i = 0; ; ++i) {
      if (i == b - 1) { outp.Add((byte)(u & 0xFF)); return; }
      if (u < l) { outp.Add((byte)u); return; }
      var r = (u - l) % h;
      outp.Add((byte)(l + r));
      u = (u - l) / h;
    }
  }

  /// <summary>Folds a signed value to its unsigned magnitude for S in {0,1}.</summary>
  private static long EncodeSign(long v, int s) {
    if (s == 0) return v;
    if (s == 1) return v >= 0 ? 2 * v : -2 * v - 1;
    throw new NotSupportedException("test encoder supports S in {0,1}");
  }

  /// <summary>Encodes a whole band of literal (or cumulative, when D=1) values.</summary>
  private static void EncodeBand(List<byte> outp, Pack200Coding c, params long[] values) {
    long running = 0;
    foreach (var target in values) {
      var v = c.D != 0 ? target - running : target;
      if (c.D != 0) running = target;
      EncodeMagnitude(outp, EncodeSign(v, c.S), c.B, c.H);
    }
  }

  private static void U5(List<byte> outp, long v) => EncodeBand(outp, Pack200Coding.Unsigned5, v);

  // ── Coding round-trips ────────────────────────────────────────────────────

  [Category("HappyPath")]
  [TestCase(0L)]
  [TestCase(1L)]
  [TestCase(63L)]
  [TestCase(191L)]
  [TestCase(192L)]
  [TestCase(1000L)]
  [TestCase(1783068244L)]
  public void Unsigned5_RoundTrips(long value) {
    var bytes = new List<byte>();
    U5(bytes, value);
    var r = new Pack200BandReader(bytes.ToArray());
    Assert.That(r.ReadValue(Pack200Coding.Unsigned5), Is.EqualTo(value));
  }

  [Category("Boundary")]
  [TestCase(0L)]
  [TestCase(5L)]
  [TestCase(-5L)]
  [TestCase(-1L)]
  [TestCase(123456L)]
  [TestCase(-123456L)]
  public void Signed5_RoundTrips(long value) {
    var bytes = new List<byte>();
    EncodeBand(bytes, Pack200Coding.Signed5, value);
    var r = new Pack200BandReader(bytes.ToArray());
    Assert.That(r.ReadValue(Pack200Coding.Signed5), Is.EqualTo(value));
  }

  [Category("HappyPath")]
  [Test]
  public void Delta5_Band_AccumulatesSignedDeltas() {
    // Matches the calibrated cp_Utf8_prefix example: raw bytes [10,10,9,9,8]
    // decode (S=1) to [5,5,-5,-5,4] then accumulate to [5,10,5,0,4].
    var bytes = new byte[] { 10, 10, 9, 9, 8 };
    var r = new Pack200BandReader(bytes);
    Assert.That(r.ReadBand(Pack200Coding.Delta5, 5),
      Is.EqualTo(new long[] { 5, 10, 5, 0, 4 }));
  }

  [Category("HappyPath")]
  [Test]
  public void Udelta5_Band_AccumulatesUnsignedDeltas() {
    var bytes = new byte[] { 1, 1, 1, 1, 1, 1 };
    var r = new Pack200BandReader(bytes);
    Assert.That(r.ReadBand(Pack200Coding.Udelta5, 6),
      Is.EqualTo(new long[] { 1, 2, 3, 4, 5, 6 }));
  }

  // ── Detection ─────────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Detect_RawMagic() =>
    Assert.That(Pack200Reader.LooksLikePack200([0xCA, 0xFE, 0xD0, 0x0D, 0x00]), Is.True);

  [Category("HappyPath")]
  [Test]
  public void Detect_GzipEnvelope() =>
    Assert.That(Pack200Reader.LooksLikePack200([0x1F, 0x8B, 0x08, 0x00]), Is.True);

  [Category("EdgeCase")]
  [Test]
  public void Detect_RejectsOther() =>
    Assert.That(Pack200Reader.LooksLikePack200([0x50, 0x4B, 0x03, 0x04]), Is.False);

  [Category("HappyPath")]
  [Test]
  public void Descriptor_Magic_IsCafeD00d() {
    var d = new Pack200FormatDescriptor();
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0xCA, 0xFE, 0xD0, 0x0D }));
  }

  // ── Hand-built minimal segment ────────────────────────────────────────────

  /// <summary>
  /// Builds a single Pack200 segment (options = 0, default codings) whose UTF-8 pool
  /// is [ "", "Hello", "java/lang/Object" ], with two Class entries and one defined
  /// class "Hello". No JDK tooling required.
  /// </summary>
  private static byte[] BuildMinimalSegment() {
    var b = new List<byte>();
    b.AddRange(Pack200Reader.Magic);      // magic
    U5(b, 7);                              // minver
    U5(b, 150);                            // majver
    U5(b, 0);                              // options = 0 (no file headers / numbers / special)

    U5(b, 3);                              // cp_Utf8_count
    U5(b, 0);                              // cp_String_count
    U5(b, 2);                              // cp_Class_count
    U5(b, 0);                              // cp_Signature_count
    U5(b, 0);                              // cp_Descr_count
    U5(b, 0);                              // cp_Field_count
    U5(b, 0);                              // cp_Method_count
    U5(b, 0);                              // cp_Imethod_count
    U5(b, 0);                              // ic_count
    U5(b, 0);                              // default_class_minver
    U5(b, 52);                             // default_class_majver
    U5(b, 1);                              // class_count

    // cp_Utf8 bands. entry1="Hello", entry2="java/lang/Object" (no shared prefix).
    EncodeBand(b, Pack200Coding.Delta5, 0);           // prefix (count-2 = 1 value)
    EncodeBand(b, Pack200Coding.Unsigned5, 5, 16);    // suffix lengths
    foreach (var ch in "Hellojava/lang/Object")       // chars (CHAR3)
      EncodeBand(b, Pack200Coding.Char3, ch);
    // big_suffix / big_chars are empty (no zero-length suffixes)

    // cp_Class -> Utf8 indices [1, 2] (ascending, UDELTA5)
    EncodeBand(b, Pack200Coding.Udelta5, 1, 2);

    // class_this -> cp_Class index [0] ("Hello"), DELTA5
    EncodeBand(b, Pack200Coding.Delta5, 0);

    return b.ToArray();
  }

  [Category("HappyPath")]
  [Test]
  public void MinimalSegment_Decodes_ClassName() {
    var seg = new Pack200Reader().Read(new MemoryStream(BuildMinimalSegment()));
    Assert.Multiple(() => {
      Assert.That(seg.Status, Is.EqualTo(Pack200DecodeStatus.Full));
      Assert.That(seg.ClassCount, Is.EqualTo(1));
      Assert.That(seg.Utf8Count, Is.EqualTo(3));
      Assert.That(seg.MajVersion, Is.EqualTo(150));
      Assert.That(seg.DefaultClassMajVersion, Is.EqualTo(52));
      Assert.That(seg.ClassNames, Is.EqualTo(new[] { "Hello" }));
    });
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_List_ReturnsClassEntry() {
    var d = new Pack200FormatDescriptor();
    var entries = d.List(new MemoryStream(BuildMinimalSegment()), null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("Hello.class"));
    Assert.That(entries[0].Method, Is.EqualTo("pack200"));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_Extract_WritesManifest() {
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      var d = new Pack200FormatDescriptor();
      d.Extract(new MemoryStream(BuildMinimalSegment()), tmp, null, null);
      var classes = File.ReadAllText(Path.Combine(tmp, "classes.txt"));
      Assert.That(classes, Does.Contain("Hello"));
      Assert.That(File.Exists(Path.Combine(tmp, "pack200-info.txt")), Is.True);
    } finally {
      Directory.Delete(tmp, true);
    }
  }

  [Category("EdgeCase")]
  [Test]
  public void GzipWrapped_MinimalSegment_Decodes() {
    var raw = BuildMinimalSegment();
    using var gzMs = new MemoryStream();
    using (var gz = new System.IO.Compression.GZipStream(gzMs, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
      gz.Write(raw);
    gzMs.Position = 0;
    var seg = new Pack200Reader().Read(gzMs);
    Assert.That(seg.ClassNames, Is.EqualTo(new[] { "Hello" }));
  }

  [Category("ExceptionalCase")]
  [Test]
  public void List_NonPack200_ReturnsEmpty() {
    var d = new Pack200FormatDescriptor();
    var entries = d.List(new MemoryStream(Encoding.ASCII.GetBytes("not a pack file")), null);
    Assert.That(entries, Is.Empty);
  }
}
