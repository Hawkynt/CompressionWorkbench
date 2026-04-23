using System.Buffers.Binary;
using System.Text;
using FileFormat.ExePackers;

namespace Compression.Tests.ExePackers;

[TestFixture]
public class ProtectorDetectorTests {

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
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x86), (ushort)sectionNames.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x94), 0);
    var tableOffset = 0x80 + 24;
    for (var i = 0; i < sectionNames.Length; i++) {
      var nameBytes = Encoding.ASCII.GetBytes(sectionNames[i]);
      var copyLen = Math.Min(8, nameBytes.Length);
      Array.Copy(nameBytes, 0, buf, tableOffset + i * 40, copyLen);
    }
    return buf;
  }

  // -- ASPack -----------------------------------------------------------

  [Test, Category("HappyPath")]
  public void AsPack_DetectsAspackSection() {
    var buf = MinimalPeWithSections(".text", ".aspack");
    using var ms = new MemoryStream(buf);
    var entries = new AsPackFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "packed_payload.bin"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void AsPack_DetectsAsPackLiteral() {
    var buf = MinimalPe();
    Encoding.ASCII.GetBytes("ASPack 2.12").CopyTo(buf.AsSpan(0x300));
    using var ms = new MemoryStream(buf);
    Assert.That(new AsPackFormatDescriptor().List(ms, null), Is.Not.Empty);
  }

  [Test, Category("EdgeCase")]
  public void AsPack_PlainPe_Throws() {
    using var ms = new MemoryStream(MinimalPeWithSections(".text", ".rsrc"));
    Assert.That(() => new AsPackFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  // -- NsPack -----------------------------------------------------------

  [Test, Category("HappyPath")]
  public void NsPack_DetectsDottedNspSection() {
    var buf = MinimalPeWithSections(".nsp0", ".nsp1", ".nsp2");
    using var ms = new MemoryStream(buf);
    var entries = new NsPackFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void NsPack_DetectsNspackLiteral() {
    var buf = MinimalPe();
    Encoding.ASCII.GetBytes("NsPack 3.4").CopyTo(buf.AsSpan(0x300));
    using var ms = new MemoryStream(buf);
    Assert.That(new NsPackFormatDescriptor().List(ms, null), Is.Not.Empty);
  }

  [Test, Category("EdgeCase")]
  public void NsPack_PlainPe_Throws() {
    using var ms = new MemoryStream(MinimalPeWithSections(".text", ".rdata"));
    Assert.That(() => new NsPackFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  // -- Yoda's Crypter ---------------------------------------------------

  [Test, Category("HappyPath")]
  public void YodaCrypter_DetectsYcSection() {
    var buf = MinimalPeWithSections(".text", ".yC");
    using var ms = new MemoryStream(buf);
    var entries = new YodaCrypterFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void YodaCrypter_DetectsYodasLiteral() {
    var buf = MinimalPe();
    Encoding.ASCII.GetBytes("Yoda's Crypter 1.3").CopyTo(buf.AsSpan(0x300));
    using var ms = new MemoryStream(buf);
    Assert.That(new YodaCrypterFormatDescriptor().List(ms, null), Is.Not.Empty);
  }

  [Test, Category("EdgeCase")]
  public void YodaCrypter_PlainPe_Throws() {
    using var ms = new MemoryStream(MinimalPeWithSections(".text", ".rsrc"));
    Assert.That(() => new YodaCrypterFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  // -- ASProtect --------------------------------------------------------

  [Test, Category("HappyPath")]
  public void AsProtect_DetectsAsProtectLiteral() {
    var buf = MinimalPe();
    Encoding.ASCII.GetBytes("ASProtect v2.4").CopyTo(buf.AsSpan(0x300));
    using var ms = new MemoryStream(buf);
    var entries = new AsProtectFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "packed_payload.bin"), Is.True);
  }

  [Test, Category("EdgeCase")]
  public void AsProtect_PlainPe_Throws() {
    // ASProtect requires the literal — bare .aspack section alone shouldn't trigger.
    using var ms = new MemoryStream(MinimalPeWithSections(".text", ".aspack"));
    Assert.That(() => new AsProtectFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  // -- Themida ----------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Themida_DetectsThemidaLiteral() {
    var buf = MinimalPe();
    Encoding.ASCII.GetBytes("Themida 3.0.5").CopyTo(buf.AsSpan(0x300));
    using var ms = new MemoryStream(buf);
    var entries = new ThemidaFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Themida_DetectsWinLicenseLiteral() {
    var buf = MinimalPe();
    Encoding.ASCII.GetBytes("WinLicense Trial").CopyTo(buf.AsSpan(0x300));
    using var ms = new MemoryStream(buf);
    Assert.That(new ThemidaFormatDescriptor().List(ms, null), Is.Not.Empty);
  }

  [Test, Category("EdgeCase")]
  public void Themida_PlainPe_Throws() {
    using var ms = new MemoryStream(MinimalPe());
    Assert.That(() => new ThemidaFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  // -- VMProtect --------------------------------------------------------

  [Test, Category("HappyPath")]
  public void VmProtect_DetectsVmpSection() {
    var buf = MinimalPeWithSections(".text", ".vmp0", ".vmp1");
    using var ms = new MemoryStream(buf);
    var entries = new VmProtectFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void VmProtect_DetectsVmProtectLiteral() {
    var buf = MinimalPe();
    Encoding.ASCII.GetBytes("VMProtect Ultimate").CopyTo(buf.AsSpan(0x300));
    using var ms = new MemoryStream(buf);
    Assert.That(new VmProtectFormatDescriptor().List(ms, null), Is.Not.Empty);
  }

  [Test, Category("EdgeCase")]
  public void VmProtect_PlainPe_Throws() {
    using var ms = new MemoryStream(MinimalPeWithSections(".text", ".rsrc"));
    Assert.That(() => new VmProtectFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }
}
