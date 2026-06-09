#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Macrium;

namespace Compression.Tests.Macrium;

/// <summary>
/// Behaviour tests for <see cref="MacriumPreXFormatDescriptor"/>. The
/// .mrimg / .mrbak files used by Macrium Reflect v6/v7/v8 (pre-X) are a
/// proprietary block container. Each block opens with a 9-byte preamble
/// <c>[flags:1=0x03][block_len:4 LE][out_len:4 LE]</c>; the surfaced
/// metadata.ini captures this preamble plus any embedded
/// <c>&lt;comment&gt;</c> tag in the first ~1 MiB of payload.
/// </summary>
[TestFixture]
public class MacriumPreXTests {

  /// <summary>
  /// Builds a minimal synthetic <c>.mrimg</c> stream made of <paramref name="blockCount"/>
  /// preamble-headed pseudo-blocks. The block bodies are filled with
  /// deterministic non-printable noise to keep the comment-scan code path
  /// happy. When <paramref name="comment"/> is non-null an ASCII
  /// <c>&lt;comment&gt;...&lt;/comment&gt;</c> sequence is embedded inside
  /// the first block.
  /// </summary>
  private static byte[] MakeMinimalMrimg(int blockCount = 1, uint blockLen = 64, uint uncompressedLen = 256, string? comment = null) {
    using var ms = new MemoryStream();
    Span<byte> preamble = stackalloc byte[9];
    for (var i = 0; i < blockCount; i++) {
      preamble.Clear();
      preamble[0] = 0x03;
      BinaryPrimitives.WriteUInt32LittleEndian(preamble[1..], blockLen);
      BinaryPrimitives.WriteUInt32LittleEndian(preamble[5..], uncompressedLen);
      ms.Write(preamble);

      // Block body — pseudo-compressed noise. For the first block, inject
      // the comment somewhere in the middle.
      var bodyLen = (int)blockLen - 9;
      var body = new byte[bodyLen];
      for (var j = 0; j < bodyLen; j++) body[j] = (byte)(j * 7 + 11);
      if (i == 0 && comment is not null) {
        var open = "<comment>"u8.ToArray();
        var c = Encoding.ASCII.GetBytes(comment);
        var close = "</comment>"u8.ToArray();
        var combined = new byte[open.Length + c.Length + close.Length];
        Buffer.BlockCopy(open, 0, combined, 0, open.Length);
        Buffer.BlockCopy(c, 0, combined, open.Length, c.Length);
        Buffer.BlockCopy(close, 0, combined, open.Length + c.Length, close.Length);
        // Drop at offset 4 inside the body — leaves the preamble untouched.
        if (combined.Length + 4 <= body.Length)
          Buffer.BlockCopy(combined, 0, body, 4, combined.Length);
      }
      ms.Write(body, 0, bodyLen);
    }
    return ms.ToArray();
  }

  // ── Equivalence class: well-formed pre-X mrimg ───────────────────────────

  [Category("HappyPath")]
  [Test]
  public void List_SyntheticMrimg_YieldsCanonicalSyntheticEntries() {
    var data = MakeMinimalMrimg(blockCount: 1);
    using var ms = new MemoryStream(data);
    var entries = new MacriumPreXFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.mrimg"), Is.True, "FULL.mrimg synthetic should be present");
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True, "metadata.ini synthetic should be present");
    Assert.That(entries.Any(e => e.Name == "header.bin"), Is.True, "header.bin synthetic should be present");

