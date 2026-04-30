#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Orc;

namespace Compression.Tests.Orc;

[TestFixture]
public class OrcTests {

  private static byte[] EncodeFooter(long numberOfRows, int stripeCount, int typeCount) {
    using var ms = new MemoryStream();
    for (var i = 0; i < stripeCount; i++) {
      ProtobufWalker.WriteTag(ms, OrcConstants.FooterFieldStripes, OrcConstants.WireLengthDelimited);
      ProtobufWalker.WriteLengthDelimited(ms, []);
    }
    for (var i = 0; i < typeCount; i++) {
      ProtobufWalker.WriteTag(ms, OrcConstants.FooterFieldTypes, OrcConstants.WireLengthDelimited);
      ProtobufWalker.WriteLengthDelimited(ms, []);
    }
    if (numberOfRows > 0) {
      ProtobufWalker.WriteTag(ms, OrcConstants.FooterFieldNumberOfRows, OrcConstants.WireVarint);
      ProtobufWalker.WriteVarLong(ms, numberOfRows);
    }
    return ms.ToArray();
  }

  private static byte[] EncodePostScript(long footerLength, int compression, long[] versions, string magic) {
    using var ms = new MemoryStream();
    ProtobufWalker.WriteTag(ms, OrcConstants.PsFieldFooterLength, OrcConstants.WireVarint);
    ProtobufWalker.WriteVarLong(ms, footerLength);
    ProtobufWalker.WriteTag(ms, OrcConstants.PsFieldCompression, OrcConstants.WireVarint);
    ProtobufWalker.WriteVarLong(ms, compression);
    foreach (var v in versions) {
      ProtobufWalker.WriteTag(ms, OrcConstants.PsFieldVersion, OrcConstants.WireVarint);
      ProtobufWalker.WriteVarLong(ms, v);
    }
    if (magic.Length > 0) {
      ProtobufWalker.WriteTag(ms, OrcConstants.PsFieldMagic, OrcConstants.WireLengthDelimited);
      ProtobufWalker.WriteString(ms, magic);
    }
    return ms.ToArray();
  }

