#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Webp;

namespace Compression.Tests.Webp;

/// <summary>
/// Given-when-then coverage for the WebP pseudo-archive surface: FULL.webp +
/// metadata.ini always, per-frame standalone WebPs for animated files, ancillary
/// metadata chunks, byte-identical FULL round-trip, and a malformed-input path
/// that must never throw.
/// </summary>
[TestFixture]
public class WebpPseudoArchiveTests {

  // ── synthetic sample builders ────────────────────────────────────────────

  private static byte[] Riff(IEnumerable<byte[]> chunks) {
    using var inner = new MemoryStream();
    inner.Write("WEBP"u8);
    foreach (var c in chunks) inner.Write(c);
    var innerBytes = inner.ToArray();

    using var ms = new MemoryStream();
    ms.Write("RIFF"u8);
    Span<byte> sz = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(sz, (uint)innerBytes.Length);
    ms.Write(sz);
    ms.Write(innerBytes);
    return ms.ToArray();
  }

  private static byte[] Chunk(string fourCc, byte[] body) {
    using var ms = new MemoryStream();
    ms.Write(Encoding.ASCII.GetBytes(fourCc));
    Span<byte> sz = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(sz, (uint)body.Length);
    ms.Write(sz);
    ms.Write(body);
    if ((body.Length & 1) == 1) ms.WriteByte(0); // even padding
    return ms.ToArray();
  }

  private static byte[] Vp8xBody(bool animated, int width, int height) {
    var b = new byte[10];
    b[0] = (byte)(animated ? 0x02 : 0x00);
    var w = width - 1;
    var h = height - 1;
    b[4] = (byte)(w & 0xFF); b[5] = (byte)((w >> 8) & 0xFF); b[6] = (byte)((w >> 16) & 0xFF);
    b[7] = (byte)(h & 0xFF); b[8] = (byte)((h >> 8) & 0xFF); b[9] = (byte)((h >> 16) & 0xFF);
    return b;
  }

  private static byte[] AnimBody(ushort loopCount) {
    var b = new byte[6];
    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), loopCount);
    return b;
  }

  private static byte[] AnmfBody(byte[] subChunk) {
    // 16-byte ANMF header (zeroed for the test) + an embedded VP8/VP8L sub-chunk.
    var b = new byte[16 + subChunk.Length];
    subChunk.CopyTo(b.AsSpan(16));
    return b;
  }

  private static byte[] MakeAnimatedWebp() => Riff([
    Chunk("VP8X", Vp8xBody(animated: true, width: 4, height: 3)),
    Chunk("ANIM", AnimBody(loopCount: 0)),
    Chunk("ANMF", AnmfBody(Chunk("VP8L", new byte[10]))),
    Chunk("ANMF", AnmfBody(Chunk("VP8L", new byte[12]))),
    Chunk("EXIF", "II*\0"u8.ToArray()),
    Chunk("XMP ", "<x:xmpmeta/>"u8.ToArray()),
  ]);

  private static byte[] MakeStaticWebp() => Riff([
    Chunk("VP8L", new byte[10]),
  ]);

  // ── tests ────────────────────────────────────────────────────────────────

  [Test]
  public void List_AnimatedWebp_ExposesFullMetadataAndFrames() {
    var data = MakeAnimatedWebp();
    using var ms = new MemoryStream(data);
    var entries = new WebpFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("FULL.webp"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("frames/frame_000.webp"));
    Assert.That(names, Does.Contain("frames/frame_001.webp"));
    Assert.That(names, Does.Contain("metadata/exif.bin"));
    Assert.That(names, Does.Contain("metadata/xmp.xml"));

    var full = entries.First(e => e.Name == "FULL.webp");
    Assert.That(full.Kind, Is.EqualTo("Track"));
    Assert.That(entries.First(e => e.Name == "metadata.ini").Kind, Is.EqualTo("Tag"));
    Assert.That(entries.First(e => e.Name == "frames/frame_000.webp").Kind, Is.EqualTo("Frame"));
  }

  [Test]
  public void List_StaticWebp_ExposesFullPlusMetadataOnly() {
    var data = MakeStaticWebp();
    using var ms = new MemoryStream(data);
    var entries = new WebpFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("FULL.webp"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Has.None.StartsWith("frames/"), "Static WebP has no animation frames.");
  }

  [Test]
  public void MetadataIni_RecordsAnimationFacts() {
    var data = MakeAnimatedWebp();
    using var ms = new MemoryStream(data);
    using var sink = new MemoryStream();
    ((IArchiveInMemoryExtract)new WebpFormatDescriptor()).ExtractEntry(ms, "metadata.ini", sink, null);
    var text = Encoding.UTF8.GetString(sink.ToArray());

    Assert.That(text, Does.Contain("animated=true"));
    Assert.That(text, Does.Contain("frame_count=2"));
    Assert.That(text, Does.Contain("loop_count=0 (infinite)"));
    Assert.That(text, Does.Contain("width=4"));
    Assert.That(text, Does.Contain("height=3"));
  }

  [Test]
  public void Extract_WritesEntries_FullIsByteIdentical() {
    var data = MakeAnimatedWebp();
    var dir = Path.Combine(Path.GetTempPath(), "webp_pa_" + Guid.NewGuid().ToString("N"));
    try {
      using (var ms = new MemoryStream(data))
        new WebpFormatDescriptor().Extract(ms, dir, null, null);

      var full = Path.Combine(dir, "FULL.webp");
      Assert.That(File.Exists(full), Is.True);
      Assert.That(File.ReadAllBytes(full), Is.EqualTo(data));

      var frame0 = Path.Combine(dir, "frames", "frame_000.webp");
      Assert.That(File.Exists(frame0), Is.True);
      var fb = File.ReadAllBytes(frame0);
      Assert.That(Encoding.ASCII.GetString(fb, 0, 4), Is.EqualTo("RIFF"));
      Assert.That(Encoding.ASCII.GetString(fb, 8, 4), Is.EqualTo("WEBP"));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public void List_MalformedInput_DoesNotThrow_FallsBackToFullPlusPartial() {
    var garbage = Encoding.ASCII.GetBytes("not a webp file at all, just text");
    using var ms = new MemoryStream(garbage);

    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new WebpFormatDescriptor().List(ms, null));

    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("FULL.webp"));
    Assert.That(names, Does.Contain("metadata.ini"));

    var full = entries.First(e => e.Name == "FULL.webp");
    Assert.That(full.OriginalSize, Is.EqualTo(garbage.Length));

    using var ms2 = new MemoryStream(garbage);
    using var sink = new MemoryStream();
    ((IArchiveInMemoryExtract)new WebpFormatDescriptor()).ExtractEntry(ms2, "metadata.ini", sink, null);
    Assert.That(Encoding.UTF8.GetString(sink.ToArray()), Does.Contain("parse_status=partial"));
  }
}