    var headerEntry = entries.Single(e => e.Name == "header.bin");
    Assert.That(headerEntry.OriginalSize, Is.EqualTo(MacriumPreXFormatDescriptor.PreambleSize));
  }

  [Category("HappyPath")]
  [Test]
  public void Extract_SyntheticMrimg_WritesAllSynthetics() {
    var data = MakeMinimalMrimg(blockCount: 1);
    var tmp = Path.Combine(Path.GetTempPath(), "mrimg_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new MacriumPreXFormatDescriptor().Extract(ms, tmp, null, null);

      Assert.That(File.Exists(Path.Combine(tmp, "FULL.mrimg")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "header.bin")), Is.True);

      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("format=mrimg-prex"));
      Assert.That(ini, Does.Contain("preamble_flags=0x03"));
      Assert.That(ini, Does.Contain("first_block_compressed_size=64"));
      Assert.That(ini, Does.Contain("first_block_uncompressed_size=256"));
      Assert.That(ini, Does.Contain("scanned_block_count=1"));
      // The synthetic fixture's body is pseudo-random noise — not valid
      // codec tokens — so the decoder bails out cleanly and metadata.ini
      // records the failure. The codec itself is "implemented"; only the
      // particular block had no decodable content.
      Assert.That(ini, Does.Contain("payload_decompression=implemented_but_no_block_decoded"));
      Assert.That(ini, Does.Contain("decoded_blocks=0"));
      Assert.That(ini, Does.Contain("decode_failures=1"));
      Assert.That(ini, Does.Contain("encryption_supported_by_format=AES-128|AES-192|AES-256"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Category("HappyPath")]
  [Test]
  public void List_MultiBlockMrimg_AccumulatesBlockTotals() {
    var data = MakeMinimalMrimg(blockCount: 3, blockLen: 100, uncompressedLen: 400);
    var tmp = Path.Combine(Path.GetTempPath(), "mrimg_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new MacriumPreXFormatDescriptor().Extract(ms, tmp, null, null);

      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("scanned_block_count=3"));
      Assert.That(ini, Does.Contain("scanned_compressed_bytes=300"));
      Assert.That(ini, Does.Contain("scanned_uncompressed_bytes=1200"));
      // Compression ratio = 300/1200 = 0.25, formatted to 4 decimals.
      Assert.That(ini, Does.Contain("scanned_compression_ratio=0.2500"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Category("HappyPath")]
  [Test]
  public void List_WithEmbeddedComment_SurfacesCommentInMetadata() {
    var data = MakeMinimalMrimg(blockCount: 1, blockLen: 256, uncompressedLen: 1024, comment: "Weekly Full Backup of C:");
    var tmp = Path.Combine(Path.GetTempPath(), "mrimg_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new MacriumPreXFormatDescriptor().Extract(ms, tmp, null, null);

      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("comment_present=yes"));
      Assert.That(ini, Does.Contain("comment=Weekly Full Backup of C:"));
      Assert.That(ini, Does.Contain("parse_status=partial"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Category("HappyPath")]
  [Test]
  public void List_NoCommentInWindow_ReportsPartialNoComment() {
    var data = MakeMinimalMrimg(blockCount: 1);
    var tmp = Path.Combine(Path.GetTempPath(), "mrimg_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new MacriumPreXFormatDescriptor().Extract(ms, tmp, null, null);

      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("comment_present=no"));
      Assert.That(ini, Does.Contain("parse_status=partial-no-comment"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  // ── Equivalence class: descriptor metadata ───────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Descriptor_MagicAndExtensions_AreReflectPreX() {
    var d = new MacriumPreXFormatDescriptor();
    Assert.That(d.Extensions, Does.Contain(".mrimg"));
    Assert.That(d.Extensions, Does.Contain(".mrbak"));
    Assert.That(d.Extensions, Does.Contain(".mrex"));
    Assert.That(d.Extensions, Does.Contain(".mrsql"));
    // .mrimgx is the NEW Reflect X format — must not collide.
    Assert.That(d.Extensions, Does.Not.Contain(".mrimgx"));
    Assert.That(d.Extensions, Does.Not.Contain(".mrbakx"));

    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x03 }));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    // Low-confidence signature — single byte is intentionally weak.
    Assert.That(d.MagicSignatures[0].Confidence, Is.LessThan(0.6));

    Assert.That(d.Id, Is.EqualTo("MacriumPreX"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  // ── Boundary tests: header recognition ───────────────────────────────────

  [Category("Boundary")]
  [TestCase((byte)0x03, 64u, 256u, ExpectedResult = true,  TestName = "Boundary:LooksLike:ValidPreamble")]
  [TestCase((byte)0x00, 64u, 256u, ExpectedResult = false, TestName = "Boundary:LooksLike:WrongFlags")]
  [TestCase((byte)0x03,  0u, 256u, ExpectedResult = false, TestName = "Boundary:LooksLike:ZeroBlockLen")]
  [TestCase((byte)0x03,  8u, 256u, ExpectedResult = false, TestName = "Boundary:LooksLike:BlockLenBelowPreamble")]
  [TestCase((byte)0x03, 64u,   0u, ExpectedResult = false, TestName = "Boundary:LooksLike:ZeroUncompressed")]
  [TestCase((byte)0x03, 100u * 1024u * 1024u, 256u, ExpectedResult = false, TestName = "Boundary:LooksLike:BlockTooLarge")]
  [TestCase((byte)0x03, 64u, 100u * 1024u * 1024u, ExpectedResult = false, TestName = "Boundary:LooksLike:UncompressedTooLarge")]
  public bool LooksLikeMrimg_Boundaries(byte flags, uint blockLen, uint uncompressedLen) {
    Span<byte> header = stackalloc byte[9];
    header[0] = flags;
    BinaryPrimitives.WriteUInt32LittleEndian(header[1..], blockLen);
    BinaryPrimitives.WriteUInt32LittleEndian(header[5..], uncompressedLen);
    return MacriumPreXFormatDescriptor.LooksLikeMrimg(header);
  }

  [Category("Boundary")]
  [Test]
  public void LooksLikeMrimg_ShortHeader_Rejected() {
    Span<byte> header = stackalloc byte[8]; // 1 byte short
    header[0] = 0x03;
    Assert.That(MacriumPreXFormatDescriptor.LooksLikeMrimg(header), Is.False);
  }

  // ── Exceptional cases: corrupt or non-mrimg input ────────────────────────

  [Category("Exception")]
  [Test]
  public void List_TruncatedFile_ReturnsOnlyFullEntry() {
    // 5 bytes — shorter than the preamble. BuildSynthetic returns no
    // synthetics, so only the always-on FULL.mrimg passthrough remains.
    var data = new byte[] { 0x03, 0x40, 0x00, 0x00, 0x00 };
    using var ms = new MemoryStream(data);
    var entries = new MacriumPreXFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.mrimg"));
  }

  [Category("Exception")]
  [Test]
  public void List_WrongMagic_ReturnsOnlyFullEntry() {
    var data = new byte[64];
    for (var i = 0; i < data.Length; i++) data[i] = 0xFF; // not 0x03 at offset 0
    using var ms = new MemoryStream(data);
    var entries = new MacriumPreXFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.mrimg"));
  }

  [Category("Exception")]
  [Test]
  public void OpenEntry_UnknownName_ReturnsEmptyStream() {
    var data = MakeMinimalMrimg(1);
    using var ms = new MemoryStream(data);
    using var entry = new MacriumPreXFormatDescriptor().OpenEntry(ms, "does-not-exist.bin", null);
    Assert.That(entry.Length, Is.EqualTo(0));
  }

  [Category("Exception")]
  [Test]
  public void OpenEntry_FullEntry_IsBoundedPassthrough() {
    var data = MakeMinimalMrimg(2, blockLen: 128, uncompressedLen: 512);
    using var ms = new MemoryStream(data);
    using var entry = new MacriumPreXFormatDescriptor().OpenEntry(ms, "FULL.mrimg", null);
    Assert.That(entry.Length, Is.EqualTo(data.Length));
    var buf = new byte[data.Length];
    var n = entry.Read(buf, 0, buf.Length);
    Assert.That(n, Is.EqualTo(data.Length));
    Assert.That(buf, Is.EqualTo(data));
  }

  [Category("Exception")]
  [Test]
  public void StripBinaryPadding_PrintableInput_RoundTrips() {
    var bytes = Encoding.UTF8.GetBytes("Hello World");
    var result = MacriumPreXFormatDescriptor.StripBinaryPadding(bytes);
    Assert.That(result, Is.EqualTo("Hello World"));
  }

  [Category("Exception")]
  [Test]
  public void StripBinaryPadding_NullPaddedInput_StripsNulls() {
    var bytes = new byte[] { (byte)'a', 0x00, (byte)'b', 0x00, (byte)'c' };
    var result = MacriumPreXFormatDescriptor.StripBinaryPadding(bytes);
    Assert.That(result, Is.EqualTo("abc"));
  }

  [Category("Exception")]
  [Test]
  public void StripBinaryPadding_AllBinary_FallsBackToHex() {
    var bytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
    var result = MacriumPreXFormatDescriptor.StripBinaryPadding(bytes);
    Assert.That(result, Is.EqualTo("01020304"));
  }

  [Category("Exception")]
  [Test]
  public void EscapeIni_NewlinesCollapsedToSpaces() {
    var result = MacriumPreXFormatDescriptor.EscapeIni("line1\r\nline2\nline3");
    Assert.That(result, Does.Not.Contain("\r"));
    Assert.That(result, Does.Not.Contain("\n"));
    Assert.That(result, Does.Contain("line1"));
    Assert.That(result, Does.Contain("line2"));
    Assert.That(result, Does.Contain("line3"));
  }
}
