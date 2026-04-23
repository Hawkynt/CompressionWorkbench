using System.Buffers.Binary;
using System.Text;
using FileFormat.ExePackers;

namespace Compression.Tests.ExePackers;

[TestFixture]
public class PackerDetectorTests {

  /// <summary>Builds a minimal MZ DOS executable header padded to 1 KB.</summary>
  private static byte[] MinimalMz() {
    var buf = new byte[1024];
    buf[0] = (byte)'M'; buf[1] = (byte)'Z';
    return buf;
  }

  /// <summary>Builds a minimal PE skeleton (MZ + PE header) padded to 1 KB.</summary>
  private static byte[] MinimalPe() {
    var buf = new byte[1024];
    buf[0] = (byte)'M'; buf[1] = (byte)'Z';
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x3C), 0x80);
    buf[0x80] = (byte)'P'; buf[0x81] = (byte)'E';
    return buf;
  }

  // ── PKLITE ────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void PkLite_DetectsCopyrightString() {
    var buf = MinimalMz();
    Encoding.ASCII.GetBytes("PKLITE Copr.").CopyTo(buf.AsSpan(0x100));

    using var ms = new MemoryStream(buf);
    var entries = new PkLiteFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "packed_payload.bin"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void PkLite_DetectsLowercaseCopyrightVariant() {
    var buf = MinimalMz();
    Encoding.ASCII.GetBytes("PKlite Copr.").CopyTo(buf.AsSpan(0x80));
    using var ms = new MemoryStream(buf);
    Assert.That(new PkLiteFormatDescriptor().List(ms, null), Is.Not.Empty);
  }

  [Test, Category("EdgeCase")]
  public void PkLite_PlainMz_Throws() {
    using var ms = new MemoryStream(MinimalMz());
    Assert.That(() => new PkLiteFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  // ── LZEXE ─────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void LzExe_Detects091Signature() {
    var buf = MinimalMz();
    Encoding.ASCII.GetBytes("LZ91").CopyTo(buf.AsSpan(0x1C));
    using var ms = new MemoryStream(buf);
    var entries = new LzExeFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void LzExe_Detects090Signature() {
    var buf = MinimalMz();
    Encoding.ASCII.GetBytes("LZ09").CopyTo(buf.AsSpan(0x1C));
    using var ms = new MemoryStream(buf);
    Assert.That(new LzExeFormatDescriptor().List(ms, null), Is.Not.Empty);
  }

  [Test, Category("EdgeCase")]
  public void LzExe_NotMz_Throws() {
    var buf = new byte[64];
    using var ms = new MemoryStream(buf);
    Assert.That(() => new LzExeFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  // ── Petite ────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Petite_DetectsPetiteString() {
    var buf = MinimalPe();
    Encoding.ASCII.GetBytes("Petite").CopyTo(buf.AsSpan(0x200));
    using var ms = new MemoryStream(buf);
    var entries = new PetiteFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
  }

  [Test, Category("EdgeCase")]
  public void Petite_PlainPe_Throws() {
    using var ms = new MemoryStream(MinimalPe());
    Assert.That(() => new PetiteFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  // ── Shrinkler ─────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Shrinkler_DetectsHunkMagicAndStubString() {
    var buf = new byte[1024];
    // AmigaOS HUNK_HEADER magic at offset 0.
    buf[0] = 0x00; buf[1] = 0x00; buf[2] = 0x03; buf[3] = 0xF3;
    Encoding.ASCII.GetBytes("Shrinkler").CopyTo(buf.AsSpan(0x80));

    using var ms = new MemoryStream(buf);
    var entries = new ShrinklerFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "hunk_header.bin"), Is.True);
  }

  [Test, Category("EdgeCase")]
  public void Shrinkler_HunkMagicWithoutStub_Throws() {
    var buf = new byte[1024];
    buf[0] = 0x00; buf[1] = 0x00; buf[2] = 0x03; buf[3] = 0xF3;
    using var ms = new MemoryStream(buf);
    Assert.That(() => new ShrinklerFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Shrinkler_StubWithoutHunkMagic_Throws() {
    var buf = new byte[1024];
    Encoding.ASCII.GetBytes("Shrinkler").CopyTo(buf.AsSpan(0x80));
    using var ms = new MemoryStream(buf);
    Assert.That(() => new ShrinklerFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }
}
