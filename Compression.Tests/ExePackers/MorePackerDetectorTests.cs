using System.Buffers.Binary;
using System.Text;
using FileFormat.ExePackers;

namespace Compression.Tests.ExePackers;

[TestFixture]
public class MorePackerDetectorTests {

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

  /// <summary>
  /// Builds a minimal PE with the given list of 8-char section names. Section
  /// table is placed right after the 24-byte COFF header (optHdrSize = 0).
  /// </summary>
  private static byte[] MinimalPeWithSections(params string[] sectionNames) {
    var buf = new byte[2048];
    buf[0] = (byte)'M'; buf[1] = (byte)'Z';
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x3C), 0x80);
    buf[0x80] = (byte)'P'; buf[0x81] = (byte)'E';
    // COFF header: numSections at offset 0x86, optHdrSize at offset 0x94 (0x84 + 16).
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x86), (ushort)sectionNames.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x94), 0);
    // Section table: eLfanew(0x80) + 4 (PE\0\0) + 20 (COFF header) + 0 (optHdr) = 0xA8.
    var tableOffset = 0x80 + 24;
    for (var i = 0; i < sectionNames.Length; i++) {
      var nameBytes = Encoding.ASCII.GetBytes(sectionNames[i]);
      var copyLen = Math.Min(8, nameBytes.Length);
      Array.Copy(nameBytes, 0, buf, tableOffset + i * 40, copyLen);
    }
    return buf;
  }

  // ── FSG ───────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Fsg_DetectsMagic() {
    var buf = MinimalPe();
    Encoding.ASCII.GetBytes("FSG!").CopyTo(buf.AsSpan(0x300));
    using var ms = new MemoryStream(buf);
    var entries = new FsgFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "packed_payload.bin"), Is.True);
  }

  [Test, Category("EdgeCase")]
  public void Fsg_PlainPe_Throws() {
    using var ms = new MemoryStream(MinimalPe());
    Assert.That(() => new FsgFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  // ── MEW ───────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Mew_DetectsMewSection() {
    var buf = MinimalPeWithSections("MEW", ".rsrc");
    using var ms = new MemoryStream(buf);
    var entries = new MewFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Mew_DetectsDottedMewSection() {
    var buf = MinimalPeWithSections(".text", ".MEWF");
    using var ms = new MemoryStream(buf);
    Assert.That(new MewFormatDescriptor().List(ms, null), Is.Not.Empty);
  }

  [Test, Category("EdgeCase")]
  public void Mew_PlainPe_Throws() {
    using var ms = new MemoryStream(MinimalPeWithSections(".text", ".data"));
    Assert.That(() => new MewFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  // ── MPRESS ────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void MPress_DetectsSectionName() {
    var buf = MinimalPeWithSections(".MPRESS1", ".MPRESS2", ".rsrc");
    using var ms = new MemoryStream(buf);
    var entries = new MPressFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void MPress_DetectsMatcodeLiteralInElf() {
    var buf = new byte[1024];
    // ELF magic
    buf[0] = 0x7F; buf[1] = (byte)'E'; buf[2] = (byte)'L'; buf[3] = (byte)'F';
    Encoding.ASCII.GetBytes("MATCODE").CopyTo(buf.AsSpan(0x200));
    using var ms = new MemoryStream(buf);
    Assert.That(new MPressFormatDescriptor().List(ms, null), Is.Not.Empty);
  }

  [Test, Category("EdgeCase")]
  public void MPress_PlainPe_Throws() {
    using var ms = new MemoryStream(MinimalPeWithSections(".text", ".rsrc"));
    Assert.That(() => new MPressFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  // ── Crinkler ──────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Crinkler_DetectsLiteral() {
    var buf = MinimalPe();
    Encoding.ASCII.GetBytes("Crinkler 2.3").CopyTo(buf.AsSpan(0x300));
    using var ms = new MemoryStream(buf);
    var entries = new CrinklerFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
    Assert.That(entries.Any(e => e.Name == "packed_payload.bin"), Is.True);
  }

  [Test, Category("EdgeCase")]
  public void Crinkler_PlainPe_Throws() {
    using var ms = new MemoryStream(MinimalPe());
    Assert.That(() => new CrinklerFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  // ── kkrunchy ──────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Kkrunchy_DetectsLiteral() {
    var buf = MinimalPe();
    Encoding.ASCII.GetBytes("packed by kkrunchy").CopyTo(buf.AsSpan(0x300));
    using var ms = new MemoryStream(buf);
    var entries = new KkrunchyFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test, Category("EdgeCase")]
  public void Kkrunchy_PlainPe_Throws() {
    using var ms = new MemoryStream(MinimalPe());
    Assert.That(() => new KkrunchyFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }
}