  private static byte[] BuildOrc(long numberOfRows, int stripeCount, int typeCount, int compression, long[] versions) {
    var footer = compression == OrcConstants.CompressionNone
      ? EncodeFooter(numberOfRows, stripeCount, typeCount)
      : new byte[16];
    var ps = EncodePostScript(footer.Length, compression, versions, "ORC");
    if (ps.Length > 255) throw new InvalidOperationException("Test PostScript too large for single-byte length.");
    using var file = new MemoryStream();
    file.Write(OrcConstants.Magic, 0, OrcConstants.Magic.Length);
    file.Write(footer, 0, footer.Length);
    file.Write(ps, 0, ps.Length);
    file.WriteByte((byte)ps.Length);
    return file.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Magic_IsORC() {
    var d = new OrcFormatDescriptor();
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x4F, 0x52, 0x43 }));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var bytes = BuildOrc(0, 0, 0, OrcConstants.CompressionNone, [0, 12]);
    bytes[0] = 0x58;
    bytes[1] = 0x58;
    bytes[2] = 0x58;
    using var ms = new MemoryStream(bytes);
    Assert.Throws<InvalidDataException>(() => _ = new OrcReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsTooSmall() {
    var bytes = new byte[3];
    using var ms = new MemoryStream(bytes);
    Assert.Throws<InvalidDataException>(() => _ = new OrcReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesMinimalFile() {
    var bytes = BuildOrc(0, 0, 0, OrcConstants.CompressionNone, [0, 12]);
    using var ms = new MemoryStream(bytes);
    var r = new OrcReader(ms);
    Assert.That(r.MagicOk, Is.True);
    Assert.That(r.ParseStatus, Is.EqualTo("full"));
    Assert.That(r.Compression, Is.EqualTo("NONE"));
    Assert.That(r.WriterVersion, Is.EqualTo("0.12"));
  }

  [Test, Category("HappyPath")]
  public void Reader_ExtractsRowCount() {
    var bytes = BuildOrc(10, 1, 2, OrcConstants.CompressionNone, [0, 12]);
    using var ms = new MemoryStream(bytes);
    var r = new OrcReader(ms);
    Assert.That(r.NumberOfRows, Is.EqualTo(10L));
    Assert.That(r.StripeCount, Is.EqualTo(1));
    Assert.That(r.TypeCount, Is.EqualTo(2));
    Assert.That(r.ParseStatus, Is.EqualTo("full"));
  }

  [Test, Category("HappyPath")]
  public void Reader_HandlesCompressedFooter_PartialStatus() {
    var bytes = BuildOrc(0, 0, 0, OrcConstants.CompressionZlib, [0, 12]);
    using var ms = new MemoryStream(bytes);
    var r = new OrcReader(ms);
    Assert.That(r.Compression, Is.EqualTo("ZLIB"));
    Assert.That(r.ParseStatus, Is.EqualTo("partial"));
    Assert.That(r.NumberOfRows, Is.EqualTo(0L));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void ProtobufWalker_VarLongRoundTrip() {
    long[] values = [0, 1, 100, 0x12345678L, 0x123456789ABCDEFL];
    foreach (var v in values) {
      using var ms = new MemoryStream();
      ProtobufWalker.WriteVarLong(ms, v);
      ms.Position = 0;
      var actual = ProtobufWalker.ReadVarLong(ms);
      Assert.That(actual, Is.EqualTo(v), $"Round-trip failed for {v}");
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void ProtobufWalker_TagRoundTrip() {
    using var ms = new MemoryStream();
    ProtobufWalker.WriteTag(ms, 5, OrcConstants.WireLengthDelimited);
    ms.Position = 0;
    var (field, wire) = ProtobufWalker.ReadTag(ms);
    Assert.That(field, Is.EqualTo(5));
    Assert.That(wire, Is.EqualTo(OrcConstants.WireLengthDelimited));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void ProtobufWalker_StringRoundTrip() {
    using var ms = new MemoryStream();
    ProtobufWalker.WriteString(ms, "hello");
    ms.Position = 0;
    var actual = ProtobufWalker.ReadString(ms);
    Assert.That(actual, Is.EqualTo("hello"));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries() {
    var bytes = BuildOrc(0, 0, 0, OrcConstants.CompressionNone, [0, 12]);
    using var ms = new MemoryStream(bytes);
    var entries = new OrcFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.orc"));
    Assert.That(entries[1].Name, Is.EqualTo("metadata.ini"));
  }

  [Test, Category("HappyPath")]
  public void Extract_FullOrc_PreservesBytes() {
    var bytes = BuildOrc(0, 0, 0, OrcConstants.CompressionNone, [0, 12]);
    var tmp = Path.Combine(Path.GetTempPath(), "orc_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(bytes);
      new OrcFormatDescriptor().Extract(ms, tmp, null, ["FULL.orc"]);
      var outPath = Path.Combine(tmp, "FULL.orc");
      Assert.That(File.Exists(outPath), Is.True);
      var written = File.ReadAllBytes(outPath);
      Assert.That(written, Is.EqualTo(bytes));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_Metadata_ContainsRowCount() {
    var bytes = BuildOrc(42, 2, 3, OrcConstants.CompressionNone, [0, 12]);
    var tmp = Path.Combine(Path.GetTempPath(), "orc_meta_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(bytes);
      new OrcFormatDescriptor().Extract(ms, tmp, null, ["metadata.ini"]);
      var iniPath = Path.Combine(tmp, "metadata.ini");
      Assert.That(File.Exists(iniPath), Is.True);
      var text = File.ReadAllText(iniPath, Encoding.UTF8);
      Assert.That(text, Does.Contain("[orc]"));
      Assert.That(text, Does.Contain("magic_ok = true"));
      Assert.That(text, Does.Contain("compression = NONE"));
      Assert.That(text, Does.Contain("number_of_rows = 42"));
      Assert.That(text, Does.Contain("stripe_count = 2"));
      Assert.That(text, Does.Contain("type_count = 3"));
      Assert.That(text, Does.Contain("writer_version = 0.12"));
      Assert.That(text, Does.Contain("parse_status = full"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Capabilities_DoesNotIncludeCanCreate() {
    var d = new OrcFormatDescriptor();
    Assert.That(d.Capabilities & FormatCapabilities.CanCreate, Is.EqualTo((FormatCapabilities)0));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new OrcFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Orc"));
    Assert.That(d.DisplayName, Is.EqualTo("Apache ORC"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".orc"));
    Assert.That(d.Extensions, Contains.Item(".orc"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(OrcConstants.Magic));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(d.MagicSignatures[0].Confidence, Is.EqualTo(0.85));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("orc"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("ORC"));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
  }
}
